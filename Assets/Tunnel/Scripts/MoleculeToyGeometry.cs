using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.AffordanceSystem.Rendering;

/// <summary>
/// Makes a molecule's toy from its model, the same way whether in the Editor (MoleculeLibraryBuilder, for the molecules
/// built into the app) or in the headset (<see cref="RepositoryMolecule"/>, for molecules from the asset repository):
/// centred and scaled like 2PLV (one ångström-to-meter scale for all); a surface in 2PLV's chain colours, one per kind of
/// subunit, an assembly in its own colours (one per chain) and atoms and bonds in theirs (one per element); a surface
/// kept no finer than <see cref="k_SurfaceDetail"/> Å and under <see cref="k_MaxVertices"/> vertices by merging vertices
/// on a grid; the tightest collider around every vertex, an oriented box or an ellipsoid, whichever is smaller (see
/// <see cref="MoleculeColliderFit"/>); atoms and bonds under <see cref="k_MinGrabSize"/> across at ×1 kept grabbable
/// (<see cref="MoleculeGrabSize"/>); a hover and select tint per part; its weight from the volume it encloses.
/// </summary>
public static class MoleculeToyGeometry
{
    /// <summary>2PLV's surface reaches 167 Å from its centre and is 0.2 units in its toy: every toy keeps this scale.</summary>
    public const float k_UnitsPerAngstrom = 0.2f / 167f;

    /// <summary>The most vertices a molecule's toy has in all (the capsids have 40,000 to 60,000).</summary>
    public const int k_MaxVertices = 200000;

    /// <summary>A surface's finest detail (Å): finer models are merged on a grid this size.</summary>
    public const float k_SurfaceDetail = 2f;

    /// <summary>Categories whose models are atoms and bonds, drawn in the model's own (element) colours at full detail.</summary>
    public static readonly string[] k_AtomCategories = { "ligands", "chemicals" };

    /// <summary>
    /// Categories of surfaces drawn in the model's own colours (one per chain, as Mol* exports them), not 2PLV's: an
    /// assembly has too many different chains for 2PLV's few colours to tell apart.
    /// </summary>
    public static readonly string[] k_OwnColourCategories = { "assemblies" };

    /// <summary>The smallest a toy's collider is across at ×1 (meters in the headset) before it gets a minimum grab size.</summary>
    public const float k_MinGrabSize = 0.02f;

    /// <summary>A toy's geometry: its parts and their materials, its collider, and what was done, for the log.</summary>
    public class Geometry
    {
        public List<Mesh> meshes;
        public List<Material> materials;
        public bool atoms;
        public MoleculeColliderFit.Fit fit, box, ellipsoid;
        /// <summary>
        /// For a flat molecule (a membrane): boxes side by side in the fitted box's frame, each only as thick as the
        /// molecule is there, so things can come close to its surface (centres and sizes in that frame); else empty.
        /// </summary>
        public List<(Vector3 center, Vector3 size)> columns = new();
        public float radius;       // toy units, from its centre
        public int subunits;       // the model's mesh parts
        public string detail = ""; // how it was simplified (or not)
    }

    public static bool IsAtoms(string category) => k_AtomCategories.Contains((category ?? "").ToLowerInvariant());

    /// <summary>
    /// The toy's parts, materials and collider from a model's meshes (each placed by toToy... from its own frame to the
    /// model's root). <paramref name="templateMaterials"/> are 2PLV's chain materials; <paramref name="mtlColours"/> an
    /// OBJ's colours by material name (empty otherwise); <paramref name="fullDetail"/> keeps every vertex;
    /// <paramref name="angstromsPerUnit"/> is the model's scale (1 for a model in ångströms, as Mol* exports them).
    /// </summary>
    public static Geometry Build(Transform modelRoot, List<MeshFilter> filters, string name, string category,
        Material[] templateMaterials, bool fullDetail, Dictionary<string, Color> mtlColours = null, float angstromsPerUnit = 1f,
        bool fitCollider = true)
    {
        mtlColours ??= new Dictionary<string, Color>();
        Matrix4x4 toRoot(Transform t) => modelRoot.worldToLocalMatrix * t.localToWorldMatrix;
        var bounds = new Bounds(toRoot(filters[0].transform).MultiplyPoint3x4(filters[0].sharedMesh.vertices[0]), Vector3.zero);
        foreach (var f in filters)
        {
            var m = toRoot(f.transform);
            foreach (var v in f.sharedMesh.vertices)
                bounds.Encapsulate(m.MultiplyPoint3x4(v));
        }

        // Centred and scaled like 2PLV.
        var normalize = Matrix4x4.Scale(Vector3.one * (k_UnitsPerAngstrom * angstromsPerUnit)) * Matrix4x4.Translate(-bounds.center);
        var result = new Geometry { atoms = IsAtoms(category), subunits = filters.Count };
        bool ownColours = result.atoms || k_OwnColourCategories.Contains((category ?? "").ToLowerInvariant());
        if (ownColours)
        {
            // Atoms and bonds, or an assembly: one mesh per colour in the model (an element each in CPK colours, or a
            // chain each), each drawn in the template's material in that colour.
            var colours = SplitByColour(filters, f => normalize * toRoot(f.transform), name, mtlColours);
            result.meshes = colours.Select(c => c.mesh).ToList();
            result.materials = colours.Select(c => ColouredMaterial(templateMaterials[0], c.colour, $"{name}_{ColorUtility.ToHtmlStringRGB(c.colour)}")).ToList();
        }
        else
        {
            // A surface: one combined mesh per kind of subunit (each distinct mesh in the model), in 2PLV's chain colours.
            var groups = filters.GroupBy(f => f.sharedMesh).ToList();
            result.meshes = new List<Mesh>();
            for (int g = 0; g < groups.Count; g++)
            {
                var combine = groups[g].Select(f => new CombineInstance { mesh = f.sharedMesh, transform = normalize * toRoot(f.transform) }).ToArray();
                int vertexCount = groups[g].Sum(f => f.sharedMesh.vertexCount);
                // The index format has to be chosen before combining: changing it afterwards drops the triangles.
                var mesh = new Mesh { name = $"{name}_Type{g + 1}", indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.CombineMeshes(combine, true, true);
                mesh.RecalculateBounds();
                result.meshes.Add(mesh);
            }
            result.materials = result.meshes.Select((m, i) => templateMaterials[i % templateMaterials.Length]).ToList();
        }

        int totalVertices = result.meshes.Sum(m => m.vertexCount);
        int totalTriangles = result.meshes.Sum(m => m.triangles.Length / 3);
        if (!result.atoms && !fullDetail)
        {
            // No finer than the library's surface detail, and under the vertex budget: the finest grid that does both,
            // all its parts alike (a surface's vertices go as one over the cell's area, so start a little finer than
            // that suggests).
            float cell = Mathf.Max(k_SurfaceDetail * k_UnitsPerAngstrom,
                0.7f * Mathf.Sqrt(totalVertices / (float)k_MaxVertices) * k_UnitsPerAngstrom);
            List<Mesh> reduced;
            while (true)
            {
                reduced = result.meshes.Select(m => Simplify(m, cell)).ToList();
                if (reduced.Sum(m => m.vertexCount) <= k_MaxVertices)
                    break;
                foreach (var m in reduced)
                    DestroyNow(m);
                cell *= 1.2f;
            }
            foreach (var m in result.meshes)
                DestroyNow(m);
            result.meshes = reduced;
            result.detail = $"simplified from {totalVertices:N0} vertices ({totalTriangles:N0} triangles) to {result.meshes.Sum(m => m.vertexCount):N0} " +
                $"({result.meshes.Sum(m => m.triangles.Length / 3):N0} triangles) on a {cell / k_UnitsPerAngstrom:0.0} Å grid; ";
        }
        else
        {
            result.detail = $"{totalVertices:N0} vertices, {totalTriangles:N0} triangles in {result.meshes.Count} colours; ";
        }
        foreach (var mesh in result.meshes)
            foreach (var v in mesh.vertices)
                result.radius = Mathf.Max(result.radius, v.magnitude);

        if (fitCollider)
            FitCollider(result, Points(result));
        return result;
    }

    /// <summary>Every vertex of the toy's parts (read on the main thread, for <see cref="FitCollider"/>).</summary>
    public static List<Vector3> Points(Geometry g) => g.meshes.SelectMany(m => m.vertices).ToList();

    /// <summary>
    /// The tightest collider around every vertex: an oriented box or an ellipsoid, whichever is smaller. Only maths
    /// on the points, so a headset can run it off the main thread (it takes seconds for a large molecule).
    /// </summary>
    public static void FitCollider(Geometry g, List<Vector3> points, bool followFlatShapes = false)
    {
        g.box = MoleculeColliderFit.Box(points);
        // The plain measured box (width, height and depth along the toy's own axes) when it's smaller than the search's.
        var aabb = MeasuredBox(points);
        if (aabb.volume < g.box.volume)
            g.box = aabb;
        g.ellipsoid = MoleculeColliderFit.Ellipsoid(points);
        g.fit = g.box.volume <= g.ellipsoid.volume ? g.box : g.ellipsoid;
        g.columns.Clear();
        if (followFlatShapes)
            FitColumns(g, points);
    }

    /// <summary>
    /// For a flat molecule (its fitted box's thinnest side under a third of its longest), boxes in a grid over its two
    /// long sides, each spanning only the molecule's own thickness in its cell, when together they enclose under three
    /// quarters of the single collider's volume. Every vertex stays inside one of them.
    /// </summary>
    static void FitColumns(Geometry g, List<Vector3> points)
    {
        var box = g.box;
        var s = box.size;
        int thin = s.x <= s.y && s.x <= s.z ? 0 : s.y <= s.z ? 1 : 2;
        int a = (thin + 1) % 3, b = (thin + 2) % 3;
        float longest = Mathf.Max(s[a], s[b]);
        if (s[thin] > longest / 3f)
            return;
        // Square cells, 24 along the long side (no more than 24 x 24 boxes in all): a membrane's boxes are then about
        // as thick as it is in each (its lipids' head groups and its tilt across the cell), under half its overall depth.
        int na = Mathf.Clamp(Mathf.RoundToInt(24f * s[a] / longest), 1, 24), nb = Mathf.Clamp(Mathf.RoundToInt(24f * s[b] / longest), 1, 24);
        var inverse = Quaternion.Inverse(box.rotation);
        var lo = -s * 0.5f;
        var min = new float[na, nb];
        var max = new float[na, nb];
        var cellLo = new Vector2[na, nb];
        var cellHi = new Vector2[na, nb];
        var used = new bool[na, nb];
        foreach (var p in points)
        {
            var l = inverse * (p - box.center); // in the box's frame, its centre at the origin
            int i = Mathf.Clamp((int)((l[a] - lo[a]) / s[a] * na), 0, na - 1);
            int j = Mathf.Clamp((int)((l[b] - lo[b]) / s[b] * nb), 0, nb - 1);
            if (!used[i, j])
            {
                used[i, j] = true;
                min[i, j] = max[i, j] = l[thin];
                cellLo[i, j] = cellHi[i, j] = new Vector2(l[a], l[b]);
            }
            else
            {
                min[i, j] = Mathf.Min(min[i, j], l[thin]);
                max[i, j] = Mathf.Max(max[i, j], l[thin]);
                cellLo[i, j] = Vector2.Min(cellLo[i, j], new Vector2(l[a], l[b]));
                cellHi[i, j] = Vector2.Max(cellHi[i, j], new Vector2(l[a], l[b]));
            }
        }
        var columns = new List<(Vector3, Vector3)>();
        float volume = 0f;
        for (int i = 0; i < na; i++)
            for (int j = 0; j < nb; j++)
            {
                if (!used[i, j])
                    continue;
                // The cell's points' own extent (so the boxes don't reach past the molecule's edge), at least a sliver.
                Vector3 c = Vector3.zero, size = Vector3.zero;
                c[a] = (cellLo[i, j].x + cellHi[i, j].x) * 0.5f;
                c[b] = (cellLo[i, j].y + cellHi[i, j].y) * 0.5f;
                c[thin] = (min[i, j] + max[i, j]) * 0.5f;
                size[a] = Mathf.Max(cellHi[i, j].x - cellLo[i, j].x, 1e-4f);
                size[b] = Mathf.Max(cellHi[i, j].y - cellLo[i, j].y, 1e-4f);
                size[thin] = Mathf.Max(max[i, j] - min[i, j], 1e-4f);
                columns.Add((c, size));
                volume += size.x * size.y * size.z;
            }
        if (volume < 0.75f * g.fit.volume)
        {
            g.columns = columns;
            g.fit = box; // the columns lie in the fitted box's frame
        }
    }

    /// <summary>The box of the points' width, height and depth along the toy's own axes.</summary>
    public static MoleculeColliderFit.Fit MeasuredBox(List<Vector3> points)
    {
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var size = max - min;
        return new MoleculeColliderFit.Fit
        {
            kind = MoleculeColliderFit.Kind.Box,
            center = (min + max) * 0.5f,
            rotation = Quaternion.identity,
            size = size,
            volume = size.x * size.y * size.z,
        };
    }

    /// <summary>A fitted collider as saved (on the headset, or with a molecule in the asset repository), toy units.</summary>
    [System.Serializable]
    public class SavedFit
    {
        public string kind; // "box" or "ellipsoid"
        public float[] center;
        public float[] rotation; // x, y, z, w
        public float[] size;     // box: full size; ellipsoid: semi-axes
        public float volume;
        public float[] boxes; // a flat molecule's boxes in that frame: centre x, y, z and size x, y, z for each

        public bool isValid => (kind == "box" || kind == "ellipsoid") && center is { Length: 3 } && rotation is { Length: 4 } && size is { Length: 3 };

        public static SavedFit From(MoleculeColliderFit.Fit f) => new()
        {
            kind = f.kind == MoleculeColliderFit.Kind.Box ? "box" : "ellipsoid",
            center = new[] { f.center.x, f.center.y, f.center.z },
            rotation = new[] { f.rotation.x, f.rotation.y, f.rotation.z, f.rotation.w },
            size = new[] { f.size.x, f.size.y, f.size.z },
            volume = f.volume,
        };

        public static SavedFit From(Geometry g)
        {
            var saved = From(g.fit);
            if (g.columns.Count > 0)
                saved.boxes = g.columns.SelectMany(c => new[] { c.center.x, c.center.y, c.center.z, c.size.x, c.size.y, c.size.z }).ToArray();
            return saved;
        }

        public MoleculeColliderFit.Fit ToFit() => new()
        {
            kind = kind == "box" ? MoleculeColliderFit.Kind.Box : MoleculeColliderFit.Kind.Ellipsoid,
            center = new Vector3(center[0], center[1], center[2]),
            rotation = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]),
            size = new Vector3(size[0], size[1], size[2]),
            volume = volume,
        };
    }

    /// <summary>Uses a saved collider instead of fitting one.</summary>
    public static void UseSavedFit(Geometry g, SavedFit saved)
    {
        g.fit = saved.ToFit();
        g.box = g.ellipsoid = g.fit;
        g.columns.Clear();
        if (saved.boxes != null)
            for (int i = 0; i + 5 < saved.boxes.Length; i += 6)
                g.columns.Add((new Vector3(saved.boxes[i], saved.boxes[i + 1], saved.boxes[i + 2]),
                    new Vector3(saved.boxes[i + 3], saved.boxes[i + 4], saved.boxes[i + 5])));
    }

    /// <summary>
    /// Puts the geometry on a toy copied from 2PLV's: its parts under the visual, a hover and select tint per part
    /// (copies of the first tint helper if there are too few), its collider in place of the template's sphere, and a
    /// minimum grab size for small atoms and bonds. <paramref name="keep"/> is told of each new object (the ellipsoid's
    /// collider mesh), so the Editor can save it. Returns a line for the log about the collider and size.
    /// </summary>
    public static string Assemble(GameObject toy, Transform visual, Geometry g, string name, float toMeters, System.Action<Object> keep = null)
    {
        for (int i = visual.childCount - 1; i >= 0; i--)
            DestroyNow(visual.GetChild(i).gameObject);

        var renderers = new List<MeshRenderer>();
        for (int i = 0; i < g.meshes.Count; i++)
        {
            var part = new GameObject(g.meshes[i].name, typeof(MeshFilter), typeof(MeshRenderer));
            part.layer = visual.gameObject.layer;
            part.transform.SetParent(visual, false);
            part.GetComponent<MeshFilter>().sharedMesh = g.meshes[i];
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = g.materials[i];
            renderers.Add(renderer);
        }

        // Hover/select tint: one colour receiver per part; drop the spares.
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
                helpers[i].rendererTarget = renderers[i];
                helpers[i].gameObject.name = $"ColorAffordance_Type{i + 1}";
                helpers[i].gameObject.SetActive(true); // a repository toy's waits, inactive, for its parts
                if (Application.isPlaying && helpers[i].enabled)
                {
                    // Already awake without a part: it takes its part when enabled again.
                    helpers[i].enabled = false;
                    helpers[i].enabled = true;
                }
            }
            else
            {
                DestroyNow(helpers[i].gameObject);
            }
        }

        var holder = ColliderHolder(toy);
        foreach (var old in holder.GetComponents<Collider>())
            DestroyNow(old);
        holder.transform.localPosition = g.fit.center;
        holder.transform.localRotation = g.fit.rotation;
        holder.transform.localScale = Vector3.one;
        if (g.columns.Count > 0)
        {
            // A flat molecule: its boxes side by side, following its surface. Added while their holder is off, so the
            // body works out its mass and inertia once, not once per box.
            bool wasActive = holder.activeSelf;
            holder.SetActive(false);
            foreach (var (columnCenter, columnSize) in g.columns)
            {
                var column = holder.AddComponent<BoxCollider>();
                column.center = columnCenter;
                column.size = columnSize;
            }
            holder.SetActive(wasActive);
        }
        else if (g.fit.kind == MoleculeColliderFit.Kind.Box)
        {
            holder.AddComponent<BoxCollider>().size = g.fit.size;
        }
        else
        {
            var hull = MoleculeColliderFit.EllipsoidMesh(g.fit.size, $"{name}_Collider");
            keep?.Invoke(hull);
            var collider = holder.AddComponent<MeshCollider>();
            collider.sharedMesh = hull;
            collider.convex = true;
        }

        // Atoms and bonds too small to take hold of easily at ×1 (a ligand is a few millimetres): the collider is kept at
        // least k_MinGrabSize across in the headset. Surfaces keep their fitted collider, as every other molecule.
        var fittedSize = g.fit.kind == MoleculeColliderFit.Kind.Box ? g.fit.size : g.fit.size * 2f;
        string grab = "";
        if (g.atoms && Mathf.Min(fittedSize.x, Mathf.Min(fittedSize.y, fittedSize.z)) * toMeters < k_MinGrabSize)
        {
            var grabSize = toy.GetComponent<MoleculeGrabSize>();
            if (grabSize == null)
                grabSize = toy.AddComponent<MoleculeGrabSize>();
            grabSize.Configure(holder.transform, fittedSize, k_MinGrabSize);
            grabSize.Fit();
            grab = $"; under {k_MinGrabSize * 100f:0} cm at ×1, so its collider is kept at least that across";
        }
        float sphereVolume = 4f / 3f * Mathf.PI * g.radius * g.radius * g.radius;
        var size = fittedSize * toMeters * 100f;
        if (g.columns.Count > 0)
        {
            float columnsVolume = g.columns.Sum(c => c.size.x * c.size.y * c.size.z);
            return $"outer radius {g.radius / k_UnitsPerAngstrom:0} Å; collider {g.columns.Count} boxes following its surface " +
                $"({columnsVolume / g.fit.volume:P0} of one box's volume); {size.x:0.00} × {size.y:0.00} × {size.z:0.00} cm at ×1";
        }
        return $"outer radius {g.radius / k_UnitsPerAngstrom:0} Å; collider {g.fit.kind} " +
            $"(box {g.box.volume / sphereVolume:P0}, ellipsoid {g.ellipsoid.volume / sphereVolume:P0} of the outer sphere's volume); " +
            $"{size.x:0.00} × {size.y:0.00} × {size.z:0.00} cm at ×1{grab}";
    }

    /// <summary>The object that holds a toy's collider (the template's sphere, or the fitted one that replaced it).</summary>
    public static GameObject ColliderHolder(GameObject toy)
    {
        // The template's sphere; once fitted, the box or convex ellipsoid in its place.
        Collider collider = toy.GetComponentInChildren<SphereCollider>(true);
        if (collider == null)
            collider = toy.GetComponentsInChildren<Collider>(true).FirstOrDefault(c => !c.isTrigger && (c is BoxCollider || c is MeshCollider));
        return collider != null ? collider.gameObject : toy;
    }

    /// <summary>
    /// The volume enclosed by a toy's surface (toy units): each mesh's signed volume, so a hollow shell counts only its
    /// protein. <see cref="MoleculeGravity"/> weighs the molecule by it.
    /// </summary>
    public static double EnclosedVolume(GameObject toy)
    {
        double volume = 0;
        var toRoot = toy.transform.worldToLocalMatrix;
        foreach (var filter in toy.GetComponentsInChildren<MeshFilter>(true))
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
        return volume;
    }

    /// <summary>
    /// Atoms and bonds split by colour: each part of the model takes its material's colour (as an OBJ's .mtl gives
    /// each element), or each triangle its vertices' colour where the model has vertex colours (as a glTF from Mol*
    /// can); one mesh per colour, carbon's grey first if there is one, in the toy's frame.
    /// </summary>
    public static List<(Color colour, Mesh mesh)> SplitByColour(List<MeshFilter> filters, System.Func<MeshFilter, Matrix4x4> toToy, string name,
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

    /// <summary>The template's material in another colour (an element's, for atoms and bonds).</summary>
    public static Material ColouredMaterial(Material template, Color colour, string name)
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
    public static Mesh Simplify(Mesh source, float cell)
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

    /// <summary>Destroys at once in the Editor (outside play), at the end of the frame in the headset.</summary>
    static void DestroyNow(Object thing)
    {
        if (thing == null)
            return;
        if (Application.isPlaying)
            Object.Destroy(thing);
        else
            Object.DestroyImmediate(thing);
    }
}
