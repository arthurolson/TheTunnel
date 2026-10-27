using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.AffordanceSystem.Rendering;

/// <summary>
/// Keeps the TS Menu's Molecules list in step with Assets/Molecules: each sub-folder is one molecule, named after the
/// folder. A folder with a networked toy prefab is used as it is; a folder with only a model (GLB, glTF, OBJ, FBX, in
/// ångström) gets a toy made for it from the 2PLV one, so every molecule behaves the same: weightless and heavily
/// damped, grabbable and scalable with the same gestures, shared over the network, with one sphere collider at its
/// outer radius, at the same ångström-to-meter scale as 2PLV and coloured by subunit type with 2PLV's chain colours.
/// The new toys are added to the network prefab list and to the molecule list in TunnelPrefabs.asset.
/// Runs from Tunnel > Molecules > Update Molecule Library, and by itself when a model is added under Assets/Molecules.
/// </summary>
static class MoleculeLibraryBuilder
{
    const string k_Root = "Assets/Molecules";
    const string k_TemplatePath = "Assets/Molecules/2PLV/Toy_2PLV_Networked.prefab";
    const string k_RegistryPath = "Assets/Tunnel/TunnelPrefabs.asset";
    const string k_NetworkListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";

    // 2PLV's surface reaches 167 Å from its centre and is 0.2 units in its toy, so this keeps true relative sizes.
    const float k_UnitsPerAngstrom = 0.2f / 167f;

    static readonly string[] k_ModelExtensions = { ".glb", ".gltf", ".obj", ".fbx" };

    [MenuItem("Tunnel/Molecules/Update Molecule Library")]
    public static void UpdateLibrary()
    {
        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
        if (registry == null)
        {
            Debug.LogError($"[Molecules] {k_RegistryPath} is missing");
            return;
        }

        var molecules = new List<TunnelPrefabRegistry.Molecule>();
        foreach (string folder in AssetDatabase.GetSubFolders(k_Root).OrderBy(f => f))
        {
            string name = Path.GetFileName(folder);
            var prefab = FindToy(folder) ?? CreateToy(folder, name);
            if (prefab == null)
                continue;
            molecules.Add(new TunnelPrefabRegistry.Molecule { name = name, prefab = prefab });
            AddToNetworkList(prefab);
        }

        registry.molecules = molecules;
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Molecules] library: {string.Join(", ", molecules.Select(m => m.name))}");
    }

    /// <summary>A prefab in the folder whose root is a network object (the molecule's toy), if there is one.</summary>
    static GameObject FindToy(string folder)
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab != null && prefab.GetComponent<NetworkObject>() != null)
                return prefab;
        }

        return null;
    }

    static GameObject CreateToy(string folder, string name)
    {
        string modelPath = AssetDatabase.FindAssets("", new[] { folder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => k_ModelExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
        var model = modelPath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(modelPath) : null;
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(k_TemplatePath);
        if (model == null || template == null)
            return null;

        // One combined mesh per kind of subunit (each distinct mesh in the model), centred and scaled like 2PLV.
        var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
        if (filters.Count == 0)
            return null;
        Matrix4x4 toRoot(Transform t) => model.transform.worldToLocalMatrix * t.localToWorldMatrix;
        var bounds = new Bounds(toRoot(filters[0].transform).MultiplyPoint3x4(filters[0].sharedMesh.vertices[0]), Vector3.zero);
        foreach (var f in filters)
        {
            var m = toRoot(f.transform);
            foreach (var v in f.sharedMesh.vertices)
                bounds.Encapsulate(m.MultiplyPoint3x4(v));
        }

        var normalize = Matrix4x4.Scale(Vector3.one * k_UnitsPerAngstrom) * Matrix4x4.Translate(-bounds.center);
        var groups = filters.GroupBy(f => f.sharedMesh).ToList();
        var meshes = new List<Mesh>();
        float radius = 0f;
        for (int g = 0; g < groups.Count; g++)
        {
            var combine = groups[g].Select(f => new CombineInstance { mesh = f.sharedMesh, transform = normalize * toRoot(f.transform) }).ToArray();
            int vertexCount = groups[g].Sum(f => f.sharedMesh.vertexCount);
            // The index format has to be chosen before combining: changing it afterwards drops the triangles.
            var mesh = new Mesh { name = $"{name}_Type{g + 1}", indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.CombineMeshes(combine, true, true);
            mesh.RecalculateBounds();
            foreach (var v in mesh.vertices)
                radius = Mathf.Max(radius, v.magnitude);
            meshes.Add(mesh);
        }

        string meshPath = $"{folder}/{name}_Meshes.asset";
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(meshes[0], meshPath);
        meshes[0].name = $"{name}_Type1"; // CreateAsset names the main object after the file
        for (int i = 1; i < meshes.Count; i++)
            AssetDatabase.AddObjectToAsset(meshes[i], meshPath);

        // The toy: a copy of 2PLV's with this molecule's meshes, collider and colour feedback.
        var toy = Object.Instantiate(template);
        toy.name = $"Toy_{name}_Networked";
        var templateVisual = template.transform.GetChild(0);
        var templateRenderers = templateVisual.GetComponentsInChildren<MeshRenderer>(true);
        var visual = toy.transform.Find(templateVisual.name);
        visual.name = $"{name}_Visual";
        for (int i = visual.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(visual.GetChild(i).gameObject);

        var renderers = new List<MeshRenderer>();
        for (int i = 0; i < meshes.Count; i++)
        {
            var part = new GameObject($"{name}_Type{i + 1}", typeof(MeshFilter), typeof(MeshRenderer));
            part.layer = visual.gameObject.layer;
            part.transform.SetParent(visual, false);
            part.GetComponent<MeshFilter>().sharedMesh = meshes[i];
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = templateRenderers[i % templateRenderers.Length].sharedMaterial;
            renderers.Add(renderer);
        }

        // Hover/select tint: one colour receiver per part; drop the template's spares.
        var helpers = toy.GetComponentsInChildren<MaterialPropertyBlockHelper>(true);
        for (int i = 0; i < helpers.Length; i++)
        {
            if (i < renderers.Count)
            {
                var so = new SerializedObject(helpers[i]);
                so.FindProperty("m_Renderer").objectReferenceValue = renderers[i];
                so.ApplyModifiedPropertiesWithoutUndo();
                helpers[i].gameObject.name = $"ColorAffordance_Type{i + 1}";
            }
            else
            {
                Object.DestroyImmediate(helpers[i].gameObject);
            }
        }

        toy.GetComponentInChildren<SphereCollider>(true).radius = radius;

        string prefabPath = $"{folder}/{toy.name}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(toy, prefabPath);
        Object.DestroyImmediate(toy);
        Debug.Log($"[Molecules] made {prefabPath} from {Path.GetFileName(modelPath)}: {meshes.Count} subunit type(s), " +
            $"{filters.Count} subunits, outer radius {radius / k_UnitsPerAngstrom:0} Å");
        return prefab;
    }

    static void AddToNetworkList(GameObject prefab)
    {
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_NetworkListPath);
        if (list == null || list.PrefabList.Any(p => p.Prefab == prefab))
            return;
        list.Add(new NetworkPrefab { Prefab = prefab });
        EditorUtility.SetDirty(list);
    }

    /// <summary>
    /// After scripts reload (or the Editor starts), catches models that arrived while it was busy: rebuilds the
    /// library if a molecule folder has no toy yet or the list doesn't match the folders.
    /// </summary>
    [InitializeOnLoadMethod]
    static void CheckAfterReload()
    {
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)
                return;
            var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
            if (registry == null || !AssetDatabase.IsValidFolder(k_Root))
                return;
            var folders = AssetDatabase.GetSubFolders(k_Root);
            bool stale = folders.Length != registry.molecules.Count
                || registry.molecules.Any(m => m.prefab == null)
                || folders.Any(f => FindToy(f) == null && AssetDatabase.FindAssets("", new[] { f })
                    .Any(g => k_ModelExtensions.Contains(Path.GetExtension(AssetDatabase.GUIDToAssetPath(g)).ToLowerInvariant())));
            if (stale)
                UpdateLibrary();
        };
    }

    /// <summary>Rebuilds the library when a model is added to, or anything is removed from, Assets/Molecules.</summary>
    class Watcher : AssetPostprocessor
    {
        static bool s_Pending;

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            bool relevant = imported.Concat(moved).Any(p => p.StartsWith(k_Root + "/") && k_ModelExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                || deleted.Concat(movedFrom).Any(p => p.StartsWith(k_Root + "/"));
            if (!relevant || s_Pending)
                return;
            s_Pending = true;
            EditorApplication.delayCall += () =>
            {
                s_Pending = false;
                UpdateLibrary();
            };
        }
    }
}
