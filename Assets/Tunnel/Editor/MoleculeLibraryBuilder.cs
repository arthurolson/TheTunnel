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
/// <see cref="MoleculeToyGeometry.Simplify"/>), which keeps a molecular surface's shape down to a few ångströms. Molecules named in
/// <see cref="k_FullDetail"/> (Hi-res Ribosome) are the exception: they keep every vertex of their model.
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
    const float k_UnitsPerAngstrom = MoleculeToyGeometry.k_UnitsPerAngstrom;

    static readonly string[] k_ModelExtensions = { ".glb", ".gltf", ".obj", ".fbx" };

    // The most vertices a molecule's toy has in all (see MoleculeToyGeometry, which makes every toy's geometry).
    const int k_MaxVertices = MoleculeToyGeometry.k_MaxVertices;

    // A surface's finest detail (Å): finer models are merged on a grid this size (IgG's surface is about this coarse).
    const float k_SurfaceDetail = MoleculeToyGeometry.k_SurfaceDetail;

    // Categories whose models are atoms and bonds, drawn in the model's own (element) colours at full detail.
    static readonly string[] k_AtomCategories = MoleculeToyGeometry.k_AtomCategories;

    // Categories of surfaces drawn in the model's own colours (one per chain, as Mol* exports them), not 2PLV's: an
    // assembly has too many different chains for 2PLV's few colours to tell apart.
    static readonly string[] k_OwnColourCategories = MoleculeToyGeometry.k_OwnColourCategories;

    // Molecules shown with every vertex of their model, never merged on the surface-detail grid or held to the vertex
    // budget: a high-resolution version kept beside its everyday one (Hi-res Ribosome beside E. coli 50S ribosome, the
    // same Gaussian-surface export at about 563k vertices), for looking closely at a few copies at a time.
    static readonly string[] k_FullDetail = { "Hi-res Ribosome" };

    // The smallest a toy's collider is across at ×1 (meters in the headset) before it gets a minimum grab size.
    const float k_MinGrabSize = MoleculeToyGeometry.k_MinGrabSize;

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

    const string k_RepositoryToyPath = "Assets/Tunnel/Toy_Repository_Networked.prefab";

    /// <summary>
    /// Makes the toy every molecule from the asset repository becomes (see <see cref="RepositoryMolecule"/>): 2PLV's
    /// toy with no parts (they are made in the headset from the molecule's model), its sphere collider holding the
    /// place, its tint helper waiting inactive, 2PLV's chain materials to colour surfaces with. Adds it to the network
    /// prefab list and to TunnelPrefabs.asset. Run once; molecules added to the repository then need no new build.
    /// </summary>
    [MenuItem("Tunnel/Molecules/Build Repository Toy")]
    public static string BuildRepositoryToy()
    {
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
        if (template == null)
            return "[Molecules] no 2PLV toy to copy";
        var toy = Object.Instantiate(template);
        if (toy.TryGetComponent(out CapsidSites sites))
            Object.DestroyImmediate(sites);
        toy.name = "Toy_Repository_Networked";
        var templateVisual = template.transform.GetChild(0);
        var chainMaterials = templateVisual.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).ToArray();
        var visual = toy.transform.Find(templateVisual.name);
        visual.name = "Repository_Visual";
        for (int i = visual.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(visual.GetChild(i).gameObject);
        var helpers = toy.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.AffordanceSystem.Rendering.MaterialPropertyBlockHelper>(true);
        for (int i = 0; i < helpers.Length; i++)
        {
            if (i > 0)
            {
                Object.DestroyImmediate(helpers[i].gameObject);
                continue;
            }
            helpers[i].rendererTarget = null;
            helpers[i].gameObject.SetActive(false); // woken when the toy's parts are made
        }
        var repository = toy.GetComponent<RepositoryMolecule>();
        if (repository == null)
            repository = toy.AddComponent<RepositoryMolecule>();
        repository.Configure(chainMaterials, Mathf.Abs(template.transform.localScale.x), visual);
        var prefab = PrefabUtility.SaveAsPrefabAsset(toy, k_RepositoryToyPath);
        Object.DestroyImmediate(toy);
        AddToNetworkList(prefab);
        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
        registry.repositoryMolecule = prefab;
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        string log = $"[Molecules] made {k_RepositoryToyPath}: 2PLV's toy without parts, {chainMaterials.Length} chain materials, in the network list and the registry";
        Debug.Log(log);
        return log;
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
        return MoleculeToyGeometry.SplitByColour(filters, toToy, name, new Dictionary<string, Color>())
            .Select(c => (c.mesh, MoleculeToyGeometry.ColouredMaterial(material, c.colour, c.mesh.name)))
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

        // The geometry, colours and collider, made as every toy's are (in the headset too, for repository molecules).
        var templateMaterials = template.transform.GetChild(0).GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).ToArray();
        var geometry = MoleculeToyGeometry.Build(model.transform, filters, name, category, templateMaterials, k_FullDetail.Contains(name), MtlColours(modelPath));
        var meshes = geometry.meshes;

        string meshPath = $"{folder}/{name}_Meshes.asset";
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(meshes[0], meshPath);
        meshes[0].name = geometry.atoms ? meshes[0].name : $"{name}_Type1"; // CreateAsset names the main object after the file
        for (int i = 1; i < meshes.Count; i++)
            AssetDatabase.AddObjectToAsset(meshes[i], meshPath);
        foreach (var material in geometry.materials.Where(m => m != null && !AssetDatabase.Contains(m)))
            AssetDatabase.AddObjectToAsset(material, meshPath);

        // The toy: a copy of 2PLV's with this molecule's meshes, collider and colour feedback.
        var toy = Object.Instantiate(template);
        if (toy.TryGetComponent(out CapsidSites sites)) // 2PLV's own binding sites (Tunnel > Movie)
            Object.DestroyImmediate(sites);
        toy.name = $"Toy_{name}_Networked";
        var templateVisual = template.transform.GetChild(0);
        var visual = toy.transform.Find(templateVisual.name);
        visual.name = $"{name}_Visual";
        float toMeters = Mathf.Abs(template.transform.localScale.x);
        string report = MoleculeToyGeometry.Assemble(toy, visual, geometry, name, toMeters, hull => AssetDatabase.AddObjectToAsset(hull, meshPath));

        string prefabPath = $"{folder}/{toy.name}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(toy, prefabPath);
        Object.DestroyImmediate(toy);
        Debug.Log($"[Molecules] made {prefabPath} from {Path.GetFileName(modelPath)}: {geometry.detail}{meshes.Count} subunit type(s), " +
            $"{geometry.subunits} subunits, {report}");
        return prefab;
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

    /// <summary>
    /// Stores the volume enclosed by the molecule's surface (toy units) on its <see cref="MoleculeGravity"/>, which
    /// weighs it by that volume: each surface mesh's signed volume, so a hollow shell counts only its protein.
    /// </summary>
    static void RecordVolume(GameObject prefab)
    {
        var physics = prefab.GetComponent<MoleculeGravity>();
        if (physics == null)
            return;
        double volume = MoleculeToyGeometry.EnclosedVolume(prefab);

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
