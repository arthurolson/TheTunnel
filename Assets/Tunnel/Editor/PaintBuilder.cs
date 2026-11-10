using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Makes painting on the tunnel: the marker (<see cref="TunnelMarker"/>) and palette (<see cref="TunnelPalette"/>)
/// prefabs, each a copy of the 2PLV molecule's toy without its molecule (so they are grabbed and shared the same way),
/// the palette's controls (copied from the TS Menu's colour chooser and rows), and in the tunnel segment prefab its
/// paint (<see cref="TunnelSegmentPaint"/>) and the TS Menu's Paint button (<see cref="TunnelPaintMenu"/>).
/// Runs from Tunnel > Paint > Build Paint.
/// </summary>
static class PaintBuilder
{
    const string k_TemplatePath = "Assets/Molecules/2PLV/Toy_2PLV_Networked.prefab";
    const string k_Folder = "Assets/Tunnel/Paint";
    const string k_RegistryPath = "Assets/Tunnel/TunnelPrefabs.asset";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_NetworkListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";

    const float k_MarkerLength = 0.15f, k_MarkerDiameter = 0.01f;
    const float k_CanvasScale = 0.0004f;                   // meters per canvas unit
    static readonly Vector2 k_CanvasSize = new(1100f, 760f); // 44 x 30 cm
    const float k_RowsX = 300f, k_RowWidth = 460f;

    [MenuItem("Tunnel/Paint/Build Paint")]
    public static string Build()
    {
        if (!AssetDatabase.IsValidFolder(k_Folder))
            AssetDatabase.CreateFolder("Assets/Tunnel", "Paint");
        var body = LitMaterial("MarkerBody", new Color(0.8f, 0.15f, 0.15f), 0.1f, 0.6f);
        var cap = LitMaterial("MarkerCap", new Color(0.12f, 0.12f, 0.13f), 0.3f, 0.5f);
        var board = LitMaterial("PaletteBoard", new Color(0.22f, 0.2f, 0.18f), 0f, 0.35f);
        var overlay = ShaderMaterial("PaintOverlay", "Tunnel/Paint Overlay");

        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
        var segment = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        string log;
        try
        {
            var control = segment.transform.Find("Control Panel");
            var marker = MakeMarker(body, cap);
            var palette = MakePalette(control, board);
            registry.marker = marker;
            registry.palette = palette;
            EditorUtility.SetDirty(registry);

            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_NetworkListPath);
            foreach (var prefab in new[] { marker, palette })
                if (!list.PrefabList.Any(p => p.Prefab == prefab))
                    list.Add(new NetworkPrefab { Prefab = prefab });
            EditorUtility.SetDirty(list);

            BuildSegment(segment, control, registry, overlay);
            bool saved = PrefabUtility.SaveAsPrefabAsset(segment, k_SegmentPath) != null;
            log = $"[Paint] built {AssetDatabase.GetAssetPath(marker)} and {AssetDatabase.GetAssetPath(palette)}; segment {(saved ? "saved" : "NOT saved")}";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(segment);
        }
        AssetDatabase.SaveAssets();
        Debug.Log(log);
        return log;
    }

    #region Materials

    static Material LitMaterial(string name, Color color, float metallic, float smoothness)
    {
        string path = $"{k_Folder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material ShaderMaterial(string name, string shader)
    {
        string path = $"{k_Folder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find(shader)) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }
        return material;
    }

    #endregion

    #region Prefabs

    // The molecule toy's workings (network object and transform, grab, physics sharing) without its molecule.
    static GameObject MakeToy(string name)
    {
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(k_TemplatePath);
        var toy = Object.Instantiate(template);
        toy.name = name;
        for (int i = toy.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(toy.transform.GetChild(i).gameObject);
        Object.DestroyImmediate(toy.GetComponent<MoleculeGravity>());
        if (toy.TryGetComponent(out CapsidSites sites)) // 2PLV's binding sites (Tunnel > Movie)
            Object.DestroyImmediate(sites);
        toy.transform.localScale = Vector3.one;

        // It stays where it's left and pushes nothing.
        var body = toy.GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        var grab = toy.GetComponent<XRGrabInteractable>();
        grab.throwOnDetach = false;
        var grabSo = new SerializedObject(grab);
        grabSo.FindProperty("m_Colliders").ClearArray(); // collected when it wakes
        grabSo.ApplyModifiedPropertiesWithoutUndo();
        if (toy.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = false;
        return toy;
    }

    static GameObject Save(GameObject toy)
    {
        var prefab = PrefabUtility.SaveAsPrefabAsset(toy, $"{k_Folder}/{toy.name}.prefab");
        Object.DestroyImmediate(toy);
        return prefab;
    }

    static GameObject MakeMarker(Material body, Material cap)
    {
        var marker = MakeToy("Paint_Marker_Networked");
        marker.transform.localScale = new Vector3(k_MarkerDiameter, k_MarkerDiameter, k_MarkerLength);
        var general = marker.GetComponent<XRGeneralGrabTransformer>();
        var generalSo = new SerializedObject(general);
        generalSo.FindProperty("m_AllowTwoHandedScaling").boolValue = true;
        generalSo.FindProperty("m_MinimumScaleRatio").floatValue = 0.05f;
        generalSo.FindProperty("m_MaximumScaleRatio").floatValue = 50f;
        generalSo.ApplyModifiedPropertiesWithoutUndo();

        // It follows the hand every frame, not in physics steps.
        marker.GetComponent<XRGrabInteractable>().movementType = XRBaseInteractable.MovementType.Instantaneous;

        var component = marker.AddComponent<TunnelMarker>();
        var so = new SerializedObject(component);
        so.FindProperty("m_Length").floatValue = k_MarkerLength;
        so.FindProperty("m_BodyMaterial").objectReferenceValue = body;
        so.FindProperty("m_CapMaterial").objectReferenceValue = cap;
        so.ApplyModifiedPropertiesWithoutUndo();
        var diameter = marker.AddComponent<MarkerDiameterTransformer>();
        var diameterSo = new SerializedObject(diameter);
        diameterSo.FindProperty("m_Length").floatValue = k_MarkerLength;
        diameterSo.ApplyModifiedPropertiesWithoutUndo();
        return Save(marker);
    }

    static GameObject MakePalette(Transform control, Material boardMaterial)
    {
        var palette = MakeToy("Paint_Palette_Networked");
        var generalSo = new SerializedObject(palette.GetComponent<XRGeneralGrabTransformer>());
        generalSo.FindProperty("m_AllowTwoHandedScaling").boolValue = false;
        generalSo.ApplyModifiedPropertiesWithoutUndo();
        var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");

        // The board, just behind the controls (its forward points away from whoever looks at it).
        var board = Box(palette.transform, "Board", new Vector3(0f, 0f, 0.007f), new Vector3(0.47f, 0.33f, 0.01f), cube, boardMaterial);

        // Held by its rim: thin grab zones round the edges, touching nothing.
        Rim(palette.transform, "Rim Top", new Vector3(0f, 0.17f, 0.006f), new Vector3(0.5f, 0.035f, 0.035f));
        Rim(palette.transform, "Rim Bottom", new Vector3(0f, -0.17f, 0.006f), new Vector3(0.5f, 0.035f, 0.035f));
        Rim(palette.transform, "Rim Left", new Vector3(-0.24f, 0f, 0.006f), new Vector3(0.035f, 0.37f, 0.035f));
        Rim(palette.transform, "Rim Right", new Vector3(0.24f, 0f, 0.006f), new Vector3(0.035f, 0.37f, 0.035f));

        // The marker's holder, along the right side.
        Box(palette.transform, "Holder", new Vector3(0.275f, -0.01f, 0.004f), new Vector3(0.028f, 0.2f, 0.012f), cube, boardMaterial);
        var dock = new GameObject("Marker Dock").transform;
        dock.SetParent(palette.transform, false);
        dock.localPosition = new Vector3(0.275f, -0.01f, -0.012f);
        dock.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.back); // its tip up
        // A grab zone round the holder: grabbed twice quickly there, the palette calls its marker back.
        Rim(palette.transform, "Holder Grab", new Vector3(0.275f, -0.01f, -0.004f), new Vector3(0.045f, 0.22f, 0.04f));
        var holderGrab = palette.transform.Find("Holder Grab").GetComponent<BoxCollider>();

        // The controls.
        var canvasObject = new GameObject("Palette Canvas", typeof(RectTransform));
        canvasObject.layer = control.gameObject.layer;
        var canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.SetParent(palette.transform, false);
        foreach (var type in new[] { typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster) })
        {
            var source = control.GetComponent(type);
            if (source != null && UnityEditorInternal.ComponentUtility.CopyComponent(source))
                UnityEditorInternal.ComponentUtility.PasteComponentAsNew(canvasObject);
        }
        canvasRect.sizeDelta = k_CanvasSize;
        canvasRect.localScale = Vector3.one * k_CanvasScale;
        canvasRect.localPosition = new Vector3(0f, 0f, -0.001f);
        canvasRect.localRotation = Quaternion.identity;

        var menuPanel = control.Find("Menu Panel").GetComponent<Image>();
        var background = UiObject("Background", canvasRect, Vector2.zero, k_CanvasSize).gameObject.AddComponent<Image>();
        background.sprite = menuPanel.sprite;
        background.type = menuPanel.type;
        background.color = menuPanel.color;

        var titleTemplate = control.Find("Container List/Title").gameObject;
        var title = Text(titleTemplate, canvasRect, "Title", "Palette", new Vector2(-270f, 335f), new Vector2(500f, 60f));
        title.alignment = TextAlignmentOptions.Left;

        // Colour chooser: the TS Menu's, editing the selected disk's colour.
        var chooser = Object.Instantiate(control.Find("Color Chooser").gameObject, canvasRect, false);
        chooser.name = "Color Chooser";
        var chooserRect = (RectTransform)chooser.transform;
        chooserRect.anchoredPosition = new Vector2(-270f, 30f);
        chooser.transform.Find("Title").GetComponent<TMP_Text>().text = "Selected disk's colour";
        Slider SliderIn(string name)
        {
            var slider = chooser.transform.Find($"{name}/MinMax Slider").GetComponent<Slider>();
            slider.onValueChanged = new Slider.SliderEvent();
            return slider;
        }
        var hue = SliderIn("Hue Slider");
        var saturation = SliderIn("Saturation Slider");
        var value = SliderIn("Value Slider");
        var opacity = SliderIn("Opacity Slider");
        var swatch = chooser.transform.Find("Swatch").GetComponent<Image>();

        // Five colour disks, under the chooser.
        var knob = hue.handleRect != null ? hue.handleRect.GetComponent<Image>().sprite : null;
        var disksLabel = Text(titleTemplate, canvasRect, "Disks Label", "Disks  <size=75%><color=#AAA>select to change; touch with the marker to use</color></size>",
            new Vector2(-270f, -262f), new Vector2(540f, 36f));
        disksLabel.fontSize = 24f;
        disksLabel.alignment = TextAlignmentOptions.Left;
        var disks = new Button[5];
        var rings = new Image[5];
        for (int i = 0; i < 5; i++)
        {
            var position = new Vector2(-490f + i * 110f, -325f);
            var ring = UiObject($"Disk Ring {i + 1}", canvasRect, position, new Vector2(118f, 118f)).gameObject.AddComponent<Image>();
            ring.sprite = knob;
            ring.color = Color.white;
            ring.raycastTarget = false;
            ring.enabled = false;
            var disk = UiObject($"Disk {i + 1}", canvasRect, position, new Vector2(96f, 96f)).gameObject;
            var image = disk.AddComponent<Image>();
            image.sprite = knob;
            image.color = TunnelPalette.k_DefaultDisks[i];
            var button = disk.AddComponent<Button>();
            button.targetGraphic = image;
            disks[i] = button;
            rings[i] = ring;
        }

        // The kinds of mark, and the pencil's and clearing's buttons.
        var rowTemplate = control.Find("Container List/Box Row").gameObject;
        Button Row(string name, float y, string text)
        {
            var row = Object.Instantiate(rowTemplate, canvasRect, false);
            row.name = name;
            var rect = (RectTransform)row.transform;
            rect.anchoredPosition = new Vector2(k_RowsX, y);
            rect.sizeDelta = new Vector2(k_RowWidth, 56f);
            var button = row.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            var label = row.GetComponentInChildren<TMP_Text>(true);
            label.text = text;
            label.richText = true;
            return button;
        }
        var paint = Row("Paint Row", 300f, "■  Paint");
        var pencil = Row("Pencil Row", 235f, "□  Pencil");
        var airbrush = Row("Airbrush Row", 170f, "□  Airbrush");
        var eraser = Row("Eraser Row", 105f, "□  Eraser");
        var curve = Row("Curve Row", 15f, "□  Curve");
        var endLine = Row("End Line Row", -50f, "End line");
        var clear = Row("Clear Row", -120f, "Clear all marks");
        var real = Row("Real Marker Row", -185f, "□  Real marker");
        var status = Text(titleTemplate, canvasRect, "Status", "", new Vector2(k_RowsX, -295f), new Vector2(k_RowWidth, 140f));
        status.fontSize = 24f;
        status.alignment = TextAlignmentOptions.TopLeft;
        status.textWrappingMode = TextWrappingModes.Normal;

        var component = palette.AddComponent<TunnelPalette>();
        var so = new SerializedObject(component);
        so.FindProperty("m_Hue").objectReferenceValue = hue;
        so.FindProperty("m_Saturation").objectReferenceValue = saturation;
        so.FindProperty("m_Value").objectReferenceValue = value;
        so.FindProperty("m_Opacity").objectReferenceValue = opacity;
        so.FindProperty("m_Swatch").objectReferenceValue = swatch;
        var disksProperty = so.FindProperty("m_Disks");
        var ringsProperty = so.FindProperty("m_DiskRings");
        disksProperty.arraySize = 5;
        ringsProperty.arraySize = 5;
        for (int i = 0; i < 5; i++)
        {
            disksProperty.GetArrayElementAtIndex(i).objectReferenceValue = disks[i];
            ringsProperty.GetArrayElementAtIndex(i).objectReferenceValue = rings[i];
        }
        so.FindProperty("m_PaintRow").objectReferenceValue = paint;
        so.FindProperty("m_PencilRow").objectReferenceValue = pencil;
        so.FindProperty("m_AirbrushRow").objectReferenceValue = airbrush;
        so.FindProperty("m_EraserRow").objectReferenceValue = eraser;
        so.FindProperty("m_CurveRow").objectReferenceValue = curve;
        so.FindProperty("m_EndLineRow").objectReferenceValue = endLine;
        so.FindProperty("m_ClearRow").objectReferenceValue = clear;
        so.FindProperty("m_RealRow").objectReferenceValue = real;
        so.FindProperty("m_BoardOverlay").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>($"{k_Folder}/PaintOverlay.mat");
        so.FindProperty("m_BoardBrush").objectReferenceValue = Shader.Find("Tunnel/Paint Brush");
        so.FindProperty("m_Status").objectReferenceValue = status;
        so.FindProperty("m_Dock").objectReferenceValue = dock;
        so.FindProperty("m_HolderGrab").objectReferenceValue = holderGrab;
        so.ApplyModifiedPropertiesWithoutUndo();
        return Save(palette);
    }

    static Transform Box(Transform parent, string name, Vector3 position, Vector3 size, Mesh mesh, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localScale = size;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        return go.transform;
    }

    static void Rim(Transform parent, string name, Vector3 position, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        var box = go.AddComponent<BoxCollider>();
        box.size = size;
        box.excludeLayers = ~0; // found by the hands and lasers, pushes nothing
    }

    static RectTransform UiObject(string name, RectTransform parent, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return rect;
    }

    static TMP_Text Text(GameObject template, RectTransform parent, string name, string text, Vector2 position, Vector2 size)
    {
        var go = Object.Instantiate(template, parent, false);
        go.name = name;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        var label = go.GetComponent<TMP_Text>();
        label.text = text;
        label.richText = true;
        label.enableAutoSizing = false;
        return label;
    }

    #endregion

    #region Tunnel segment

    static void BuildSegment(GameObject segment, Transform control, TunnelPrefabRegistry registry, Material overlay)
    {
        var geometry = segment.GetComponentInChildren<TunnelSegmentGeometry>(true);

        var paint = segment.GetComponent<TunnelSegmentPaint>() ?? segment.AddComponent<TunnelSegmentPaint>();
        var paintSo = new SerializedObject(paint);
        paintSo.FindProperty("m_Geometry").objectReferenceValue = geometry;
        paintSo.FindProperty("m_OverlayMaterial").objectReferenceValue = overlay;
        paintSo.FindProperty("m_BrushShader").objectReferenceValue = Shader.Find("Tunnel/Paint Brush");
        paintSo.ApplyModifiedPropertiesWithoutUndo();

        // The Paint button, under Containers.
        var menuPanel = control.Find("Menu Panel");
        var paintButton = menuPanel.Find("Paint Button");
        if (paintButton == null)
        {
            paintButton = Object.Instantiate(menuPanel.Find("Containers Button").gameObject, menuPanel, false).transform;
            paintButton.name = "Paint Button";
        }
        var button = paintButton.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        paintButton.Find("Button Front/Text (TMP)").GetComponent<TMP_Text>().text = "Paint";
        TunnelMenuLayout.Arrange(menuPanel);

        var menu = segment.GetComponent<TunnelPaintMenu>() ?? segment.AddComponent<TunnelPaintMenu>();
        var menuSo = new SerializedObject(menu);
        menuSo.FindProperty("m_Prefabs").objectReferenceValue = registry;
        menuSo.FindProperty("m_Geometry").objectReferenceValue = geometry;
        menuSo.FindProperty("m_PaintButton").objectReferenceValue = button;
        menuSo.FindProperty("m_Beside").objectReferenceValue = (RectTransform)menuPanel;
        menuSo.ApplyModifiedPropertiesWithoutUndo();
    }

    #endregion
}
