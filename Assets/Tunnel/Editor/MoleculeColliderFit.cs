using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// The smallest collider around a molecule's surface, of two kinds: an oriented box (the rectangular solid of least
/// volume, found by searching orientations) and an ellipsoid (the minimum-volume enclosing ellipsoid, by Khachiyan's
/// algorithm). Both enclose every vertex. Unity has no ellipsoid collider, so the ellipsoid is a convex mesh collider:
/// a 12 x 8 polyhedron pushed out just enough that its faces clear the true ellipsoid, and its volume is compared as
/// that polyhedron's, so the choice is between the colliders as they really are.
/// </summary>
static class MoleculeColliderFit
{
    public enum Kind { Box, Ellipsoid }

    public struct Fit
    {
        public Kind kind;
        public Vector3 center;      // in the molecule's frame
        public Quaternion rotation; // the collider's axes
        public Vector3 size;        // box: full size along its axes; ellipsoid: semi-axes
        public float volume;        // of the collider as built
    }

    // The ellipsoid polyhedron: segments around and bands from pole to pole (168 triangles; a convex mesh collider
    // allows 255).
    const int k_Segments = 12, k_Bands = 8;

    const int k_SearchPoints = 3000;

    public static Fit Box(IReadOnlyList<Vector3> points)
    {
        var sample = Sample(points, k_SearchPoints);

        // Start from the principal axes, then search rotations around them, coarse to fine.
        var best = PrincipalAxes(points);
        float bestVolume = BoxVolume(sample, best, out _, out _);
        foreach (float step in new[] { 15f, 5f, 1.5f, 0.5f, 0.15f })
        {
            bool improved = true;
            for (int round = 0; improved && round < 20; round++)
            {
                improved = false;
                var center = best;
                for (int x = -3; x <= 3; x++)
                for (int y = -3; y <= 3; y++)
                for (int z = -3; z <= 3; z++)
                {
                    var rotation = center * Quaternion.Euler(x * step, y * step, z * step);
                    float volume = BoxVolume(sample, rotation, out _, out _);
                    if (volume < bestVolume - 1e-9f)
                    {
                        bestVolume = volume;
                        best = rotation;
                        improved = true;
                    }
                }
            }
        }

        // Exact extents over every vertex.
        float exact = BoxVolume(points, best, out var localCenter, out var size);
        return new Fit { kind = Kind.Box, rotation = best, center = best * localCenter, size = size, volume = exact };
    }

    public static Fit Ellipsoid(IReadOnlyList<Vector3> points)
    {
        // Khachiyan's algorithm on the convex hull's likely vertices (the points furthest out in many directions),
        // which is where the enclosing ellipsoid touches; the result is checked against every point below.
        var hull = ExtremePoints(points, 2000);
        int n = hull.Count;
        var u = Enumerable.Repeat(1.0 / n, n).ToArray();
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            // X = sum u_i q_i q_i^T with q_i = (p_i, 1)
            var X = new double[4, 4];
            for (int i = 0; i < n; i++)
            {
                var q = Lift(hull[i]);
                for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    X[r, c] += u[i] * q[r] * q[c];
            }

            var Xi = Invert(X);
            int j = 0;
            double maxM = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                var q = Lift(hull[i]);
                double m = 0;
                for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m += q[r] * Xi[r, c] * q[c];
                if (m > maxM)
                {
                    maxM = m;
                    j = i;
                }
            }

            double step = (maxM - 4) / (4 * (maxM - 1));
            if (step < 1e-5)
                break;
            for (int i = 0; i < n; i++)
                u[i] *= 1 - step;
            u[j] += step;
        }

        // Centre and shape: (x - c)^T A (x - c) <= 1 with A = (P U P^T - c c^T)^-1 / 3.
        var cen = new double[3];
        for (int i = 0; i < n; i++)
            for (int r = 0; r < 3; r++)
                cen[r] += u[i] * hull[i][r];
        var S = new double[3, 3];
        for (int i = 0; i < n; i++)
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                S[r, c] += u[i] * (hull[i][r] - cen[r]) * (hull[i][c] - cen[c]);

        // The ellipsoid's axes are S's eigenvectors, its semi-axes sqrt(3 * eigenvalue).
        Jacobi(S, out var values, out var vectors);
        var center = new Vector3((float)cen[0], (float)cen[1], (float)cen[2]);
        var axes = Enumerable.Range(0, 3).Select(k => new Vector3((float)vectors[0, k], (float)vectors[1, k], (float)vectors[2, k]).normalized).ToArray();
        if (Vector3.Dot(Vector3.Cross(axes[0], axes[1]), axes[2]) < 0f)
            axes[2] = -axes[2];
        var rotation = Quaternion.LookRotation(axes[2], axes[1]);
        var semi = new Vector3(
            Mathf.Sqrt(3f * Mathf.Max((float)values[0], 1e-12f)),
            Mathf.Sqrt(3f * Mathf.Max((float)values[1], 1e-12f)),
            Mathf.Sqrt(3f * Mathf.Max((float)values[2], 1e-12f)));
        // LookRotation(z = axes[2], up = axes[1]) puts axes[0] on x (up to sign), so the semi-axes line up.

        // Grow it until every vertex is inside (the algorithm stops a little short of the optimum).
        var inverse = Quaternion.Inverse(rotation);
        float worst = 0f;
        foreach (var p in points)
        {
            var l = inverse * (p - center);
            worst = Mathf.Max(worst, (l.x * l.x) / (semi.x * semi.x) + (l.y * l.y) / (semi.y * semi.y) + (l.z * l.z) / (semi.z * semi.z));
        }
        semi *= Mathf.Sqrt(Mathf.Max(worst, 1f)) * 1.001f;

        float polyhedron = Mathf.Pow(PolyhedronGrowth, 3) * PolyhedronVolumeFactor;
        return new Fit
        {
            kind = Kind.Ellipsoid,
            center = center,
            rotation = rotation,
            size = semi,
            volume = polyhedron * semi.x * semi.y * semi.z,
        };
    }

    /// <summary>The ellipsoid collider's mesh: a unit sphere polyhedron whose faces all lie outside the unit sphere,
    /// stretched to the semi-axes.</summary>
    public static Mesh EllipsoidMesh(Vector3 semiAxes, string name)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        vertices.Add(Vector3.up);
        for (int b = 1; b < k_Bands; b++)
        {
            float theta = Mathf.PI * b / k_Bands;
            for (int s = 0; s < k_Segments; s++)
            {
                float phi = 2f * Mathf.PI * s / k_Segments;
                vertices.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi)));
            }
        }
        vertices.Add(Vector3.down);
        int last = vertices.Count - 1;
        int Ring(int b, int s) => 1 + (b - 1) * k_Segments + (s % k_Segments);
        for (int s = 0; s < k_Segments; s++)
        {
            triangles.AddRange(new[] { 0, Ring(1, s + 1), Ring(1, s) });
            triangles.AddRange(new[] { last, Ring(k_Bands - 1, s), Ring(k_Bands - 1, s + 1) });
        }
        for (int b = 1; b < k_Bands - 1; b++)
            for (int s = 0; s < k_Segments; s++)
            {
                triangles.AddRange(new[] { Ring(b, s), Ring(b, s + 1), Ring(b + 1, s + 1) });
                triangles.AddRange(new[] { Ring(b, s), Ring(b + 1, s + 1), Ring(b + 1, s) });
            }

        float grow = PolyhedronGrowth;
        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices.Select(v => Vector3.Scale(v * grow, semiAxes)).ToList());
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Vertices on the unit sphere make a polyhedron that dips inside it between them; this pushes every face out
    // past the sphere: the nearest a face comes to the centre is at least cos(half the segment angle) x cos(half the
    // band angle) of the vertex radius.
    static float PolyhedronGrowth => 1f / (Mathf.Cos(Mathf.PI / k_Segments) * Mathf.Cos(Mathf.PI / (2f * k_Bands)));

    // Volume of the unit polyhedron (before growth), as a fraction of the box 1 x 1 x 1 semi-axes: computed once.
    static float? s_PolyhedronVolumeFactor;
    static float PolyhedronVolumeFactor
    {
        get
        {
            if (s_PolyhedronVolumeFactor == null)
            {
                var mesh = EllipsoidMesh(Vector3.one / PolyhedronGrowth, "probe");
                var v = mesh.vertices;
                var t = mesh.triangles;
                double volume = 0;
                for (int i = 0; i < t.Length; i += 3)
                    volume += Vector3.Dot(v[t[i]], Vector3.Cross(v[t[i + 1]], v[t[i + 2]])) / 6.0;
                Object.DestroyImmediate(mesh);
                s_PolyhedronVolumeFactor = Mathf.Abs((float)volume);
            }
            return s_PolyhedronVolumeFactor.Value;
        }
    }

    static float BoxVolume(IReadOnlyList<Vector3> points, Quaternion rotation, out Vector3 localCenter, out Vector3 size)
    {
        var inverse = Quaternion.Inverse(rotation);
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            var l = inverse * points[i];
            min = Vector3.Min(min, l);
            max = Vector3.Max(max, l);
        }
        size = max - min;
        localCenter = (min + max) * 0.5f;
        return size.x * size.y * size.z;
    }

    static Quaternion PrincipalAxes(IReadOnlyList<Vector3> points)
    {
        var mean = Vector3.zero;
        foreach (var p in points)
            mean += p;
        mean /= points.Count;
        var C = new double[3, 3];
        foreach (var p in points)
        {
            var d = p - mean;
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                C[r, c] += d[r] * d[c];
        }
        Jacobi(C, out _, out var vectors);
        var z = new Vector3((float)vectors[0, 2], (float)vectors[1, 2], (float)vectors[2, 2]);
        var y = new Vector3((float)vectors[0, 1], (float)vectors[1, 1], (float)vectors[2, 1]);
        return Quaternion.LookRotation(z, y);
    }

    static List<Vector3> Sample(IReadOnlyList<Vector3> points, int count)
    {
        var extreme = ExtremePoints(points, count);
        return extreme.Count > 0 ? extreme : points.ToList();
    }

    /// <summary>The points furthest out along many directions spread over the sphere: they include every point that
    /// decides a bounding box or ellipsoid, at a fraction of the cost.</summary>
    static List<Vector3> ExtremePoints(IReadOnlyList<Vector3> points, int directions)
    {
        var chosen = new HashSet<int>();
        float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
        for (int k = 0; k < directions; k++)
        {
            float y = 1f - 2f * (k + 0.5f) / directions;
            float r = Mathf.Sqrt(1f - y * y);
            var direction = new Vector3(Mathf.Cos(golden * k) * r, y, Mathf.Sin(golden * k) * r);
            int best = 0;
            float bestDot = float.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                float dot = Vector3.Dot(points[i], direction);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = i;
                }
            }
            chosen.Add(best);
        }
        return chosen.Select(i => points[i]).ToList();
    }

    static double[] Lift(Vector3 p) => new double[] { p.x, p.y, p.z, 1.0 };

    static double[,] Invert(double[,] m)
    {
        int n = m.GetLength(0);
        var a = new double[n, 2 * n];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
                a[r, c] = m[r, c];
            a[r, n + r] = 1;
        }
        for (int c = 0; c < n; c++)
        {
            int pivot = c;
            for (int r = c + 1; r < n; r++)
                if (System.Math.Abs(a[r, c]) > System.Math.Abs(a[pivot, c]))
                    pivot = r;
            for (int k = 0; k < 2 * n; k++)
                (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
            double d = a[c, c];
            for (int k = 0; k < 2 * n; k++)
                a[c, k] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == c)
                    continue;
                double f = a[r, c];
                for (int k = 0; k < 2 * n; k++)
                    a[r, k] -= f * a[c, k];
            }
        }
        var inverse = new double[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                inverse[r, c] = a[r, n + c];
        return inverse;
    }

    /// <summary>Eigenvalues (ascending) and eigenvectors (columns) of a symmetric 3 x 3 matrix.</summary>
    static void Jacobi(double[,] input, out double[] values, out double[,] vectors)
    {
        var a = (double[,])input.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
            if (off < 1e-20)
                break;
            for (int p = 0; p < 2; p++)
            for (int q = p + 1; q < 3; q++)
            {
                if (System.Math.Abs(a[p, q]) < 1e-30)
                    continue;
                double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                double t = System.Math.Sign(theta) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1));
                if (theta == 0)
                    t = 1;
                double c = 1 / System.Math.Sqrt(t * t + 1), s = t * c;
                for (int k = 0; k < 3; k++)
                {
                    double akp = a[k, p], akq = a[k, q];
                    a[k, p] = c * akp - s * akq;
                    a[k, q] = s * akp + c * akq;
                }
                for (int k = 0; k < 3; k++)
                {
                    double apk = a[p, k], aqk = a[q, k];
                    a[p, k] = c * apk - s * aqk;
                    a[q, k] = s * apk + c * aqk;
                }
                for (int k = 0; k < 3; k++)
                {
                    double vkp = v[k, p], vkq = v[k, q];
                    v[k, p] = c * vkp - s * vkq;
                    v[k, q] = s * vkp + c * vkq;
                }
            }
        }

        var order = Enumerable.Range(0, 3).OrderBy(i => a[i, i]).ToArray();
        values = order.Select(i => a[i, i]).ToArray();
        vectors = new double[3, 3];
        for (int k = 0; k < 3; k++)
            for (int r = 0; r < 3; r++)
                vectors[r, k] = v[r, order[k]];
    }
}
