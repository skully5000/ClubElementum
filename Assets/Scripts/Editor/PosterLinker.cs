using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

/// <summary>
/// Fills Poster.avoidSameAs so every Poster whose slot uses the same material asset
/// avoids showing the same image as the rest of its group, and links every Poster to the
/// scene's PosterCache (adding one if needed). Udon can't search the scene at runtime, so
/// this runs in the editor: automatically on entering Play mode and before every VRChat
/// build, or manually from Tools > Posters > Link Posters.
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
        EditorApplication.delayCall += () =>
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode) EnsureCacheProgramAsset();
        };
    }

    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        LinkAll();
        return true;
    }

    [MenuItem("Tools/Posters/Link Posters")]
    public static void LinkAll()
    {
        LinkCache();

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

    /// <summary>
    /// Runs Generate URL Pool on every Poster in the open scenes, for stands whose URLs were
    /// generated in the scene and so no longer follow the prefab (e.g. after a format change).
    /// </summary>
    [MenuItem("Tools/Posters/Regenerate URL Pools")]
    public static void RegenerateUrlPools()
    {
        Poster[] posters = Object.FindObjectsOfType<Poster>(true);
        int changed = 0;
        foreach (Poster poster in posters)
        {
            string before = poster.imageUrls != null && poster.imageUrls.Length > 0 ? poster.imageUrls[0].Get() : "";
            poster.GenerateUrlPool();
            if (poster.imageUrls.Length > 0 && poster.imageUrls[0].Get() != before) changed++;
        }

        // Each Poster's own Image Extension is used, so show what came out; if nothing changed,
        // the extension (usually set on the prefab) is probably still the old one.
        string example = posters.Length > 0 && posters[0].imageUrls.Length > 0 ? posters[0].imageUrls[0].Get() : "none";
        Debug.Log($"[Poster] Regenerated URL pools on {posters.Length} posters, {changed} changed. First URL: {example}");
    }

    /// <summary>
    /// Points every Poster at the scene's PosterCache, adding one if there isn't one yet,
    /// and gives the cache the list of posters it serves.
    /// </summary>
    private static void LinkCache()
    {
        Poster[] posters = Object.FindObjectsOfType<Poster>(true);
        if (posters.Length == 0) return;

        PosterCache[] caches = Object.FindObjectsOfType<PosterCache>(true);
        PosterCache cache = caches.FirstOrDefault();
        if (cache == null)
        {
            if (!EnsureCacheProgramAsset()) return;
            var go = new GameObject("PosterCache");
            Undo.RegisterCreatedObjectUndo(go, "Add PosterCache");
            cache = UdonSharpEditor.UdonSharpUndo.AddComponent<PosterCache>(go);
            Debug.Log("[Poster] Added a PosterCache object to the scene. Save the scene to keep it.");
        }
        else if (caches.Length > 1)
        {
            Debug.LogWarning($"[Poster] Found {caches.Length} PosterCache objects; using '{cache.name}'. Delete the others.");
        }

        // Compared as sets, since FindObjectsOfType doesn't promise an order.
        if (cache.posters == null || cache.posters.Length != posters.Length || posters.Except(cache.posters).Any())
        {
            Undo.RecordObject(cache, "Link PosterCache");
            cache.posters = posters;
            EditorUtility.SetDirty(cache);
            UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(cache);
        }
        foreach (Poster poster in posters)
        {
            if (poster.cache == cache) continue;
            Undo.RecordObject(poster, "Link PosterCache");
            poster.cache = cache;
            EditorUtility.SetDirty(poster);
            UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(poster);
        }
    }

    /// <summary>
    /// A U# script needs a program asset before it can go on a GameObject. Makes one next to
    /// PosterCache.cs if the project doesn't have it yet, the same way Create > U# Script does.
    /// </summary>
    private static bool EnsureCacheProgramAsset()
    {
        MonoScript script = AssetDatabase.FindAssets("PosterCache t:MonoScript")
            .Select(guid => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid)))
            .FirstOrDefault(s => s != null && s.GetClass() == typeof(PosterCache));
        if (script == null) return false;
        if (UdonSharp.UdonSharpProgramAsset.GetAllUdonSharpPrograms().Any(p => p.sourceCsScript == script)) return true;

        var programAsset = ScriptableObject.CreateInstance<UdonSharp.UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        string path = Path.ChangeExtension(AssetDatabase.GetAssetPath(script), ".asset");
        AssetDatabase.CreateAsset(programAsset, path);
        AssetDatabase.Refresh();
        UdonSharp.UdonSharpProgramAsset.CompileAllCsPrograms(forceCompile: true);
        Debug.Log($"[Poster] Created the UdonSharp program asset {path} for PosterCache.");
        return true;
    }

    private static Material GetSlotMaterial(Poster poster)
    {
        if (poster.targetRenderer == null) return null;
        Material[] mats = poster.targetRenderer.sharedMaterials;
        int i = poster.materialIndex;
        return i >= 0 && i < mats.Length ? mats[i] : null;
    }
}
