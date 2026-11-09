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
/// damped, grabbable and scalable with the same gestures, shared over the network, at the same ångström-to-meter scale
/// as 2PLV and coloured by subunit type with 2PLV's chain colours. Its collider is the smallest that encloses the
/// whole surface: an oriented box or an ellipsoid, whichever has less volume (see <see cref="MoleculeColliderFit"/>).
/// A model too detailed for the headset (more than <see cref="k_MaxVertices"/> vertices in all, such as a whole virion)
/// is simplified first: its vertices are merged on a grid just coarse enough to bring it under that (see
/// <see cref="Simplify"/>), which keeps a molecular surface's shape down to a few ångströms.
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

    // The most vertices a molecule's toy has in all (the capsids have 40,000 to 60,000).
    const int k_MaxVertices = 200000;

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
            RecordVolume(prefab);
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
            meshes.Add(mesh);
        }

        string simplified = "";
        int totalVertices = meshes.Sum(m => m.vertexCount);
        if (totalVertices > k_MaxVertices)
        {
            // The finest grid that brings it under the budget, all its parts alike (a surface's vertices go as one over the
            // cell's area, so start a little finer than that suggests).
            float cell = 0.7f * Mathf.Sqrt(totalVertices / (float)k_MaxVertices) * k_UnitsPerAngstrom;
            List<Mesh> reduced;
            while (true)
            {
                reduced = meshes.Select(m => Simplify(m, cell)).ToList();
                if (reduced.Sum(m => m.vertexCount) <= k_MaxVertices)
                    break;
                foreach (var m in reduced)
                    Object.DestroyImmediate(m);
                cell *= 1.2f;
            }
            foreach (var m in meshes)
                Object.DestroyImmediate(m);
            meshes = reduced;
            simplified = $"simplified from {totalVertices:N0} to {meshes.Sum(m => m.vertexCount):N0} vertices ({meshes.Sum(m => m.triangles.Length / 3):N0} triangles) " +
                $"on a {cell / k_UnitsPerAngstrom:0.0} Å grid; ";
        }
        foreach (var mesh in meshes)
            foreach (var v in mesh.vertices)
                radius = Mathf.Max(radius, v.magnitude);

        string meshPath = $"{folder}/{name}_Meshes.asset";
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(meshes[0], meshPath);
        meshes[0].name = $"{name}_Type1"; // CreateAsset names the main object after the file
        for (int i = 1; i < meshes.Count; i++)
            AssetDatabase.AddObjectToAsset(meshes[i], meshPath);

        // The toy: a copy of 2PLV's with this molecule's meshes, collider and colour feedback.
        var toy = Object.Instantiate(template);
        if (toy.TryGetComponent(out CapsidSites sites)) // 2PLV's own binding sites (Tunnel > Movie)
            Object.DestroyImmediate(sites);
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

        // The tightest collider around every vertex: an oriented box or an ellipsoid, whichever is smaller.
        var points = meshes.SelectMany(m => m.vertices).ToList();
        var box = MoleculeColliderFit.Box(points);
        var ellipsoid = MoleculeColliderFit.Ellipsoid(points);
        var fit = box.volume <= ellipsoid.volume ? box : ellipsoid;
        var sphere = toy.GetComponentInChildren<SphereCollider>(true);
        var holder = sphere.gameObject;
        Object.DestroyImmediate(sphere);
        holder.transform.localPosition = fit.center;
        holder.transform.localRotation = fit.rotation;
        holder.transform.localScale = Vector3.one;
        if (fit.kind == MoleculeColliderFit.Kind.Box)
        {
            holder.AddComponent<BoxCollider>().size = fit.size;
        }
        else
        {
            var hull = MoleculeColliderFit.EllipsoidMesh(fit.size, $"{name}_Collider");
            AssetDatabase.AddObjectToAsset(hull, meshPath);
            var collider = holder.AddComponent<MeshCollider>();
            collider.sharedMesh = hull;
            collider.convex = true;
        }
        float sphereVolume = 4f / 3f * Mathf.PI * radius * radius * radius;

        string prefabPath = $"{folder}/{toy.name}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(toy, prefabPath);
        Object.DestroyImmediate(toy);
        Debug.Log($"[Molecules] made {prefabPath} from {Path.GetFileName(modelPath)}: {simplified}{meshes.Count} subunit type(s), " +
            $"{filters.Count} subunits, outer radius {radius / k_UnitsPerAngstrom:0} Å; collider {fit.kind} " +
            $"(box {box.volume / sphereVolume:P0}, ellipsoid {ellipsoid.volume / sphereVolume:P0} of the outer sphere's volume)");
        return prefab;
    }

    /// <summary>
    /// A coarser copy of a mesh: every vertex in the same grid cell becomes one, at their average position (and normal),
    /// and the triangles that shrink to a line or a point go. On a smooth molecular surface this keeps its shape at the
    /// grid's scale.
    /// </summary>
    static Mesh Simplify(Mesh source, float cell)
    {
        var vertices = source.vertices;
        var normals = source.normals;
        bool hasNormals = normals != null && normals.Length == vertices.Length;
        var cellOf = new Dictionary<long, int>();
        var remap = new int[vertices.Length];
        var sums = new List<Vector3>();
        var normalSums = new List<Vector3>();
        var counts = new List<int>();
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            long key = ((long)(Mathf.FloorToInt(v.x / cell) + 1000000) << 42) | ((long)(Mathf.FloorToInt(v.y / cell) + 1000000) << 21) | (long)(Mathf.FloorToInt(v.z / cell) + 1000000);
            if (!cellOf.TryGetValue(key, out int index))
            {
                index = sums.Count;
                cellOf[key] = index;
                sums.Add(Vector3.zero);
                normalSums.Add(Vector3.zero);
                counts.Add(0);
            }
            remap[i] = index;
            sums[index] += v;
            if (hasNormals)
                normalSums[index] += normals[i];
            counts[index]++;
        }

        var positions = new Vector3[sums.Count];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = sums[i] / counts[i];
        var triangles = new List<int>();
        var seen = new HashSet<(int, int, int)>();
        var sourceTriangles = source.triangles;
        for (int t = 0; t < sourceTriangles.Length; t += 3)
        {
            int a = remap[sourceTriangles[t]], b = remap[sourceTriangles[t + 1]], c = remap[sourceTriangles[t + 2]];
            if (a == b || b == c || a == c)
                continue;
            // The same triangle once (in whichever order its corners came first).
            var key = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
            if (!seen.Add(key))
                continue;
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        var mesh = new Mesh { name = source.name, indexFormat = positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.SetVertices(positions);
        mesh.SetTriangles(triangles, 0);
        if (hasNormals)
        {
            var averaged = new Vector3[positions.Length];
            for (int i = 0; i < averaged.Length; i++)
                averaged[i] = normalSums[i].normalized;
            mesh.SetNormals(averaged);
        }
        else
        {
            mesh.RecalculateNormals();
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Stores the volume enclosed by the molecule's surface (toy units) on its <see cref="MoleculeGravity"/>, which
    /// weighs it by that volume: each surface mesh's signed volume, so a hollow shell counts only its protein.
    /// </summary>
    static void RecordVolume(GameObject prefab)
    {
        var physics = prefab.GetComponent<MoleculeGravity>();
        if (physics == null)
            return;
        double volume = 0;
        var toRoot = prefab.transform.worldToLocalMatrix;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = filter.sharedMesh;
            if (mesh == null)
                continue;
            var m = toRoot * filter.transform.localToWorldMatrix;
            var v = mesh.vertices;
            var t = mesh.triangles;
            double signed = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                var a = m.MultiplyPoint3x4(v[t[i]]);
                var b = m.MultiplyPoint3x4(v[t[i + 1]]);
                var c = m.MultiplyPoint3x4(v[t[i + 2]]);
                signed += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            }
            volume += System.Math.Abs(signed);
        }

        if (Mathf.Abs(physics.volume - (float)volume) <= 1e-4f * (float)volume)
            return;
        var so = new SerializedObject(physics);
        so.FindProperty("m_Volume").floatValue = (float)volume;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(prefab);
        Debug.Log($"[Molecules] {prefab.name}: enclosed volume {volume / System.Math.Pow(k_UnitsPerAngstrom, 3):N0} Å³");
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
