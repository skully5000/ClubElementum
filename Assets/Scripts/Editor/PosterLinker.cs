using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

/// <summary>
/// Fills Poster.avoidSameAs so every Poster whose slot uses the same material asset
/// avoids showing the same image as the rest of its group. Udon can't search the scene
/// at runtime, so this runs in the editor: automatically on entering Play mode and before
/// every VRChat build, or manually from Tools > Posters > Link Posters By Material.
/// </summary>
[InitializeOnLoad]
public class PosterLinker : IVRCSDKBuildRequestedCallback
{
    public int callbackOrder => 0;

    static PosterLinker()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode) LinkAll();
        };
    }

    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        LinkAll();
        return true;
    }

    [MenuItem("Tools/Posters/Link Posters By Material")]
    public static void LinkAll()
    {
        var groups = new Dictionary<Material, List<Poster>>();
        foreach (Poster poster in Object.FindObjectsOfType<Poster>(true))
        {
            if (!poster.autoLinkByMaterial) continue;
            Material mat = GetSlotMaterial(poster);
            if (mat == null) continue;
            if (!groups.TryGetValue(mat, out var list)) groups[mat] = list = new List<Poster>();
            list.Add(poster);
        }

        int changed = 0;
        foreach (var pair in groups)
        {
            foreach (Poster poster in pair.Value)
            {
                Poster[] others = pair.Value.Where(o => o != poster).ToArray();
                if (poster.avoidSameAs != null && poster.avoidSameAs.SequenceEqual(others)) continue;

                Undo.RecordObject(poster, "Link Posters By Material");
                poster.avoidSameAs = others;
                EditorUtility.SetDirty(poster);
                // Push to the backing UdonBehaviour in case UdonSharp already synced it for this play/build.
                UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(poster);
                changed++;
            }
        }

        string summary = string.Join(", ", groups.Select(g => $"{g.Key.name} ({g.Value.Count})"));
        Debug.Log($"[Poster] Linked posters by material: {summary}. {changed} updated.");
    }

    private static Material GetSlotMaterial(Poster poster)
    {
        if (poster.targetRenderer == null) return null;
        Material[] mats = poster.targetRenderer.sharedMaterials;
        int i = poster.materialIndex;
        return i >= 0 && i < mats.Length ? mats[i] : null;
    }
}
