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
    const string k_LineMaterialPath = "Assets/Tunnel/Materials/ContainerLine.mat";
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
        var material = LineMaterial();
        var box = MakePrefab(TunnelContainer.Shape.Box, material);
        var sphere = MakePrefab(TunnelContainer.Shape.Sphere, material);

        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
        registry.containerBox = box;
        registry.containerSphere = sphere;
        EditorUtility.SetDirty(registry);

        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_NetworkListPath);
        foreach (var prefab in new[] { box, sphere })
            if (!list.PrefabList.Any(p => p.Prefab == prefab))
                list.Add(new NetworkPrefab { Prefab = prefab });
        EditorUtility.SetDirty(list);

        string menu = BuildMenu(registry);
        AssetDatabase.SaveAssets();
        string log = $"[Containers] built {AssetDatabase.GetAssetPath(box)} and {AssetDatabase.GetAssetPath(sphere)}; {menu}";
        Debug.Log(log);
        return log;
    }

    static Material LineMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_LineMaterialPath);
        if (material != null)
            return material;
        material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "ContainerLine" };
        material.SetColor("_BaseColor", Color.green);
        AssetDatabase.CreateAsset(material, k_LineMaterialPath);
        return material;
    }

    static GameObject MakePrefab(TunnelContainer.Shape shape, Material material)
    {
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(k_TemplatePath);
        var container = Object.Instantiate(template);
        container.name = $"Container_{shape}_Networked";

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
        so.FindProperty("m_Shape").enumValueIndex = (int)shape;
        so.FindProperty("m_LineMaterial").objectReferenceValue = material;
        so.ApplyModifiedPropertiesWithoutUndo();
        if (shape == TunnelContainer.Shape.Box)
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

            string[] order = { "Colors Button", "Textures Button", "Molecules Button", "Containers Button", "New Browser Button",
                "New Viewer Button", "Add Segment Button", "Remove Segment Button", "Align Button" };
            for (int i = 0; i < order.Length; i++)
            {
                var rect = (RectTransform)menuPanel.Find(order[i]);
                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, 248f - 62f * i);
            }

            // The Containers list: the molecule list's panel, title and status, with two shape rows and a Place button.
            var old = control.Find("Container List");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
            var moleculeList = control.Find("Molecule List");
            var list = Object.Instantiate(moleculeList.gameObject, control, false);
            list.name = "Container List";
            Object.DestroyImmediate(list.transform.Find("Magnification").gameObject);
            Object.DestroyImmediate(list.transform.Find("Scroll View").gameObject);
            var panelRect = (RectTransform)list.transform;
            panelRect.sizeDelta = new Vector2(540f, 460f);
            panelRect.anchoredPosition = ((RectTransform)moleculeList).anchoredPosition + new Vector2(0f, 70f);

            var title = (RectTransform)list.transform.Find("Title");
            title.anchoredPosition = new Vector2(title.anchoredPosition.x, 190f);
            title.GetComponent<TMPro.TMP_Text>().text = "Containers";
            var status = (RectTransform)list.transform.Find("Status");
            status.anchoredPosition = new Vector2(0f, 115f);
            status.sizeDelta = new Vector2(500f, 100f);

            var template = list.transform.Find("Row Template");
            Button Row(string name, float y, string text)
            {
                var row = Object.Instantiate(template.gameObject, list.transform, false);
                row.name = name;
                row.SetActive(true);
                var rect = (RectTransform)row.transform;
                rect.anchorMin = new Vector2(0f, 0.5f);
                rect.anchorMax = new Vector2(1f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(-60f, 56f);
                rect.anchoredPosition = new Vector2(0f, y);
                var b = row.GetComponent<Button>();
                b.onClick = new Button.ButtonClickedEvent();
                row.GetComponentInChildren<TMPro.TMP_Text>(true).text = text;
                return b;
            }
            var boxRow = Row("Box Row", 15f, "■  Box");
            var sphereRow = Row("Sphere Row", -50f, "□  Sphere");
            var place = Row("Place Button", -150f, "Place Container");
            var placeLabel = place.GetComponentInChildren<TMPro.TMP_Text>(true);
            placeLabel.alignment = TMPro.TextAlignmentOptions.Center;
            placeLabel.fontStyle = TMPro.FontStyles.Bold;
            Object.DestroyImmediate(template.gameObject);
            list.SetActive(false);

            // The menu that runs it, beside the molecule menu, and the TS Menu's links to both.
            var moleculeMenu = root.GetComponent<TunnelMoleculeMenu>();
            var containerMenu = root.GetComponent<TunnelContainerMenu>() ?? root.AddComponent<TunnelContainerMenu>();
            var so = new SerializedObject(containerMenu);
            so.FindProperty("m_Prefabs").objectReferenceValue = registry;
            so.FindProperty("m_Geometry").objectReferenceValue = new SerializedObject(moleculeMenu).FindProperty("m_Geometry").objectReferenceValue;
            so.FindProperty("m_ListPanel").objectReferenceValue = list;
            so.FindProperty("m_BoxRow").objectReferenceValue = boxRow;
            so.FindProperty("m_SphereRow").objectReferenceValue = sphereRow;
            so.FindProperty("m_PlaceButton").objectReferenceValue = place;
            so.FindProperty("m_StatusText").objectReferenceValue = status.GetComponent<TMPro.TMP_Text>();
            so.ApplyModifiedPropertiesWithoutUndo();

            var tunnelMenu = control.GetComponent<TunnelMenu>();
            var menuSo = new SerializedObject(tunnelMenu);
            menuSo.FindProperty("m_ContainersButton").objectReferenceValue = button;
            menuSo.FindProperty("m_ContainersLabel").objectReferenceValue = label;
            menuSo.FindProperty("m_Containers").objectReferenceValue = containerMenu;
            menuSo.ApplyModifiedPropertiesWithoutUndo();

            bool saved = PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath) != null;
            return saved ? "TS Menu: Containers button and list added" : "TS Menu: FAILED to save";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
