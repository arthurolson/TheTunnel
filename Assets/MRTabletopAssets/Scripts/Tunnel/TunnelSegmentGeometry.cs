using System.Collections.Generic;

namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// Builds the Tunnel Segment (TG) shell: two vertical walls joined at the top by a cylindrical arch.
    /// Dimensions are authored in feet. The local origin is the centre of the floor, the tunnel runs along
    /// local Z and the walls sit at ±X. Inner and outer faces are separate triangles, so both sides light
    /// correctly and the shell can be made transparent.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    public class TunnelSegmentGeometry : MonoBehaviour
    {
        const float k_FeetToMeters = 0.3048f;

        [SerializeField]
        [Tooltip("Height of each wall, from the floor to where the arch begins (feet).")]
        float m_WallHeightFeet = 8f;

        [SerializeField]
        [Tooltip("Distance between the two walls (feet).")]
        float m_WidthFeet = 9f;

        [SerializeField]
        [Tooltip("Height of the top of the arch above the floor (feet).")]
        float m_ApexHeightFeet = 10f;

        [SerializeField]
        [Tooltip("Length of the segment along the tunnel (feet).")]
        float m_LengthFeet = 10f;

        [SerializeField]
        [Range(4, 64)]
        int m_ArchSegments = 32;

        [SerializeField]
        [Tooltip("Optional floor between the walls; gets its own mesh so it can be shaded separately.")]
        MeshFilter m_Floor;

        [Header("Placed relative to the shell")]
        [SerializeField]
        [Tooltip("Grab handles, placed upright at the near (-Z) end of each wall.")]
        Transform[] m_Handles;

        [SerializeField]
        [Tooltip("Height of the centre of the grab handles above the floor (feet).")]
        float m_HandleHeightFeet = 3f;

        [SerializeField]
        [Tooltip("How far the grab handles stand out in front of the near end of the walls (meters).")]
        float m_HandleOutset = 0.06f;

        [SerializeField]
        [Tooltip("Interior control panel (buttons + colour chooser), placed on the inside of the +X wall near the near (-Z) end, by the entrance.")]
        Transform m_ControlPanel;

        [SerializeField]
        [Tooltip("Height of the control panel above the floor (meters).")]
        float m_ControlPanelHeight = 1.3f;

        [SerializeField]
        [Tooltip("Distance of the control panel from the near (-Z) end of the segment (meters).")]
        float m_ControlPanelInset = 0.4f;

        [Header("Collision")]
        [SerializeField]
        [Tooltip("Give the walls, arch and floor solid colliders at runtime so physics objects bounce off them.")]
        bool m_GenerateColliders = true;

        [SerializeField]
        [Tooltip("Thickness of the collision boxes, extending outward from the visible surfaces (meters).")]
        float m_ColliderThickness = 0.05f;

        [SerializeField]
        [Range(4, 32)]
        int m_ArchColliderSegments = 12;

        Mesh m_Mesh;
        Mesh m_FloorMesh;
        Transform m_ColliderRoot;

        public float width => m_WidthFeet * k_FeetToMeters;
        public float wallHeight => m_WallHeightFeet * k_FeetToMeters;
        public float apexHeight => m_ApexHeightFeet * k_FeetToMeters;
        public float length => m_LengthFeet * k_FeetToMeters;

        /// <summary>Distance around the profile (walls and arch), in meters. One texture height spans this.</summary>
        public float perimeter { get; private set; } = 1f;

        /// <summary>Every tunnel segment in the scene.</summary>
        public static readonly List<TunnelSegmentGeometry> all = new();

        /// <summary>Goes up whenever any segment's colliders are rebuilt (so whoever ignores them can do it again).</summary>
        public static int collidersVersion { get; private set; }

        // Openings in the flat walls, by whoever made them: the wall (-1 for -X, 1 for +X) and the rectangle, in the
        // wall's own coordinates (x along the tunnel, local Z; y up from the floor).
        readonly Dictionary<object, (int side, Rect rect)> m_Openings = new();

        void OnEnable()
        {
            if (!all.Contains(this))
                all.Add(this);
            Build();
        }

        void OnDisable() => all.Remove(this);

        /// <summary>
        /// Makes (or moves) a hole in a flat wall's collider, so things pass through it there and lasers reach past it:
        /// where a Container's mixed reality box is set into the wall. The rectangle is in the wall's own coordinates
        /// (x along the tunnel, local Z; y up from the floor). Only the collider has the hole; the shell is drawn.
        /// </summary>
        public void SetWallOpening(object owner, int side, Rect rect)
        {
            if (m_Openings.TryGetValue(owner, out var current) && current.side == side
                && Vector2.Distance(current.rect.min, rect.min) < 0.002f && Vector2.Distance(current.rect.max, rect.max) < 0.002f)
                return;
            m_Openings[owner] = (side, rect);
            if (m_ColliderRoot != null && Application.isPlaying)
                BuildColliders();
        }

        /// <summary>Closes the hole <see cref="SetWallOpening"/> made.</summary>
        public void ClearWallOpening(object owner)
        {
            if (m_Openings.Remove(owner) && m_ColliderRoot != null && Application.isPlaying)
                BuildColliders();
        }

        /// <summary>Whether these colliders pass through every segment's walls, arch and floor (or collide again).</summary>
        public static void IgnoreWalls(IReadOnlyList<Collider> colliders, bool ignore)
        {
            foreach (var segment in all)
            {
                if (segment == null || segment.m_ColliderRoot == null)
                    continue;
                foreach (Transform box in segment.m_ColliderRoot)
                {
                    var wall = box.GetComponent<Collider>();
                    if (wall == null || !box.gameObject.activeSelf)
                        continue; // a spare (switched back on later, it's rebuilt and ignored again)
                    foreach (var collider in colliders)
                        if (collider != null)
                            Physics.IgnoreCollision(collider, wall, ignore);
                }
            }
        }

        void Start()
        {
            // Colliders are runtime-only objects, so they're never saved into the prefab or scene.
            if (Application.isPlaying && m_GenerateColliders)
                BuildColliders();
        }

        void OnValidate()
        {
            m_WallHeightFeet = Mathf.Max(0.1f, m_WallHeightFeet);
            m_WidthFeet = Mathf.Max(0.1f, m_WidthFeet);
            m_ApexHeightFeet = Mathf.Max(m_WallHeightFeet + 0.01f, m_ApexHeightFeet);
            m_LengthFeet = Mathf.Max(0.1f, m_LengthFeet);
            if (isActiveAndEnabled)
                Build();
        }

        void OnDestroy()
        {
            foreach (var mesh in new[] { m_Mesh, m_FloorMesh })
            {
                if (mesh == null)
                    continue;
                if (Application.isPlaying)
                    Destroy(mesh);
                else
                    DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// Regenerates the shell mesh and re-places the handles and control panel.
        /// </summary>
        public void Build()
        {
            if (m_Mesh == null)
            {
                m_Mesh = new Mesh { name = "TG Tunnel Segment" };
                m_Mesh.hideFlags = HideFlags.DontSave;
            }

            float halfWidth = width * 0.5f;
            float halfLength = length * 0.5f;

            // Cross-section profile, left wall foot -> up -> over the arch -> down -> right wall foot.
            // Arch: circle through both wall tops and the apex; sagitta s over half-chord a gives r = (a² + s²) / 2s.
            float sagitta = apexHeight - wallHeight;
            float radius = (halfWidth * halfWidth + sagitta * sagitta) / (2f * sagitta);
            float centerY = apexHeight - radius;
            float startAngle = Mathf.Atan2(wallHeight - centerY, -halfWidth);
            float endAngle = Mathf.Atan2(wallHeight - centerY, halfWidth);

            var profile = new List<Vector2> { new(-halfWidth, 0f), new(-halfWidth, wallHeight) };
            var inward = new List<Vector2> { Vector2.right, Vector2.right };
            for (int i = 1; i < m_ArchSegments; i++)
            {
                float a = Mathf.Lerp(startAngle, endAngle, i / (float)m_ArchSegments);
                var p = new Vector2(Mathf.Cos(a) * radius, centerY + Mathf.Sin(a) * radius);
                profile.Add(p);
                inward.Add((new Vector2(0f, centerY) - p).normalized);
            }
            profile.Add(new Vector2(halfWidth, wallHeight));
            profile.Add(new Vector2(halfWidth, 0f));
            inward.Add(Vector2.left);
            inward.Add(Vector2.left);

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            // Texture coordinates: v runs 0..1 around the profile (floor of the -X wall, up, over the arch and down
            // the +X wall) by distance along the surface, so an image wraps walls and ceiling as one continuous
            // piece without stretching on the curve. u is distance along the tunnel in the same units (fractions of
            // the profile length), so square texels stay square; materials set the tiling for the image's aspect.
            var distance = new float[profile.Count];
            for (int i = 1; i < profile.Count; i++)
                distance[i] = distance[i - 1] + Vector2.Distance(profile[i - 1], profile[i]);
            perimeter = distance[profile.Count - 1];

            // Each profile edge becomes a quad along Z, emitted once facing in and once facing out.
            // The wall-to-arch corner gets its own vertices so the wall stays flat-shaded.
            for (int i = 0; i < profile.Count - 1; i++)
            {
                Vector2 a = profile[i], b = profile[i + 1];
                bool wallEdge = Mathf.Approximately(a.x, b.x);
                Vector2 na = wallEdge ? (a.x < 0 ? Vector2.right : Vector2.left) : inward[i];
                Vector2 nb = wallEdge ? na : inward[i + 1];
                if (!wallEdge)
                {
                    // At the wall tops the arch vertex uses the arch normal, not the wall's.
                    if (i == 1)
                        na = (new Vector2(0f, centerY) - a).normalized;
                    if (i + 1 == profile.Count - 2)
                        nb = (new Vector2(0f, centerY) - b).normalized;
                }

                float va = distance[i] / perimeter, vb = distance[i + 1] / perimeter;
                float uMax = length / perimeter;
                AddQuad(vertices, normals, uvs, triangles, a, b, na, nb, va, vb, uMax, halfLength, true);
                AddQuad(vertices, normals, uvs, triangles, a, b, -na, -nb, va, vb, uMax, halfLength, false);
            }

            m_Mesh.Clear();
            m_Mesh.SetVertices(vertices);
            m_Mesh.SetNormals(normals);
            m_Mesh.SetUVs(0, uvs);
            m_Mesh.SetTriangles(triangles, 0);
            m_Mesh.RecalculateBounds();
            GetComponent<MeshFilter>().sharedMesh = m_Mesh;

            if (m_Floor != null)
                BuildFloor(halfWidth, halfLength);
            if (m_ColliderRoot != null && Application.isPlaying)
                BuildColliders();

            PlaceAttachments(halfWidth, halfLength);
        }

        /// <summary>
        /// Solid boxes behind each visible surface: one per wall, one under the floor and a chain around the arch.
        /// The boxes' inner faces lie on the surfaces, so objects inside bounce exactly where the shell is drawn.
        /// </summary>
        public void BuildColliders()
        {
            if (m_ColliderRoot == null)
            {
                m_ColliderRoot = new GameObject("Colliders").transform;
                m_ColliderRoot.SetParent(transform, false);
            }

            float t = m_ColliderThickness;
            float halfWidth = width * 0.5f;
            float l = length;

            // The flat walls, in pieces around any openings.
            var walls = new List<(string name, Vector3 center, Vector3 size)>();
            foreach (int side in new[] { -1, 1 })
                AddWallPieces(walls, side, halfWidth, t);

            int needed = 1 + m_ArchColliderSegments + walls.Count;
            while (m_ColliderRoot.childCount < needed)
                new GameObject("Box", typeof(BoxCollider)).transform.SetParent(m_ColliderRoot, false);
            // Boxes no longer needed are switched off, never destroyed: the hands' and lasers' queries may still be
            // holding on to one this frame, and a destroyed collider there stops them grabbing anything at all.
            for (int i = 0; i < m_ColliderRoot.childCount; i++)
            {
                var box = m_ColliderRoot.GetChild(i);
                bool used = i < needed;
                if (box.gameObject.activeSelf != used)
                    box.gameObject.SetActive(used);
                if (!used)
                    box.name = "Unused";
            }
            collidersVersion++;

            SetBox(0, "Floor", new Vector3(0f, -t * 0.5f, 0f), 0f, new Vector3(width + 2f * t, t, l));
            for (int i = 0; i < walls.Count; i++)
                SetBox(1 + m_ArchColliderSegments + i, walls[i].name, walls[i].center, 0f, walls[i].size);

            float sagitta = apexHeight - wallHeight;
            float radius = (halfWidth * halfWidth + sagitta * sagitta) / (2f * sagitta);
            var center = new Vector2(0f, apexHeight - radius);
            float startAngle = Mathf.Atan2(wallHeight - center.y, -halfWidth);
            float endAngle = Mathf.Atan2(wallHeight - center.y, halfWidth);
            for (int i = 0; i < m_ArchColliderSegments; i++)
            {
                float a0 = Mathf.Lerp(startAngle, endAngle, i / (float)m_ArchColliderSegments);
                float a1 = Mathf.Lerp(startAngle, endAngle, (i + 1) / (float)m_ArchColliderSegments);
                var p0 = center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
                var p1 = center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
                Vector2 mid = (p0 + p1) * 0.5f;
                Vector2 outward = (mid - center).normalized;
                Vector2 along = p1 - p0;
                Vector2 c = mid + outward * (t * 0.5f);
                float angle = Mathf.Atan2(along.y, along.x) * Mathf.Rad2Deg;
                // A little extra length closes the gaps between neighbouring segments.
                SetBox(1 + i, $"Arch {i}", new Vector3(c.x, c.y, 0f), angle, new Vector3(along.magnitude + t, t, l));
            }
        }

        /// <summary>
        /// One wall's collider as boxes that cover it except its openings: the wall is cut into columns at the
        /// openings' sides, and each column into the stretches no opening covers.
        /// </summary>
        void AddWallPieces(List<(string name, Vector3 center, Vector3 size)> pieces, int side, float halfWidth, float t)
        {
            float halfLength = length * 0.5f, height = wallHeight;
            var wallRect = Rect.MinMaxRect(-halfLength, 0f, halfLength, height);
            var holes = new List<Rect>();
            foreach (var (holeSide, rect) in m_Openings.Values)
            {
                if (holeSide != side || !rect.Overlaps(wallRect))
                    continue;
                holes.Add(Rect.MinMaxRect(Mathf.Max(rect.xMin, -halfLength), Mathf.Max(rect.yMin, 0f),
                    Mathf.Min(rect.xMax, halfLength), Mathf.Min(rect.yMax, height)));
            }

            string name = side < 0 ? "Wall -X" : "Wall +X";
            float x = side * (halfWidth + t * 0.5f);
            if (holes.Count == 0)
            {
                pieces.Add((name, new Vector3(x, height * 0.5f, 0f), new Vector3(t, height, length)));
                return;
            }

            var cuts = new List<float> { -halfLength, halfLength };
            foreach (var hole in holes)
            {
                cuts.Add(hole.xMin);
                cuts.Add(hole.xMax);
            }
            cuts.Sort();
            for (int c = 0; c < cuts.Count - 1; c++)
            {
                float z0 = cuts[c], z1 = cuts[c + 1];
                if (z1 - z0 < 0.001f)
                    continue;
                // The stretches of this column the openings cover, merged; the wall is what's left.
                var covered = new List<Vector2>();
                foreach (var hole in holes)
                    if (hole.xMin < z1 - 0.0005f && hole.xMax > z0 + 0.0005f)
                        covered.Add(new Vector2(hole.yMin, hole.yMax));
                covered.Sort((a, b) => a.x.CompareTo(b.x));
                float y = 0f;
                foreach (var span in covered)
                {
                    if (span.x > y + 0.001f)
                        pieces.Add(($"{name} piece", new Vector3(x, (y + span.x) * 0.5f, (z0 + z1) * 0.5f), new Vector3(t, span.x - y, z1 - z0)));
                    y = Mathf.Max(y, span.y);
                }
                if (height > y + 0.001f)
                    pieces.Add(($"{name} piece", new Vector3(x, (y + height) * 0.5f, (z0 + z1) * 0.5f), new Vector3(t, height - y, z1 - z0)));
            }
        }

        void SetBox(int index, string name, Vector3 center, float zAngle, Vector3 size)
        {
            var box = m_ColliderRoot.GetChild(index);
            box.name = name;
            box.localPosition = center;
            box.localRotation = Quaternion.Euler(0f, 0f, zAngle);
            box.localScale = Vector3.one;
            var collider = box.GetComponent<BoxCollider>();
            collider.center = Vector3.zero;
            collider.size = size;
        }

        void BuildFloor(float halfWidth, float halfLength)
        {
            if (m_FloorMesh == null)
            {
                m_FloorMesh = new Mesh { name = "TG Tunnel Floor" };
                m_FloorMesh.hideFlags = HideFlags.DontSave;
            }

            // A hair above the real floor so it never z-fights with a physical/AR floor; top and underside faces.
            const float lift = 0.003f;
            var corners = new[]
            {
                new Vector3(-halfWidth, lift, -halfLength), new Vector3(-halfWidth, lift, halfLength),
                new Vector3(halfWidth, lift, halfLength), new Vector3(halfWidth, lift, -halfLength),
            };
            var vertices = new List<Vector3>(corners);
            vertices.AddRange(corners);
            var normals = new List<Vector3> { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.down, Vector3.down, Vector3.down, Vector3.down };
            var triangles = new List<int> { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };

            m_FloorMesh.Clear();
            m_FloorMesh.SetVertices(vertices);
            m_FloorMesh.SetNormals(normals);
            m_FloorMesh.SetTriangles(triangles, 0);
            m_FloorMesh.RecalculateBounds();
            m_Floor.sharedMesh = m_FloorMesh;
        }

        static void AddQuad(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles,
            Vector2 a, Vector2 b, Vector2 na, Vector2 nb, float va, float vb, float uMax, float halfLength, bool facingInward)
        {
            int start = vertices.Count;
            vertices.Add(new Vector3(a.x, a.y, -halfLength));
            vertices.Add(new Vector3(b.x, b.y, -halfLength));
            vertices.Add(new Vector3(b.x, b.y, halfLength));
            vertices.Add(new Vector3(a.x, a.y, halfLength));
            normals.Add(new Vector3(na.x, na.y, 0f));
            normals.Add(new Vector3(nb.x, nb.y, 0f));
            normals.Add(new Vector3(nb.x, nb.y, 0f));
            normals.Add(new Vector3(na.x, na.y, 0f));
            uvs.Add(new Vector2(0f, va));
            uvs.Add(new Vector2(0f, vb));
            uvs.Add(new Vector2(uMax, vb));
            uvs.Add(new Vector2(uMax, va));

            // With the profile running left wall -> arch -> right wall, this winding faces the tunnel interior.
            if (facingInward)
                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            else
                triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
        }

        void PlaceAttachments(float halfWidth, float halfLength)
        {
            if (m_Handles != null)
            {
                // Upright bars just in front of the near end of each wall, at about hip height.
                for (int i = 0; i < m_Handles.Length; i++)
                {
                    if (m_Handles[i] == null)
                        continue;
                    float side = i % 2 == 0 ? -1f : 1f;
                    m_Handles[i].localPosition = new Vector3(side * halfWidth, m_HandleHeightFeet * k_FeetToMeters, -halfLength - m_HandleOutset);
                    m_Handles[i].localRotation = Quaternion.identity;
                }
            }

            if (m_ControlPanel != null)
            {
                // Just inside the +X wall near the entrance, facing the tunnel interior (-X).
                m_ControlPanel.localPosition = new Vector3(halfWidth - 0.01f, m_ControlPanelHeight, -halfLength + m_ControlPanelInset);
                m_ControlPanel.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
            }
        }
    }
}
