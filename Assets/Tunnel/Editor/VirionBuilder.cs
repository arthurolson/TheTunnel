using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Makes the SARS-CoV-2 virion's toy (Assets/Molecules/SARS) light enough for the headset and gives it colliders that
/// follow its shape. The model is a bead model (every group of atoms a small sphere, some 39,000 of them in three
/// parts: the lipid membrane, the membrane proteins and the spikes), millions of triangles as it is. From the beads
/// each part gets a smooth surface like the other molecules' (a Gaussian surface, contoured on a grid of 10 to 12 Å,
/// see <see cref="k_Surfaces"/>), the membrane's solid out to the middle of its bilayer, so it has no inner surface. Its colliders are a
/// sphere covering the membrane's surface and a cylinder enclosing each spike, found by grouping the spike beads that
/// touch. The toy keeps the molecule library's workings (see <see cref="MoleculeLibraryBuilder"/>), which then weighs
/// it by its new surfaces. Runs from Tunnel > Molecules > Build SARS Toy (after the library has made the toy).
/// </summary>
static class VirionBuilder
{
    const string k_Folder = "Assets/Molecules/SARS";
    const string k_ModelPath = k_Folder + "/SARS-COV-2_VIRION.glb";
    const string k_ToyPath = k_Folder + "/Toy_SARS_Networked.prefab";
    const string k_MeshPath = k_Folder + "/SARS_Meshes.asset";

    const float k_UnitsPerAngstrom = 0.2f / 167f; // as the molecule library
    // Each part's surface: Å between grid points, and the smallest a bead counts as for it (the membrane's lipids are
    // small, packed beads: counted larger they make one smooth sheet instead of a pitted one full of bubbles).
    static readonly (float grid, float minRadius)[] k_Surfaces = { (12f, 12f), (10f, 0f), (11f, 0f) };
    const float k_Blobbiness = 2f;                 // how sharply each bead's density falls off past its radius
    const float k_Touching = 4f;                   // spike beads this close (Å, surface to surface) are one spike

    // The model's parts, in its node order, and their names in the toy.
    static readonly string[] k_Parts = { "Membrane", "Membrane Proteins", "Spikes" };

    struct Bead
    {
        public Vector3 centre;
        public float radius;
    }

    [MenuItem("Tunnel/Molecules/Build SARS Toy")]
    public static string Build()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(k_ModelPath);
        if (model == null || AssetDatabase.LoadAssetAtPath<GameObject>(k_ToyPath) == null)
            return $"[Molecules] SARS: needs {k_ModelPath} and its toy (Tunnel > Molecules > Update Molecule Library) first";

        // The beads of each part, centred on the whole virion.
        var filters = model.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).ToList();
        var parts = filters.Select(f => Beads(f, model.transform)).ToList();
        var all = parts.SelectMany(p => p).ToList();
        var lowest = all.Aggregate(Vector3.positiveInfinity, (m, b) => Vector3.Min(m, b.centre - Vector3.one * b.radius));
        var highest = all.Aggregate(Vector3.negativeInfinity, (m, b) => Vector3.Max(m, b.centre + Vector3.one * b.radius));
        var centre = (lowest + highest) * 0.5f;
        for (int p = 0; p < parts.Count; p++)
            parts[p] = parts[p].Select(b => new Bead { centre = b.centre - centre, radius = b.radius }).ToList();

        // The membrane's inside: solid out to the middle of the bilayer, so only its outer leaflet makes a surface (the
        // solid shows where the outer leaflet has gaps).
        var envelope = parts.Take(Mathf.Min(2, parts.Count)).SelectMany(p => p).ToList();
        var envelopeReach = envelope.Select(b => b.centre.magnitude + b.radius).OrderBy(r => r).ToList();
        float core = parts[0].Select(b => b.centre.magnitude).OrderBy(r => r).ElementAt(parts[0].Count / 2);

        var meshes = new List<Mesh>();
        var report = new List<string>();
        for (int p = 0; p < parts.Count; p++)
        {
            var (grid, minRadius) = k_Surfaces[Mathf.Min(p, k_Surfaces.Length - 1)];
            var mesh = Surface(parts[p].Select(b => new Bead { centre = b.centre, radius = Mathf.Max(b.radius, minRadius) }).ToList(), p == 0 ? core : 0f, grid);
            mesh.name = $"SARS_{(p < k_Parts.Length ? k_Parts[p] : $"Part{p + 1}").Replace(' ', '_')}";
            meshes.Add(mesh);
            report.Add($"{(p < k_Parts.Length ? k_Parts[p] : $"part {p + 1}")}: {parts[p].Count:N0} beads, {mesh.triangles.Length / 3:N0} triangles");
        }

        // Colliders: a sphere over the membrane (as far out as all but its outermost few beads reach), a cylinder round each spike.
        float sphere = envelopeReach[(int)(envelopeReach.Count * 0.98f)];
        var spikes = parts.Count > 2 ? Spikes(parts[2]) : new List<(Vector3 basePoint, Vector3 axis, float length, float radius)>();
        var cylinder = CylinderMesh(24);

        // Write the meshes and update the toy.
        AssetDatabase.DeleteAsset(k_MeshPath);
        AssetDatabase.CreateAsset(meshes[0], k_MeshPath);
        meshes[0].name = $"SARS_{k_Parts[0]}";
        for (int i = 1; i < meshes.Count; i++)
            AssetDatabase.AddObjectToAsset(meshes[i], k_MeshPath);
        AssetDatabase.AddObjectToAsset(cylinder, k_MeshPath);

        var root = PrefabUtility.LoadPrefabContents(k_ToyPath);
        try
        {
            var visual = root.transform.Find("SARS_Visual");
            for (int i = 0; i < visual.childCount && i < meshes.Count; i++)
            {
                var part = visual.GetChild(i);
                part.name = $"SARS_{k_Parts[i].Replace(' ', '_')}";
                part.GetComponent<MeshFilter>().sharedMesh = meshes[i];
            }

            var holder = root.transform.Find("Collider");
            foreach (var collider in holder.GetComponents<Collider>())
                Object.DestroyImmediate(collider);
            for (int i = holder.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(holder.GetChild(i).gameObject);
            holder.localPosition = Vector3.zero;
            holder.localRotation = Quaternion.identity;
            holder.localScale = Vector3.one;
            holder.gameObject.AddComponent<SphereCollider>().radius = sphere * k_UnitsPerAngstrom;
            for (int i = 0; i < spikes.Count; i++)
            {
                var (basePoint, axis, length, radius) = spikes[i];
                var spike = new GameObject($"Spike {i + 1}").transform;
                spike.gameObject.layer = holder.gameObject.layer;
                spike.SetParent(holder, false);
                spike.localPosition = basePoint * k_UnitsPerAngstrom;
                spike.localRotation = Quaternion.FromToRotation(Vector3.forward, axis);
                spike.localScale = new Vector3(2f * radius, 2f * radius, length) * k_UnitsPerAngstrom;
                var collider = spike.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = cylinder;
                collider.convex = true;
            }
            PrefabUtility.SaveAsPrefabAsset(root, k_ToyPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        AssetDatabase.SaveAssets();

        var lengths = spikes.Select(s => s.length).OrderBy(l => l).ToList();
        var widths = spikes.Select(s => s.radius * 2f).OrderBy(w => w).ToList();
        string log = $"[Molecules] SARS toy: {string.Join("; ", report)}; collider sphere {sphere:0} Å, {spikes.Count} spike cylinders " +
            (spikes.Count > 0 ? $"{lengths[0]:0}-{lengths[^1]:0} Å long, {widths[0]:0}-{widths[^1]:0} Å across" : "");
        Debug.Log(log);
        return log;
    }

    /// <summary>The beads of one part: each connected piece of its mesh is one bead, at its centre, of its mean radius.</summary>
    static List<Bead> Beads(MeshFilter filter, Transform root)
    {
        var toRoot = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
        var mesh = filter.sharedMesh;
        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = toRoot.MultiplyPoint3x4(vertices[i]);
        var parent = new int[vertices.Length];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }
        var triangles = mesh.triangles;
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = Find(triangles[t]);
            parent[Find(triangles[t + 1])] = a;
            parent[Find(triangles[t + 2])] = a;
        }

        var sums = new Dictionary<int, (Vector3 sum, int count)>();
        for (int i = 0; i < vertices.Length; i++)
        {
            int r = Find(i);
            sums.TryGetValue(r, out var s);
            sums[r] = (s.sum + vertices[i], s.count + 1);
        }
        var radii = new Dictionary<int, float>();
        for (int i = 0; i < vertices.Length; i++)
        {
            int r = Find(i);
            var (sum, count) = sums[r];
            radii.TryGetValue(r, out float d);
            radii[r] = d + (vertices[i] - sum / count).magnitude / count;
        }
        return sums.Select(kv => new Bead { centre = kv.Value.sum / kv.Value.count, radius = radii[kv.Key] }).ToList();
    }

    /// <summary>
    /// A smooth surface round beads: each adds a Gaussian density (1 at its own radius), contoured at 1 on a grid by
    /// surface nets (a vertex in each grid cell the surface crosses, at the mean of its edge crossings; a quad across
    /// each grid edge it crosses). Everything nearer the centre than core counts as inside. In toy units.
    /// </summary>
    static Mesh Surface(List<Bead> beads, float core, float h)
    {
        var lowest = beads.Aggregate(Vector3.positiveInfinity, (m, b) => Vector3.Min(m, b.centre - Vector3.one * 2f * b.radius)) - Vector3.one * 2f * h;
        var highest = beads.Aggregate(Vector3.negativeInfinity, (m, b) => Vector3.Max(m, b.centre + Vector3.one * 2f * b.radius)) + Vector3.one * 2f * h;
        int nx = Mathf.CeilToInt((highest.x - lowest.x) / h) + 1, ny = Mathf.CeilToInt((highest.y - lowest.y) / h) + 1, nz = Mathf.CeilToInt((highest.z - lowest.z) / h) + 1;
        var density = new float[nx * ny * nz];
        int Index(int x, int y, int z) => (z * ny + y) * nx + x;
        Vector3 Point(int x, int y, int z) => lowest + new Vector3(x, y, z) * h;

        foreach (var bead in beads)
        {
            float reach = 2f * bead.radius;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((bead.centre.x - reach - lowest.x) / h)), x1 = Mathf.Min(nx - 1, Mathf.CeilToInt((bead.centre.x + reach - lowest.x) / h));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((bead.centre.y - reach - lowest.y) / h)), y1 = Mathf.Min(ny - 1, Mathf.CeilToInt((bead.centre.y + reach - lowest.y) / h));
            int z0 = Mathf.Max(0, Mathf.FloorToInt((bead.centre.z - reach - lowest.z) / h)), z1 = Mathf.Min(nz - 1, Mathf.CeilToInt((bead.centre.z + reach - lowest.z) / h));
            float inverse = 1f / (bead.radius * bead.radius);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float d2 = (Point(x, y, z) - bead.centre).sqrMagnitude * inverse;
                        if (d2 < 4f)
                            density[Index(x, y, z)] += Mathf.Exp(-k_Blobbiness * (d2 - 1f));
                    }
        }
        if (core > 0f)
        {
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        if (Point(x, y, z).sqrMagnitude < core * core)
                            density[Index(x, y, z)] = Mathf.Max(density[Index(x, y, z)], 2f);

            // Solid: whatever the space outside can't reach (flooded in from the grid's edges) is inside, so only
            // the outer skin is left.
            var outside = new bool[density.Length];
            var queue = new Queue<int>();
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        if ((x == 0 || y == 0 || z == 0 || x == nx - 1 || y == ny - 1 || z == nz - 1) && density[Index(x, y, z)] <= 1f)
                        {
                            outside[Index(x, y, z)] = true;
                            queue.Enqueue(Index(x, y, z));
                        }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
                void Visit(int vx, int vy, int vz)
                {
                    if (vx < 0 || vy < 0 || vz < 0 || vx >= nx || vy >= ny || vz >= nz)
                        return;
                    int j = Index(vx, vy, vz);
                    if (outside[j] || density[j] > 1f)
                        return;
                    outside[j] = true;
                    queue.Enqueue(j);
                }
                Visit(x - 1, y, z);
                Visit(x + 1, y, z);
                Visit(x, y - 1, z);
                Visit(x, y + 1, z);
                Visit(x, y, z - 1);
                Visit(x, y, z + 1);
            }
            for (int i = 0; i < density.Length; i++)
                if (!outside[i] && density[i] <= 1f)
                    density[i] = 2f;
        }

        // A vertex in each cell the surface passes through.
        var cellVertex = new Dictionary<int, int>();
        var vertices = new List<Vector3>();
        int CellIndex(int x, int y, int z) => (z * (ny - 1) + y) * (nx - 1) + x;
        var corners = new Vector3Int[8];
        for (int c = 0; c < 8; c++)
            corners[c] = new Vector3Int(c & 1, (c >> 1) & 1, (c >> 2) & 1);
        int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
        var values = new float[8];
        for (int z = 0; z < nz - 1; z++)
            for (int y = 0; y < ny - 1; y++)
                for (int x = 0; x < nx - 1; x++)
                {
                    int inside = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        values[c] = density[Index(x + corners[c].x, y + corners[c].y, z + corners[c].z)] - 1f;
                        if (values[c] > 0f)
                            inside++;
                    }
                    if (inside == 0 || inside == 8)
                        continue;
                    var sum = Vector3.zero;
                    int crossings = 0;
                    for (int e = 0; e < 12; e++)
                    {
                        float a = values[edges[e, 0]], b = values[edges[e, 1]];
                        if (a > 0f == b > 0f)
                            continue;
                        float t = a / (a - b);
                        sum += Vector3.Lerp(corners[edges[e, 0]], corners[edges[e, 1]], t);
                        crossings++;
                    }
                    cellVertex[CellIndex(x, y, z)] = vertices.Count;
                    vertices.Add(Point(x, y, z) + sum / crossings * h);
                }

        // A quad across each grid edge the surface crosses, between the four cells round it.
        var triangles = new List<int>();
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (flip)
                triangles.AddRange(new[] { a, c, b, a, d, c });
            else
                triangles.AddRange(new[] { a, b, c, a, c, d });
        }
        for (int z = 1; z < nz - 1; z++)
            for (int y = 1; y < ny - 1; y++)
                for (int x = 1; x < nx - 1; x++)
                {
                    bool here = density[Index(x, y, z)] > 1f;
                    if (here != density[Index(x + 1, y, z)] > 1f
                        && cellVertex.TryGetValue(CellIndex(x, y - 1, z - 1), out int a) && cellVertex.TryGetValue(CellIndex(x, y, z - 1), out int b)
                        && cellVertex.TryGetValue(CellIndex(x, y, z), out int c) && cellVertex.TryGetValue(CellIndex(x, y - 1, z), out int d))
                        Quad(a, b, c, d, !here);
                    if (here != density[Index(x, y + 1, z)] > 1f
                        && cellVertex.TryGetValue(CellIndex(x - 1, y, z - 1), out a) && cellVertex.TryGetValue(CellIndex(x - 1, y, z), out b)
                        && cellVertex.TryGetValue(CellIndex(x, y, z), out c) && cellVertex.TryGetValue(CellIndex(x, y, z - 1), out d))
                        Quad(a, b, c, d, !here);
                    if (here != density[Index(x, y, z + 1)] > 1f
                        && cellVertex.TryGetValue(CellIndex(x - 1, y - 1, z), out a) && cellVertex.TryGetValue(CellIndex(x, y - 1, z), out b)
                        && cellVertex.TryGetValue(CellIndex(x, y, z), out c) && cellVertex.TryGetValue(CellIndex(x - 1, y, z), out d))
                        Quad(a, b, c, d, !here);
                }

        var mesh = new Mesh { indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.SetVertices(vertices.Select(v => v * k_UnitsPerAngstrom).ToList());
        mesh.SetTriangles(triangles, 0);

        // Facing out: a closed surface facing out encloses a positive volume.
        float volume = 0f;
        var v3 = mesh.vertices;
        var t3 = mesh.triangles;
        for (int i = 0; i < t3.Length; i += 3)
            volume += Vector3.Dot(v3[t3[i]], Vector3.Cross(v3[t3[i + 1]], v3[t3[i + 2]]));
        if (volume < 0f)
        {
            for (int i = 0; i < t3.Length; i += 3)
                (t3[i + 1], t3[i + 2]) = (t3[i + 2], t3[i + 1]);
            mesh.SetTriangles(t3, 0);
        }
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// The spikes: spike beads that touch are one spike. Each gets the cylinder that encloses its beads, along its
    /// length (its beads' principal axis, pointing out of the virion), from its foot to its tip.
    /// </summary>
    static List<(Vector3 basePoint, Vector3 axis, float length, float radius)> Spikes(List<Bead> beads)
    {
        float cell = beads.Max(b => b.radius) * 2f + k_Touching;
        var grid = new Dictionary<Vector3Int, List<int>>();
        Vector3Int Cell(Vector3 p) => new(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
        for (int i = 0; i < beads.Count; i++)
        {
            var key = Cell(beads[i].centre);
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = new List<int>();
            list.Add(i);
        }
        var parent = Enumerable.Range(0, beads.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }
        for (int i = 0; i < beads.Count; i++)
        {
            var key = Cell(beads[i].centre);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue(key + new Vector3Int(dx, dy, dz), out var list))
                            continue;
                        foreach (int j in list)
                            if (j > i && (beads[i].centre - beads[j].centre).magnitude < beads[i].radius + beads[j].radius + k_Touching)
                                parent[Find(j)] = Find(i);
                    }
        }

        var result = new List<(Vector3, Vector3, float, float)>();
        foreach (var group in Enumerable.Range(0, beads.Count).GroupBy(Find))
        {
            var members = group.Select(i => beads[i]).ToList();
            if (members.Count < 10)
                continue; // a stray bead or two, not a spike
            var mean = members.Aggregate(Vector3.zero, (s, b) => s + b.centre) / members.Count;

            // Principal axis: power iteration on the covariance.
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var b in members)
            {
                var d = b.centre - mean;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            var axis = mean.normalized;
            for (int k = 0; k < 50; k++)
                axis = new Vector3(xx * axis.x + xy * axis.y + xz * axis.z, xy * axis.x + yy * axis.y + yz * axis.z, xz * axis.x + yz * axis.y + zz * axis.z).normalized;
            if (Vector3.Dot(axis, mean) < 0f)
                axis = -axis;

            float low = float.MaxValue, high = float.MinValue, radius = 0f;
            foreach (var b in members)
            {
                var d = b.centre - mean;
                float along = Vector3.Dot(d, axis);
                low = Mathf.Min(low, along - b.radius);
                high = Mathf.Max(high, along + b.radius);
                radius = Mathf.Max(radius, (d - axis * along).magnitude + b.radius);
            }
            result.Add((mean + axis * low, axis, high - low, radius));
        }
        return result;
    }

    // A cylinder of diameter 1 along +z from 0 to 1.
    static Mesh CylinderMesh(int sides)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2f / sides;
            var p = new Vector3(Mathf.Cos(a) * 0.5f, Mathf.Sin(a) * 0.5f, 0f);
            vertices.Add(p);
            vertices.Add(p + Vector3.forward);
        }
        int bottom = vertices.Count;
        vertices.Add(Vector3.zero);
        int top = vertices.Count;
        vertices.Add(Vector3.forward);
        for (int i = 0; i < sides; i++)
        {
            int a = i * 2, b = (i + 1) % sides * 2;
            triangles.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1, bottom, b, a, top, a + 1, b + 1 });
        }
        var mesh = new Mesh { name = "SARS_Spike_Collider" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
