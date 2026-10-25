using UdonSharp;
using UnityEngine;
using VRC.SDK3.Image;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;
#endif

// The object's owner (the instance master by default) picks each poster a full interval ahead
// and syncs it with the server time it should appear. Every client downloads it early and swaps
// at that time, so VRChat's image queue (one download per 5 s, served in random order) can't
// leave one player behind. Late joiners get the current and upcoming posters on join.
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

    [Tooltip("Seconds between each random poster change. VRChat downloads at most 12 images a minute per player for the whole world, so keep (poster faces x 60 / this) at about 6 or less. Linked posters also stay 6 s apart, which stretches this when there are many.")]
    public float changeInterval = 30f;

    [Tooltip("Up to this many extra random seconds are added to each interval, so posters drift out of sync.")]
    public float intervalJitter = 5f;

    [Tooltip("Seconds to wait before retrying a poster image that failed to download.")]
    public float retryDelay = 10f;

    [Tooltip("When on, Avoid Same As is filled in automatically (on Play and before each build) with every other Poster whose slot uses the same material. Turn off to set it by hand.")]
    public bool autoLinkByMaterial = true;

    [Tooltip("Other Poster components this one should avoid showing the same image as. Filled automatically when Auto Link By Material is on. If empty, uses the other Poster components on this GameObject.")]
    public Poster[] avoidSameAs;

    // Synced: the poster up now, the one replacing it, and when the swap happens (server time
    // in ms). currentIndex stays -1 until the owner's first pick. Partners read all three.
    [UdonSynced, HideInInspector] public int currentIndex = -1;
    [UdonSynced, HideInInspector] public int nextIndex = -1;
    [UdonSynced, HideInInspector] public int switchAt;
    [UdonSynced] private int _syncedCount; // lets a new owner keep picking before it loads count.txt

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
    [Tooltip("Write each pick, sync update and download to the VRChat log (output_log), for troubleshooting.")]
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

    private VRCImageDownloader _activeDownloader;
    private VRCImageDownloader _pendingDownloader;
    private int _posterCount = -1;

    // Shuffle bag: every poster is shown once in random order before any repeats.
    private int[] _bag;
    private int _bagPos;

    private Material _material;
    private bool _fading;
    private float _fadeTime;
    private Texture2D _incomingTexture;
    private VRCImageDownloader _incomingDownloader;

    private int _shownIndex = -1;    // poster on screen, or downloading to go on screen
    private int _prefetchIndex = -1; // upcoming poster, downloading or held until its swap
    private VRCImageDownloader _prefetchDownloader;
    private Texture2D _prefetchTexture;
    private float _retryAfter;
    private float _pendingRequestedAt;
    private float _prefetchRequestedAt;

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

        if (currentIndex < 0) return; // the owner hasn't picked anything yet

        // Compare by subtracting so this stays right when the millisecond server clock wraps.
        int now = Networking.GetServerTimeInMilliseconds();
        if (now - switchAt >= 0 && Networking.IsOwner(gameObject)) Advance(now);
        bool swapDue = now - switchAt >= 0;

        if (Time.time < _retryAfter) return; // waiting out a failed download

        // Everyone works out what should be on screen from the synced state, so a late
        // or missed update corrects itself on the next frame.
        int target = swapDue ? nextIndex : currentIndex;
        if (target != _shownIndex && IsValid(target)) Show(target);

        // Fetch the upcoming poster once the one on screen has arrived, so a late joiner's
        // current posters don't queue behind its upcoming ones.
        if (!swapDue && _pendingDownloader == null && nextIndex != _prefetchIndex && nextIndex != _shownIndex && IsValid(nextIndex))
        {
            Prefetch(nextIndex);
        }
    }

    private void Advance(int now)
    {
        if (_posterCount <= 0) _posterCount = Mathf.Min(_syncedCount, imageUrls == null ? 0 : imageUrls.Length);
        if (_posterCount <= 0) return;

        currentIndex = nextIndex;
        nextIndex = PickNext();
        switchAt = SpacedFromPartners(now + NextDelayMs());
        _syncedCount = _posterCount;
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
        _syncedCount = _posterCount;
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

    private void Show(int index)
    {
        _shownIndex = index;

        // Normally the poster was fetched ahead of its swap, so hand that download over.
        if (index == _prefetchIndex)
        {
            Log("showing poster " + (index + 1) + (_prefetchTexture != null ? " (prefetched)" : " (prefetch still downloading)"));
            if (_pendingDownloader != null) _pendingDownloader.Dispose();
            _pendingDownloader = _prefetchDownloader;
            _pendingRequestedAt = _prefetchRequestedAt;
            Texture2D ready = _prefetchTexture;
            _prefetchDownloader = null;
            _prefetchTexture = null;
            _prefetchIndex = -1;
            if (ready != null) Display(ready); // otherwise OnImageLoadSuccess shows it on arrival
            return;
        }

        // Late joiner, or the prefetch failed: fetch it now.
        Log("showing poster " + (index + 1) + " (downloading now)");
        if (_pendingDownloader != null) _pendingDownloader.Dispose();
        _pendingDownloader = Download(index);
        _pendingRequestedAt = Time.time;
    }

    private void Prefetch(int index)
    {
        Log("prefetching poster " + (index + 1));
        if (_prefetchDownloader != null) _prefetchDownloader.Dispose();
        _prefetchIndex = index;
        _prefetchTexture = null;
        _prefetchDownloader = Download(index);
        _prefetchRequestedAt = Time.time;
    }

    private VRCImageDownloader Download(int index)
    {
        TextureInfo texInfo = new TextureInfo();
        texInfo.GenerateMipMaps = true;

        // Each poster gets its own downloader so its texture can be freed once it's replaced.
        VRCImageDownloader downloader = new VRCImageDownloader();
        downloader.DownloadImage(imageUrls[index], null, (IUdonEventReceiver)this, texInfo);
        return downloader;
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

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        string url = result.Url.Get();
        if (_prefetchIndex >= 0 && url == imageUrls[_prefetchIndex].Get())
        {
            Log("prefetched poster " + (_prefetchIndex + 1) + " after " + Seconds(Time.time - _prefetchRequestedAt) + "s; " + Describe());
            _prefetchTexture = result.Result; // held until its swap time
            return;
        }
        if (_shownIndex >= 0 && url == imageUrls[_shownIndex].Get())
        {
            Log("loaded poster " + (_shownIndex + 1) + " after " + Seconds(Time.time - _pendingRequestedAt) + "s");
            Display(result.Result);
            return;
        }
        Log("ignored a replaced download: " + url);
    }

    private void Display(Texture2D texture)
    {
        if (_material == null)
        {
            Debug.LogError("[Poster] targetRenderer is not assigned!");
            return;
        }

        if (fadeDuration <= 0f)
        {
            _material.SetTexture(materialTextureName, texture);
            if (_activeDownloader != null) _activeDownloader.Dispose();
            _activeDownloader = _pendingDownloader;
            _pendingDownloader = null;
            return;
        }

        // A new image arrived mid-fade — snap the current fade to its end first.
        if (_fading) FinishFade();

        _incomingTexture = texture;
        _incomingDownloader = _pendingDownloader;
        _pendingDownloader = null;

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

        // The old texture is no longer displayed, so it's safe to free now.
        if (_activeDownloader != null) _activeDownloader.Dispose();
        _activeDownloader = _incomingDownloader;
        _incomingDownloader = null;
        _incomingTexture = null;
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        // A download replaced before it arrived reports "Canceled" here; that's expected.
        string url = result.Url.Get();
        bool prefetch = _prefetchIndex >= 0 && url == imageUrls[_prefetchIndex].Get();
        bool shown = !prefetch && _shownIndex >= 0 && url == imageUrls[_shownIndex].Get();
        if (!prefetch && !shown)
        {
            Log("cancelled a replaced download: " + url);
            return;
        }

        Debug.LogError("[Poster] " + gameObject.name + " failed to load " + url + ": "
            + result.Error + " " + result.ErrorMessage);

        // Forget the failed download so Update fetches it again once the retry delay has passed.
        if (prefetch) _prefetchIndex = -1;
        else _shownIndex = -1;
        _retryAfter = Time.time + retryDelay;
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

    void OnDestroy()
    {
        if (_activeDownloader != null) _activeDownloader.Dispose();
        if (_pendingDownloader != null) _pendingDownloader.Dispose();
        if (_incomingDownloader != null) _incomingDownloader.Dispose();
        if (_prefetchDownloader != null) _prefetchDownloader.Dispose();
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
