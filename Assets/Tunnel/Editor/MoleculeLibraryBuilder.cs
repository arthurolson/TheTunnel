using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.AffordanceSystem.Rendering;

/// <summary>
/// Keeps the TS Menu's Molecules list in step with Assets/Molecules. The molecules are sorted into category folders,
/// Assets/Molecules/<category>/<molecule>, and the list shows them under a heading per category, in the order of
/// <see cref="TunnelPrefabRegistry.categories"/> (viral capsids, assemblies, proteins, ligands, chemicals, nucleic acids). Each
/// folder in a category folder is one molecule, named after its folder; a folder directly in Assets/Molecules that
/// holds a model itself is a molecule with no category (listed last, under "Other"). Loose files in Assets/Molecules
/// are never molecules. Adding a molecule is dropping a folder with a model into a category folder.
/// A folder with a networked toy prefab is used as it is; a folder with only a model (GLB, glTF, OBJ, FBX, in
/// ångström) gets a toy made for it from the 2PLV one, so every molecule behaves the same: weightless and heavily
/// damped, grabbable and scalable with the same gestures, shared over the network, at the same ångström-to-meter scale
/// as 2PLV. A surface (viral capsids, proteins, nucleic acids) is coloured by subunit type with 2PLV's chain colours
/// and kept no finer than <see cref="k_SurfaceDetail"/> Å; an assembly (such as a ribosome, with dozens of different
/// chains) is a surface too, but keeps the model's own colours, one per chain as Mol* exported them; atoms and bonds (ligands, chemicals) keep the model's own
/// colours, one per element (CPK), and all their detail. Its collider is the smallest that encloses the whole surface:
/// an oriented box or an ellipsoid, whichever has less volume (see <see cref="MoleculeColliderFit"/>); atoms and bonds
/// under <see cref="k_MinGrabSize"/> across at ×1 also get <see cref="MoleculeGrabSize"/>, so a hand or ray can still
/// take them.
/// A model too detailed for the headset (more than <see cref="k_MaxVertices"/> vertices in all, such as a whole virion)
/// is simplified first: its vertices are merged on a grid just coarse enough to bring it under that (see
/// <see cref="Simplify"/>), which keeps a molecular surface's shape down to a few ångströms.
/// The new toys are added to the network prefab list and to the molecule list in TunnelPrefabs.asset.
/// Runs from Tunnel > Molecules > Update Molecule Library, and by itself when a model is added under Assets/Molecules.
/// </summary>
static class MoleculeLibraryBuilder
{
    public const string k_Root = "Assets/Molecules";
    const string k_RegistryPath = "Assets/Tunnel/TunnelPrefabs.asset";
    const string k_NetworkListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";

    /// <summary>2PLV's toy: the template every molecule's toy (and several other builders' grabbable things) is made from.</summary>
    public static string TemplatePath => ToyPath("2PLV");

    // 2PLV's surface reaches 167 Å from its centre and is 0.2 units in its toy, so this keeps true relative sizes.
    const float k_UnitsPerAngstrom = 0.2f / 167f;

    static readonly string[] k_ModelExtensions = { ".glb", ".gltf", ".obj", ".fbx" };

    // The most vertices a molecule's toy has in all (the capsids have 40,000 to 60,000).
    const int k_MaxVertices = 200000;

    // A surface's finest detail (Å): finer models are merged on a grid this size (IgG's surface is about this coarse).
    const float k_SurfaceDetail = 2f;

    // Categories whose models are atoms and bonds, drawn in the model's own (element) colours at full detail.
    static readonly string[] k_AtomCategories = { "ligands", "chemicals" };

    // Categories of surfaces drawn in the model's own colours (one per chain, as Mol* exports them), not 2PLV's: an
    // assembly has too many different chains for 2PLV's few colours to tell apart.
    static readonly string[] k_OwnColourCategories = { "assemblies" };

    // The smallest a toy's collider is across at ×1 (meters in the headset) before it gets a minimum grab size.
    const float k_MinGrabSize = 0.02f;

    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";

    /// <summary>
    /// The Molecules list's Magnification slider in the TS Menu (TG_TunnelSegment): it counts the stops of
    /// <see cref="TunnelMoleculeMenu.k_Steps"/> (×1 to ×10 one at a time, then two at a time to ×20), with a tick label
    /// at each. Only the slider's range and its ticks change.
    /// </summary>
    [MenuItem("Tunnel/Molecules/Build Magnification Slider")]
    public static string BuildMagnificationSlider()
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var group = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Magnification" && t.Find("Ticks") != null);
            var slider = group != null ? group.GetComponentInChildren<UnityEngine.UI.Slider>(true) : null;
            if (slider == null)
                return "[Molecules] no Magnification slider in the TS Menu";
            var steps = TunnelMoleculeMenu.k_Steps;
            slider.minValue = 0f;
            slider.maxValue = steps.Length - 1;
            slider.wholeNumbers = true;
            slider.SetValueWithoutNotify(0f);
            var ticks = group.Find("Ticks");
            while (ticks.childCount > steps.Length)
                Object.DestroyImmediate(ticks.GetChild(ticks.childCount - 1).gameObject);
            while (ticks.childCount < steps.Length)
                Object.Instantiate(ticks.GetChild(ticks.childCount - 1).gameObject, ticks, false);
            for (int i = 0; i < steps.Length; i++)
            {
                var tick = (RectTransform)ticks.GetChild(i);
                tick.name = $"Tick {steps[i]}";
                tick.anchorMin = tick.anchorMax = new Vector2(i / (float)(steps.Length - 1), tick.anchorMin.y);
                tick.GetComponent<TMPro.TMP_Text>().text = steps[i].ToString();
            }
            PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath);
            string log = $"[Molecules] Magnification slider: {steps.Length} stops, ×{string.Join(", ×", steps)}";
            Debug.Log(log);
            return log;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

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
        foreach (var (category, folder) in MoleculeFolders())
        {
            string name = Path.GetFileName(folder);
            var prefab = FindToy(folder) ?? CreateToy(folder, name, category);
            if (prefab == null)
                continue;
            molecules.Add(new TunnelPrefabRegistry.Molecule { name = name, prefab = prefab, category = category });
            AddToNetworkList(prefab);
            RecordVolume(prefab);
        }

        registry.molecules = molecules;
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        Debug.Log("[Molecules] library: " + string.Join("; ", molecules.GroupBy(m => m.category)
            .Select(g => $"{TunnelPrefabRegistry.CategoryHeading(g.Key)}: {string.Join(", ", g.Select(m => m.name))}")));
    }

    /// <summary>
    /// Every molecule folder with its category, in the order the Molecules list shows them: by category (see
    /// <see cref="TunnelPrefabRegistry.CategoryOrder"/>), then by name. A folder in Assets/Molecules is a category
    /// unless it holds a model or a toy itself (an older, uncategorised molecule); the category folders' own folders
    /// are the molecules.
    /// </summary>
    public static List<(string category, string folder)> MoleculeFolders()
    {
        var found = new List<(string category, string folder)>();
        if (!AssetDatabase.IsValidFolder(k_Root))
            return found;
        foreach (string top in AssetDatabase.GetSubFolders(k_Root))
        {
            string topName = Path.GetFileName(top);
            bool isCategory = TunnelPrefabRegistry.categories.Contains(topName.ToLowerInvariant()) || !HoldsMolecule(top);
            if (!isCategory)
            {
                found.Add(("", top));
                continue;
            }
            foreach (string folder in AssetDatabase.GetSubFolders(top))
                found.Add((topName, folder));
        }

        return found
            .OrderBy(m => TunnelPrefabRegistry.CategoryOrder(m.category))
            .ThenBy(m => m.category, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => Path.GetFileName(m.folder), System.StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether a folder itself (not its sub-folders) holds a model or a networked toy.</summary>
    static bool HoldsMolecule(string folder) =>
        AssetDatabase.FindAssets("", new[] { folder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => Path.GetDirectoryName(p).Replace('\\', '/') == folder)
            .Any(p => k_ModelExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())
                || (p.EndsWith(".prefab") && AssetDatabase.LoadAssetAtPath<GameObject>(p)?.GetComponent<NetworkObject>() != null));

    /// <summary>The folder of the molecule of this name (such as "2PLV" or "IGG"), whatever category it is in; null if
    /// there is none.</summary>
    public static string FolderOf(string molecule) =>
        MoleculeFolders().Select(m => m.folder).FirstOrDefault(f => Path.GetFileName(f) == molecule);

    /// <summary>The asset path of the toy of the molecule of this name, whatever category it is in; null if there is
    /// none yet.</summary>
    public static string ToyPath(string molecule)
    {
        string folder = FolderOf(molecule);
        var toy = folder != null ? FindToy(folder) : null;
        return toy != null ? AssetDatabase.GetAssetPath(toy) : null;
    }

    /// <summary>
    /// How the library placed a molecule's model in its toy: the matrix that takes a point in the model's first mesh
    /// (as Unity imported it) to the toy's own coordinates (centred on the model's bounds, at the ångström-to-meter
    /// scale), as <see cref="CreateToy"/> does. False if the folder has no model.
    /// </summary>
    public static bool ModelToToy(string folder, out Matrix4x4 meshToToy, out string modelPath, System.Func<MeshFilter, bool> include = null)
    {
        meshToToy = Matrix4x4.identity;
        modelPath = AssetDatabase.FindAssets("", new[] { folder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => k_ModelExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
        var model = modelPath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(modelPath) : null;
        var filters = model != null ? model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && (include == null || include(f))).ToList() : null;
        if (filters == null || filters.Count == 0)
            return false;
        Matrix4x4 toRoot(Transform t) => model.transform.worldToLocalMatrix * t.localToWorldMatrix;
        var bounds = new Bounds(toRoot(filters[0].transform).MultiplyPoint3x4(filters[0].sharedMesh.vertices[0]), Vector3.zero);
        foreach (var f in filters)
        {
            var m = toRoot(f.transform);
            foreach (var v in f.sharedMesh.vertices)
                bounds.Encapsulate(m.MultiplyPoint3x4(v));
        }
        meshToToy = Matrix4x4.Scale(Vector3.one * k_UnitsPerAngstrom) * Matrix4x4.Translate(-bounds.center) * toRoot(filters[0].transform);
        return true;
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

    /// <summary>
    /// Makes a molecule's toy again from only some parts of its model (the ligand without the residue it is bonded
    /// to in the crystal, say), keeping the toy's prefab (and so its network id); its frame is then those parts'
    /// (see <see cref="ModelToToy"/> with the same parts). Used by Tunnel > Movie > Build Ligand Binding.
    /// </summary>
    public static GameObject RebuildToy(string folder, string category, System.Func<MeshFilter, bool> include)
    {
        var prefab = CreateToy(folder, Path.GetFileName(folder), category, include);
        if (prefab != null)
        {
            AddToNetworkList(prefab);
            RecordVolume(prefab);
        }
        return prefab;
    }

    /// <summary>
    /// A model's parts as atoms in their own colours: one mesh per colour (each made from the template molecule's
    /// material in that colour), in whatever frame toToy gives. Used for a ligand's toy and for parts shown only
    /// while it is bound.
    /// </summary>
    public static List<(Mesh mesh, Material material)> ColouredParts(List<MeshFilter> filters, System.Func<MeshFilter, Matrix4x4> toToy, string name)
    {
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
        var material = template.transform.GetChild(0).GetComponentsInChildren<MeshRenderer>(true)[0].sharedMaterial;
        return SplitByColour(filters, toToy, name, new Dictionary<string, Color>())
            .Select(c => (c.mesh, ColouredMaterial(material, c.colour, c.mesh.name)))
            .ToList();
    }

    static GameObject CreateToy(string folder, string name, string category, System.Func<MeshFilter, bool> include = null)
    {
        string modelPath = AssetDatabase.FindAssets("", new[] { folder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => k_ModelExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
        var model = modelPath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(modelPath) : null;
        string templatePath = TemplatePath;
        var template = templatePath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(templatePath) : null;
        if (model == null || template == null)
        {
            Debug.LogWarning($"[Molecules] {folder}: {(model == null ? "no model" : "no 2PLV toy to copy")}, so no toy");
            return null;
        }

        var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && (include == null || include(f))).ToList();
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

        // Centred and scaled like 2PLV.
        var normalize = Matrix4x4.Scale(Vector3.one * k_UnitsPerAngstrom) * Matrix4x4.Translate(-bounds.center);
        bool atoms = k_AtomCategories.Contains((category ?? "").ToLowerInvariant());
        bool ownColours = atoms || k_OwnColourCategories.Contains((category ?? "").ToLowerInvariant());
        var templateMaterials = template.transform.GetChild(0).GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).ToArray();
        List<Mesh> meshes;
        List<Material> materials;
        if (ownColours)
        {
            // Atoms and bonds, or an assembly: one mesh per colour in the model (an element each in CPK colours, or a
            // chain each), each drawn in the template's material in that colour.
            var colours = SplitByColour(filters, f => normalize * toRoot(f.transform), name, MtlColours(modelPath));
            meshes = colours.Select(c => c.mesh).ToList();
            materials = colours.Select(c => ColouredMaterial(templateMaterials[0], c.colour, $"{name}_{ColorUtility.ToHtmlStringRGB(c.colour)}")).ToList();
        }
        else
        {
            // A surface: one combined mesh per kind of subunit (each distinct mesh in the model), in 2PLV's chain colours.
            var groups = filters.GroupBy(f => f.sharedMesh).ToList();
            meshes = new List<Mesh>();
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
            materials = meshes.Select((m, i) => templateMaterials[i % templateMaterials.Length]).ToList();
        }

        string simplified = "";
        int totalVertices = meshes.Sum(m => m.vertexCount);
        int totalTriangles = meshes.Sum(m => m.triangles.Length / 3);
        if (!atoms)
        {
            // No finer than the library's surface detail, and under the vertex budget: the finest grid that does both,
            // all its parts alike (a surface's vertices go as one over the cell's area, so start a little finer than
            // that suggests).
            float cell = Mathf.Max(k_SurfaceDetail * k_UnitsPerAngstrom,
                0.7f * Mathf.Sqrt(totalVertices / (float)k_MaxVertices) * k_UnitsPerAngstrom);
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
            simplified = $"simplified from {totalVertices:N0} vertices ({totalTriangles:N0} triangles) to {meshes.Sum(m => m.vertexCount):N0} " +
                $"({meshes.Sum(m => m.triangles.Length / 3):N0} triangles) on a {cell / k_UnitsPerAngstrom:0.0} Å grid; ";
        }
        else
        {
            simplified = $"{totalVertices:N0} vertices, {totalTriangles:N0} triangles in {meshes.Count} colours; ";
        }
        float radius = 0f;
        foreach (var mesh in meshes)
            foreach (var v in mesh.vertices)
                radius = Mathf.Max(radius, v.magnitude);

        string meshPath = $"{folder}/{name}_Meshes.asset";
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(meshes[0], meshPath);
        meshes[0].name = atoms ? meshes[0].name : $"{name}_Type1"; // CreateAsset names the main object after the file
        for (int i = 1; i < meshes.Count; i++)
            AssetDatabase.AddObjectToAsset(meshes[i], meshPath);
        foreach (var material in materials.Where(m => m != null && !AssetDatabase.Contains(m)))
            AssetDatabase.AddObjectToAsset(material, meshPath);

        // The toy: a copy of 2PLV's with this molecule's meshes, collider and colour feedback.
        var toy = Object.Instantiate(template);
        if (toy.TryGetComponent(out CapsidSites sites)) // 2PLV's own binding sites (Tunnel > Movie)
            Object.DestroyImmediate(sites);
        toy.name = $"Toy_{name}_Networked";
        var templateVisual = template.transform.GetChild(0);
        var visual = toy.transform.Find(templateVisual.name);
        visual.name = $"{name}_Visual";
        for (int i = visual.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(visual.GetChild(i).gameObject);

        var renderers = new List<MeshRenderer>();
        for (int i = 0; i < meshes.Count; i++)
        {
            var part = new GameObject(meshes[i].name, typeof(MeshFilter), typeof(MeshRenderer));
            part.layer = visual.gameObject.layer;
            part.transform.SetParent(visual, false);
            part.GetComponent<MeshFilter>().sharedMesh = meshes[i];
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = materials[i];
            renderers.Add(renderer);
        }

        // Hover/select tint: one colour receiver per part (copies of the first if the template has too few); drop the
        // template's spares.
        var helpers = toy.GetComponentsInChildren<MaterialPropertyBlockHelper>(true).ToList();
        while (helpers.Count < renderers.Count && helpers.Count > 0)
        {
            var copy = Object.Instantiate(helpers[0].gameObject, helpers[0].transform.parent, false);
            helpers.Add(copy.GetComponent<MaterialPropertyBlockHelper>());
        }
        for (int i = 0; i < helpers.Count; i++)
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

        // Atoms and bonds too small to take hold of easily at ×1 (a ligand is a few millimetres): the collider is kept at
        // least k_MinGrabSize across in the headset. Surfaces keep their fitted collider, as every other molecule.
        var fittedSize = fit.kind == MoleculeColliderFit.Kind.Box ? fit.size : fit.size * 2f;
        float toMeters = Mathf.Abs(template.transform.localScale.x);
        string grab = "";
        if (atoms && Mathf.Min(fittedSize.x, Mathf.Min(fittedSize.y, fittedSize.z)) * toMeters < k_MinGrabSize)
        {
            var grabSize = toy.AddComponent<MoleculeGrabSize>();
            grabSize.Configure(holder.transform, fittedSize, k_MinGrabSize);
            grabSize.Fit(); // at ×1, so the prefab's collider is already the size it will be when placed
            grab = $"; under {k_MinGrabSize * 100f:0} cm at ×1, so its collider is kept at least that across";
        }
        var size = fittedSize * toMeters * 100f;

        string prefabPath = $"{folder}/{toy.name}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(toy, prefabPath);
        Object.DestroyImmediate(toy);
        Debug.Log($"[Molecules] made {prefabPath} from {Path.GetFileName(modelPath)}: {simplified}{meshes.Count} subunit type(s), " +
            $"{filters.Count} subunits, outer radius {radius / k_UnitsPerAngstrom:0} Å; collider {fit.kind} " +
            $"(box {box.volume / sphereVolume:P0}, ellipsoid {ellipsoid.volume / sphereVolume:P0} of the outer sphere's volume); " +
            $"{size.x:0.00} × {size.y:0.00} × {size.z:0.00} cm at ×1{grab}");
        return prefab;
    }

    /// <summary>
    /// Atoms and bonds split by colour: each part of the model takes its material's colour (as an OBJ's .mtl gives
    /// each element), or each triangle its vertices' colour where the model has vertex colours (as a glTF from Mol*
    /// can); one mesh per colour, carbon's grey first if there is one, in the toy's frame.
    /// </summary>
    static List<(Color colour, Mesh mesh)> SplitByColour(List<MeshFilter> filters, System.Func<MeshFilter, Matrix4x4> toToy, string name,
        Dictionary<string, Color> mtlColours)
    {
        var parts = new Dictionary<Color32, (List<Vector3> v, List<Vector3> n, List<int> t, Dictionary<(int, int), int> index)>();
        for (int f = 0; f < filters.Count; f++)
        {
            var filter = filters[f];
            var mesh = filter.sharedMesh;
            var m = toToy(filter);
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var colors = mesh.colors32;
            bool vertexColours = colors != null && colors.Length == vertices.Length;
            var shared = filter.TryGetComponent(out MeshRenderer renderer) ? renderer.sharedMaterials : new Material[0];
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var material = shared.Length > 0 ? shared[Mathf.Min(sub, shared.Length - 1)] : null;
                Color32 materialColour = material == null ? Color.white
                    : mtlColours.TryGetValue(material.name, out var kd) ? kd
                    : material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.color;
                var triangles = mesh.GetTriangles(sub);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    // A glTF's vertex colours are linear (Mol* writes them so); a material's colour reads as shown.
                    Color32 c = vertexColours ? ((Color)colors[triangles[i]]).gamma : materialColour;
                    c.a = 255;
                    if (!parts.TryGetValue(c, out var part))
                        parts[c] = part = (new List<Vector3>(), new List<Vector3>(), new List<int>(), new Dictionary<(int, int), int>());
                    for (int k = 0; k < 3; k++)
                    {
                        int source = triangles[i + k];
                        if (!part.index.TryGetValue((f, source), out int index))
                        {
                            index = part.v.Count;
                            part.index[(f, source)] = index;
                            part.v.Add(m.MultiplyPoint3x4(vertices[source]));
                            part.n.Add(normals.Length == vertices.Length ? m.MultiplyVector(normals[source]).normalized : Vector3.zero);
                        }
                        part.t.Add(index);
                    }
                }
            }
        }

        // Carbon (grey, the most common) first, then the others from most to least.
        return parts
            .OrderByDescending(p => Mathf.Abs(p.Key.r - p.Key.g) < 8 && Mathf.Abs(p.Key.g - p.Key.b) < 8 ? 1 : 0)
            .ThenByDescending(p => p.Value.t.Count)
            .Select(p =>
            {
                var mesh = new Mesh { name = $"{name}_{ColorUtility.ToHtmlStringRGB(p.Key)}", indexFormat = p.Value.v.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(p.Value.v);
                mesh.SetTriangles(p.Value.t, 0);
                if (p.Value.n.All(n => n != Vector3.zero))
                    mesh.SetNormals(p.Value.n);
                else
                    mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return ((Color)p.Key, mesh);
            })
            .ToList();
    }

    /// <summary>
    /// The colours an OBJ's .mtl file gives its materials (Kd), by name, read as written: Unity's OBJ import takes them
    /// as linear and so shows them paler than Mol* made them. Empty for other models.
    /// </summary>
    static Dictionary<string, Color> MtlColours(string modelPath)
    {
        var colours = new Dictionary<string, Color>();
        if (!modelPath.EndsWith(".obj", System.StringComparison.OrdinalIgnoreCase))
            return colours;
        string folder = Path.GetDirectoryName(modelPath);
        foreach (string line in File.ReadLines(modelPath).Where(l => l.StartsWith("mtllib ")).Take(4))
        {
            string mtl = Path.Combine(folder, line.Substring(7).Trim());
            if (!File.Exists(mtl))
                continue;
            string current = null;
            foreach (string raw in File.ReadLines(mtl))
            {
                var parts = raw.Trim().Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "newmtl")
                    current = parts[1];
                else if (parts.Length >= 4 && parts[0] == "Kd" && current != null
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r)
                    && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g)
                    && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
                    colours[current] = new Color(r, g, b, 1f);
            }
        }
        return colours;
    }

    /// <summary>The template's material in another colour (an element's, for atoms and bonds).</summary>
    static Material ColouredMaterial(Material template, Color colour, string name)
    {
        var material = new Material(template) { name = name };
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", colour);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.Lerp(colour, Color.white, 0.35f));
        return material;
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
    /// library if a molecule folder has no toy yet or the list doesn't match the folders (or their categories).
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
            var found = MoleculeFolders();
            var folders = found.Select(m => m.folder).ToList();
            var categories = found.Select(m => m.category).ToList();
            bool stale = folders.Count != registry.molecules.Count
                || registry.molecules.Any(m => m.prefab == null)
                || registry.molecules.Select(m => m.category ?? "").Where((c, i) => i >= categories.Count || c != categories[i]).Any()
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
