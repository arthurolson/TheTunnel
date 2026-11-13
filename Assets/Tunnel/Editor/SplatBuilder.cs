using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Makes what a splat-box shows (see <see cref="SplatBox"/>): for each splat in <see cref="k_Splats"/>, a prefab of its
/// baked mesh (imported by glTFast) drawn with the Tunnel/Splat Unlit material (vertex colours, unlit, both sides), the
/// splat library that lists them by name (Assets/Tunnel/Splats/SplatLibrary.asset), and the small menu shown beside
/// every splat-box. The splat-box itself, its row in the Containers list and its network prefab are made by
/// Tunnel > Containers > Build Containers, which runs this first. Tunnel > Containers > Build Splats remakes only the
/// splats and the library (enough after adding a splat: the menu makes its rows from the library).
/// </summary>
static class SplatBuilder
{
    public const string k_Folder = "Assets/Tunnel/Splats";
    const string k_LibraryPath = k_Folder + "/SplatLibrary.asset";
    const string k_MaterialPath = k_Folder + "/SplatUnlit.mat";
    const string k_PanelPath = k_Folder + "/SplatBox_Menu.prefab";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";

    const float k_CanvasScale = 0.0006f; // meters per canvas unit: half as large again as the movie panel, as it's seen from further
    const float k_CanvasWidth = 420f;    // 25 cm

    /// <summary>
    /// The splats: the name on the menu, the model (a glb with the splat baked to a vertex-coloured mesh) and how it is
    /// turned in its box (degrees). Add new ones at the end: a splat-box shows its splat by its place in the list.
    /// </summary>
    static readonly (string name, string model, Vector3 rotation)[] k_Splats =
    {
        ("David Goodsell", k_Folder + "/goodsellportrait_mesh.glb", Vector3.zero),
    };

    [MenuItem("Tunnel/Containers/Build Splats")]
    public static string BuildSplats() => BuilderScratchScene.Run(BuildSplatsInScratch);

    static string BuildSplatsInScratch()
    {
        var library = BuildLibrary();
        AssetDatabase.SaveAssets();
        string log = $"[Splat] library {k_LibraryPath}: {string.Join(", ", library.splats.Select(s => $"{s.name} ({(s.prefab != null ? AssetDatabase.GetAssetPath(s.prefab) : "no prefab")})"))}";
        Debug.Log(log);
        return log;
    }

    /// <summary>Makes each splat's prefab and lists them in the library (entries added to it by hand are kept).</summary>
    public static SplatLibrary BuildLibrary()
    {
        if (!AssetDatabase.IsValidFolder(k_Folder))
            AssetDatabase.CreateFolder("Assets/Tunnel", "Splats");
        var material = SplatMaterial();
        var library = AssetDatabase.LoadAssetAtPath<SplatLibrary>(k_LibraryPath);
        if (library == null)
        {
            library = ScriptableObject.CreateInstance<SplatLibrary>();
            AssetDatabase.CreateAsset(library, k_LibraryPath);
        }
        foreach (var (splatName, model, rotation) in k_Splats)
        {
            var prefab = BuildSplatPrefab(splatName, model, material);
            if (prefab == null)
                continue;
            var entry = library.splats.FirstOrDefault(s => s != null && s.name == splatName);
            if (entry == null)
            {
                entry = new SplatLibrary.Splat { name = splatName, rotation = rotation };
                library.splats.Add(entry);
            }
            entry.prefab = prefab;
        }
        EditorUtility.SetDirty(library);
        return library;
    }

    // Unlit vertex colours from both sides (the glTF is double-sided), white tint.
    static Material SplatMaterial()
    {
        var shader = Shader.Find("Tunnel/Splat Unlit");
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_MaterialPath);
        if (material == null)
        {
            material = new Material(shader) { name = "SplatUnlit" };
            AssetDatabase.CreateAsset(material, k_MaterialPath);
        }
        material.shader = shader;
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Exposure", 1f);
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        material.enableInstancing = false;
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>
    /// The model's meshes (the importer's own, shared, not copied) with the splat material, and nothing else: no
    /// colliders, shadows or probes.
    /// </summary>
    static GameObject BuildSplatPrefab(string splatName, string modelPath, Material material)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null)
        {
            Debug.LogWarning($"[Splat] {splatName}: no model at {modelPath} (is glTFast importing it?)");
            return null;
        }
        // Made in a preview scene, so the open scene is left as it was.
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try
        {
            var go = PrefabUtility.InstantiatePrefab(model, scene) as GameObject;
            if (go == null)
            {
                go = Object.Instantiate(model);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            }
            else if (PrefabUtility.IsPartOfPrefabInstance(go))
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = "Splat_" + new string(splatName.Where(char.IsLetterOrDigit).ToArray());
            foreach (var component in go.GetComponentsInChildren<Component>(true))
                if (!(component is Transform || component is MeshFilter || component is MeshRenderer))
                    Object.DestroyImmediate(component);
            int vertices = 0, triangles = 0;
            foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.sharedMaterials = Enumerable.Repeat(material, Mathf.Max(1, renderer.sharedMaterials.Length)).ToArray();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                renderer.gameObject.layer = 0;
            }
            foreach (var filter in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;
                vertices += mesh.vertexCount;
                for (int i = 0; i < mesh.subMeshCount; i++)
                    triangles += (int)mesh.GetIndexCount(i) / 3;
                if (mesh.vertexCount > 65535 && mesh.indexFormat != UnityEngine.Rendering.IndexFormat.UInt32)
                    Debug.LogError($"[Splat] {splatName}: {mesh.name} has {mesh.vertexCount} vertices but 16-bit indices");
                if (!mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color))
                    Debug.LogWarning($"[Splat] {splatName}: {mesh.name} has no vertex colours");
            }
            go.layer = 0;
            string path = $"{k_Folder}/{go.name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            Debug.Log($"[Splat] {splatName}: {path}, {vertices} vertices, {triangles} triangles");
            return prefab;
        }
        finally
        {
            UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    /// <summary>
    /// The box's size for its first splat: the splat's proportions, a margin all round, its largest side this long
    /// (meters).
    /// </summary>
    public static Vector3 BoxSize(SplatLibrary library, float margin, float largest)
    {
        var splat = library != null ? library.Get(0) : null;
        if (splat == null || splat.prefab == null)
            return Vector3.one * largest;
        // Measured on the prefab itself (nothing put in the scene), turned as the box turns it.
        var need = SplatBox.MeshBounds(splat.prefab.transform, Matrix4x4.Rotate(Quaternion.Euler(splat.rotation))).size * (1f + margin);
        return need * (largest / Mathf.Max(need.x, Mathf.Max(need.y, need.z)));
    }

    /// <summary>
    /// The menu beside a splat-box: the Containers list's look (its canvas, title and rows), a title and one hidden row
    /// that the menu copies for each splat in the library.
    /// </summary>
    public static SplatBoxPanel BuildPanel()
    {
        var segment = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var control = segment.transform.Find("Control Panel");
            var panel = new GameObject("Splat Menu");

            var canvasObject = new GameObject("Splat Canvas", typeof(RectTransform));
            canvasObject.layer = control.gameObject.layer;
            var canvas = (RectTransform)canvasObject.transform;
            canvas.SetParent(panel.transform, false);
            foreach (var type in new[] { typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster) })
            {
                var source = control.GetComponent(type);
                if (source != null && UnityEditorInternal.ComponentUtility.CopyComponent(source))
                    UnityEditorInternal.ComponentUtility.PasteComponentAsNew(canvasObject);
            }
            canvas.sizeDelta = new Vector2(k_CanvasWidth, 160f);
            canvas.localScale = Vector3.one * k_CanvasScale;
            canvas.localPosition = Vector3.zero;
            canvas.localRotation = Quaternion.identity;

            var menuPanel = control.Find("Menu Panel").GetComponent<Image>();
            var backgroundObject = new GameObject("Background", typeof(RectTransform));
            backgroundObject.layer = canvasObject.layer;
            var backgroundRect = (RectTransform)backgroundObject.transform;
            backgroundRect.SetParent(canvas, false);
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = backgroundRect.offsetMax = Vector2.zero;
            var background = backgroundObject.AddComponent<Image>();
            background.sprite = menuPanel.sprite;
            background.type = menuPanel.type;
            background.color = menuPanel.color;

            // The title along the top.
            var titleObject = Object.Instantiate(control.Find("Container List/Title").gameObject, canvas, false);
            titleObject.name = "Title";
            var titleRect = (RectTransform)titleObject.transform;
            titleRect.anchorMin = titleRect.anchorMax = new Vector2(0.5f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -12f);
            titleRect.sizeDelta = new Vector2(k_CanvasWidth - 40f, 60f);
            var title = titleObject.GetComponent<TMP_Text>();
            title.text = "Splats";
            title.richText = true;
            title.enableAutoSizing = false;
            title.alignment = TextAlignmentOptions.Left;

            // One row, hidden: the menu copies it for each splat, down from under the title.
            var rowObject = Object.Instantiate(control.Find("Container List/Box Row").gameObject, canvas, false);
            rowObject.name = "Row Template";
            var rowRect = (RectTransform)rowObject.transform;
            rowRect.anchorMin = rowRect.anchorMax = new Vector2(0.5f, 1f);
            rowRect.pivot = new Vector2(0.5f, 0.5f);
            rowRect.sizeDelta = new Vector2(k_CanvasWidth - 40f, 56f);
            rowRect.anchoredPosition = new Vector2(0f, -112f);
            var row = rowObject.GetComponent<Button>();
            row.onClick = new Button.ButtonClickedEvent();
            row.interactable = true;
            var label = rowObject.GetComponentInChildren<TMP_Text>(true);
            label.text = "□  Splat";
            label.richText = true;
            rowObject.SetActive(false);

            var component = panel.AddComponent<SplatBoxPanel>();
            component.Configure(canvas, row, title);
            var prefab = PrefabUtility.SaveAsPrefabAsset(panel, k_PanelPath);
            Object.DestroyImmediate(panel);
            return prefab.GetComponent<SplatBoxPanel>();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(segment);
        }
    }
}
