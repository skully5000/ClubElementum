using UdonSharp;
using UnityEngine;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;
#endif

// The object's owner (the instance master by default) picks each poster a full interval ahead
// and syncs it with the server time it should appear. Every client asks the shared PosterCache
// for it early and swaps at that time, so nobody is left behind waiting on a download. Late
// joiners get the current and upcoming posters on join. Images come from PosterCache, which
// downloads each one once per visit and shares it between posters.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class Poster : UdonSharpBehaviour
{
    [Header("Poster Settings")]
    [Tooltip("The Renderer whose material will receive the downloaded image.")]
    public Renderer targetRenderer;

    [Tooltip("Which material slot on the renderer shows the poster. PosterStand: 1 = front, 2 = back.")]
    public int materialIndex = 0;

    [Tooltip("Pool of generic poster URLs (poster1..posterN). Fill via right-click > Generate URL Pool.")]
    public VRCUrl[] imageUrls;

    [Tooltip("VRCUrl pointing to served/count.txt — the number of posters that actually exist.")]
    public VRCUrl countUrl;

    [Tooltip("Seconds between each random poster change. Images are cached after their first download, so this can be short; linked posters also stay 6 s apart, which stretches it when there are many.")]
    public float changeInterval = 30f;

    [Tooltip("Up to this many extra random seconds are added to each interval, so posters drift out of sync.")]
    public float intervalJitter = 5f;

    [Tooltip("When on, Avoid Same As is filled in automatically (on Play and before each build) with every other Poster whose slot uses the same material. Turn off to set it by hand.")]
    public bool autoLinkByMaterial = true;

    [Tooltip("Other Poster components this one should avoid showing the same image as. Filled automatically when Auto Link By Material is on. If empty, uses the other Poster components on this GameObject.")]
    public Poster[] avoidSameAs;

    [Tooltip("The PosterCache that downloads images and shares them between posters. Added and filled in automatically on Play and before each build.")]
    public PosterCache cache;

    // Synced: the poster up now, the one replacing it, and when the swap happens (server time
    // in ms). currentIndex stays -1 until the owner's first pick. Partners read all three.
    [UdonSynced, HideInInspector] public int currentIndex = -1;
    [UdonSynced, HideInInspector] public int nextIndex = -1;
    [UdonSynced, HideInInspector] public int switchAt;
    // The poster count, so a new owner can keep picking before it loads count.txt and
    // PosterCache knows how many to download ahead of time.
    [UdonSynced, HideInInspector] public int syncedCount;

    // Read by PosterCache so it never frees an image that's on screen or about to be.
    [HideInInspector] public int shownIndex = -1;      // poster that should be on screen (may still be downloading)
    [HideInInspector] public int displayedIndex = -1;  // poster actually on the material
    [HideInInspector] public int fadingFromIndex = -1; // poster fading out during a crossfade

    [Tooltip("Shader texture property name on the poster material.")]
    public string materialTextureName = "_MainTex";

    [Header("Transition")]
    [Tooltip("Seconds to crossfade between posters. Requires the ClubElementum/PosterCrossfade shader. Set to 0 for an instant swap.")]
    public float fadeDuration = 1.5f;

    [Tooltip("Shader texture property that holds the incoming poster during a fade.")]
    public string nextTextureName = "_NextTex";

    [Tooltip("Shader float property (0..1) that blends from current to next poster.")]
    public string blendPropertyName = "_Blend";

    [Header("Debug")]
    [Tooltip("Write each pick, sync update and swap to the VRChat log (output_log), for troubleshooting. PosterCache logs the downloads.")]
    public bool debugLogging = true;

    [Header("URL Pool Generator (editor only)")]
    [Tooltip("Poster URL prefix — the pool index and extension are appended.")]
    public string baseImageUrl = "https://skully5000.github.io/ClubElementum/Posters/served/poster";

    [Tooltip("Number of URLs to generate. Must match POOL_SIZE in build-posters.yml.")]
    public int poolSize = 80;

    public string imageExtension = ".jpg";

    public string countFileUrl = "https://skully5000.github.io/ClubElementum/Posters/served/count.txt";

    // VRChat downloads one image per 5 s per player and serves extra requests in random order,
    // so linked posters swap at least this far apart to keep that queue from backing up.
    private const int SwapGapMs = 6000;

    private int _posterCount = -1;

    // Shuffle bag: every poster is shown once in random order before any repeats.
    private int[] _bag;
    private int _bagPos;

    private Material _material;
    private bool _fading;
    private float _fadeTime;
    private Texture2D _incomingTexture;
    private int _requestedNext = -1; // upcoming poster already asked of the cache

    void Start()
    {
        if (targetRenderer != null)
        {
            // .materials returns this renderer's instanced materials, so two Poster
            // components on one renderer (front/back) each get their own slot.
            Material[] materials = targetRenderer.materials;
            if (materialIndex >= 0 && materialIndex < materials.Length) _material = materials[materialIndex];
            else Debug.LogError("[Poster] materialIndex " + materialIndex + " is out of range.");
        }

        // No partners set: pair up with the other Poster components on this GameObject
        // (IsUsedByPartner skips this one).
        if (avoidSameAs == null || avoidSameAs.Length == 0) avoidSameAs = GetComponents<Poster>();

        // Unity's generator can start from the same state every launch, so seed it from the clock.
        // Mixing in the current generator state and this object's identity keeps posters that
        // start in the same frame from getting identical seeds.
        int seed = (int)(System.DateTime.UtcNow.Ticks & 0x7FFFFFFF)
            ^ Random.Range(int.MinValue, int.MaxValue)
            ^ gameObject.name.GetHashCode()
            ^ (materialIndex * 7919);
        Random.InitState(seed);

        if (cache == null) Debug.LogError("[Poster] " + gameObject.name + " has no PosterCache. Enter Play mode or build once so it's added and linked automatically.");

        // The owner needs the count before its first pick; everyone else waits for the owner.
        bool owner = Networking.IsOwner(gameObject);
        Log(owner ? "start: owner, loading count" : "start: waiting for the owner's picks");
        if (owner) RefreshCount();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        Log("ownership moved to p" + player.playerId);
        // The previous owner left before its first pick, so start the rotation here instead.
        if (player.isLocal && currentIndex < 0) RefreshCount();
    }

    public override void OnDeserialization()
    {
        Log("received: " + Describe());
    }

    void Update()
    {
        if (_fading)
        {
            _fadeTime += Time.deltaTime;
            float t = Mathf.Clamp01(_fadeTime / fadeDuration);
            _material.SetFloat(blendPropertyName, Mathf.SmoothStep(0f, 1f, t));
            if (t >= 1f) FinishFade();
        }

        if (currentIndex < 0 || cache == null) return; // the owner hasn't picked anything yet

        // Compare by subtracting so this stays right when the millisecond server clock wraps.
        int now = Networking.GetServerTimeInMilliseconds();
        if (now - switchAt >= 0 && Networking.IsOwner(gameObject)) Advance(now);
        bool swapDue = now - switchAt >= 0;

        // Everyone works out what should be on screen from the synced state, so a late
        // or missed update corrects itself on the next frame.
        int target = swapDue ? nextIndex : currentIndex;
        if (target != shownIndex && IsValid(target)) Show(target, now);

        // Ask for the upcoming poster now so it's downloaded by its swap. The cache does
        // nothing if it already has it, and fetches whatever is needed soonest first.
        if (!swapDue && nextIndex != _requestedNext && IsValid(nextIndex))
        {
            _requestedNext = nextIndex;
            cache.Request(nextIndex, switchAt);
        }
    }

    private void Advance(int now)
    {
        if (_posterCount <= 0) _posterCount = Mathf.Min(syncedCount, imageUrls == null ? 0 : imageUrls.Length);
        if (_posterCount <= 0) return;

        currentIndex = nextIndex;
        nextIndex = PickNext();
        switchAt = SpacedFromPartners(now + NextDelayMs());
        syncedCount = _posterCount;
        RequestSerialization();
        Log("picked: " + Describe());

        // Refresh the count each cycle so newly served posters are picked up mid-session.
        RefreshCount();
    }

    private void StartRotation()
    {
        if (currentIndex >= 0 || _posterCount <= 0 || !Networking.IsOwner(gameObject)) return;

        currentIndex = PickNext();
        nextIndex = PickNext();
        switchAt = SpacedFromPartners(Networking.GetServerTimeInMilliseconds() + NextDelayMs());
        syncedCount = _posterCount;
        RequestSerialization();
        Log("first pick: " + Describe());
    }

    private int SpacedFromPartners(int time)
    {
        // Push the swap later until it's SwapGapMs clear of every partner's pending swap.
        // It only ever moves forward, so it can't land back near a partner it already
        // cleared, and partners + 1 passes are enough. Subtracting handles clock wrap.
        if (avoidSameAs == null) return time;
        for (int pass = 0; pass <= avoidSameAs.Length; pass++)
        {
            bool moved = false;
            for (int i = 0; i < avoidSameAs.Length; i++)
            {
                Poster partner = avoidSameAs[i];
                if (partner == null || partner == this || partner.currentIndex < 0) continue;
                int gap = time - partner.switchAt;
                if (gap > -SwapGapMs && gap < SwapGapMs)
                {
                    time = partner.switchAt + SwapGapMs;
                    moved = true;
                }
            }
            if (!moved) break;
        }
        return time;
    }

    private int NextDelayMs()
    {
        return (int)((changeInterval + Random.Range(0f, Mathf.Max(0f, intervalJitter))) * 1000f);
    }

    private void RefreshCount()
    {
        if (countUrl != null && countUrl.Get().Length > 0)
        {
            VRCStringDownloader.LoadUrl(countUrl, (IUdonEventReceiver)this);
        }
        else
        {
            _posterCount = imageUrls == null ? 0 : imageUrls.Length;
            StartRotation();
        }
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        int count;
        if (int.TryParse(result.Result.Trim(), out count))
        {
            _posterCount = Mathf.Clamp(count, 0, imageUrls == null ? 0 : imageUrls.Length);
            Log("count.txt says " + count);
        }
        else
        {
            Debug.LogError("[Poster] count.txt is not a number: " + result.Result);
        }
        StartRotation();
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogError("[Poster] Failed to load poster count: " + result.Error);
        // Never loaded a count — fall back to the whole pool so something still shows.
        if (_posterCount < 0) _posterCount = imageUrls == null ? 0 : imageUrls.Length;
        StartRotation();
    }

    private bool IsValid(int index)
    {
        return index >= 0 && imageUrls != null && index < imageUrls.Length;
    }

    private void Show(int index, int now)
    {
        shownIndex = index;

        // Normally the cache fetched it ahead of the swap.
        Texture2D texture = cache.Get(index);
        if (texture != null)
        {
            Log("showing poster " + (index + 1) + " (cached)");
            Display(texture, index);
            return;
        }

        // Late joiner, or the download is running late: it's needed right now.
        Log("showing poster " + (index + 1) + " (waiting for download)");
        cache.Request(index, now);
    }

    // Called by PosterCache after each download. The image is passed in rather than fetched,
    // because calling back into the cache from inside its own callback isn't safe in Udon.
    public void _OnPosterCached(int index, Texture2D texture)
    {
        if (index != shownIndex || index == displayedIndex) return;
        Log("poster " + (index + 1) + " arrived");
        Display(texture, index);
    }

    private int PickNext()
    {
        if (_bag == null || _bag.Length != _posterCount || _bagPos >= _bag.Length) RefillBag();

        // Prefer the next bag entry that isn't up or coming up, here or on a partner poster.
        // With very few posters that may be impossible, so settle for anything that at
        // least changes this poster (otherwise both sides could get stuck forever).
        int chosen = -1;
        for (int i = _bagPos; i < _bag.Length && chosen < 0; i++)
        {
            if (_bag[i] != currentIndex && !IsUsedByPartner(_bag[i])) chosen = i;
        }
        for (int i = _bagPos; i < _bag.Length && chosen < 0; i++)
        {
            if (_bag[i] != currentIndex) chosen = i;
        }
        if (chosen < 0) chosen = _bagPos;

        int pick = _bag[chosen];
        _bag[chosen] = _bag[_bagPos];
        _bag[_bagPos] = pick;
        _bagPos++;
        return pick;
    }

    private void RefillBag()
    {
        _bag = new int[_posterCount];
        for (int i = 0; i < _posterCount; i++) _bag[i] = i;

        // Fisher–Yates shuffle.
        for (int i = _posterCount - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            int tmp = _bag[i];
            _bag[i] = _bag[j];
            _bag[j] = tmp;
        }
        _bagPos = 0;
    }

    private bool IsUsedByPartner(int index)
    {
        if (avoidSameAs == null) return false;
        for (int i = 0; i < avoidSameAs.Length; i++)
        {
            Poster partner = avoidSameAs[i];
            if (partner != null && partner != this && (partner.currentIndex == index || partner.nextIndex == index)) return true;
        }
        return false;
    }

    private void Display(Texture2D texture, int index)
    {
        if (_material == null)
        {
            Debug.LogError("[Poster] targetRenderer is not assigned!");
            return;
        }

        if (fadeDuration <= 0f)
        {
            _material.SetTexture(materialTextureName, texture);
            displayedIndex = index;
            return;
        }

        // A new image arrived mid-fade — snap the current fade to its end first.
        if (_fading) FinishFade();

        fadingFromIndex = displayedIndex;
        displayedIndex = index;
        _incomingTexture = texture;

        _material.SetTexture(nextTextureName, _incomingTexture);
        _material.SetFloat(blendPropertyName, 0f);
        _fadeTime = 0f;
        _fading = true;
    }

    private void FinishFade()
    {
        // Promote the incoming poster to current and reset the blend in the same frame.
        _material.SetTexture(materialTextureName, _incomingTexture);
        _material.SetFloat(blendPropertyName, 0f);
        _fading = false;
        _incomingTexture = null;
        fadingFromIndex = -1; // the old image is off screen, so PosterCache may free it
    }

    private void Log(string message)
    {
        if (!debugLogging) return;
        // Test clients all share one account, so player IDs are what tell them apart.
        VRCPlayerApi me = Networking.LocalPlayer;
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        Debug.Log("[Poster] " + gameObject.name + " slot " + materialIndex
            + " me=p" + (Utilities.IsValid(me) ? me.playerId : 0)
            + " owner=p" + (Utilities.IsValid(owner) ? owner.playerId : 0) + ": " + message);
    }

    private string Describe()
    {
        return "current " + (currentIndex + 1) + ", next " + (nextIndex + 1)
            + ", swap in " + Seconds((switchAt - Networking.GetServerTimeInMilliseconds()) / 1000f) + "s";
    }

    private float Seconds(float value)
    {
        return Mathf.Round(value * 10f) / 10f;
    }

#if !COMPILER_UDONSHARP && UNITY_EDITOR
    [ContextMenu("Generate URL Pool")]
    public void GenerateUrlPool()
    {
        Undo.RecordObject(this, "Generate URL Pool");
        imageUrls = new VRCUrl[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            imageUrls[i] = new VRCUrl(baseImageUrl + (i + 1) + imageExtension);
        }
        countUrl = new VRCUrl(countFileUrl);
        EditorUtility.SetDirty(this);
        Debug.Log("[Poster] Generated " + poolSize + " poster URLs.");
    }
#endif
}
