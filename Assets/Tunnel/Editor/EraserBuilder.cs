using System.Linq;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Makes the TS Menu's eraser (see <see cref="TunnelEraser"/>): a block eraser, modelled simply as a wooden block with
/// a rounded grip on top and a dark felt pad underneath (13 x 5 cm); its prefab (a copy of the 2PLV molecule's toy
/// without its molecule, so it is grabbed and shared the same way, but not scaled, not pushed and pushing nothing);
/// its entry in the network prefab list; and the TS Menu's link to it in the tunnel segment prefab (its Delete button
/// calls it up, see <see cref="TunnelMenu"/>). Runs from Tunnel > Eraser > Build Eraser.
/// </summary>
static class EraserBuilder
{
    const string k_TemplatePath = "Assets/Molecules/2PLV/Toy_2PLV_Networked.prefab";
    public const string k_Folder = "Assets/Tunnel/Eraser";
    public const string k_PrefabPath = k_Folder + "/Eraser_Networked.prefab";
    const string k_WoodMaterialPath = k_Folder + "/EraserWood.mat";
    const string k_GripMaterialPath = k_Folder + "/EraserGrip.mat";
    const string k_FeltMaterialPath = k_Folder + "/EraserFelt.mat";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_NetworkListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";

    // The parts (meters), the felt underneath (-Y), the long side along X.
    static readonly Vector3 k_BlockSize = new(0.13f, 0.03f, 0.05f);
    static readonly Vector3 k_FeltSize = new(0.13f, 0.012f, 0.05f);
    static readonly Color k_FeltColour = new(0.16f, 0.17f, 0.2f);

    [MenuItem("Tunnel/Eraser/Build Eraser")]
    public static string Build() => BuilderScratchScene.Run(BuildAll);

    static string BuildAll()
    {
        if (!AssetDatabase.IsValidFolder(k_Folder))
            AssetDatabase.CreateFolder("Assets/Tunnel", "Eraser");
        var eraser = BuildPrefab();

        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_NetworkListPath);
        foreach (var entry in list.PrefabList.Where(p => p.Prefab == null).ToList())
            list.Remove(entry);
        if (!list.PrefabList.Any(p => p.Prefab == eraser))
            list.Add(new NetworkPrefab { Prefab = eraser });
        EditorUtility.SetDirty(list);

        string menu = LinkMenu(eraser.GetComponent<TunnelEraser>());
        AssetDatabase.SaveAssets();
        string log = $"[Eraser] built {k_PrefabPath}; network prefab listed; {menu}";
        Debug.Log(log);
        return log;
    }

    static Material LitMaterial(string path, string name, Color colour, float smoothness)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetColor("_BaseColor", colour);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(material);
        return material;
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

    static GameObject BuildPrefab()
    {
        var wood = LitMaterial(k_WoodMaterialPath, "EraserWood", new Color(0.66f, 0.45f, 0.26f), 0.35f);
        var grip = LitMaterial(k_GripMaterialPath, "EraserGrip", new Color(0.42f, 0.27f, 0.15f), 0.45f);
        var felt = LitMaterial(k_FeltMaterialPath, "EraserFelt", k_FeltColour, 0.05f);
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(k_TemplatePath);

        // Made in a preview scene, so the open scene is left as it was.
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try
        {
            var eraser = (GameObject)PrefabUtility.InstantiatePrefab(template, scene);
            PrefabUtility.UnpackPrefabInstance(eraser, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            eraser.name = "Eraser_Networked";

            // Only the toy's workings: no molecule, its colour feedback or its collider.
            for (int i = eraser.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(eraser.transform.GetChild(i).gameObject);
            if (eraser.TryGetComponent(out CapsidSites sites))
                Object.DestroyImmediate(sites);
            if (eraser.TryGetComponent(out AntibodyBinder binder))
                Object.DestroyImmediate(binder);
            Object.DestroyImmediate(eraser.GetComponent<MoleculeGravity>());
            eraser.transform.localScale = Vector3.one;

            // The parts: the block, the felt under it and a rounded grip along its top.
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var capsule = Resources.GetBuiltinResource<Mesh>("Capsule.fbx");
            Part("Block", eraser.transform, cube, wood, new Vector3(0f, k_BlockSize.y * 0.5f, 0f), Quaternion.identity, k_BlockSize);
            var feltPart = Part("Felt", eraser.transform, cube, felt, new Vector3(0f, -k_FeltSize.y * 0.5f, 0f), Quaternion.identity, k_FeltSize);
            // (the built-in capsule's size varies with the import: scaled from its bounds; turned to lie along X)
            const float gripDiameter = 0.024f, gripLength = 0.09f;
            var capsuleSize = capsule.bounds.size;
            Part("Grip", eraser.transform, capsule, grip, new Vector3(0f, k_BlockSize.y + gripDiameter * 0.3f, 0f),
                Quaternion.Euler(0f, 0f, 90f), new Vector3(gripDiameter / capsuleSize.x, gripLength / capsuleSize.y, gripDiameter / capsuleSize.z));

            // What the hand grabs: one box round it all, which pushes nothing (it only erases by touch).
            float top = k_BlockSize.y + gripDiameter * 0.8f, bottom = -k_FeltSize.y;
            var colliders = new GameObject("Colliders").transform;
            colliders.SetParent(eraser.transform, false);
            var box = colliders.gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(k_BlockSize.x, top - bottom, k_BlockSize.z);
            box.center = new Vector3(0f, (top + bottom) * 0.5f, 0f);
            box.excludeLayers = ~0;

            // It stays where it's left, isn't pushed and isn't parked when it appears; one hand moves and turns it,
            // two don't scale it.
            var body = eraser.GetComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            var grab = eraser.GetComponent<XRGrabInteractable>();
            grab.throwOnDetach = false;
            var grabSo = new SerializedObject(grab);
            grabSo.FindProperty("m_Colliders").ClearArray(); // its own collider, collected when it wakes
            grabSo.ApplyModifiedPropertiesWithoutUndo();
            if (eraser.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable networkPhysics))
                networkPhysics.spawnLocked = false;
            var general = eraser.GetComponent<XRGeneralGrabTransformer>();
            if (general != null)
            {
                var generalSo = new SerializedObject(general);
                generalSo.FindProperty("m_AllowTwoHandedScaling").boolValue = false;
                generalSo.ApplyModifiedPropertiesWithoutUndo();
            }

            var component = eraser.AddComponent<TunnelEraser>();
            component.Configure(feltPart.transform, feltPart.GetComponent<MeshRenderer>(), k_FeltColour);

            var prefab = PrefabUtility.SaveAsPrefabAsset(eraser, k_PrefabPath);
            Object.DestroyImmediate(eraser);
            return prefab;
        }
        finally
        {
            UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    // The TS Menu's Delete button calls it up.
    static string LinkMenu(TunnelEraser eraser)
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var menu = root.GetComponentInChildren<TunnelMenu>(true);
            if (menu == null)
                return "TS Menu: FAILED (no TunnelMenu in the segment)";
            var so = new SerializedObject(menu);
            so.FindProperty("m_EraserPrefab").objectReferenceValue = eraser;
            so.ApplyModifiedPropertiesWithoutUndo();
            bool saved = PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath) != null;
            return saved ? "TS Menu: Delete calls up the eraser" : "TS Menu: FAILED to save";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
