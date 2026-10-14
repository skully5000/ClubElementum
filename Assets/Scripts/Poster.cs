using UdonSharp;
using UnityEngine;
using VRC.SDK3.Image;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;
#endif

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
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

    [Tooltip("Seconds between each random poster change.")]
    public float changeInterval = 30f;

    [Tooltip("Up to this many extra random seconds are added to each interval, so posters drift out of sync.")]
    public float intervalJitter = 5f;

    [Tooltip("When on, Avoid Same As is filled in automatically (on Play and before each build) with every other Poster whose slot uses the same material. Turn off to set it by hand.")]
    public bool autoLinkByMaterial = true;

    [Tooltip("Other Poster components this one should avoid showing the same image as. Filled automatically when Auto Link By Material is on. If empty, uses the other Poster components on this GameObject.")]
    public Poster[] avoidSameAs;

    [HideInInspector] public int shownIndex = -1;

    [Tooltip("Shader texture property name on the poster material.")]
    public string materialTextureName = "_MainTex";

    [Header("Transition")]
    [Tooltip("Seconds to crossfade between posters. Requires the ClubElementum/PosterCrossfade shader. Set to 0 for an instant swap.")]
    public float fadeDuration = 1.5f;

    [Tooltip("Shader texture property that holds the incoming poster during a fade.")]
    public string nextTextureName = "_NextTex";

    [Tooltip("Shader float property (0..1) that blends from current to next poster.")]
    public string blendPropertyName = "_Blend";

    [Header("URL Pool Generator (editor only)")]
    [Tooltip("Poster URL prefix — the pool index and extension are appended.")]
    public string baseImageUrl = "https://skully5000.github.io/ClubElementum/Posters/served/poster";

    [Tooltip("Number of URLs to generate. Must match POOL_SIZE in build-posters.yml.")]
    public int poolSize = 80;

    public string imageExtension = ".png";

    public string countFileUrl = "https://skully5000.github.io/ClubElementum/Posters/served/count.txt";

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
        // (IsShownByPartner skips this one).
        if (avoidSameAs == null || avoidSameAs.Length == 0) avoidSameAs = GetComponents<Poster>();

        // Unity's generator can start from the same state every launch, so seed it from the clock.
        // Mixing in the current generator state and this object's identity keeps posters that
        // start in the same frame from getting identical seeds.
        int seed = (int)(System.DateTime.UtcNow.Ticks & 0x7FFFFFFF)
            ^ Random.Range(int.MinValue, int.MaxValue)
            ^ gameObject.name.GetHashCode()
            ^ (materialIndex * 7919);
        Random.InitState(seed);

        ChangeImage();
    }

    void Update()
    {
        if (!_fading) return;

        _fadeTime += Time.deltaTime;
        float t = Mathf.Clamp01(_fadeTime / fadeDuration);
        _material.SetFloat(blendPropertyName, Mathf.SmoothStep(0f, 1f, t));
        if (t >= 1f) FinishFade();
    }

    public void ChangeImage()
    {
        // Refresh the count each cycle so newly served posters are picked up mid-session.
        if (countUrl != null && countUrl.Get().Length > 0)
        {
            VRCStringDownloader.LoadUrl(countUrl, (IUdonEventReceiver)this);
        }
        else
        {
            _posterCount = imageUrls == null ? 0 : imageUrls.Length;
            LoadRandomImage();
        }
        SendCustomEventDelayedSeconds("ChangeImage", changeInterval + Random.Range(0f, Mathf.Max(0f, intervalJitter)));
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        int count;
        if (int.TryParse(result.Result.Trim(), out count))
        {
            _posterCount = Mathf.Clamp(count, 0, imageUrls == null ? 0 : imageUrls.Length);
        }
        else
        {
            Debug.LogError("[Poster] count.txt is not a number: " + result.Result);
        }
        LoadRandomImage();
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogError("[Poster] Failed to load poster count: " + result.Error);
        // Never loaded a count — fall back to the whole pool so something still shows.
        if (_posterCount < 0) _posterCount = imageUrls == null ? 0 : imageUrls.Length;
        LoadRandomImage();
    }

    private void LoadRandomImage()
    {
        if (_posterCount <= 0) return;

        int pick = PickNext();
        if (pick == shownIndex && _material != null && _activeDownloader != null) return; // only one poster exists
        shownIndex = pick;

        TextureInfo texInfo = new TextureInfo();
        texInfo.GenerateMipMaps = true;

        // Each poster gets its own downloader so the previous texture can be freed once the new one is shown.
        if (_pendingDownloader != null) _pendingDownloader.Dispose();
        _pendingDownloader = new VRCImageDownloader();
        _pendingDownloader.DownloadImage(imageUrls[pick], null, (IUdonEventReceiver)this, texInfo);
    }

    private int PickNext()
    {
        if (_bag == null || _bag.Length != _posterCount || _bagPos >= _bag.Length) RefillBag();

        // Prefer the next bag entry that isn't on screen here or on a partner poster.
        // With very few posters that may be impossible, so settle for anything that at
        // least changes this poster (otherwise both sides could get stuck forever).
        int chosen = -1;
        for (int i = _bagPos; i < _bag.Length && chosen < 0; i++)
        {
            if (_bag[i] != shownIndex && !IsShownByPartner(_bag[i])) chosen = i;
        }
        for (int i = _bagPos; i < _bag.Length && chosen < 0; i++)
        {
            if (_bag[i] != shownIndex) chosen = i;
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

    private bool IsShownByPartner(int index)
    {
        if (avoidSameAs == null) return false;
        for (int i = 0; i < avoidSameAs.Length; i++)
        {
            if (avoidSameAs[i] != null && avoidSameAs[i] != this && avoidSameAs[i].shownIndex == index) return true;
        }
        return false;
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        if (_material == null)
        {
            Debug.LogError("[Poster] targetRenderer is not assigned!");
            return;
        }

        if (fadeDuration <= 0f)
        {
            _material.SetTexture(materialTextureName, result.Result);
            if (_activeDownloader != null) _activeDownloader.Dispose();
            _activeDownloader = _pendingDownloader;
            _pendingDownloader = null;
            return;
        }

        // A new image arrived mid-fade — snap the current fade to its end first.
        if (_fading) FinishFade();

        _incomingTexture = result.Result;
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
        Debug.LogError("[Poster] Failed to load image: " + result.Error);
    }

    void OnDestroy()
    {
        if (_activeDownloader != null) _activeDownloader.Dispose();
        if (_pendingDownloader != null) _pendingDownloader.Dispose();
        if (_incomingDownloader != null) _incomingDownloader.Dispose();
    }

#if !COMPILER_UDONSHARP && UNITY_EDITOR
    [ContextMenu("Generate URL Pool")]
    private void GenerateUrlPool()
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
