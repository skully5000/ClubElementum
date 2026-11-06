using TMPro;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.Udon;

/// <summary>
/// Tools > Random Player Teleport > Build Canvas swaps a world-object button for a canvas. The
/// canvas goes where the object is, showing the winners above a button that greys out during the
/// cooldown. The script stays on the old object, so its targets and pick zones are untouched, but
/// that object's renderer and collider are switched off so it can't be seen or pressed.
/// Select the object first if the scene has more than one.
/// </summary>
public static class RandomPlayerTeleportBuilder
{
    private const float CanvasScale = 0.001f;
    private static readonly Vector2 CanvasSize = new Vector2(600f, 500f);
    private static readonly Color PanelColor = new Color(0.08f, 0.08f, 0.1f, 0.95f);
    private static readonly Color ButtonColor = new Color(0.55f, 0.2f, 0.75f, 1f);
    private static readonly Color ButtonGreyedOut = new Color(0.3f, 0.3f, 0.3f, 1f);

    [MenuItem("Tools/Random Player Teleport/Build Canvas")]
    public static void BuildCanvas()
    {
        RandomPlayerTeleport teleport = FindTarget();
        if (teleport == null) return;

        if (teleport.button != null)
        {
            Debug.LogError("[RandomPlayerTeleport] " + teleport.name + " already has a canvas button. Clear its Button field to build a fresh one.", teleport);
            return;
        }

        if (UdonSharpProgramAsset.IsAnyProgramAssetSourceDirty()) UdonSharpCompilerV1.CompileSync();
        if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
        {
            Debug.LogError("[RandomPlayerTeleport] UdonSharp hasn't compiled these scripts. Fix any errors in the Console, then run this again.");
            return;
        }

        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(teleport);

        RectTransform canvas = BuildCanvasObject(teleport.transform);
        Undo.RegisterCreatedObjectUndo(canvas.gameObject, "Build Teleport Canvas");

        TextMeshProUGUI title = BuildText(canvas, "Title", "Winners", 56f);
        Pin(title.rectTransform, 1f, -20f, 80f);

        TextMeshProUGUI winners = BuildText(canvas, "Winners", "Nobody yet", 48f);
        winners.enableWordWrapping = false;
        winners.overflowMode = TextOverflowModes.Ellipsis;
        Pin(winners.rectTransform, 1f, -110f, 180f);

        string prompt = backing.interactText == null ? "" : backing.interactText.Trim();
        Button button = BuildButton(canvas, prompt.Length > 0 ? prompt : "Pick Players");
        Pin((RectTransform)button.transform, 0f, 30f, 150f);
        UnityEventTools.AddStringPersistentListener(button.onClick, new UnityAction<string>(backing.SendCustomEvent), nameof(RandomPlayerTeleport.PickPlayers));

        Undo.RecordObject(teleport, "Build Teleport Canvas");
        Undo.RecordObject(backing, "Build Teleport Canvas");
        teleport.button = button;
        teleport.winnersText = winners;
        EditorUtility.SetDirty(teleport);
        UdonSharpEditorUtility.CopyProxyToUdon(teleport);

        foreach (Renderer renderer in teleport.GetComponents<Renderer>())
        {
            Undo.RecordObject(renderer, "Build Teleport Canvas");
            renderer.enabled = false;
        }
        foreach (Collider collider in teleport.GetComponents<Collider>())
        {
            Undo.RecordObject(collider, "Build Teleport Canvas");
            collider.enabled = false;
        }

        Selection.activeGameObject = canvas.gameObject;
        Debug.Log("[RandomPlayerTeleport] Built. Move and turn the canvas into place, then save the scene.", canvas);
    }

    private static RandomPlayerTeleport FindTarget()
    {
        if (Selection.activeGameObject != null)
        {
            var selected = Selection.activeGameObject.GetComponent<RandomPlayerTeleport>();
            if (selected != null) return selected;
        }

        RandomPlayerTeleport[] all = Object.FindObjectsOfType<RandomPlayerTeleport>(true);
        if (all.Length == 1) return all[0];

        Debug.LogError(all.Length == 0
            ? "[RandomPlayerTeleport] This scene has none. Add the script to an object first."
            : "[RandomPlayerTeleport] This scene has " + all.Length + ". Select the one to build for.");
        return null;
    }

    private static RectTransform BuildCanvasObject(Transform source)
    {
        var go = new GameObject("TeleportCanvas");
        go.transform.SetParent(source.parent, false);

        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = CanvasSize;
        rect.position = source.position;
        rect.rotation = source.rotation;

        // A sibling of the old object, so its small scale doesn't apply, but the parent's still does.
        Vector3 parentScale = source.parent != null ? source.parent.lossyScale : Vector3.one;
        rect.localScale = new Vector3(CanvasScale / parentScale.x, CanvasScale / parentScale.y, CanvasScale / parentScale.z);

        go.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 4f;
        go.AddComponent<GraphicRaycaster>();
        go.AddComponent<VRCUiShape>();

        var collider = go.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = new Vector3(CanvasSize.x, CanvasSize.y, 10f);

        var panel = go.AddComponent<Image>();
        panel.color = PanelColor;
        panel.raycastTarget = false;
        return rect;
    }

    private static TextMeshProUGUI BuildText(Transform parent, string name, string text, float fontSize)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;
        // Display names can contain < and >, which shouldn't be read as formatting tags.
        label.richText = false;
        return label;
    }

    private static Button BuildButton(Transform parent, string text)
    {
        var go = new GameObject("Button");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();

        var image = go.AddComponent<Image>();

        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.navigation = new Navigation { mode = Navigation.Mode.None };

        // The image stays white so these are the colours that show, greyed out one included.
        ColorBlock colors = button.colors;
        colors.normalColor = ButtonColor;
        colors.highlightedColor = Color.Lerp(ButtonColor, Color.white, 0.2f);
        colors.pressedColor = Color.Lerp(ButtonColor, Color.black, 0.2f);
        colors.selectedColor = ButtonColor;
        colors.disabledColor = ButtonGreyedOut;
        button.colors = colors;

        TextMeshProUGUI label = BuildText(go.transform, "Label", text, 48f);
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = Vector2.zero;
        label.rectTransform.offsetMax = Vector2.zero;
        return button;
    }

    /// <summary>Pins across the width, offset from the top (anchorY 1) or bottom (anchorY 0) edge.</summary>
    private static void Pin(RectTransform rect, float anchorY, float offsetY, float height)
    {
        rect.anchorMin = new Vector2(0f, anchorY);
        rect.anchorMax = new Vector2(1f, anchorY);
        rect.pivot = new Vector2(0.5f, anchorY);
        rect.sizeDelta = new Vector2(-80f, height);
        rect.anchoredPosition = new Vector2(0f, offsetY);
    }
}
