using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Makes the spawn nozzle (see <see cref="SpawnNozzle"/>): its body (a turned barrel with a cup at the back for the
/// molecule, a ring round the cup and a grip), its prefab (a copy of the 2PLV molecule's toy without its molecule, so
/// it is grabbed and shared the same way, but not scaled), the menu shown beside it (see
/// <see cref="SpawnNozzlePanel"/>), its entry in the network prefab list, and the Spawn nozzle check box on the TS
/// Menu's Molecules list in the tunnel segment prefab (see <see cref="TunnelMoleculeMenu"/>). Needs the Containers list
/// (Tunnel > Containers > Build Containers), whose look the menu copies. Runs from Tunnel > Molecules > Build Spawn
/// Nozzle.
/// </summary>
static class NozzleBuilder
{
    const string k_TemplatePath = "Assets/Molecules/2PLV/Toy_2PLV_Networked.prefab";
    public const string k_Folder = "Assets/Tunnel/Nozzle";
    public const string k_PrefabPath = k_Folder + "/SpawnNozzle_Networked.prefab";
    const string k_PanelPath = k_Folder + "/SpawnNozzle_Menu.prefab";
    const string k_MeshesPath = k_Folder + "/SpawnNozzle_Meshes.asset";
    const string k_BodyMaterialPath = k_Folder + "/NozzleBody.mat";
    const string k_GripMaterialPath = k_Folder + "/NozzleGrip.mat";
    const string k_RingMaterialPath = k_Folder + "/NozzleRing.mat";
    const string k_RegistryPath = "Assets/Tunnel/TunnelPrefabs.asset";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_NetworkListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";

    public const string k_RowName = "Spawn Nozzle Row";

    // The menu: as the movie panel (0.4 mm per canvas unit), 24 x 19 cm.
    const float k_CanvasScale = 0.0004f;
    static readonly Vector2 k_CanvasSize = new(600f, 480f);

    // The body (meters, along +Z, the mouth at the front): the profile turned round the Z axis, from the cup's floor
    // at the back round its rim, along the barrel and in to the mouth's lip. Each step is (z, radius).
    static readonly Vector2[] k_Profile =
    {
        new(-0.066f, 0f),      // the cup's floor, in the middle
        new(-0.066f, 0.019f),  // the cup's floor, at its wall
        new(-0.080f, 0.021f),  // up the cup's inside to the rim
        new(-0.080f, 0.026f),  // across the rim
        new(-0.064f, 0.025f),  // down the cup's outside
        new(-0.060f, 0.021f),  // in to the barrel
        new(0.040f, 0.021f),   // along the barrel
        new(0.052f, 0.017f),   // a step
        new(0.078f, 0.009f),   // the taper to the mouth
        new(0.086f, 0.008f),   // the lip
        new(0.086f, 0.004f),   // across the lip
        new(0.080f, 0f),       // into the mouth
    };
    const int k_Sides = 32;

    // Where the attached molecule's surface sits (just inside the cup's rim), and where copies come out.
    const float k_CradleZ = -0.074f;
    const float k_MouthZ = 0.087f;

    [MenuItem("Tunnel/Molecules/Build Spawn Nozzle")]
    public static string Build() => BuilderScratchScene.Run(BuildAll);

    static string BuildAll()
    {
        if (!AssetDatabase.IsValidFolder(k_Folder))
            AssetDatabase.CreateFolder("Assets/Tunnel", "Nozzle");
        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
        var meshes = BuildMeshes();
        var panel = BuildPanel();
        var nozzle = BuildPrefab(registry, meshes, panel);

        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_NetworkListPath);
        foreach (var entry in list.PrefabList.Where(p => p.Prefab == null).ToList())
            list.Remove(entry);
        if (!list.PrefabList.Any(p => p.Prefab == nozzle))
            list.Add(new NetworkPrefab { Prefab = nozzle });
        EditorUtility.SetDirty(list);

        string menu = BuildMenuRow(nozzle.GetComponent<SpawnNozzle>());
        AssetDatabase.SaveAssets();
        string log = $"[Nozzle] built {k_PrefabPath} and {k_PanelPath}; network prefab listed; {menu}";
        Debug.Log(log);
        return log;
    }

    #region Body

    static Material LitMaterial(string path, string name, Color colour, float metallic, float smoothness)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetColor("_BaseColor", colour);
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>The barrel (the profile turned round Z) and the cup's ring (a torus), in one mesh asset.</summary>
    static (Mesh barrel, Mesh ring) BuildMeshes()
    {
        var barrel = Lathe("Nozzle Barrel", k_Profile, k_Sides);
        var ring = Torus("Nozzle Cup Ring", 0.0235f, 0.0035f, k_Sides, 12);
        ring.vertices = ring.vertices.Select(v => v + new Vector3(0f, 0f, -0.080f)).ToArray();
        ring.RecalculateBounds();

        // Kept in one asset, replaced in place so the prefab's references stay good.
        var existing = AssetDatabase.LoadAllAssetsAtPath(k_MeshesPath).OfType<Mesh>().ToDictionary(m => m.name);
        if (!AssetDatabase.LoadAllAssetsAtPath(k_MeshesPath).Any())
        {
            var holder = new Mesh { name = "Nozzle Barrel" };
            AssetDatabase.CreateAsset(holder, k_MeshesPath);
            existing[holder.name] = holder;
        }
        Mesh Store(Mesh made)
        {
            if (existing.TryGetValue(made.name, out var stored))
            {
                stored.Clear();
                stored.vertices = made.vertices;
                stored.normals = made.normals;
                stored.triangles = made.triangles;
                stored.RecalculateBounds();
                Object.DestroyImmediate(made);
                EditorUtility.SetDirty(stored);
                return stored;
            }
            AssetDatabase.AddObjectToAsset(made, k_MeshesPath);
            return made;
        }
        return (Store(barrel), Store(ring));
    }

    /// <summary>
    /// A profile of (z, radius) steps turned round the Z axis: each step is its own band with its own normals, so the
    /// edges between steps are crisp, like a turned metal part.
    /// </summary>
    static Mesh Lathe(string name, Vector2[] profile, int sides)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        for (int s = 0; s < profile.Length - 1; s++)
        {
            var a = profile[s];
            var b = profile[s + 1];
            // The profile runs from the back round the outside to the front, so the air is always on the same side
            // of each step: its direction turned a quarter, in (z, radius).
            var along = (b - a).normalized;
            var n2 = new Vector2(-along.y, along.x);
            int start = vertices.Count;
            for (int i = 0; i <= sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                var round = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                vertices.Add(new Vector3(round.x * a.y, round.y * a.y, a.x));
                vertices.Add(new Vector3(round.x * b.y, round.y * b.y, b.x));
                var normal = (round * n2.y + Vector3.forward * n2.x).normalized;
                normals.Add(normal);
                normals.Add(normal);
            }

            // Which way round its faces go, from its first two triangles (one of them may be a point, on the axis).
            var c1 = Vector3.Cross(vertices[start + 1] - vertices[start], vertices[start + 2] - vertices[start]);
            var c2 = Vector3.Cross(vertices[start + 1] - vertices[start + 2], vertices[start + 3] - vertices[start + 2]);
            var c = c1.sqrMagnitude > c2.sqrMagnitude ? c1 : c2;
            bool flip = Vector3.Dot(c, normals[start]) < 0f;
            for (int i = 0; i < sides; i++)
            {
                int v = start + i * 2;
                if (!flip)
                    triangles.AddRange(new[] { v, v + 1, v + 2, v + 2, v + 1, v + 3 });
                else
                    triangles.AddRange(new[] { v, v + 2, v + 1, v + 2, v + 3, v + 1 });
            }
        }
        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    static Mesh Torus(string name, float major, float minor, int around, int tube)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i <= around; i++)
        {
            float u = i * Mathf.PI * 2f / around;
            var radial = new Vector3(Mathf.Cos(u), Mathf.Sin(u), 0f);
            for (int j = 0; j <= tube; j++)
            {
                float v = j * Mathf.PI * 2f / tube;
                var normal = radial * Mathf.Cos(v) + Vector3.forward * Mathf.Sin(v);
                vertices.Add(radial * major + normal * minor);
                normals.Add(normal);
            }
        }
        int row = tube + 1;
        for (int i = 0; i < around; i++)
            for (int j = 0; j < tube; j++)
            {
                int a = i * row + j, b = (i + 1) * row + j;
                triangles.AddRange(new[] { a, a + 1, b, b, a + 1, b + 1 });
            }
        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        // Outward faces: check one and flip all if needed.
        var t = mesh.triangles;
        var p = mesh.vertices;
        if (Vector3.Dot(Vector3.Cross(p[t[1]] - p[t[0]], p[t[2]] - p[t[0]]), normals[t[0]]) < 0f)
        {
            for (int k = 0; k < t.Length; k += 3)
                (t[k + 1], t[k + 2]) = (t[k + 2], t[k + 1]);
            mesh.triangles = t;
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    static GameObject Part(string name, Transform parent, Mesh mesh, Material material, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.transform.SetLocalPositionAndRotation(position, rotation);
        go.transform.localScale = scale;
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        return go;
    }

    static GameObject BuildPrefab(TunnelPrefabRegistry registry, (Mesh barrel, Mesh ring) meshes, SpawnNozzlePanel panel)
    {
        var bodyMaterial = LitMaterial(k_BodyMaterialPath, "NozzleBody", new Color(0.74f, 0.77f, 0.82f), 0.75f, 0.65f);
        var gripMaterial = LitMaterial(k_GripMaterialPath, "NozzleGrip", new Color(0.16f, 0.17f, 0.2f), 0f, 0.3f);
        var ringMaterial = LitMaterial(k_RingMaterialPath, "NozzleRing", new Color(0.55f, 0.58f, 0.62f), 0.2f, 0.6f);
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(k_TemplatePath);

        // Made in a preview scene, so the open scene is left as it was.
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try
        {
            var nozzle = (GameObject)PrefabUtility.InstantiatePrefab(template, scene);
            PrefabUtility.UnpackPrefabInstance(nozzle, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            nozzle.name = "SpawnNozzle_Networked";

            // Only the toy's workings: no molecule, its colour feedback or its collider.
            for (int i = nozzle.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(nozzle.transform.GetChild(i).gameObject);
            Object.DestroyImmediate(nozzle.GetComponent<MoleculeGravity>());
            if (nozzle.TryGetComponent(out CapsidSites sites))
                Object.DestroyImmediate(sites);
            if (nozzle.TryGetComponent(out AntibodyBinder binder))
                Object.DestroyImmediate(binder);
            nozzle.transform.localScale = Vector3.one;

            // The parts.
            Part("Barrel", nozzle.transform, meshes.barrel, bodyMaterial, Vector3.zero, Quaternion.identity, Vector3.one);
            var ring = Part("Cup Ring", nozzle.transform, meshes.ring, ringMaterial, Vector3.zero, Quaternion.identity, Vector3.one);
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var gripRotation = Quaternion.Euler(-18f, 0f, 0f); // raked back, as a pistol grip
            var gripPosition = new Vector3(0f, -0.055f, -0.022f);
            Part("Grip", nozzle.transform, cube, gripMaterial, gripPosition, gripRotation, new Vector3(0.024f, 0.075f, 0.03f));

            var cradle = new GameObject("Cradle").transform;
            cradle.SetParent(nozzle.transform, false);
            cradle.SetLocalPositionAndRotation(new Vector3(0f, 0f, k_CradleZ), Quaternion.LookRotation(Vector3.back, Vector3.up));
            var mouth = new GameObject("Mouth").transform;
            mouth.SetParent(nozzle.transform, false);
            mouth.SetLocalPositionAndRotation(new Vector3(0f, 0f, k_MouthZ), Quaternion.identity);

            // What the hand grabs: the barrel and the grip (the cup is part of the barrel's capsule).
            var colliders = new GameObject("Colliders").transform;
            colliders.SetParent(nozzle.transform, false);
            var barrelCollider = colliders.gameObject.AddComponent<CapsuleCollider>();
            barrelCollider.direction = 2;
            barrelCollider.radius = 0.026f;
            barrelCollider.height = 0.166f;
            barrelCollider.center = new Vector3(0f, 0f, 0.003f);
            var gripObject = new GameObject("Grip Collider").transform;
            gripObject.SetParent(colliders, false);
            gripObject.SetLocalPositionAndRotation(gripPosition, gripRotation);
            var gripCollider = gripObject.gameObject.AddComponent<BoxCollider>();
            gripCollider.size = new Vector3(0.024f, 0.075f, 0.03f);

            // It stays where it's left, isn't pushed and isn't parked when it appears; one hand moves and turns it,
            // two don't scale it.
            var body = nozzle.GetComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            var grab = nozzle.GetComponent<XRGrabInteractable>();
            grab.throwOnDetach = false;
            var grabSo = new SerializedObject(grab);
            grabSo.FindProperty("m_Colliders").ClearArray(); // its own colliders, collected when it wakes
            grabSo.ApplyModifiedPropertiesWithoutUndo();
            if (nozzle.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable networkPhysics))
                networkPhysics.spawnLocked = false;
            var general = nozzle.GetComponent<XRGeneralGrabTransformer>();
            var generalSo = new SerializedObject(general);
            generalSo.FindProperty("m_AllowTwoHandedScaling").boolValue = false;
            generalSo.ApplyModifiedPropertiesWithoutUndo();

            var component = nozzle.AddComponent<SpawnNozzle>();
            component.Configure(registry, cradle, mouth, ring.GetComponent<MeshRenderer>(), panel);

            var prefab = PrefabUtility.SaveAsPrefabAsset(nozzle, k_PrefabPath);
            Object.DestroyImmediate(nozzle);
            return prefab;
        }
        finally
        {
            UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    #endregion

    #region Menu

    /// <summary>
    /// The menu beside the nozzle, in the Containers list's look: a title, what's attached, the Copies slider (from
    /// the viscosity slider), the Spawn button and a status line.
    /// </summary>
    static SpawnNozzlePanel BuildPanel()
    {
        var segment = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var control = segment.transform.Find("Control Panel");
            var panel = new GameObject("Spawn Nozzle Menu");

            var canvasObject = new GameObject("Spawn Nozzle Canvas", typeof(RectTransform));
            canvasObject.layer = control.gameObject.layer;
            var canvas = (RectTransform)canvasObject.transform;
            canvas.SetParent(panel.transform, false);
            foreach (var type in new[] { typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster) })
            {
                var source = control.GetComponent(type);
                if (source != null && UnityEditorInternal.ComponentUtility.CopyComponent(source))
                    UnityEditorInternal.ComponentUtility.PasteComponentAsNew(canvasObject);
            }
            canvas.sizeDelta = k_CanvasSize;
            canvas.localScale = Vector3.one * k_CanvasScale;
            canvas.localPosition = Vector3.zero;
            canvas.localRotation = Quaternion.identity;

            var menuPanel = control.Find("Menu Panel").GetComponent<Image>();
            var background = UiObject("Background", canvas, Vector2.zero, k_CanvasSize).gameObject.AddComponent<Image>();
            background.sprite = menuPanel.sprite;
            background.type = menuPanel.type;
            background.color = menuPanel.color;

            var titleTemplate = control.Find("Container List/Title").gameObject;
            var title = Text(titleTemplate, canvas, "Title", "Spawn nozzle", new Vector2(0f, 198f), new Vector2(560f, 64f));
            title.alignment = TextAlignmentOptions.Left;
            var loaded = Text(titleTemplate, canvas, "Attached", "", new Vector2(0f, 140f), new Vector2(560f, 50f));
            loaded.fontSize = 24f;
            loaded.fontStyle = FontStyles.Normal;
            loaded.alignment = TextAlignmentOptions.Left;
            loaded.textWrappingMode = TextWrappingModes.Normal;

            // Copies: the Containers list's viscosity slider, 1 to 100.
            var countGroup = Object.Instantiate(control.Find("Container List/Viscosity").gameObject, canvas, false);
            countGroup.name = "Copies";
            var countRect = (RectTransform)countGroup.transform;
            countRect.anchoredPosition = new Vector2(0f, 45f);
            countRect.sizeDelta = new Vector2(540f, 60f);
            var slider = countGroup.GetComponentInChildren<Slider>(true);
            slider.name = "Copies Slider";
            slider.onValueChanged = new Slider.SliderEvent();
            slider.minValue = SpawnNozzle.k_MinCount;
            slider.maxValue = SpawnNozzle.k_MaxCount;
            slider.wholeNumbers = true;
            slider.interactable = true;
            slider.SetValueWithoutNotify(SpawnNozzle.k_DefaultCount);
            foreach (var labelName in new[] { "Value Text", "Type Text" })
            {
                var label = slider.transform.Find(labelName);
                if (label != null)
                    label.GetComponent<TMP_Text>().text = "";
            }
            var countText = countGroup.transform.Find("Viscosity Text").GetComponent<TMP_Text>();
            countText.name = "Copies Text";
            countText.text = $"Copies: {SpawnNozzle.k_DefaultCount}";
            var ticks = countGroup.transform.Find("Ticks");
            var tickValues = new[] { 1, 25, 50, 75, 100 };
            while (ticks.childCount > tickValues.Length)
                Object.DestroyImmediate(ticks.GetChild(ticks.childCount - 1).gameObject);
            while (ticks.childCount < tickValues.Length)
                Object.Instantiate(ticks.GetChild(ticks.childCount - 1).gameObject, ticks, false);
            for (int i = 0; i < tickValues.Length; i++)
            {
                var tick = (RectTransform)ticks.GetChild(i);
                float at = (tickValues[i] - slider.minValue) / (slider.maxValue - slider.minValue);
                tick.name = $"Tick {tickValues[i]}";
                tick.anchorMin = tick.anchorMax = new Vector2(at, 0.5f);
                tick.GetComponent<TMP_Text>().text = tickValues[i].ToString();
            }

            // The activator.
            var row = Object.Instantiate(control.Find("Container List/Box Row").gameObject, canvas, false);
            row.name = "Spawn Button";
            var rowRect = (RectTransform)row.transform;
            rowRect.anchorMin = rowRect.anchorMax = rowRect.pivot = new Vector2(0.5f, 0.5f);
            rowRect.anchoredPosition = new Vector2(0f, -60f);
            rowRect.sizeDelta = new Vector2(560f, 60f);
            var spawn = row.GetComponent<Button>();
            spawn.onClick = new Button.ButtonClickedEvent();
            spawn.interactable = true;
            var spawnLabel = row.GetComponentInChildren<TMP_Text>(true);
            spawnLabel.text = $"Spawn {SpawnNozzle.k_DefaultCount}";
            spawnLabel.richText = true;
            spawnLabel.alignment = TextAlignmentOptions.Center;
            spawnLabel.fontStyle = FontStyles.Bold;

            var status = Text(titleTemplate, canvas, "Status", "", new Vector2(0f, -170f), new Vector2(560f, 120f));
            status.fontSize = 22f;
            status.fontStyle = FontStyles.Normal;
            status.alignment = TextAlignmentOptions.TopLeft;
            status.textWrappingMode = TextWrappingModes.Normal;

            var component = panel.AddComponent<SpawnNozzlePanel>();
            component.Configure(canvas, title, loaded, slider, countText, spawn, status);
            var prefab = PrefabUtility.SaveAsPrefabAsset(panel, k_PanelPath);
            Object.DestroyImmediate(panel);
            return prefab.GetComponent<SpawnNozzlePanel>();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(segment);
        }
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

    /// <summary>
    /// The Spawn nozzle check box on the Molecules list, under the Magnification slider: the list grows downward to
    /// make room (its top stays put), and the molecule menu is given the row and the nozzle.
    /// </summary>
    static string BuildMenuRow(SpawnNozzle nozzle)
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var list = (RectTransform)root.transform.Find("Control Panel/Molecule List");
            // The layout, top to bottom, set outright so running this again changes nothing: the list was 560 high
            // at y 60 (top at 340); it's now 640 high, its contents where they were.
            const float top = 340f, height = 640f;
            list.sizeDelta = new Vector2(list.sizeDelta.x, height);
            list.anchoredPosition = new Vector2(list.anchoredPosition.x, top - height * 0.5f);
            float centre = top - height * 0.5f;
            // (each child's y in the original list's frame, centred at 60; kept the same on screen)
            foreach (var (child, y) in new[] { ("Title", 235f), ("Status", 190f), ("Scroll View", 20f), ("Magnification", -185f) })
            {
                var rect = (RectTransform)list.Find(child);
                if (rect != null)
                    rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, y + 60f - centre);
            }

            var old = list.Find(k_RowName);
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
            var template = list.Find("Row Template");
            var rowObject = Object.Instantiate(template.gameObject, list, false);
            rowObject.name = k_RowName;
            rowObject.SetActive(true);
            var rowRect = (RectTransform)rowObject.transform;
            rowRect.anchorMin = rowRect.anchorMax = rowRect.pivot = new Vector2(0.5f, 0.5f);
            rowRect.sizeDelta = new Vector2(460f, 56f);
            rowRect.anchoredPosition = new Vector2(0f, -275f); // near the list's new bottom (-320)
            var row = rowObject.GetComponent<Button>();
            row.onClick = new Button.ButtonClickedEvent();
            var label = rowObject.GetComponentInChildren<TMP_Text>(true);
            label.text = "□  Spawn nozzle";
            label.richText = true;

            var menu = root.GetComponent<TunnelMoleculeMenu>();
            var so = new SerializedObject(menu);
            so.FindProperty("m_NozzleRow").objectReferenceValue = row;
            so.FindProperty("m_NozzlePrefab").objectReferenceValue = nozzle;
            so.ApplyModifiedPropertiesWithoutUndo();

            bool saved = PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath) != null;
            return saved ? "TS Menu: Spawn nozzle row added to the Molecules list" : "TS Menu: FAILED to save";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    #endregion
}
