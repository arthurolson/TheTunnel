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
        Material[] templateMaterials, bool fullDetail, Dictionary<string, Color> mtlColours = null, float angstromsPerUnit = 1f)
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

        // The tightest collider around every vertex: an oriented box or an ellipsoid, whichever is smaller.
        var points = result.meshes.SelectMany(m => m.vertices).ToList();
        result.box = MoleculeColliderFit.Box(points);
        result.ellipsoid = MoleculeColliderFit.Ellipsoid(points);
        result.fit = result.box.volume <= result.ellipsoid.volume ? result.box : result.ellipsoid;
        return result;
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
        if (g.fit.kind == MoleculeColliderFit.Kind.Box)
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
