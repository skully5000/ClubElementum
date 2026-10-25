using UdonSharp;
using UnityEngine;
using VRC.SDK3.Image;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

// One per scene. Downloads each poster image once per visit and shares it with every Poster.
// VRChat doesn't cache image downloads and allows only one every 5 s per player, so without
// this each poster face downloaded an image again every time it came back around. Downloads
// run one at a time, most urgent first, then the rest of the pool fills in while idle, so after
// a few minutes every swap is instant. PosterLinker adds this object and links it automatically.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PosterCache : UdonSharpBehaviour
{
    [Tooltip("Most poster images kept in memory. Past this, the least recently shown one that no poster is using is freed, and downloaded again if it comes back. An 857x2048 poster is about 9 MB.")]
    public int maxCached = 40;

    [Tooltip("Seconds to wait before retrying an image that failed to download.")]
    public float retryDelay = 10f;

    [Tooltip("Seconds before giving up on a download that never finishes, so it can't hold up the rest.")]
    public float downloadTimeout = 60f;

    [Tooltip("Write each download, load and freed image to the VRChat log (output_log), for troubleshooting.")]
    public bool debugLogging = true;

    [Tooltip("Every Poster in the scene. Filled automatically on Play and before each build.")]
    public Poster[] posters;

    // Indexed by pool position. Every poster uses the same URL pool, so the first one's is used.
    private VRCUrl[] _urls;
    private Texture2D[] _textures;
    private VRCImageDownloader[] _downloaders;
    private bool[] _requested;
    private int[] _deadline;     // server time (ms) a poster needs it by
    private float[] _retryAfter; // Time.time before which a failed download isn't retried
    private float[] _lastUsed;
    private int _cachedCount;

    private int _loading = -1; // pool index downloading now; only one at a time
    private float _loadStarted;

    void Start()
    {
        if (!EnsureInit()) Debug.LogError("[PosterCache] No posters linked. Enter Play mode or build once so they're linked automatically.");
    }

    // The image if it's downloaded, otherwise null.
    public Texture2D Get(int index)
    {
        if (!EnsureInit() || index < 0 || index >= _textures.Length) return null;
        Texture2D texture = _textures[index];
        if (texture != null) _lastUsed[index] = Time.time;
        return texture;
    }

    // Download this poster unless it's here or already queued. The earliest deadline (server
    // time in ms it's needed by) downloads first. Every poster is handed the image through
    // _OnPosterCached when it arrives.
    public void Request(int index, int deadline)
    {
        if (!EnsureInit() || index < 0 || index >= _urls.Length || _textures[index] != null) return;
        if (!_requested[index] || deadline - _deadline[index] < 0) _deadline[index] = deadline;
        _requested[index] = true;
        _StartNext();
    }

    public void _StartNext()
    {
        if (_loading >= 0 || !EnsureInit()) return;

        float now = Time.time;
        int next = -1;
        float soonestRetry = -1f;
        for (int i = 0; i < _urls.Length; i++)
        {
            if (!_requested[i]) continue;
            if (now < _retryAfter[i])
            {
                if (soonestRetry < 0f || _retryAfter[i] < soonestRetry) soonestRetry = _retryAfter[i];
                continue;
            }
            // Subtracting keeps this right when the millisecond server clock wraps.
            if (next < 0 || _deadline[i] - _deadline[next] < 0) next = i;
        }

        // Nothing urgent: fill in the rest of the pool so later swaps don't have to wait.
        if (next < 0 && _cachedCount < maxCached) next = NextToPreload(now);

        if (next < 0)
        {
            // Only failed downloads are left; come back when the first may retry.
            if (soonestRetry >= 0f) SendCustomEventDelayedSeconds("_StartNext", soonestRetry - now);
            return;
        }

        _loading = next;
        _loadStarted = now;
        if (_downloaders[next] != null) _downloaders[next].Dispose();

        TextureInfo texInfo = new TextureInfo();
        texInfo.GenerateMipMaps = true;

        // One downloader per image so each can be freed on its own.
        _downloaders[next] = new VRCImageDownloader();
        _downloaders[next].DownloadImage(_urls[next], null, (IUdonEventReceiver)this, texInfo);
        Log("downloading poster " + (next + 1) + (_requested[next]
            ? ", needed in " + Seconds((_deadline[next] - Networking.GetServerTimeInMilliseconds()) / 1000f) + "s"
            : " ahead of time"));
        SendCustomEventDelayedSeconds("_CheckTimeout", downloadTimeout);
    }

    private int NextToPreload(float now)
    {
        // The owner syncs the poster count to every poster; take the largest seen.
        int count = 0;
        for (int i = 0; i < posters.Length; i++)
        {
            if (posters[i] != null && posters[i].syncedCount > count) count = posters[i].syncedCount;
        }
        count = Mathf.Min(count, _urls.Length);

        for (int i = 0; i < count; i++)
        {
            if (_textures[i] == null && now >= _retryAfter[i]) return i;
        }
        return -1;
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        int index = _loading;
        // Anything else is a download already given up on after a timeout.
        if (index < 0 || result.Url.Get() != _urls[index].Get()) return;

        _loading = -1;
        _textures[index] = result.Result;
        _requested[index] = false;
        _lastUsed[index] = Time.time;
        _cachedCount++;
        Log("loaded poster " + (index + 1) + " after " + Seconds(Time.time - _loadStarted) + "s; " + _cachedCount + " cached");

        FreeOverLimit();
        Texture2D texture = _textures[index];
        for (int i = 0; i < posters.Length; i++)
        {
            if (posters[i] != null) posters[i]._OnPosterCached(index, texture);
        }
        _StartNext();
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        int index = _loading;
        if (index < 0 || result.Url.Get() != _urls[index].Get()) return;

        Debug.LogError("[PosterCache] Failed to load " + result.Url + ": " + result.Error + " " + result.ErrorMessage
            + ". Retrying in " + retryDelay + "s.");
        Fail(index);
    }

    public void _CheckTimeout()
    {
        // One of these is queued per download, so only act if the current one really is overdue.
        if (_loading < 0 || Time.time - _loadStarted < downloadTimeout - 0.1f) return;

        Debug.LogError("[PosterCache] Poster " + (_loading + 1) + " took over " + downloadTimeout + "s. Retrying in " + retryDelay + "s.");
        Fail(_loading);
    }

    private void Fail(int index)
    {
        _downloaders[index].Dispose();
        _downloaders[index] = null;
        _retryAfter[index] = Time.time + retryDelay;
        _loading = -1;
        _StartNext();
    }

    private void FreeOverLimit()
    {
        if (_cachedCount <= maxCached) return;

        // Never free an image a poster is showing, fading out, waiting for or about to show.
        bool[] inUse = new bool[_textures.Length];
        for (int i = 0; i < posters.Length; i++)
        {
            Poster poster = posters[i];
            if (poster == null) continue;
            MarkInUse(inUse, poster.currentIndex);
            MarkInUse(inUse, poster.nextIndex);
            MarkInUse(inUse, poster.shownIndex);
            MarkInUse(inUse, poster.displayedIndex);
            MarkInUse(inUse, poster.fadingFromIndex);
        }

        while (_cachedCount > maxCached)
        {
            int oldest = -1;
            for (int i = 0; i < _textures.Length; i++)
            {
                if (_textures[i] != null && !inUse[i] && (oldest < 0 || _lastUsed[i] < _lastUsed[oldest])) oldest = i;
            }
            if (oldest < 0) return; // everything cached is in use, so go over the limit for now

            _downloaders[oldest].Dispose();
            _downloaders[oldest] = null;
            _textures[oldest] = null;
            _cachedCount--;
            Log("freed poster " + (oldest + 1) + " to stay within " + maxCached);
        }
    }

    private void MarkInUse(bool[] inUse, int index)
    {
        if (index >= 0 && index < inUse.Length) inUse[index] = true;
    }

    private bool EnsureInit()
    {
        if (_urls != null) return true;
        if (posters == null) return false;

        for (int i = 0; i < posters.Length && _urls == null; i++)
        {
            if (posters[i] != null && posters[i].imageUrls != null) _urls = posters[i].imageUrls;
        }
        if (_urls == null) return false;

        int size = _urls.Length;
        _textures = new Texture2D[size];
        _downloaders = new VRCImageDownloader[size];
        _requested = new bool[size];
        _deadline = new int[size];
        _retryAfter = new float[size];
        _lastUsed = new float[size];
        return true;
    }

    private void Log(string message)
    {
        if (debugLogging) Debug.Log("[PosterCache] " + message);
    }

    private float Seconds(float value)
    {
        return Mathf.Round(value * 10f) / 10f;
    }

    void OnDestroy()
    {
        if (_downloaders == null) return;
        for (int i = 0; i < _downloaders.Length; i++)
        {
            if (_downloaders[i] != null) _downloaders[i].Dispose();
        }
    }
}
