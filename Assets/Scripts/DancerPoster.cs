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
    [Tooltip("The Renderer whose material will receive the downloaded image.")]
    public Renderer targetRenderer;

    [Tooltip("Full URL to manifest.txt on GitHub Pages.")]
    public string manifestUrl = "https://skully5000.github.io/ClubElementum/Posters/Dancers/manifest.txt";

    [Tooltip("Base URL for images — must end with a forward slash.")]
    public string baseImageUrl = "https://skully5000.github.io/ClubElementum/Posters/Dancers/";

    [Tooltip("Seconds between each random poster change.")]
    public float changeInterval = 30f;

    [Tooltip("Shader texture property name on the poster material.")]
    public string materialTextureName = "_MainTex";

    private string[] _imageNames;
    private VRCImageDownloader _imageDownloader;
    private int _lastIndex = -1;

    void Start()
    {
        _imageDownloader = new VRCImageDownloader();
        VRCStringDownloader.LoadUrl(new VRCUrl(manifestUrl), (IUdonEventReceiver)this);
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        string raw = result.Result.Trim();
        string[] lines = raw.Split(new char[] { '\n' });

        int count = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0) count++;
        }

        _imageNames = new string[count];
        int idx = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length > 0) _imageNames[idx++] = line;
        }

        Debug.Log("[DancerPoster] Loaded " + _imageNames.Length + " image(s) from manifest.");
        LoadRandomImage();
        SendCustomEventDelayedSeconds("ChangeImage", changeInterval);
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogError("[DancerPoster] Failed to load manifest: " + result.Error);
    }

    public void ChangeImage()
    {
        LoadRandomImage();
        SendCustomEventDelayedSeconds("ChangeImage", changeInterval);
    }

    private void LoadRandomImage()
    {
        if (_imageNames == null || _imageNames.Length == 0) return;

        int pick = 0;
        if (_imageNames.Length == 1)
        {
            pick = 0;
        }
        else
        {
            pick = Random.Range(0, _imageNames.Length);
            int attempts = 0;
            while (pick == _lastIndex && attempts < 10)
            {
                pick = Random.Range(0, _imageNames.Length);
                attempts++;
            }
        }
        _lastIndex = pick;

        string url = baseImageUrl + _imageNames[pick];
        Debug.Log("[DancerPoster] Loading: " + url);

        TextureInfo texInfo = new TextureInfo();
        texInfo.GenerateMipMaps = true;

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
        Debug.LogError("[DancerPoster] Failed to load image: " + result.Error);
    }
}
