using System.IO;
using System.Linq;
using TMPro;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Builds the whole dance tracker into the open scene from Tools > Club Dance Tracker > Build In
/// Scene, all wired together. The names in the hierarchy say what can be switched on and off: the
/// tracker and picker stay on for everyone, and whatever signs staff in enables the group holding
/// the staff button and the boards, which can be moved anywhere inside it.
/// </summary>
public static class ClubDanceTrackerBuilder
{
    private const string RootName = "ClubDanceTracker - ALWAYS ON";
    private const string PickerName = "Picker - ALWAYS ON";
    private const string StaffToolsName = "StaffTools - TOGGLE THIS";
    private const string NumberTemplateName = "NumberTemplate - LEAVE OFF";
    private const string BeamTemplateName = "BeamTemplate - LEAVE OFF";

    private const string ListUrlBase = "https://skully5000.github.io/ClubElementum/";
    private const string BoosterList = "BoosterList";

    private const int IgnoreRaycastLayer = 2;

    // The same click distance as the numbers this replaces, so staff walk up to the person.
    private const float NumberProximity = 0.1f;

    private const float CanvasScale = 0.001f;
    private static readonly Color PanelColor = new Color(0.08f, 0.08f, 0.1f, 0.95f);
    private static readonly Color ButtonColor = new Color(0.55f, 0.2f, 0.75f, 1f);
    private static readonly Color TickboxColor = new Color(0.25f, 0.25f, 0.3f, 1f);
    private static readonly Color BoosterColor = new Color(1f, 0.55f, 0.85f, 1f);

    // Where each column of the board sits, as a fraction of the row's width.
    private const float DancesFrom = 0.36f;
    private const float AddDanceFrom = 0.46f;
    private const float StageFrom = 0.53f;
    private const float AddStageFrom = 0.63f;
    private const float BoosterFrom = 0.70f;
    private const float FreshFrom = 0.85f;
    private const float RowButtonSize = 44f;

    [MenuItem("Tools/Club Dance Tracker/Build In Scene")]
    public static void BuildInScene()
    {
        if (Object.FindObjectOfType<ClubDanceTracker>(true) != null)
        {
            Debug.LogError("[ClubDanceTracker] This scene already has one. Delete it first to build a fresh one.");
            return;
        }

        string folder = FindFolder();
        if (folder == null)
        {
            Debug.LogError("[ClubDanceTracker] Couldn't find Art/NumberAtlas.png.");
            return;
        }

        EnsureProgramAssets();
        if (UdonSharpProgramAsset.IsAnyProgramAssetSourceDirty() || UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
        {
            Debug.LogError("[ClubDanceTracker] UdonSharp hasn't compiled these scripts. Fix any errors in the Console, then run this again.");
            return;
        }

        Material[] numbers = EnsureNumberMaterials(folder, out Material noDance);
        Material freshMaterial = EnsureFreshMaterial(folder);
        Material beamMaterial = EnsureBeamMaterial(folder);

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "Build Club Dance Tracker");
        var tracker = AddBehaviour<ClubDanceTracker>(root, Networking.SyncType.Manual);

        tracker.numberTemplate = BuildNumberTemplate(root.transform, numbers[0], freshMaterial);
        tracker.numberMaterials = numbers;
        tracker.noDanceMaterial = noDance;

        var picker = BuildPicker(root.transform, tracker, beamMaterial);
        var staffTools = new GameObject(StaffToolsName);
        staffTools.transform.SetParent(root.transform, false);

        BuildDancerBoard(staffTools.transform, tracker, picker);
        BuildSecurityBoard(staffTools.transform, tracker, picker);
        tracker.toggleLabel = BuildStaffButton(staffTools.transform, tracker);

        // Off for everyone until they sign in.
        staffTools.SetActive(false);

        Save(picker);
        Save(tracker);

        Selection.activeGameObject = staffTools;
        Debug.Log("[ClubDanceTracker] Built. Next: 1) Move the boards and the button into place inside StaffTools. "
            + "2) Make whatever signs staff in enable StaffTools. It starts off, and enabling it also presses Show Numbers (untick Show Numbers On Enable on StaffButton to stop that). "
            + "3) Save the scene. Leave everything marked ALWAYS ON switched on, or the counts stop syncing.");
    }

    private static GameObject BuildNumberTemplate(Transform parent, Material startMaterial, Material freshMaterial)
    {
        var template = new GameObject(NumberTemplateName);
        template.transform.SetParent(parent, false);

        // Dances, stage, then the icon: ClubDanceTracker reads them in this order.
        BuildNumber(template.transform, "Dances", -0.14f, 0.24f, false, "Add Dance", startMaterial);
        BuildNumber(template.transform, "StageDances", 0.12f, 0.18f, true, "Add Stage Dance", startMaterial);
        BuildFreshIcon(template.transform, freshMaterial);

        template.SetActive(false);
        return template;
    }

    private static void BuildNumber(Transform parent, string name, float x, float size, bool isStage, string prompt, Material material)
    {
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        quad.layer = IgnoreRaycastLayer;
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = new Vector3(x, 0f, 0f);
        quad.transform.localScale = Vector3.one * size;
        Object.DestroyImmediate(quad.GetComponent<Collider>());

        var collider = quad.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = new Vector3(1f, 1f, 0.2f);

        SetUpRenderer(quad, material);

        var number = AddBehaviour<ClubDanceNumber>(quad, Networking.SyncType.None);
        number.isStage = isStage;
        Save(number);

        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(number);
        backing.interactText = prompt;
        backing.proximity = NumberProximity;
    }

    // Sits above the numbers. Nothing to click, so no collider.
    private static void BuildFreshIcon(Transform parent, Material material)
    {
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "FreshMeat";
        quad.layer = IgnoreRaycastLayer;
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = new Vector3(0f, 0.25f, 0f);
        quad.transform.localScale = new Vector3(0.36f, 0.18f, 1f);
        Object.DestroyImmediate(quad.GetComponent<Collider>());

        SetUpRenderer(quad, material);
    }

    private static void SetUpRenderer(GameObject quad, Material material)
    {
        var renderer = quad.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    private static ClubDancePicker BuildPicker(Transform parent, ClubDanceTracker tracker, Material beamMaterial)
    {
        var go = new GameObject(PickerName);
        go.transform.SetParent(parent, false);
        // Network events are refused by a behaviour set to None, even with nothing to sync.
        var picker = AddBehaviour<ClubDancePicker>(go, Networking.SyncType.Manual);

        var beam = new GameObject(BeamTemplateName);
        beam.transform.SetParent(go.transform, false);
        var line = beam.AddComponent<LineRenderer>();
        line.sharedMaterial = beamMaterial;
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.widthMultiplier = 0.12f;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.lightProbeUsage = LightProbeUsage.Off;
        line.reflectionProbeUsage = ReflectionProbeUsage.Off;
        beam.SetActive(false);

        picker.tracker = tracker;
        picker.beamTemplate = line;
        picker.beamColor = new Color(1f, 0.92f, 0.65f, 1f);
        picker.boosterListUrl = new VRCUrl(ListUrlBase + BoosterList);
        return picker;
    }

    private static void BuildDancerBoard(Transform parent, ClubDanceTracker tracker, ClubDancePicker picker)
    {
        RectTransform canvas = BuildBoard(parent, "DancerBoard", "Dancers", 1400f, new Vector3(0f, 1.5f, 0f), 260f, true, tracker, picker);

        picker.resultText = BuildText(canvas, "Result", "", 42f, TextAlignmentOptions.Center);
        PinBottom(picker.resultText.rectTransform, 160f, 80f);

        Button button = BuildButton(canvas, "PickButton", "Pick Someone To Dance On", out _);
        PinBottom((RectTransform)button.transform, 30f, 120f);
        AddEvent(button, picker, nameof(ClubDancePicker._Pick));
    }

    // For marking new arrivals: it shows everything but can only tick Fresh Meat.
    private static void BuildSecurityBoard(Transform parent, ClubDanceTracker tracker, ClubDancePicker picker)
    {
        BuildBoard(parent, "SecurityBoard", "Security", 1120f, new Vector3(-1.4f, 1.36f, 0f), 40f, false, tracker, picker);
    }

    private static RectTransform BuildBoard(Transform parent, string name, string title, float height, Vector3 position, float rowsFromBottom, bool canEditCounts, ClubDanceTracker tracker, ClubDancePicker picker)
    {
        RectTransform canvas = BuildCanvas(parent, name, new Vector2(1200f, height), position);

        var titleText = BuildText(canvas, "Title", title, 64f, TextAlignmentOptions.Center);
        PinTop(titleText.rectTransform, 20f, 90f);

        RectTransform header = BuildHeader(canvas);
        PinTop(header, 120f, 60f);

        var board = AddBehaviour<ClubDanceBoard>(canvas.gameObject, Networking.SyncType.None);
        board.tracker = tracker;
        board.picker = picker;

        // Rows go in here, stacked and scrolling.
        var scroll = DefaultControls.CreateScrollView(StandardResources());
        scroll.name = "Rows";
        var scrollRect = scroll.GetComponent<ScrollRect>();
        scroll.transform.SetParent(canvas, false);
        Object.DestroyImmediate(scrollRect.horizontalScrollbar.gameObject);
        scrollRect.horizontalScrollbar = null;
        scrollRect.horizontal = false;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 30f;
        scrollRect.verticalScrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
        scroll.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);
        Stretch((RectTransform)scroll.transform, 40f, 190f, 40f, rowsFromBottom);

        RectTransform content = scrollRect.content;
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        RectTransform row = BuildRow(content, canEditCounts);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 52f;
        row.gameObject.SetActive(false);
        board.rowTemplate = row.gameObject;

        Save(board);
        return canvas;
    }

    private static TextMeshProUGUI BuildStaffButton(Transform parent, ClubDanceTracker tracker)
    {
        RectTransform canvas = BuildCanvas(parent, "StaffButton", new Vector2(600f, 200f), new Vector3(1.2f, 1.2f, 0f));
        Button button = BuildButton(canvas, "Button", "Show Numbers", out TextMeshProUGUI label);
        Stretch((RectTransform)button.transform, 20f, 20f, 20f, 20f);
        AddEvent(button, tracker, nameof(ClubDanceTracker._ToggleNumbers));

        var autoShow = AddBehaviour<ClubDanceStaffButton>(canvas.gameObject, Networking.SyncType.None);
        autoShow.tracker = tracker;
        Save(autoShow);

        return label;
    }

    private static RectTransform BuildCanvas(Transform parent, string name, Vector2 size, Vector3 position)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = size;
        rect.localPosition = position;
        rect.localScale = Vector3.one * CanvasScale;

        go.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 4f;
        go.AddComponent<GraphicRaycaster>();
        go.AddComponent<VRCUiShape>();

        var collider = go.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.size = new Vector3(size.x, size.y, 10f);

        var panel = go.AddComponent<Image>();
        panel.color = PanelColor;
        panel.raycastTarget = false;
        return rect;
    }

    private static RectTransform BuildHeader(Transform parent)
    {
        var go = new GameObject("Header");
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();

        BuildColumn(rect, "Name", "Name", 40f, FontStyles.Bold, TextAlignmentOptions.Left, 0f, DancesFrom);
        BuildColumn(rect, "Dances", "Dances", 40f, FontStyles.Bold, TextAlignmentOptions.Center, DancesFrom, StageFrom);
        BuildColumn(rect, "Stage", "Stage", 40f, FontStyles.Bold, TextAlignmentOptions.Center, StageFrom, BoosterFrom);
        BuildColumn(rect, "Booster", "Booster", 34f, FontStyles.Bold, TextAlignmentOptions.Center, BoosterFrom, FreshFrom);
        TextMeshProUGUI fresh = BuildColumn(rect, "Fresh", "Fresh Meat", 28f, FontStyles.Bold, TextAlignmentOptions.Center, FreshFrom, 1f);
        fresh.enableWordWrapping = true;
        fresh.overflowMode = TextOverflowModes.Overflow;
        return rect;
    }

    /// <summary>One row. ClubDanceBoard and ClubDanceRow find its columns by these names.</summary>
    private static RectTransform BuildRow(Transform parent, bool canEditCounts)
    {
        var go = new GameObject("RowTemplate");
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        var row = AddBehaviour<ClubDanceRow>(go, Networking.SyncType.None);

        BuildColumn(rect, "Name", "Name", 34f, FontStyles.Normal, TextAlignmentOptions.Left, 0f, DancesFrom);
        BuildColumn(rect, "Dances", "0", 34f, FontStyles.Normal, TextAlignmentOptions.Center, DancesFrom, AddDanceFrom);
        if (canEditCounts) BuildRowButton(rect, "AddDance", "+", ButtonColor, AddDanceFrom, StageFrom, row, nameof(ClubDanceRow._AddDance));
        BuildColumn(rect, "Stage", "0", 34f, FontStyles.Normal, TextAlignmentOptions.Center, StageFrom, AddStageFrom);
        if (canEditCounts) BuildRowButton(rect, "AddStage", "+", ButtonColor, AddStageFrom, BoosterFrom, row, nameof(ClubDanceRow._AddStage));
        TextMeshProUGUI booster = BuildColumn(rect, "Booster", "", 34f, FontStyles.Bold, TextAlignmentOptions.Center, BoosterFrom, FreshFrom);
        booster.color = BoosterColor;
        BuildRowButton(rect, "Fresh", "", TickboxColor, FreshFrom, 1f, row, nameof(ClubDanceRow._ToggleFresh));

        Save(row);
        return rect;
    }

    private static void BuildRowButton(RectTransform row, string name, string text, Color color, float from, float to, ClubDanceRow target, string eventName)
    {
        Button button = BuildButton(row, name, text, out TextMeshProUGUI label);
        label.fontSize = 34f;
        button.targetGraphic.color = color;

        var rect = (RectTransform)button.transform;
        rect.anchorMin = rect.anchorMax = new Vector2((from + to) / 2f, 0.5f);
        rect.sizeDelta = Vector2.one * RowButtonSize;

        AddEvent(button, target, eventName);
    }

    private static TextMeshProUGUI BuildColumn(RectTransform row, string name, string text, float fontSize, FontStyles style, TextAlignmentOptions alignment, float from, float to)
    {
        TextMeshProUGUI label = BuildText(row, name, text, fontSize, alignment);
        label.fontStyle = style;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;

        RectTransform rect = label.rectTransform;
        rect.anchorMin = new Vector2(from, 0f);
        rect.anchorMax = new Vector2(to, 1f);
        rect.offsetMin = new Vector2(16f, 0f);
        rect.offsetMax = new Vector2(-16f, 0f);
        return label;
    }

    private static TextMeshProUGUI BuildText(Transform parent, string name, string text, float fontSize, TextAlignmentOptions alignment)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.color = Color.white;
        label.raycastTarget = false;
        // Display names can contain < and >, which shouldn't be read as formatting tags.
        label.richText = false;
        return label;
    }

    private static Button BuildButton(Transform parent, string name, string text, out TextMeshProUGUI label)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();

        var image = go.AddComponent<Image>();
        image.color = ButtonColor;

        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.navigation = new Navigation { mode = Navigation.Mode.None };

        label = BuildText(go.transform, "Label", text, 48f, TextAlignmentOptions.Center);
        Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
        return button;
    }

    private static void AddEvent(Button button, UdonSharpBehaviour target, string eventName)
    {
        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(target);
        UnityEventTools.AddStringPersistentListener(button.onClick, new UnityAction<string>(backing.SendCustomEvent), eventName);
    }

    private static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    private static void PinTop(RectTransform rect, float fromTop, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(40f, -fromTop - height);
        rect.offsetMax = new Vector2(-40f, -fromTop);
    }

    private static void PinBottom(RectTransform rect, float fromBottom, float height)
    {
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.offsetMin = new Vector2(40f, fromBottom);
        rect.offsetMax = new Vector2(-40f, fromBottom + height);
    }

    private static DefaultControls.Resources StandardResources()
    {
        var resources = new DefaultControls.Resources();
        resources.standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        resources.background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
        resources.mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd");
        return resources;
    }

    private static T AddBehaviour<T>(GameObject go, Networking.SyncType syncType) where T : UdonSharpBehaviour
    {
        var behaviour = UdonSharpUndo.AddComponent<T>(go);
        UdonSharpEditorUtility.GetBackingUdonBehaviour(behaviour).SyncMethod = syncType;
        return behaviour;
    }

    private static void Save(UdonSharpBehaviour behaviour)
    {
        EditorUtility.SetDirty(behaviour);
        UdonSharpEditorUtility.CopyProxyToUdon(behaviour);
    }

    private static string FindFolder()
    {
        string atlas = AssetDatabase.FindAssets("NumberAtlas t:Texture2D")
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(path => path.Replace('\\', '/').EndsWith("/Art/NumberAtlas.png"));
        return atlas == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(atlas)).Replace('\\', '/');
    }

    /// <summary>
    /// A U# script needs a program asset before it can go on a GameObject. Makes any that are
    /// missing next to their scripts, the same way Create > U# Script does.
    /// </summary>
    private static void EnsureProgramAssets()
    {
        bool created = false;
        created |= EnsureProgramAsset(typeof(ClubDanceTracker));
        created |= EnsureProgramAsset(typeof(ClubDanceNumber));
        created |= EnsureProgramAsset(typeof(ClubDanceBoard));
        created |= EnsureProgramAsset(typeof(ClubDancePicker));
        created |= EnsureProgramAsset(typeof(ClubDanceRow));
        created |= EnsureProgramAsset(typeof(ClubDanceStaffButton));

        if (created) AssetDatabase.Refresh();

        // The sync version, because the build carries straight on and needs the result.
        if (created || UdonSharpProgramAsset.IsAnyProgramAssetSourceDirty()) UdonSharpCompilerV1.CompileSync();
    }

    private static bool EnsureProgramAsset(System.Type type)
    {
        MonoScript script = AssetDatabase.FindAssets(type.Name + " t:MonoScript")
            .Select(guid => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid)))
            .FirstOrDefault(s => s != null && s.GetClass() == type);
        if (script == null || UdonSharpProgramAsset.GetAllUdonSharpPrograms().Any(p => p.sourceCsScript == script)) return false;

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        // Already written in the current format, so there is nothing for an upgrade pass to do.
        programAsset.ScriptVersion = UdonSharpProgramVersion.CurrentVersion;
        AssetDatabase.CreateAsset(programAsset, Path.ChangeExtension(AssetDatabase.GetAssetPath(script), ".asset"));
        return true;
    }

    /// <summary>One material per cell of the 4x4 atlas: 0 to 9 reading across and down, then ND.</summary>
    private static Material[] EnsureNumberMaterials(string folder, out Material noDance)
    {
        string atlasPath = folder + "/Art/NumberAtlas.png";
        var importer = (TextureImporter)AssetImporter.GetAtPath(atlasPath);
        if (!importer.alphaIsTransparency || importer.wrapMode != TextureWrapMode.Clamp)
        {
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }
        var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);

        var numbers = new Material[10];
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = EnsureCellMaterial(folder + "/Art/Number " + i + ".mat", atlas, i);
        }
        noDance = EnsureCellMaterial(folder + "/Art/Number ND.mat", atlas, 10);
        return numbers;
    }

    private static Material EnsureCellMaterial(string path, Texture2D atlas, int cell)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;

        material = new Material(Shader.Find("Unlit/Transparent"));
        material.mainTexture = atlas;
        material.mainTextureScale = new Vector2(0.25f, 0.25f);
        material.mainTextureOffset = new Vector2((cell % 4) * 0.25f, 0.75f - (cell / 4) * 0.25f);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    private static Material EnsureFreshMaterial(string folder)
    {
        string path = folder + "/Art/FreshMeat.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;

        string texturePath = folder + "/Art/FreshMeat.png";
        var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
        importer.alphaIsTransparency = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.SaveAndReimport();

        material = new Material(Shader.Find("Unlit/Transparent"));
        material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    private static Material EnsureBeamMaterial(string folder)
    {
        string path = folder + "/Art/Beam.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;

        string glowPath = folder + "/Art/BeamGlow.png";
        var importer = (TextureImporter)AssetImporter.GetAtPath(glowPath);
        importer.alphaIsTransparency = true;
        importer.wrapMode = TextureWrapMode.Clamp;
        // A long thin beam would otherwise drop to a low mip and lose its soft edge.
        importer.mipmapEnabled = false;
        importer.SaveAndReimport();

        material = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
        material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(glowPath);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }
}
