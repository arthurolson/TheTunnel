using System.Linq;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Makes the TS Menu's Containers (see <see cref="TunnelContainer"/>): a box and a sphere prefab, each a copy of the
/// 2PLV molecule's toy without its molecule (so they are grabbed, scaled and shared the same way), and the menu's
/// Containers button and list in the tunnel segment prefab. Runs from Tunnel > Containers > Build Containers.
/// </summary>
static class ContainerBuilder
{
    const string k_TemplatePath = "Assets/Molecules/2PLV/Toy_2PLV_Networked.prefab";
    const string k_Folder = "Assets/Tunnel/Containers";
    const string k_EdgeMaterialPath = "Assets/Tunnel/Materials/ContainerEdge.mat";
    const string k_GlassMaterialPath = "Assets/Tunnel/Materials/ContainerMRGlass.mat";
    const string k_SpherePath = "Assets/Tunnel/Containers/Container_Sphere_Networked.prefab";
    const string k_WallMaterialPath = "Assets/Tunnel/Materials/ContainerMRWall.mat";
    const string k_WindowMaterialPath = "Assets/Tunnel/Materials/ContainerMRWindow.mat";
    // The tunnel shell's materials, which open where a mixed reality box's window is.
    static readonly string[] k_ShellMaterialPaths = { "Assets/Tunnel/Materials/TG_Shell_Opaque.mat", "Assets/Tunnel/Materials/TG_Shell_Transparent.mat" };
    const string k_RegistryPath = "Assets/Tunnel/TunnelPrefabs.asset";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_NetworkListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";

    // A new container is 30 cm across.
    const float k_Size = 0.3f;

    [MenuItem("Tunnel/Containers/Build Containers")]
    public static string Build()
    {
        if (!AssetDatabase.IsValidFolder(k_Folder))
            AssetDatabase.CreateFolder("Assets/Tunnel", "Containers");
        var material = EdgeMaterial();
        var wall = WallMaterial();
        var window = WindowMaterial();
        var glass = GlassMaterial();
        foreach (var path in k_ShellMaterialPaths)
        {
            var shell = AssetDatabase.LoadAssetAtPath<Material>(path);
            shell.SetFloat("_WindowStencilComp", (float)UnityEngine.Rendering.CompareFunction.NotEqual);
            EditorUtility.SetDirty(shell);
        }
        var box = MakePrefab(material, wall, window, glass);

        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
        registry.containerBox = box;
        EditorUtility.SetDirty(registry);

        // Boxes only: the sphere container is gone from the session's prefabs and the project.
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_NetworkListPath);
        var sphere = AssetDatabase.LoadAssetAtPath<GameObject>(k_SpherePath);
        foreach (var entry in list.PrefabList.Where(p => p.Prefab == null || (sphere != null && p.Prefab == sphere)).ToList())
            list.Remove(entry);
        if (!list.PrefabList.Any(p => p.Prefab == box))
            list.Add(new NetworkPrefab { Prefab = box });
        EditorUtility.SetDirty(list);
        if (sphere != null)
            AssetDatabase.DeleteAsset(k_SpherePath);

        string menu = BuildMenu(registry);
        AssetDatabase.SaveAssets();
        string log = $"[Containers] built {AssetDatabase.GetAssetPath(box)}{(sphere != null ? ", deleted the sphere" : "")}; {menu}";
        Debug.Log(log);
        return log;
    }

    // The box's edges: a light, slightly metallic grey, lit.
    static Material EdgeMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_EdgeMaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "ContainerEdge" };
            AssetDatabase.CreateAsset(material, k_EdgeMaterialPath);
        }
        material.SetColor("_BaseColor", new Color(0.78f, 0.82f, 0.88f));
        material.SetFloat("_Metallic", 0.5f);
        material.SetFloat("_Smoothness", 0.7f);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material GlassMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_GlassMaterialPath);
        if (material != null)
            return material;
        material = new Material(Shader.Find("Tunnel/Container Glass")) { name = "ContainerMRGlass" };
        AssetDatabase.CreateAsset(material, k_GlassMaterialPath);
        return material;
    }

    // The mixed reality box's walls: opaque, a dark slate grey, lit.
    static Material WallMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_WallMaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "ContainerMRWall" };
            AssetDatabase.CreateAsset(material, k_WallMaterialPath);
        }
        material.SetColor("_BaseColor", new Color(0.2f, 0.22f, 0.27f));
        material.SetFloat("_Smoothness", 0.15f);
        material.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material WindowMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_WindowMaterialPath);
        if (material != null)
            return material;
        material = new Material(Shader.Find("Tunnel/Container Window")) { name = "ContainerMRWindow" };
        AssetDatabase.CreateAsset(material, k_WindowMaterialPath);
        return material;
    }

    static GameObject MakePrefab(Material material, Material wall, Material window, Material glass)
    {
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(k_TemplatePath);
        var container = Object.Instantiate(template);
        container.name = "Container_Box_Networked";

        // Only the toy's workings: no molecule, its colour feedback or its collider.
        for (int i = container.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(container.transform.GetChild(i).gameObject);
        Object.DestroyImmediate(container.GetComponent<MoleculeGravity>());
        container.transform.localScale = Vector3.one * k_Size;

        // It stays where it's left and pushes what's inside when moved, but isn't pushed itself.
        var body = container.GetComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        var grab = container.GetComponent<XRGrabInteractable>();
        grab.throwOnDetach = false;
        // Not frozen when it appears (a frozen kinematic box turns in the hand but doesn't move).
        if (container.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = false;
        var grabSo = new SerializedObject(grab);
        grabSo.FindProperty("m_Colliders").ClearArray(); // collected when it wakes, after it builds them
        grabSo.ApplyModifiedPropertiesWithoutUndo();

        var general = container.GetComponent<XRGeneralGrabTransformer>();
        var generalSo = new SerializedObject(general);
        generalSo.FindProperty("m_MinimumScaleRatio").floatValue = 0.15f;
        generalSo.FindProperty("m_MaximumScaleRatio").floatValue = 15f;
        generalSo.ApplyModifiedPropertiesWithoutUndo();

        var component = container.AddComponent<TunnelContainer>();
        var so = new SerializedObject(component);
        so.FindProperty("m_EdgeMaterial").objectReferenceValue = material;
        so.FindProperty("m_WallMaterial").objectReferenceValue = wall;
        so.FindProperty("m_WindowMaterial").objectReferenceValue = window;
        so.FindProperty("m_GlassMaterial").objectReferenceValue = glass;
        so.ApplyModifiedPropertiesWithoutUndo();
        container.AddComponent<ContainerStretchTransformer>();

        string path = $"{k_Folder}/{container.name}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(container, path);
        Object.DestroyImmediate(container);
        return prefab;
    }

    static string BuildMenu(TunnelPrefabRegistry registry)
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var control = root.transform.Find("Control Panel");
            var menuPanel = control.Find("Menu Panel");

            // The Containers button, under Molecules; the panel's buttons re-spaced to fit one more.
            var containersButton = menuPanel.Find("Containers Button");
            if (containersButton == null)
            {
                var molecules = menuPanel.Find("Molecules Button");
                containersButton = Object.Instantiate(molecules.gameObject, menuPanel, false).transform;
                containersButton.name = "Containers Button";
                containersButton.SetSiblingIndex(molecules.GetSiblingIndex() + 1);
            }
            var button = containersButton.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            var label = containersButton.Find("Button Front/Text (TMP)").GetComponent<TMPro.TMP_Text>();
            label.text = "Containers";

            // Delete, under Containers: while on, whatever molecule or container its player grabs is deleted.
            var deleteButton = menuPanel.Find("Delete Button");
            if (deleteButton == null)
            {
                deleteButton = Object.Instantiate(containersButton.gameObject, menuPanel, false).transform;
                deleteButton.name = "Delete Button";
                deleteButton.SetSiblingIndex(containersButton.GetSiblingIndex() + 1);
            }
            var delete = deleteButton.GetComponent<Button>();
            delete.onClick = new Button.ButtonClickedEvent();
            var deleteLabel = deleteButton.Find("Button Front/Text (TMP)").GetComponent<TMPro.TMP_Text>();
            deleteLabel.text = "Delete";
            deleteLabel.richText = true;

            string[] order = { "Colors Button", "Textures Button", "Molecules Button", "Containers Button", "Delete Button",
                "New Browser Button", "New Viewer Button", "Add Segment Button", "Remove Segment Button", "Align Button" };
            for (int i = 0; i < order.Length; i++)
            {
                var rect = (RectTransform)menuPanel.Find(order[i]);
                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, 252f - 56f * i);
            }

            // The Containers list: the molecule list's panel, title and status, with a Place button.
            var old = control.Find("Container List");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
            var moleculeList = control.Find("Molecule List");
            var list = Object.Instantiate(moleculeList.gameObject, control, false);
            list.name = "Container List";
            Object.DestroyImmediate(list.transform.Find("Scroll View").gameObject);
            // Two columns: the rows on the left, the thermometer on the right.
            var panelRect = (RectTransform)list.transform;
            panelRect.sizeDelta = new Vector2(680f, 800f);
            panelRect.anchoredPosition = ((RectTransform)moleculeList).anchoredPosition + new Vector2(-70f, -100f);
            const float rowsX = -80f, rowsWidth = 460f, thermometerX = 225f;

            var title = (RectTransform)list.transform.Find("Title");
            title.anchoredPosition = new Vector2(title.anchoredPosition.x - 70f, 360f);
            title.GetComponent<TMPro.TMP_Text>().text = "Containers";
            var status = (RectTransform)list.transform.Find("Status");
            status.anchoredPosition = new Vector2(0f, 285f);
            status.sizeDelta = new Vector2(620f, 100f);

            var template = list.transform.Find("Row Template");
            Button Row(string name, float y, string text)
            {
                var row = Object.Instantiate(template.gameObject, list.transform, false);
                row.name = name;
                row.SetActive(true);
                var rect = (RectTransform)row.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(rowsWidth, 56f);
                rect.anchoredPosition = new Vector2(rowsX, y);
                var b = row.GetComponent<Button>();
                b.onClick = new Button.ButtonClickedEvent();
                var rowLabel = row.GetComponentInChildren<TMPro.TMP_Text>(true);
                rowLabel.text = text;
                rowLabel.richText = true;
                return b;
            }
            var place = Row("Place Button", 185f, "Place Container");
            var placeLabel = place.GetComponentInChildren<TMPro.TMP_Text>(true);
            placeLabel.alignment = TMPro.TextAlignmentOptions.Center;
            placeLabel.fontStyle = TMPro.FontStyles.Bold;
            var mixedRealityRow = Row("Mixed Reality Row", 95f, "□  Mixed reality box");
            var unsnapRow = Row("Unsnap Row", 30f, "Unsnap from wall");
            var randomRow = Row("Random Row", -60f, "■  Random motion");
            var ballisticRow = Row("Ballistic Row", -125f, "□  Ballistic motion");

            // Viscosity: the molecule list's Magnification slider, 0 to 1 in tenths.
            var viscosity = list.transform.Find("Magnification");
            // (its copy, made first, becomes the thermometer)
            var heat = Object.Instantiate(viscosity.gameObject, list.transform, false).transform;
            viscosity.name = "Viscosity";
            ((RectTransform)viscosity).anchoredPosition = new Vector2(rowsX, -220f);
            ((RectTransform)viscosity).sizeDelta = new Vector2(rowsWidth, 60f);
            var viscositySlider = viscosity.GetComponentInChildren<Slider>(true);
            viscositySlider.name = "Viscosity Slider";
            viscositySlider.onValueChanged = new Slider.SliderEvent();
            viscositySlider.minValue = 0f;
            viscositySlider.maxValue = 10f;
            viscositySlider.wholeNumbers = true;
            viscositySlider.SetValueWithoutNotify(10f);
            var viscosityText = viscosity.Find("Magnification Text").GetComponent<TMPro.TMP_Text>();
            viscosityText.name = "Viscosity Text";
            viscosityText.richText = true;
            viscosityText.text = "Viscosity: 1.0";
            var ticks = viscosity.Find("Ticks");
            while (ticks.childCount < 11)
                Object.Instantiate(ticks.GetChild(ticks.childCount - 1).gameObject, ticks, false);
            for (int i = 0; i < ticks.childCount; i++)
            {
                var tick = (RectTransform)ticks.GetChild(i);
                tick.name = $"Tick {i / 10f:0.0}";
                tick.anchorMin = tick.anchorMax = new Vector2(i / 10f, 0.5f);
                tick.GetComponent<TMPro.TMP_Text>().text = i == 0 ? "0" : i == 10 ? "1" : $".{i}";
            }

            var thermometer = BuildThermometer(heat, thermometerX, out var temperatureSlider, out var temperatureText);
            ((RectTransform)thermometer).anchoredPosition = new Vector2(0f, 5f); // its title just under the status
            Object.DestroyImmediate(template.gameObject);
            list.SetActive(false);

            // The menu that runs it, beside the molecule menu, and the TS Menu's links to both.
            var moleculeMenu = root.GetComponent<TunnelMoleculeMenu>();
            var containerMenu = root.GetComponent<TunnelContainerMenu>() ?? root.AddComponent<TunnelContainerMenu>();
            var so = new SerializedObject(containerMenu);
            so.FindProperty("m_Prefabs").objectReferenceValue = registry;
            so.FindProperty("m_Geometry").objectReferenceValue = new SerializedObject(moleculeMenu).FindProperty("m_Geometry").objectReferenceValue;
            so.FindProperty("m_ListPanel").objectReferenceValue = list;
            so.FindProperty("m_PlaceButton").objectReferenceValue = place;
            so.FindProperty("m_MixedRealityRow").objectReferenceValue = mixedRealityRow;
            so.FindProperty("m_UnsnapRow").objectReferenceValue = unsnapRow;
            so.FindProperty("m_StatusText").objectReferenceValue = status.GetComponent<TMPro.TMP_Text>();
            so.FindProperty("m_ViscositySlider").objectReferenceValue = viscositySlider;
            so.FindProperty("m_ViscosityText").objectReferenceValue = viscosityText;
            so.FindProperty("m_RandomRow").objectReferenceValue = randomRow;
            so.FindProperty("m_BallisticRow").objectReferenceValue = ballisticRow;
            so.FindProperty("m_TemperatureSlider").objectReferenceValue = temperatureSlider;
            so.FindProperty("m_TemperatureText").objectReferenceValue = temperatureText;
            so.ApplyModifiedPropertiesWithoutUndo();

            var tunnelMenu = control.GetComponent<TunnelMenu>();
            var menuSo = new SerializedObject(tunnelMenu);
            menuSo.FindProperty("m_ContainersButton").objectReferenceValue = button;
            menuSo.FindProperty("m_ContainersLabel").objectReferenceValue = label;
            menuSo.FindProperty("m_Containers").objectReferenceValue = containerMenu;
            menuSo.FindProperty("m_DeleteButton").objectReferenceValue = delete;
            menuSo.FindProperty("m_DeleteLabel").objectReferenceValue = deleteLabel;
            menuSo.ApplyModifiedPropertiesWithoutUndo();

            bool saved = PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath) != null;
            return saved ? "TS Menu: Containers button and list added" : "TS Menu: FAILED to save";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
    /// <summary>
    /// The Containers list's thermometer, made from a copy of the Magnification slider: a vertical tube that fills red
    /// from a red bulb as the temperature rises (0 to 10, continuous), with its scale beside it and the reading (the
    /// setting, kelvin and °C) under the bulb. Laid out in the panel's own coordinates, around x.
    /// </summary>
    static Transform BuildThermometer(Transform group, float x, out Slider slider, out TMPro.TMP_Text reading)
    {
        group.name = "Temperature";
        var groupRect = (RectTransform)group;
        groupRect.anchorMin = groupRect.anchorMax = groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = Vector2.zero;
        groupRect.localScale = Vector3.one;

        RectTransform Place(Transform t, Vector2 position, Vector2 size)
        {
            var rect = (RectTransform)t;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        const float tubeTop = 160f, tubeBottom = -250f, tubeWidth = 34f;
        var red = new Color(0.86f, 0.16f, 0.12f);

        var title = group.Find("Magnification Text").GetComponent<TMPro.TMP_Text>();
        title.name = "Temperature Title";
        title.text = "Temperature";
        title.alignment = TMPro.TextAlignmentOptions.Center;
        Place(title.transform, new Vector2(x + 20f, 200f), new Vector2(190f, 36f));

        slider = group.GetComponentInChildren<Slider>(true);
        slider.name = "Thermometer";
        slider.onValueChanged = new Slider.SliderEvent();
        slider.direction = Slider.Direction.BottomToTop;
        slider.minValue = 0f;
        slider.maxValue = TunnelContainer.k_MaxTemperature;
        slider.wholeNumbers = false;
        Place(slider.transform, new Vector2(x, (tubeTop + tubeBottom) / 2f), new Vector2(tubeWidth, tubeTop - tubeBottom));
        foreach (string unused in new[] { "Value Text", "Type Text" })
        {
            var t = slider.transform.Find(unused);
            if (t != null)
                Object.DestroyImmediate(t.gameObject);
        }

        // The fill and handle run up the tube (the slider sets their position along it, not across).
        var fill = slider.fillRect;
        fill.anchorMin = new Vector2(0f, 0f);
        fill.anchorMax = new Vector2(1f, 0f);
        fill.sizeDelta = Vector2.zero;
        fill.GetComponent<Image>().color = red;
        var handle = slider.handleRect;
        handle.anchorMin = new Vector2(0f, 0f);
        handle.anchorMax = new Vector2(1f, 0f);
        handle.sizeDelta = new Vector2(26f, 26f);
        slider.SetValueWithoutNotify(0f);

        // The bulb: a red disc over the tube's foot, in the handle's round shape.
        var bulbObject = new GameObject("Bulb", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        bulbObject.layer = group.gameObject.layer;
        bulbObject.transform.SetParent(group, false);
        var bulb = bulbObject.GetComponent<Image>();
        bulb.sprite = handle.GetComponent<Image>().sprite;
        bulb.type = Image.Type.Simple;
        bulb.color = red;
        bulb.raycastTarget = false;
        Place(bulbObject.transform, new Vector2(x, tubeBottom - 18f), new Vector2(72f, 72f));

        // The scale, 0 to 10, beside the tube.
        var ticks = group.Find("Ticks");
        Place(ticks, Vector2.zero, Vector2.zero);
        var tickTemplate = ticks.GetChild(0).gameObject;
        for (int i = ticks.childCount - 1; i >= 1; i--)
            Object.DestroyImmediate(ticks.GetChild(i).gameObject);
        for (int i = 0; i <= 10; i++)
        {
            var tick = i == 0 ? tickTemplate : Object.Instantiate(tickTemplate, ticks, false);
            tick.name = $"Tick {i}";
            var text = tick.GetComponent<TMPro.TMP_Text>();
            text.text = i == 10 ? "10 <size=70%>20 °C</size>" : i.ToString();
            text.richText = true;
            text.alignment = TMPro.TextAlignmentOptions.Left;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            var rect = Place(tick.transform, new Vector2(x + 30f + 50f, tubeBottom + i / 10f * (tubeTop - tubeBottom)), new Vector2(100f, 30f));
            rect.pivot = new Vector2(0.5f, 0.5f);
        }

        // The reading, under the bulb.
        var readingObject = Object.Instantiate(title.gameObject, group, false);
        readingObject.name = "Temperature Reading";
        reading = readingObject.GetComponent<TMPro.TMP_Text>();
        reading.richText = true;
        reading.text = "<b>0.0</b>\n<size=75%>0 K\n-273 °C</size>";
        reading.alignment = TMPro.TextAlignmentOptions.Top;
        reading.enableAutoSizing = false;
        Place(readingObject.transform, new Vector2(x + 20f, -350f), new Vector2(190f, 100f));
        return group;
    }
}
