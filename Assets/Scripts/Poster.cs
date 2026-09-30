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
public class DancerPoster : UdonSharpBehaviour
{
    [Header("Poster Settings")]
    [Tooltip("The Renderer whose material will receive the downloaded image.")]
    public Renderer targetRenderer;

    [Tooltip("Pool of generic poster URLs (poster1..posterN). Fill via right-click > Generate URL Pool.")]
    public VRCUrl[] imageUrls;

    [Tooltip("VRCUrl pointing to served/count.txt — the number of posters that actually exist.")]
    public VRCUrl countUrl;

    [Tooltip("Seconds between each random poster change.")]
    public float changeInterval = 30f;

    [Tooltip("Shader texture property name on the poster material.")]
    public string materialTextureName = "_MainTex";

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
    private int _lastIndex = -1;

    void Start()
    {
        ChangeImage();
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
        SendCustomEventDelayedSeconds("ChangeImage", changeInterval);
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
            Debug.LogError("[DancerPoster] count.txt is not a number: " + result.Result);
        }
        LoadRandomImage();
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogError("[DancerPoster] Failed to load poster count: " + result.Error);
        // Never loaded a count — fall back to the whole pool so something still shows.
        if (_posterCount < 0) _posterCount = imageUrls == null ? 0 : imageUrls.Length;
        LoadRandomImage();
    }

    private void LoadRandomImage()
    {
        if (_posterCount <= 0) return;

        int pick = 0;
        if (_posterCount > 1)
        {
            pick = Random.Range(0, _posterCount);
            int attempts = 0;
            while (pick == _lastIndex && attempts < 10)
            {
                pick = Random.Range(0, _posterCount);
                attempts++;
            }
        }
        _lastIndex = pick;

        TextureInfo texInfo = new TextureInfo();
        texInfo.GenerateMipMaps = true;

        // Each poster gets its own downloader so the previous texture can be freed once the new one is shown.
        if (_pendingDownloader != null) _pendingDownloader.Dispose();
        _pendingDownloader = new VRCImageDownloader();
        _pendingDownloader.DownloadImage(imageUrls[pick], null, (IUdonEventReceiver)this, texInfo);
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        if (targetRenderer == null)
        {
            Debug.LogError("[DancerPoster] targetRenderer is not assigned!");
            return;
        }
        targetRenderer.material.SetTexture(materialTextureName, result.Result);

        if (_activeDownloader != null) _activeDownloader.Dispose();
        _activeDownloader = _pendingDownloader;
        _pendingDownloader = null;
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        Debug.LogError("[DancerPoster] Failed to load image: " + result.Error);
    }

    void OnDestroy()
    {
        if (_activeDownloader != null) _activeDownloader.Dispose();
        if (_pendingDownloader != null) _pendingDownloader.Dispose();
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
        Debug.Log("[DancerPoster] Generated " + poolSize + " poster URLs.");
    }
#endif
}
