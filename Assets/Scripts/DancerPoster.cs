using UdonSharp;
using UnityEngine;
using VRC.SDK3.Image;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DancerPoster : UdonSharpBehaviour
{
    [Header("Poster Settings")]
    [Tooltip("The Renderer whose material will be updated with the poster image.")]
    public Renderer targetRenderer;

    [Tooltip("Full URL to the manifest.txt file on GitHub Pages.")]
    public string manifestUrl = "https://skully5000.github.io/ClubElementum/Posters/Dancers/manifest.txt";

    [Tooltip("Base URL for image files — must end with a forward slash.")]
    public string baseImageUrl = "https://skully5000.github.io/ClubElementum/Posters/Dancers/";

    [Tooltip("Seconds between each random poster change.")]
    public float changeInterval = 30f;

    [Tooltip("Shader texture property name on the poster material.")]
    public string materialTextureName = "_MainTex";

    // ── runtime state ──────────────────────────────────────────────────────────
    private string[] _imageNames;
    private VRCImageDownloader _imageDownloader;
    private int _lastIndex = -1;

    void Start()
    {
        _imageDownloader = new VRCImageDownloader();
        VRCStringDownloader.LoadUrl(new VRCUrl(manifestUrl), (IUdonEventReceiver)this);
    }

    // Called when manifest.txt finishes downloading
    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        string raw = result.Result.Trim();

        // Split on newlines (handles both \r\n and \n)
        string[] lines = raw.Split('\n');

        int count = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim().Trim('\r');
            if (line.Length > 0) count++;
        }

        _imageNames = new string[count];
        int idx = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim().Trim('\r');
            if (line.Length > 0) _imageNames[idx++] = line;
        }

        Debug.Log($"[DancerPoster] Loaded manifest with {_imageNames.Length} image(s).");

        LoadRandomImage();
        SendCustomEventDelayedSeconds(nameof(ChangeImage), changeInterval);
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogError($"[DancerPoster] Failed to load manifest: {result.Error}");
    }

    // Scheduled event — fires every changeInterval seconds
    public void ChangeImage()
    {
        LoadRandomImage();
        SendCustomEventDelayedSeconds(nameof(ChangeImage), changeInterval);
    }

    private void LoadRandomImage()
    {
        if (_imageNames == null || _imageNames.Length == 0) return;

        int pick;
        if (_imageNames.Length == 1)
        {
            pick = 0;
        }
        else
        {
            do { pick = Random.Range(0, _imageNames.Length); }
            while (pick == _lastIndex);
        }
        _lastIndex = pick;

        string url = baseImageUrl + _imageNames[pick];
        Debug.Log($"[DancerPoster] Loading image: {url}");

        TextureInfo texInfo = new TextureInfo();
        texInfo.GenerateMipMaps = true;
        texInfo.WrapModeU = TextureWrapMode.Clamp;
        texInfo.WrapModeV = TextureWrapMode.Clamp;

        _imageDownloader.DownloadImage(new VRCUrl(url), null, (IUdonEventReceiver)this, texInfo);
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        if (targetRenderer == null)
        {
            Debug.LogError("[DancerPoster] targetRenderer is not assigned!");
            return;
        }
        targetRenderer.material.SetTexture(materialTextureName, result.Result);
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        Debug.LogError($"[DancerPoster] Failed to load image: {result.Error}");
    }
}
