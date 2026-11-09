using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// Makes the containers' 3D movie (see <see cref="TunnelMovie"/>):
/// the capsids' binding sites (<see cref="CapsidSites"/>) on the symmetry axes found from each capsid's own surface
/// (2-folds for TBSV and poliovirus, 3-folds for Zika); the antibody's Fab tips (<see cref="AntibodyBinder"/>) found
/// from its chains; the movie's control panel (<see cref="TunnelMoviePanel"/>, from the TS Menu's rows and the
/// browser's keyboard); and then the containers again, which carry the movie (Tunnel > Containers > Build Containers).
/// Runs from Tunnel > Movie > Build 3D Movie.
/// </summary>
static class MovieBuilder
{
    const string k_Folder = "Assets/Tunnel/Movie";
    const string k_PanelPath = k_Folder + "/Movie_Panel.prefab";
    const string k_AntibodyPath = "Assets/Molecules/IGG/Toy_IGG_Networked.prefab";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_BrowserPath = "Assets/Tunnel/BrowserPanel.prefab";

    // The capsids and the symmetry axes their sites stand on: 2 = 2-folds, 3 = 3-folds, 5 = 5-folds.
    static readonly (string name, int fold)[] k_Capsids = { ("TBSV", 2), ("2PLV", 2), ("Zika", 3) };

    const float k_UnitsPerAngstrom = 0.2f / 167f; // as the molecule library
    const float k_SiteDiameterFraction = 0.1f;   // of the capsid's diameter

    const float k_CanvasScale = 0.0004f;                   // meters per canvas unit, as the palette
    static readonly Vector2 k_CanvasSize = new(620f, 560f); // 25 x 22 cm

    [MenuItem("Tunnel/Movie/Build 3D Movie")]
    public static string Build()
    {
        if (!AssetDatabase.IsValidFolder(k_Folder))
            AssetDatabase.CreateFolder("Assets/Tunnel", "Movie");
        var log = new StringBuilder("[Movie] ");
        var siteMaterial = SiteMaterial();
        var antibody = AssetDatabase.LoadAssetAtPath<GameObject>(k_AntibodyPath);
        foreach (var (name, fold) in k_Capsids)
            log.Append(BuildCapsid(name, fold, antibody, siteMaterial)).Append("; ");
        log.Append(BuildAntibody()).Append("; ");
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
        string path = $"Assets/Molecules/{name}/Toy_{name}_Networked.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
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
            var title = Text(titleTemplate, canvas, "Title", "3D Movie", new Vector2(0f, 228f), new Vector2(560f, 64f));
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
            var start = Row("Start Row", new Vector2(0f, 155f), 560f, "Start movie", true);
            var pause = Row("Pause Row", new Vector2(-142f, 90f), 276f, "Pause", true);
            var play = Row("Play Row", new Vector2(142f, 90f), 276f, "Play", true);
            var sites = Row("Sites Row", new Vector2(0f, 10f), 560f, "□  Show binding sites", false);
            var save = Row("Save Row", new Vector2(0f, -55f), 560f, "Save starting state...", false);
            var status = Text(titleTemplate, canvas, "Status", "", new Vector2(0f, -185f), new Vector2(560f, 180f));
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

            var component = panel.AddComponent<TunnelMoviePanel>();
            component.Configure(start, pause, play, sites, save, title, status, keyboard, k_CanvasSize.y * k_CanvasScale);
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
