using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Makes the containers' Antibody action, their 3D movie (see <see cref="TunnelMovie"/>):
/// the capsids' binding sites (<see cref="CapsidSites"/>) on the symmetry axes found from each capsid's own surface
/// (2-folds for TBSV, 5-folds for poliovirus, 3-folds for Zika) and on SARS-CoV-2's spikes; the antibody's Fab tips
/// (<see cref="AntibodyBinder"/>) found from its chains; the movie's menu (<see cref="TunnelMoviePanel"/>, from the
/// TS Menu's rows and the browser's keyboard, with a bar along its top to carry it by); and then the containers again,
/// which carry the movie (Tunnel > Containers > Build Containers).
/// It also makes the Ligand binding movie's parts (see <see cref="BuildLigandBinding"/>): EGFR's capture sphere and its
/// ligand's bound pose, from the crystal structure 9S3X, and the ligand's binder.
/// Runs from Tunnel > Movie > Build 3D Movie (everything) or Tunnel > Movie > Build Ligand Binding (EGFR, its ligand
/// and the menu only).
/// </summary>
static class MovieBuilder
{
    const string k_Folder = "Assets/Tunnel/Movie";
    const string k_PanelPath = k_Folder + "/Movie_Panel.prefab";
    static string k_AntibodyPath => MoleculeLibraryBuilder.ToyPath("IGG"); // in Assets/Molecules/proteins
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_BrowserPath = "Assets/Tunnel/BrowserPanel.prefab";

    // The capsids and the symmetry axes their sites stand on: 2 = 2-folds, 3 = 3-folds, 5 = 5-folds.
    static readonly (string name, int fold)[] k_Capsids = { ("TBSV", 2), ("2PLV", 5), ("Zika", 3) };

    const float k_UnitsPerAngstrom = 0.2f / 167f; // as the molecule library
    const float k_SiteDiameterFraction = 0.1f;   // of the capsid's diameter

    const float k_CanvasScale = 0.0004f;                   // meters per canvas unit, as the palette
    static readonly Vector2 k_CanvasSize = new(620f, 990f); // 25 x 40 cm

    // The bar along the menu's top that hands take it by (meters): its size, and the room it takes above the canvas.
    static readonly Vector3 k_BarSize = new(0.16f, 0.018f, 0.01f);
    const float k_BarSpace = 0.035f;

    [MenuItem("Tunnel/Movie/Build 3D Movie")]
    public static string Build() => BuilderScratchScene.Run(BuildAll);

    [MenuItem("Tunnel/Movie/Build Ligand Binding")]
    public static string BuildLigandMovie() => BuilderScratchScene.Run(() =>
    {
        if (!AssetDatabase.IsValidFolder(k_Folder))
            AssetDatabase.CreateFolder("Assets/Tunnel", "Movie");
        string log = $"[Movie] {BuildLigandBinding(SiteMaterial())}; {BuildPanel()}";
        AssetDatabase.SaveAssets();
        Debug.Log(log);
        return log;
    });

    static string BuildAll()
    {
        if (!AssetDatabase.IsValidFolder(k_Folder))
            AssetDatabase.CreateFolder("Assets/Tunnel", "Movie");
        var log = new StringBuilder("[Movie] ");
        var siteMaterial = SiteMaterial();
        var antibody = AssetDatabase.LoadAssetAtPath<GameObject>(k_AntibodyPath);
        foreach (var (name, fold) in k_Capsids)
            log.Append(BuildCapsid(name, fold, antibody, siteMaterial)).Append("; ");
        log.Append(BuildVirion(antibody, siteMaterial)).Append("; ");
        log.Append(BuildAntibody()).Append("; ");
        log.Append(BuildLigandBinding(siteMaterial)).Append("; ");
        log.Append(BuildPanel()).Append("; ");
        AssetDatabase.SaveAssets();
        log.Append(ContainerBuilder.Build());
        Debug.Log(log.ToString());
        return log.ToString();
    }

    // Shown sites: see-through yellow.
    static Material SiteMaterial()
    {
        string path = $"{k_Folder}/BindingSite.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "BindingSite" };
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetColor("_BaseColor", new Color(1f, 0.85f, 0.15f, 0.45f));
        material.SetFloat("_Surface", 1f); // transparent
        material.SetFloat("_Blend", 0f);   // alpha
        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        material.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        EditorUtility.SetDirty(material);
        return material;
    }

    static List<Vector3> Vertices(GameObject prefab, IEnumerable<MeshFilter> filters = null)
    {
        var points = new List<Vector3>();
        foreach (var filter in filters ?? prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            var m = prefab.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            foreach (var v in filter.sharedMesh.vertices)
                points.Add(m.MultiplyPoint3x4(v));
        }
        return points;
    }

    #region Capsids

    static string BuildCapsid(string name, int fold, GameObject antibody, Material siteMaterial)
    {
        string path = MoleculeLibraryBuilder.ToyPath(name);
        var prefab = path != null ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
        if (prefab == null)
            return $"{name}: no toy";
        var points = Vertices(prefab);
        float radius = points.Max(p => p.magnitude);
        var symmetry = new Symmetry(points, 1f * k_UnitsPerAngstrom);
        var fiveFolds = symmetry.FiveFolds(out string fit);
        var axes = fold switch
        {
            5 => fiveFolds,
            3 => Symmetry.ThreeFolds(fiveFolds),
            _ => Symmetry.TwoFolds(fiveFolds),
        };

        // Each site's base: the outermost surface within the site's footprint around the axis.
        float footprint = radius * k_SiteDiameterFraction; // the site's radius
        var bases = axes.Select(axis =>
        {
            float best = 0f;
            foreach (var p in points)
            {
                float along = Vector3.Dot(p, axis);
                if (along > 0f && (p - axis * along).sqrMagnitude <= footprint * footprint && along > best)
                    best = along;
            }
            return best > 0f ? best : radius;
        }).ToArray();
        float check = symmetry.Score(axes[0], 360f / fold);

        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var sites = root.GetComponent<CapsidSites>() ?? root.AddComponent<CapsidSites>();
            sites.Configure($"{fold}-fold", axes.ToArray(), bases, radius, antibody, siteMaterial);
            EditorUtility.SetDirty(sites);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        return $"{name}: {axes.Count} {fold}-fold sites ({fit}; {fold}-fold match {check:P0}), outer radius {radius / k_UnitsPerAngstrom:0} Å, " +
            $"site bases {bases.Min() / k_UnitsPerAngstrom:0}-{bases.Max() / k_UnitsPerAngstrom:0} Å";
    }

    static string k_VirionPath => MoleculeLibraryBuilder.ToyPath("SARS"); // in Assets/Molecules/viral capsids

    /// <summary>
    /// SARS-CoV-2's binding sites: its spikes, from the cylinders that enclose them (its "Spike N" colliders, made by
    /// Tunnel > Molecules > Build SARS Toy). An antibody binds at the middle of a spike's outer end.
    /// </summary>
    public static string BuildVirion() => BuildVirion(AssetDatabase.LoadAssetAtPath<GameObject>(k_AntibodyPath), SiteMaterial());

    static string BuildVirion(GameObject antibody, Material siteMaterial)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(k_VirionPath) == null)
            return "SARS: no toy";
        var root = PrefabUtility.LoadPrefabContents(k_VirionPath);
        try
        {
            var holder = root.transform.Find("Collider");
            var spikes = holder == null ? new List<Transform>() : holder.Cast<Transform>()
                .Where(t => t.name.StartsWith("Spike ") && t.GetComponent<MeshCollider>() != null).ToList();
            if (spikes.Count == 0)
                return "SARS: no spike colliders (Tunnel > Molecules > Build SARS Toy first)";
            var toRoot = root.transform.worldToLocalMatrix;
            var points = new Vector3[spikes.Count];
            var axes = new Vector3[spikes.Count];
            var lengths = new float[spikes.Count];
            var diameters = new float[spikes.Count];
            for (int i = 0; i < spikes.Count; i++)
            {
                // Each cylinder runs along its +z from its position, its length its z scale, its diameter its x.
                var spike = spikes[i];
                var start = toRoot.MultiplyPoint3x4(spike.position);
                var end = toRoot.MultiplyPoint3x4(spike.TransformPoint(Vector3.forward));
                axes[i] = (end - start).normalized;
                points[i] = end;
                lengths[i] = (end - start).magnitude;
                diameters[i] = toRoot.MultiplyVector(spike.TransformVector(Vector3.right)).magnitude;
            }
            float radius = points.Max(p => p.magnitude);
            var sites = root.GetComponent<CapsidSites>() ?? root.AddComponent<CapsidSites>();
            sites.ConfigureProtrusions("spikes", "virion", points, axes, lengths, diameters, radius, antibody, siteMaterial);
            EditorUtility.SetDirty(sites);
            PrefabUtility.SaveAsPrefabAsset(root, k_VirionPath);
            return $"SARS: {spikes.Count} spike sites, tips {points.Min(p => p.magnitude) / k_UnitsPerAngstrom:0}-{radius / k_UnitsPerAngstrom:0} Å out";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>
    /// Finds a capsid's icosahedral symmetry from its surface's vertices: how well the surface lands on itself when
    /// turned about a direction (the fraction of vertices that land within about an ångström of another). A 5-fold
    /// axis is searched for over the sphere of directions and refined; a second one 63.4° from it; then the two are
    /// made exactly icosahedral and refined together, and the rest of the axes follow from them.
    /// </summary>
    class Symmetry
    {
        readonly HashSet<long> m_Grid = new();
        readonly List<Vector3> m_Sample = new();
        readonly float m_Cell;
        static readonly float k_Neighbour = Mathf.Rad2Deg * Mathf.Acos(1f / Mathf.Sqrt(5f)); // 63.43°

        public Symmetry(List<Vector3> points, float cell)
        {
            m_Cell = cell;
            foreach (var p in points)
                m_Grid.Add(Key(Cell(p.x), Cell(p.y), Cell(p.z)));
            var random = new System.Random(1);
            for (int i = 0; i < 2500; i++)
                m_Sample.Add(points[random.Next(points.Count)]);
        }

        int Cell(float v) => Mathf.FloorToInt(v / m_Cell);

        static long Key(int x, int y, int z) => ((long)(x + 100000) << 40) | ((long)(y + 100000) << 20) | (long)(z + 100000);

        bool Near(Vector3 p)
        {
            int x = Cell(p.x), y = Cell(p.y), z = Cell(p.z);
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                    for (int k = -1; k <= 1; k++)
                        if (m_Grid.Contains(Key(x + i, y + j, z + k)))
                            return true;
            return false;
        }

        public float Score(Vector3 axis, float angle, int samples = int.MaxValue)
        {
            var turn = Quaternion.AngleAxis(angle, axis);
            int count = Mathf.Min(samples, m_Sample.Count), hits = 0;
            for (int i = 0; i < count; i++)
                if (Near(turn * m_Sample[i]))
                    hits++;
            return hits / (float)count;
        }

        Vector3 Refine(Vector3 axis, float angle, float step)
        {
            float best = Score(axis, angle);
            while (step > 0.02f)
            {
                bool better = false;
                var u = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                var v = Vector3.Cross(axis, u);
                foreach (var direction in new[] { u, -u, v, -v, (u + v).normalized, (u - v).normalized, (v - u).normalized, -(u + v).normalized })
                {
                    var candidate = Quaternion.AngleAxis(step, Vector3.Cross(axis, direction)) * axis;
                    float score = Score(candidate, angle);
                    if (score > best)
                    {
                        best = score;
                        axis = candidate;
                        better = true;
                    }
                }
                if (!better)
                    step *= 0.5f;
            }
            return axis.normalized;
        }

        // Two neighbouring 5-folds made exactly 63.43° apart.
        static (Vector3, Vector3) Exact(Vector3 a, Vector3 b)
        {
            a.Normalize();
            var across = (b - Vector3.Dot(b, a) * a).normalized;
            return (a, Mathf.Cos(k_Neighbour * Mathf.Deg2Rad) * a + Mathf.Sin(k_Neighbour * Mathf.Deg2Rad) * across);
        }

        float FrameScore(Quaternion turn, Vector3 a, Vector3 b) =>
            Score(turn * a, 72f) + Score(turn * b, 72f) + Score(turn * (a + b).normalized, 180f);

        public List<Vector3> FiveFolds(out string fit)
        {
            // A first 5-fold: the best of a few thousand directions (a hemisphere: an axis either way is the same).
            const int directions = 4000;
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            Vector3 first = Vector3.forward;
            float best = -1f;
            for (int i = 0; i < directions; i++)
            {
                float z = 1f - (i + 0.5f) / directions, r = Mathf.Sqrt(1f - z * z);
                var direction = new Vector3(r * Mathf.Cos(golden * i), r * Mathf.Sin(golden * i), z);
                float score = Score(direction, 72f, 600);
                if (score > best)
                {
                    best = score;
                    first = direction;
                }
            }
            first = Refine(first, 72f, 2f);

            // A neighbouring one, round the ring 63.4° from it.
            var side = Vector3.Cross(first, Mathf.Abs(first.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
            var start = Quaternion.AngleAxis(k_Neighbour, side) * first;
            Vector3 second = start;
            best = -1f;
            for (int i = 0; i < 288; i++) // 72° (the rest repeat) in quarter degrees
            {
                var direction = Quaternion.AngleAxis(i * 0.25f, first) * start;
                float score = Score(direction, 72f);
                if (score > best)
                {
                    best = score;
                    second = direction;
                }
            }
            second = Refine(second, 72f, 0.5f);

            // Exactly icosahedral, then turned as a whole to fit best.
            var (a, b) = Exact(first, second);
            var turn = Quaternion.identity;
            float frame = FrameScore(turn, a, b);
            for (float step = 1f; step > 0.02f;)
            {
                bool better = false;
                foreach (var axis in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
                {
                    var candidate = Quaternion.AngleAxis(step, axis) * turn;
                    float score = FrameScore(candidate, a, b);
                    if (score > frame)
                    {
                        frame = score;
                        turn = candidate;
                        better = true;
                    }
                }
                if (!better)
                    step *= 0.5f;
            }
            a = turn * a;
            b = turn * b;

            var axes = new List<Vector3> { a, -a };
            for (int k = 0; k < 5; k++)
            {
                var neighbour = Quaternion.AngleAxis(72f * k, a) * b;
                axes.Add(neighbour);
                axes.Add(-neighbour);
            }
            fit = $"5-fold match {Score(a, 72f):P0}";
            return axes;
        }

        static IEnumerable<(int, int)> Neighbours(List<Vector3> fiveFolds)
        {
            for (int i = 0; i < fiveFolds.Count; i++)
                for (int j = i + 1; j < fiveFolds.Count; j++)
                    if (Vector3.Dot(fiveFolds[i], fiveFolds[j]) > 0.4f)
                        yield return (i, j);
        }

        /// <summary>The 30 2-folds: between each pair of neighbouring 5-folds.</summary>
        public static List<Vector3> TwoFolds(List<Vector3> fiveFolds) =>
            Neighbours(fiveFolds).Select(pair => (fiveFolds[pair.Item1] + fiveFolds[pair.Item2]).normalized).ToList();

        /// <summary>The 20 3-folds: in the middle of each triangle of neighbouring 5-folds.</summary>
        public static List<Vector3> ThreeFolds(List<Vector3> fiveFolds)
        {
            var pairs = new HashSet<(int, int)>(Neighbours(fiveFolds));
            var result = new List<Vector3>();
            for (int i = 0; i < fiveFolds.Count; i++)
                for (int j = i + 1; j < fiveFolds.Count; j++)
                    for (int k = j + 1; k < fiveFolds.Count; k++)
                        if (pairs.Contains((i, j)) && pairs.Contains((i, k)) && pairs.Contains((j, k)))
                            result.Add((fiveFolds[i] + fiveFolds[j] + fiveFolds[k]).normalized);
            return result;
        }
    }

    #endregion

    #region Antibody

    /// <summary>
    /// The antibody's two Fab tips. Its chains are two light chains (the smaller meshes) and two heavy chains; the Fc
    /// is the part of the heavy chains farthest from both light chains. Each Fab arm is a light chain with the heavy
    /// chain beside it, its direction out from the antibody's centre through the arm's middle, and its tip the arm's
    /// far end along that direction (where the antigen binding site is).
    /// </summary>
    static string BuildAntibody()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(k_AntibodyPath);
        if (prefab == null)
            return "antibody: no IGG toy";
        var chains = prefab.GetComponentsInChildren<MeshFilter>(true)
            .Where(f => f.sharedMesh != null)
            .Select(f => Vertices(prefab, new[] { f }))
            .OrderBy(c => c.Count)
            .ToList();
        if (chains.Count < 4)
            return $"antibody: {chains.Count} chains, expected 4";
        Vector3 Centre(IEnumerable<Vector3> points)
        {
            var sum = Vector3.zero;
            int n = 0;
            foreach (var p in points)
            {
                sum += p;
                n++;
            }
            return sum / Mathf.Max(n, 1);
        }

        var light = chains.Take(2).ToList();
        var heavy = chains.Skip(2).SelectMany(c => c).ToList();
        var centre = Centre(chains.SelectMany(c => c));
        var lightCentres = light.Select(Centre).ToList();
        var fc = Centre(heavy.OrderByDescending(p => Mathf.Min((p - lightCentres[0]).magnitude, (p - lightCentres[1]).magnitude)).Take(heavy.Count / 3));

        var tips = new Vector3[2];
        var arms = new Vector3[2];
        var report = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var own = lightCentres[i];
            var other = lightCentres[1 - i];
            var fab = light[i].Concat(heavy.Where(p => (p - own).magnitude < (p - fc).magnitude && (p - own).magnitude < (p - other).magnitude)).ToList();
            var arm = (Centre(fab) - centre).normalized;
            float end = fab.Max(p => Vector3.Dot(p - centre, arm));
            var top = Centre(fab.Where(p => Vector3.Dot(p - centre, arm) > end - 8f * k_UnitsPerAngstrom));
            tips[i] = top + arm * (end - Vector3.Dot(top - centre, arm));
            arms[i] = arm;
            report.Add($"{(tips[i] - centre).magnitude / k_UnitsPerAngstrom:0} Å out, {Vector3.Angle(arm, fc - centre):0}° from the Fc");
        }

        var root = PrefabUtility.LoadPrefabContents(k_AntibodyPath);
        try
        {
            var binder = root.GetComponent<AntibodyBinder>() ?? root.AddComponent<AntibodyBinder>();
            binder.Configure(tips, arms);
            EditorUtility.SetDirty(binder);
            PrefabUtility.SaveAsPrefabAsset(root, k_AntibodyPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        return $"antibody Fab tips: {string.Join(", ", report)}; arms {Vector3.Angle(arms[0], arms[1]):0}° apart";
    }

    #endregion

    #region Ligand binding

    const string k_Protein = "EGFR";
    const string k_Ligand = "EGFR ligand";

    // Both models come from Mol* as GLB files of PDB entry 9S3X (EGFR T790M/L858R with the covalent inhibitor
    // NCL-26007, which Mol* exports with the cysteine it is bonded to): their vertices are in the structure's own
    // coordinates, so the ligand's place on EGFR is where the two meshes already are (each file's node matrix only
    // re-centres it, and the library centres each toy on its own bounds instead).

    // The ligand model's part that is the cysteine its inhibitor bonds to (the first of 9S3X-ligand.glb's two meshes;
    // the second is the inhibitor, NCL-26007), and what it is called.
    const int k_BondedResiduePart = 0;
    const string k_BondedResidue = "Cys775";

    // Light blue: an EGFR with its ligand bound.
    static readonly Color k_BoundBlue = new(0.55f, 0.82f, 1f);

    // EGFR's body collider: this many convex pieces of its surface (a single convex shape fills the cleft the ligand
    // binds in, so its pocket would lie 19 Å inside it and no free ligand could reach a 10 Å capture sphere), leaving
    // out the surface within this distance of where the ligand sits (Å), so its pocket stays open.
    const int k_BodyPieces = 8;
    const float k_PocketOpening = 14f;

    /// <summary>
    /// The Ligand binding movie's parts. EGFR's toy gets one binding site, its ligand's pocket: the pose a bound ligand
    /// takes, which is the ligand toy placed as in the crystal structure, and a capture sphere centred on that bound
    /// ligand (the middle of its mesh). Both models' meshes (egfr.glb and 9S3X-ligand.glb, from Mol*) are in the
    /// structure's coordinates, and each is mapped into its toy as the molecule library did (see
    /// <see cref="MoleculeLibraryBuilder.ModelToToy"/>), so the pose is one toy's frame in the other's. Its body
    /// collider follows its surface and leaves the pocket open (see <see cref="BuildOpenPocketBody"/>). The ligand's toy
    /// gets a binder with no Fab arms (it glides straight into its pose).
    /// </summary>
    static string BuildLigandBinding(Material siteMaterial)
    {
        string proteinFolder = MoleculeLibraryBuilder.FolderOf(k_Protein);
        string ligandFolder = MoleculeLibraryBuilder.FolderOf(k_Ligand);
        string proteinPath = MoleculeLibraryBuilder.ToyPath(k_Protein);
        if (proteinPath == null || ligandFolder == null)
            return "ligand binding: no EGFR toy or EGFR ligand folder (Tunnel > Molecules > Update Molecule Library)";
        if (!MoleculeLibraryBuilder.ModelToToy(ligandFolder, out _, out string ligandModel))
            return "ligand binding: no ligand model";
        if (!ligandModel.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase) && !ligandModel.EndsWith(".gltf", System.StringComparison.OrdinalIgnoreCase))
            return $"ligand binding: {ligandModel} is not a GLB from Mol* (only those keep the structure's coordinates)";

        // The free ligand is the inhibitor alone: its toy is made again without the cysteine it is bonded to in the
        // crystal, which goes on EGFR instead, shown only while a ligand sits bound.
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ligandModel);
        var parts = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
        var residue = parts.Count > 1 ? parts.FirstOrDefault(f => f.transform.GetSiblingIndex() == k_BondedResiduePart) : null;
        bool Inhibitor(MeshFilter f) => f != residue;
        var ligandPrefab = MoleculeLibraryBuilder.RebuildToy(ligandFolder, "ligands", Inhibitor);
        if (ligandPrefab == null)
            return "ligand binding: couldn't make the ligand's toy";
        string ligandPath = AssetDatabase.GetAssetPath(ligandPrefab);
        if (!MoleculeLibraryBuilder.ModelToToy(proteinFolder, out var proteinMeshToToy, out _)
            || !MoleculeLibraryBuilder.ModelToToy(ligandFolder, out var ligandMeshToToy, out _, Inhibitor))
            return "ligand binding: no EGFR or ligand model";

        // A point of the structure goes into each model's meshes the same way (Unity's glTF import mirrors x), so the
        // ligand toy's frame in the protein toy's is one mapping after the other's inverse.
        var pose = proteinMeshToToy * ligandMeshToToy.inverse;
        var poseScale = pose.lossyScale;
        if (Mathf.Abs(poseScale.x - 1f) > 1e-3f || Mathf.Abs(poseScale.y - 1f) > 1e-3f || Mathf.Abs(poseScale.z - 1f) > 1e-3f)
            return $"ligand binding: the two toys aren't at one scale ({poseScale})";
        // The capture sphere is centred on the bound ligand: its toy's origin is the middle of its mesh.
        var captureCentre = pose.GetPosition();

        var protein = PrefabUtility.LoadPrefabContents(proteinPath);
        float radius;
        string body;
        string bonded = "";
        try
        {
            radius = Vertices(protein).Max(p => p.magnitude);
            body = BuildOpenPocketBody(protein, proteinPath, captureCentre);
            var shown = residue != null ? BuildBondedResidue(protein, residue, proteinMeshToToy, out bonded) : null;
            var sites = protein.GetComponent<CapsidSites>() ?? protein.AddComponent<CapsidSites>();
            sites.ConfigurePocket(captureCentre, pose.GetPosition(), pose.rotation, radius, ligandPrefab, siteMaterial, k_BoundBlue,
                k_UnitsPerAngstrom, shown);
            EditorUtility.SetDirty(sites);
            PrefabUtility.SaveAsPrefabAsset(protein, proteinPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(protein);
        }

        var ligand = PrefabUtility.LoadPrefabContents(ligandPath);
        try
        {
            var binder = ligand.GetComponent<AntibodyBinder>() ?? ligand.AddComponent<AntibodyBinder>();
            binder.Configure(new Vector3[0], new Vector3[0]);
            EditorUtility.SetDirty(binder);
            PrefabUtility.SaveAsPrefabAsset(ligand, ligandPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(ligand);
        }

        var offset = pose.GetPosition() / k_UnitsPerAngstrom;
        return $"ligand binding: the free ligand is the inhibitor alone ({SpawnNozzle.Triangles(ligandPrefab):N0} triangles); {body}; {bonded}; " +
            $"the bound ligand (and its capture sphere's centre) at ({offset.x:0.00}, {offset.y:0.00}, {offset.z:0.00}) Å in EGFR's toy, " +
            $"{offset.magnitude:0.0} Å from its centre, turned {Quaternion.Angle(Quaternion.identity, pose.rotation):0.00}°";
    }

    /// <summary>
    /// The residue the ligand is bonded to (Cys775), in its atoms' colours, as a hidden part of the protein's toy
    /// ("Bound Cys775", not tinted with it), placed as in the crystal: shown only while a ligand sits bound. Its meshes
    /// and materials go in the protein's meshes asset. No collider: it lies inside the protein.
    /// </summary>
    static GameObject BuildBondedResidue(GameObject protein, MeshFilter residue, Matrix4x4 proteinMeshToToy, out string report)
    {
        foreach (Transform child in protein.transform.Cast<Transform>().ToList())
            if (child.name.StartsWith("Bound Cys"))
                Object.DestroyImmediate(child.gameObject);
        string meshPath = AssetDatabase.GetAssetPath(protein.GetComponentsInChildren<MeshFilter>(true)
            .First(f => f.sharedMesh != null && f.transform.IsChildOf(protein.transform.GetChild(0))).sharedMesh);
        foreach (var old in AssetDatabase.LoadAllAssetsAtPath(meshPath).Where(o => o != null && o.name.StartsWith(k_BondedResidue + "_")).ToList())
            Object.DestroyImmediate(old, true);

        // Its mesh is in the structure's coordinates, as EGFR's own mesh is: the same mapping takes it into EGFR's toy.
        var parts = MoleculeLibraryBuilder.ColouredParts(new List<MeshFilter> { residue }, f => proteinMeshToToy, k_BondedResidue);
        var holder = new GameObject($"Bound {k_BondedResidue}");
        holder.transform.SetParent(protein.transform, false);
        holder.layer = protein.transform.GetChild(0).gameObject.layer;
        int triangles = 0;
        foreach (var (mesh, material) in parts)
        {
            AssetDatabase.AddObjectToAsset(mesh, meshPath);
            AssetDatabase.AddObjectToAsset(material, meshPath);
            var part = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
            part.layer = holder.layer;
            part.transform.SetParent(holder.transform, false);
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            part.GetComponent<MeshRenderer>().sharedMaterial = material;
            triangles += mesh.triangles.Length / 3;
        }
        holder.SetActive(false);
        report = $"{k_BondedResidue} ({triangles:N0} triangles, {parts.Count} colours) shown on EGFR only while a ligand is bound";
        return holder;
    }

    /// <summary>
    /// Gives a protein a body collider that follows its surface and leaves its ligand's pocket open: its surface split
    /// into <see cref="k_BodyPieces"/> patches (k-means on its vertices, the same every time), each patch's convex hull a
    /// collider, without the surface within <see cref="k_PocketOpening"/> Å of the pocket. Replaces the one convex
    /// shape the molecule library gave it; the pieces' meshes go in its meshes asset.
    /// </summary>
    static string BuildOpenPocketBody(GameObject protein, string proteinPath, Vector3 pocket)
    {
        var holder = protein.transform.Find("Collider");
        var surface = protein.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.transform.IsChildOf(protein.transform.GetChild(0))).ToList();
        if (holder == null || surface.Count == 0)
            return "no collider holder or surface";
        var toRoot = protein.transform.worldToLocalMatrix;
        var points = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var filter in surface)
        {
            var m = toRoot * filter.transform.localToWorldMatrix;
            int start = points.Count;
            points.AddRange(filter.sharedMesh.vertices.Select(v => m.MultiplyPoint3x4(v)));
            triangles.AddRange(filter.sharedMesh.triangles.Select(t => t + start));
        }

        // k-means, seeded by farthest points from the centre outward, so it comes out the same every time.
        var centres = new List<Vector3> { points.OrderByDescending(p => p.sqrMagnitude).First() };
        while (centres.Count < k_BodyPieces)
            centres.Add(points.OrderByDescending(p => centres.Min(c => (p - c).sqrMagnitude)).First());
        var patch = new int[points.Count];
        for (int round = 0; round < 30; round++)
        {
            for (int i = 0; i < points.Count; i++)
            {
                float nearest = float.MaxValue;
                for (int c = 0; c < centres.Count; c++)
                {
                    float d = (points[i] - centres[c]).sqrMagnitude;
                    if (d < nearest)
                    {
                        nearest = d;
                        patch[i] = c;
                    }
                }
            }
            for (int c = 0; c < centres.Count; c++)
            {
                var members = Enumerable.Range(0, points.Count).Where(i => patch[i] == c).ToList();
                if (members.Count > 0)
                    centres[c] = members.Aggregate(Vector3.zero, (sum, i) => sum + points[i]) / members.Count;
            }
        }

        // The old collider and pieces go; the holder sits at the toy's origin.
        string meshPath = AssetDatabase.GetAssetPath(surface[0].sharedMesh);
        foreach (var old in AssetDatabase.LoadAllAssetsAtPath(meshPath).OfType<Mesh>().Where(m => m.name.Contains("_Body ") || m.name.EndsWith("_Collider")).ToList())
            Object.DestroyImmediate(old, true);
        foreach (var collider in holder.GetComponentsInChildren<Collider>(true))
        {
            if (collider.gameObject == holder.gameObject)
                Object.DestroyImmediate(collider);
            else
                Object.DestroyImmediate(collider.gameObject);
        }
        holder.localPosition = Vector3.zero;
        holder.localRotation = Quaternion.identity;
        holder.localScale = Vector3.one;

        float opening = k_PocketOpening * k_UnitsPerAngstrom;
        string name = CapsidSites.MoleculeName(protein);
        int made = 0;
        for (int c = 0; c < centres.Count; c++)
        {
            var keep = Enumerable.Range(0, points.Count).Where(i => patch[i] == c && (points[i] - pocket).magnitude > opening).ToList();
            var index = new Dictionary<int, int>();
            for (int i = 0; i < keep.Count; i++)
                index[keep[i]] = i;
            var kept = new List<int>();
            for (int t = 0; t < triangles.Count; t += 3)
                if (index.TryGetValue(triangles[t], out int a) && index.TryGetValue(triangles[t + 1], out int b) && index.TryGetValue(triangles[t + 2], out int d))
                    kept.AddRange(new[] { a, b, d });
            if (kept.Count < 12)
                continue;
            var mesh = new Mesh { name = $"{name}_Body {made + 1}" };
            mesh.SetVertices(keep.Select(i => points[i]).ToList());
            mesh.SetTriangles(kept, 0);
            mesh.RecalculateBounds();
            AssetDatabase.AddObjectToAsset(mesh, meshPath);
            var piece = new GameObject($"Body {made + 1}");
            piece.layer = holder.gameObject.layer;
            piece.transform.SetParent(holder, false);
            var collider = piece.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            collider.convex = true;
            made++;
        }
        return $"{name}'s body: {made} convex pieces, its pocket open within {k_PocketOpening:0} Å";
    }

    #endregion

    #region Panel

    static string BuildPanel()
    {
        var segment = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        var browser = PrefabUtility.LoadPrefabContents(k_BrowserPath);
        try
        {
            var control = segment.transform.Find("Control Panel");
            var panel = new GameObject("Movie Panel");

            var canvasObject = new GameObject("Movie Canvas", typeof(RectTransform));
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
            var title = Text(titleTemplate, canvas, "Title", TunnelMovie.k_Title, new Vector2(0f, 443f), new Vector2(560f, 64f));
            title.alignment = TextAlignmentOptions.Left;

            var rowTemplate = control.Find("Container List/Box Row").gameObject;
            Button Row(string name, Vector2 position, float width, string text, bool centred)
            {
                var row = Object.Instantiate(rowTemplate, canvas, false);
                row.name = name;
                var rect = (RectTransform)row.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = position;
                rect.sizeDelta = new Vector2(width, 56f);
                var button = row.GetComponent<Button>();
                button.onClick = new Button.ButtonClickedEvent();
                var label = row.GetComponentInChildren<TMP_Text>(true);
                label.text = text;
                label.richText = true;
                if (centred)
                {
                    label.alignment = TextAlignmentOptions.Center;
                    label.fontStyle = FontStyles.Bold;
                }
                return button;
            }
            var scenario = Row("Scenario Row", new Vector2(0f, 370f), 560f, $"Movie: {TunnelMovie.k_Title}", false);
            var start = Row("Start Row", new Vector2(0f, 300f), 560f, "Start", true);
            var pause = Row("Pause Row", new Vector2(-142f, 235f), 276f, "Pause", true);
            var play = Row("Play Row", new Vector2(142f, 235f), 276f, "Play", true);
            var playAgain = Row("Play Again Row", new Vector2(0f, 170f), 560f, "Play again", false);
            var sites = Row("Sites Row", new Vector2(0f, 100f), 560f, "□  Show binding sites", false);

            // The sites' diameter: the Containers list's viscosity slider, 2% to 30% of the capsid's diameter.
            var diameterGroup = Object.Instantiate(control.Find("Container List/Viscosity").gameObject, canvas, false);
            diameterGroup.name = "Site Diameter";
            var diameterRect = (RectTransform)diameterGroup.transform;
            diameterRect.anchoredPosition = new Vector2(0f, 5f);
            diameterRect.sizeDelta = new Vector2(540f, 60f);
            var diameter = diameterGroup.GetComponentInChildren<Slider>(true);
            diameter.name = "Site Diameter Slider";
            diameter.onValueChanged = new Slider.SliderEvent();
            diameter.minValue = TunnelMovie.k_MinSiteDiameter * 100f;
            diameter.maxValue = TunnelMovie.k_MaxSiteDiameter * 100f;
            diameter.wholeNumbers = true;
            diameter.SetValueWithoutNotify(TunnelMovie.k_DefaultSiteDiameter * 100f);
            foreach (var labelName in new[] { "Value Text", "Type Text" })
            {
                var label = diameter.transform.Find(labelName);
                if (label != null)
                    label.GetComponent<TMP_Text>().text = "";
            }
            var diameterText = diameterGroup.transform.Find("Viscosity Text").GetComponent<TMP_Text>();
            diameterText.name = "Site Diameter Text";
            diameterText.text = "Binding site diameter: 10%";
            var ticks = diameterGroup.transform.Find("Ticks");
            var tickValues = new[] { 2, 5, 10, 15, 20, 25, 30 };
            while (ticks.childCount > tickValues.Length)
                Object.DestroyImmediate(ticks.GetChild(ticks.childCount - 1).gameObject);
            for (int i = 0; i < tickValues.Length; i++)
            {
                var tick = (RectTransform)ticks.GetChild(i);
                float at = (tickValues[i] - diameter.minValue) / (diameter.maxValue - diameter.minValue);
                tick.name = $"Tick {tickValues[i]}";
                tick.anchorMin = tick.anchorMax = new Vector2(at, 0.5f);
                tick.GetComponent<TMP_Text>().text = $"{tickValues[i]}%";
            }

            // The binding time: another copy, 10 s to 1 min in whole seconds.
            var timeGroup = Object.Instantiate(control.Find("Container List/Viscosity").gameObject, canvas, false);
            timeGroup.name = "Binding Time";
            var timeRect = (RectTransform)timeGroup.transform;
            timeRect.anchoredPosition = new Vector2(0f, -135f);
            timeRect.sizeDelta = new Vector2(540f, 60f);
            var bindingTime = timeGroup.GetComponentInChildren<Slider>(true);
            bindingTime.name = "Binding Time Slider";
            bindingTime.onValueChanged = new Slider.SliderEvent();
            bindingTime.minValue = TunnelMovie.k_MinBindingTime;
            bindingTime.maxValue = TunnelMovie.k_MaxBindingTime;
            bindingTime.wholeNumbers = true;
            bindingTime.SetValueWithoutNotify(TunnelMovie.k_DefaultBindingTime);
            foreach (var labelName in new[] { "Value Text", "Type Text" })
            {
                var label = bindingTime.transform.Find(labelName);
                if (label != null)
                    label.GetComponent<TMP_Text>().text = "";
            }
            var bindingTimeText = timeGroup.transform.Find("Viscosity Text").GetComponent<TMP_Text>();
            bindingTimeText.name = "Binding Time Text";
            bindingTimeText.text = $"Binding time: {TunnelMovie.k_DefaultBindingTime:0} s";
            var timeTicks = timeGroup.transform.Find("Ticks");
            var timeValues = new[] { 10, 20, 30, 40, 50, 60 };
            while (timeTicks.childCount > timeValues.Length)
                Object.DestroyImmediate(timeTicks.GetChild(timeTicks.childCount - 1).gameObject);
            for (int i = 0; i < timeValues.Length; i++)
            {
                var tick = (RectTransform)timeTicks.GetChild(i);
                float at = (timeValues[i] - bindingTime.minValue) / (bindingTime.maxValue - bindingTime.minValue);
                tick.name = $"Tick {timeValues[i]} s";
                tick.anchorMin = tick.anchorMax = new Vector2(at, 0.5f);
                tick.GetComponent<TMP_Text>().text = $"{timeValues[i]} s";
            }

            var save = Row("Save Row", new Vector2(0f, -250f), 560f, "Save starting state...", false);
            var status = Text(titleTemplate, canvas, "Status", "", new Vector2(0f, -395f), new Vector2(560f, 170f));
            status.fontSize = 22f;
            status.fontStyle = FontStyles.Normal;
            status.alignment = TextAlignmentOptions.TopLeft;
            status.textWrappingMode = TextWrappingModes.Normal;

            // The browser's keyboard, tilted toward whoever uses it, in front of the panel's bottom edge; out only while
            // a name is typed.
            var keys = browser.transform.Find("Keyboard").GetComponentInChildren<XRKeyboard>(true);
            var keyboardRoot = new GameObject("Keyboard").transform;
            keyboardRoot.SetParent(panel.transform, false);
            keyboardRoot.localPosition = new Vector3(0f, -k_CanvasSize.y * k_CanvasScale * 0.5f - 0.06f, -0.14f);
            keyboardRoot.localRotation = Quaternion.Euler(40f, 0f, 0f);
            var keyboardObject = Object.Instantiate(keys.gameObject, keyboardRoot, false);
            keyboardObject.name = keys.gameObject.name;
            keyboardObject.transform.localPosition = keys.transform.localPosition;
            keyboardObject.transform.localRotation = keys.transform.localRotation;
            keyboardObject.transform.localScale = keys.transform.localScale;
            var keyboard = keyboardObject.GetComponent<XRKeyboard>();
            keyboard.closeOnSubmit = false;
            keyboardObject.SetActive(false);

            // The bar along its top, which hands take it by (one hand moves it, kept upright; two don't scale it).
            var grab = BuildBar(panel, title.font);

            var component = panel.AddComponent<TunnelMoviePanel>();
            component.Configure(start, pause, play, sites, save, diameter, diameterText, title, status, keyboard,
                k_CanvasSize.x * k_CanvasScale, k_CanvasSize.y * k_CanvasScale, playAgain, bindingTime, bindingTimeText, grab, k_BarSpace, scenario);
            PrefabUtility.SaveAsPrefabAsset(panel, k_PanelPath);
            Object.DestroyImmediate(panel);
            return $"panel {k_PanelPath}";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(browser);
            PrefabUtility.UnloadPrefabContents(segment);
        }
    }

    /// <summary>
    /// The bar above the menu's top edge, with "Grab here to move" on it, and what makes the menu grabbable by it: a
    /// still (kinematic) body and a grab interactable whose only collider is the bar's, moved by the hand where it took
    /// it, kept upright and never scaled.
    /// </summary>
    static XRGrabInteractable BuildBar(GameObject panel, TMP_FontAsset font)
    {
        string path = $"{k_Folder}/MoveBar.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "MoveBar" };
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetColor("_BaseColor", new Color(0.32f, 0.36f, 0.44f));
        material.SetFloat("_Smoothness", 0.4f);
        material.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(material);

        float top = k_CanvasSize.y * k_CanvasScale * 0.5f;
        var bar = new GameObject("Move Bar", typeof(MeshFilter), typeof(MeshRenderer));
        bar.transform.SetParent(panel.transform, false);
        bar.transform.localPosition = new Vector3(0f, top + k_BarSpace * 0.5f, k_BarSize.z * 0.5f);
        bar.transform.localScale = k_BarSize;
        bar.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        var renderer = bar.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // A grab box a little larger than the bar, so it's easy to take.
        var handle = new GameObject("Move Bar Grab");
        handle.transform.SetParent(panel.transform, false);
        handle.transform.localPosition = bar.transform.localPosition;
        var box = handle.AddComponent<BoxCollider>();
        box.size = new Vector3(k_BarSize.x + 0.02f, k_BarSpace, 0.03f);
        box.excludeLayers = ~0; // found by the hands and lasers, but pushes no molecule

        var labelObject = new GameObject("Move Bar Label", typeof(RectTransform));
        labelObject.transform.SetParent(panel.transform, false);
        var labelRect = (RectTransform)labelObject.transform;
        labelRect.localPosition = new Vector3(0f, top + k_BarSpace * 0.5f, -0.0005f);
        labelRect.sizeDelta = new Vector2(k_BarSize.x, k_BarSize.y);
        var label = labelObject.AddComponent<TextMeshPro>();
        if (font != null)
            label.font = font;
        label.text = "Grab here to move";
        label.fontSize = 0.1f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = new Color(0.92f, 0.94f, 0.97f);
        label.textWrappingMode = TextWrappingModes.NoWrap;

        var body = panel.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        var grab = panel.AddComponent<XRGrabInteractable>();
        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        grab.throwOnDetach = false;
        grab.useDynamicAttach = true;
        grab.trackScale = false;
        grab.colliders.Clear();
        grab.colliders.Add(box);
        var general = panel.AddComponent<XRGeneralGrabTransformer>();
        var generalSo = new SerializedObject(general);
        generalSo.FindProperty("m_AllowTwoHandedScaling").boolValue = false;
        generalSo.ApplyModifiedPropertiesWithoutUndo();
        panel.AddComponent<UnityEngine.XR.Templates.MRTTabletopAssets.UprightGrabTransformer>();
        return grab;
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
}
