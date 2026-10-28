using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;

/// <summary>
/// Hides event-organising icons (3D objects and canvases) while the local player's handheld
/// photo camera is out, so they never show up in photos.
///
/// Photos are rendered on the photographer's own client, so this only needs to run locally.
/// Udon can't change the photo camera's culling mask, so instead the renderers and canvases
/// are switched off while VRCCameraSettings.PhotoCamera.Active is true.
///
/// Renderers/Canvases are toggled (not GameObject.SetActive) so this doesn't fight Icon.cs,
/// which activates/deactivates its pool objects itself.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class HideFromPhotoCamera : UdonSharpBehaviour
{
    [Tooltip("Root objects to hide from photos. Every Renderer and Canvas on them and their children (including inactive ones) is hidden.")]
    public GameObject[] hideTargets;

    [Tooltip("Optional. The iconPool of each Icon script listed here is hidden too, so you don't have to drag the pool in again.")]
    public Icon[] iconSources;

    private Renderer[] _renderers;
    private Canvas[] _canvases;

    // Only the components WE turned off get turned back on.
    private bool[] _rendererWasOn;
    private bool[] _canvasWasOn;

    private bool _hidden;

    private int _rendererCount;
    private int _canvasCount;

    void Start()
    {
        _rendererCount = 0;
        _canvasCount = 0;

        // Two passes: count, then fill (Udon has no List<T>).
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                _renderers = new Renderer[_rendererCount];
                _canvases = new Canvas[_canvasCount];
                _rendererCount = 0;
                _canvasCount = 0;
            }

            for (int i = 0; i < hideTargets.Length; i++)
            {
                Collect(hideTargets[i], pass);
            }

            for (int i = 0; i < iconSources.Length; i++)
            {
                if (iconSources[i] == null || iconSources[i].iconPool == null) continue;
                GameObject[] pool = iconSources[i].iconPool;
                for (int j = 0; j < pool.Length; j++)
                {
                    Collect(pool[j], pass);
                }
            }
        }

        _rendererWasOn = new bool[_renderers.Length];
        _canvasWasOn = new bool[_canvases.Length];
    }

    private void Collect(GameObject root, int pass)
    {
        if (root == null) return;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (pass == 1) _renderers[_rendererCount] = renderers[i];
            _rendererCount++;
        }

        Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
        for (int i = 0; i < canvases.Length; i++)
        {
            if (pass == 1) _canvases[_canvasCount] = canvases[i];
            _canvasCount++;
        }
    }

    void Update()
    {
        // PhotoCamera is null in ClientSim; in a real client it never is.
        VRCCameraSettings photo = VRCCameraSettings.PhotoCamera;
        bool cameraOut = photo != null && photo.Active;

        if (cameraOut == _hidden) return;
        _hidden = cameraOut;

        if (_hidden) Hide();
        else Show();
    }

    private void Hide()
    {
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null) continue;
            _rendererWasOn[i] = _renderers[i].enabled;
            _renderers[i].enabled = false;
        }

        for (int i = 0; i < _canvases.Length; i++)
        {
            if (_canvases[i] == null) continue;
            _canvasWasOn[i] = _canvases[i].enabled;
            _canvases[i].enabled = false;
        }
    }

    private void Show()
    {
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] != null && _rendererWasOn[i]) _renderers[i].enabled = true;
        }

        for (int i = 0; i < _canvases.Length; i++)
        {
            if (_canvases[i] != null && _canvasWasOn[i]) _canvases[i].enabled = true;
        }
    }
}
