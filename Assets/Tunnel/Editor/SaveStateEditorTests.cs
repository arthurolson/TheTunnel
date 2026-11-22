using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Checks the TS Menu's Saved states (see <see cref="TunnelSaveStateMenu"/>) in the open scene, not connected, through
/// the menu's own buttons: it sets things up in two segments (molecules loose and frozen, a box with a movie, its medium
/// and two molecules inside, a sphere, a Viewer on a wall and a browser standing free, a sign, a New figure, a palette,
/// the spawn nozzle with a molecule on its cup, paint on a wall and a segment's colour), presses Save now, then
/// changes everything (Clear all, a third segment, another colour, the paint wiped), checks the saved state and
/// presses Restore twice. Then it compares what's in the tunnel with what was there when it was saved: each thing's
/// kind and settings, its segment, its pose in that segment and its size; the segments' number, colours and paint.
/// Saved states go to a folder of the test's own, removed afterwards; sharing them sees an empty repository and no
/// token, so nothing goes to GitHub (SharedStatesEditorTests checks sharing). Runs from Tunnel > Menu > Test Saved States (in
/// Play mode, SampleScene; not connected, or with this Editor hosting a session) and logs PASS or FAIL with the tag
/// [SaveState]; it runs over a few seconds.
/// </summary>
static class SaveStateEditorTests
{
    const BindingFlags k_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static object Call(object target, string method, params object[] args)
    {
        var info = target.GetType().GetMethods(k_Flags).First(m => m.Name == method && m.GetParameters().Length == args.Length);
        return info.Invoke(target, args);
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, k_Flags).GetValue(target);

    // Gone for everyone when shared (the host despawns it), else simply destroyed.
    static void Remove(GameObject go)
    {
        if (go.TryGetComponent(out Unity.Netcode.NetworkObject networkObject) && networkObject.IsSpawned)
            networkObject.Despawn(true);
        else
            Object.Destroy(go);
    }

    /// <summary>The last result, for whoever ran it from a script.</summary>
    public static string lastReport { get; private set; } = "";

    public static bool running { get; private set; }

    [MenuItem("Tunnel/Menu/Test Saved States (in Play mode)")]
    public static string Test()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[SaveState] enter Play mode in SampleScene first, then run Tunnel > Menu > Test Saved States");
            return "needs Play mode";
        }
        if (running)
            return "already running";
        running = true;
        lastReport = "running";
        TunnelSaveState.Run(Run());
        return "started: watch the log for [SaveState] PASS or FAIL";
    }

    // One thing as the comparison sees it: what it is and how it is set up, and where.
    struct Thing
    {
        public string key;
        public int segment;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public float tolerance; // meters
        public override string ToString() => $"{key} in segment {segment} at {position:F3}, scale {scale:F3}";
    }

    static IEnumerator Run()
    {
        bool shared = Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsListening;
        var report = new StringBuilder($"[SaveState] saved states test ({(shared ? "shared: this Editor is the host" : "not connected")})\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }

        string previousFolder = TunnelStateStore.folder;
        string folder = Path.Combine(Application.temporaryCachePath, "SavedStatesTest");
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        TunnelStateStore.folder = folder;
        var previousSend = TunnelSharedStates.send;
        string previousToken = TunnelSharedStates.tokenOverride;
        TunnelSharedStates.send = (method, url, data, token) =>
            System.Threading.Tasks.Task.FromResult(new TunnelSharedStates.Reply { code = 404, text = "{\"message\":\"Not Found\"}" });
        TunnelSharedStates.tokenOverride = "";
        TunnelSharedStates.ResetForTests();
        IEnumerator body = Body(report, Check);
        while (true)
        {
            bool more;
            try
            {
                more = body.MoveNext();
            }
            catch (System.Exception e)
            {
                Check(false, $"the test stopped: {e}");
                more = false;
            }
            if (!more)
                break;
            yield return body.Current;
        }
        TunnelStateStore.folder = previousFolder;
        TunnelSharedStates.send = previousSend;
        TunnelSharedStates.tokenOverride = previousToken;
        TunnelSharedStates.ResetForTests();
        try { Directory.Delete(folder, true); } catch { }
        report.Insert(0, failures == 0 ? "PASS " : $"FAIL ({failures}) ");
        lastReport = report.ToString();
        if (failures == 0)
            Debug.Log(lastReport);
        else
            Debug.LogError(lastReport);
        running = false;
    }

    static IEnumerator Body(StringBuilder report, System.Action<bool, string> Check)
    {
        // Two segments at least.
        var segments = TunnelSaveState.Segments();
        Check(segments.Count > 0, $"the scene has a tunnel ({segments.Count} segments)");
        if (segments.Count == 0)
            yield break;
        // Start clean: nothing from before, two segments.
        foreach (var go in TunnelSaveState.Removables())
            Remove(go);
        yield return null;
        while (TunnelSaveState.Segments().Count < 2)
        {
            TunnelSaveState.Segments().Last().GetComponentInParent<TunnelSegmentActions>().AddSegmentAtEnd();
            yield return null;
        }
        while (TunnelSaveState.Segments().Count > 2)
        {
            Remove(TunnelSaveState.Segments().Last().GetComponentInParent<TunnelSegmentLink>().gameObject);
            yield return null;
        }
        segments = TunnelSaveState.Segments();
        var first = segments[0].GetComponentInParent<TunnelSegmentLink>().gameObject;
        var second = segments[1].GetComponentInParent<TunnelSegmentLink>().gameObject;
        var menu = first.GetComponentInChildren<TunnelSaveStateMenu>(true);
        Check(menu != null, "the segment has the Saved states list");
        Check(first.GetComponentInChildren<TunnelMenu>(true) is { } tsMenu && Field<Button>(tsMenu, "m_SavedStatesButton") != null,
            "the TS Menu has its Saved states button");
        if (menu == null)
            yield break;
        Check(menu.prefabs.registry != null && menu.prefabs.splatBox != null && menu.prefabs.nozzle != null && menu.prefabs.figure != null && menu.prefabs.sign != null,
            "the list has every prefab a state is made from");
        var registry = menu.prefabs.registry;
        int Index(string name) => registry.molecules.FindIndex(m => m.name == name);

        T Newest<T>(HashSet<T> before) where T : Object => Object.FindObjectsByType<T>(FindObjectsSortMode.None).FirstOrDefault(t => !before.Contains(t));
        HashSet<T> Now<T>() where T : Object => new(Object.FindObjectsByType<T>(FindObjectsSortMode.None));
        void Move(Component thing, TunnelSegmentGeometry segment, Vector3 local, float yaw = 0f)
        {
            var frame = segment.transform;
            var pose = new Pose(frame.position + frame.rotation * local, frame.rotation * Quaternion.Euler(0f, yaw, 0f));
            thing.transform.SetPositionAndRotation(pose.position, pose.rotation);
            if (thing.TryGetComponent(out Rigidbody body))
            {
                body.position = pose.position;
                body.rotation = pose.rotation;
            }
        }

        var moleculeMenu1 = first.GetComponentInChildren<TunnelMoleculeMenu>(true);
        var moleculeMenu2 = second.GetComponentInChildren<TunnelMoleculeMenu>(true);

        // Molecules: 2PLV frozen at x3 in the first segment, IgG loose and parked in the second.
        var before = Now<MoleculeGravity>();
        Call(moleculeMenu1, "SpawnMolecule", Index("2PLV"), 3, true);
        var capsid = Newest(before);
        Move(capsid, segments[0], new Vector3(-0.4f, 1.3f, 0.5f), 30f);
        before = Now<MoleculeGravity>();
        Call(moleculeMenu2, "SpawnMolecule", Index("IGG"), 5, true);
        var igg = Newest(before);
        Move(igg, segments[1], new Vector3(0.3f, 1.1f, -0.6f), -45f);
        Check(capsid != null && igg != null, "two molecules placed from the Molecules lists");

        // A box in the second segment: resized, its medium set, Ligand binding chosen, two molecules inside.
        var boxes = Now<TunnelContainer>();
        Call(second.GetComponentInChildren<TunnelContainerMenu>(true), "SpawnContainer", TunnelContainer.Shape.Box);
        var box = Newest(boxes);
        Move(box, segments[1], new Vector3(0f, 1.2f, 0.4f), 20f);
        box.Resize(new Vector3(0.5f, 0.4f, 0.3f));
        box.SetViscosity(0.3f);
        box.SetTemperature(15f);
        box.SetMotion(TunnelContainer.Motion.Ballistic);
        box.movie?.SetMovie(MovieDefinition.LigandBinding);
        box.movie?.SetBindingTime(25f);
        var inBox = new List<MoleculeGravity>();
        foreach (var (name, offset) in new[] { ("EGFR", new Vector3(-0.1f, 0f, 0f)), ("EGFR ligand", new Vector3(0.1f, 0.05f, 0f)) })
        {
            before = Now<MoleculeGravity>();
            Call(moleculeMenu2, "SpawnMolecule", Index(name), 4, true);
            var molecule = Newest(before);
            if (molecule == null)
                continue;
            var p = box.transform.position + box.transform.rotation * offset;
            molecule.transform.SetPositionAndRotation(p, box.transform.rotation);
            molecule.body.position = p;
            molecule.body.rotation = box.transform.rotation;
            inBox.Add(molecule);
        }
        Check(box != null && inBox.Count == 2, "a box with two molecules inside");

        // A mixed reality box set into the first segment's wall.
        boxes = Now<TunnelContainer>();
        Call(first.GetComponentInChildren<TunnelContainerMenu>(true), "SpawnContainer", TunnelContainer.Shape.Box);
        var wallBox = Newest(boxes);
        Move(wallBox, segments[0], new Vector3(segments[0].width * 0.5f - 0.2f, 1.2f, -1.0f), 90f);
        wallBox.SetMixedReality(true);
        bool snapped = (bool)Call(wallBox, "TrySnapToWall");
        yield return null;
        Check(wallBox != null && snapped && wallBox.isSetInWall, "a mixed reality box set into a wall");

        // A sphere in the first segment.
        boxes = Now<TunnelContainer>();
        Call(first.GetComponentInChildren<TunnelContainerMenu>(true), "SpawnContainer", TunnelContainer.Shape.Sphere);
        var sphere = Newest(boxes);
        Move(sphere, segments[0], new Vector3(0.2f, 0.9f, -0.5f));
        sphere.transform.localScale = Vector3.one * 0.35f;

        // A Viewer on the second segment's wall, a browser standing free in the first.
        var panels = Now<BrowserPanel>();
        var head = segments[1].transform;
        Call(second.GetComponentInChildren<TunnelSegmentActions>(true), "SpawnPanel", true, head.position + Vector3.up * 1.6f, head.right);
        var viewerPanel = Newest(panels);
        var viewer = viewerPanel != null ? viewerPanel.GetComponentInChildren<ViewerPanel>(true) : null;
        var entry = ViewerLibrary.all.FirstOrDefault(e => !e.isLink && !ViewerLibrary.IsVideoKind(e.kind));
        if (viewer != null && entry != null)
            viewer.Open(entry);
        else if (viewer != null)
        {
            // No Viewer media in this checkout: an image of the test's own, by its file:// address.
            var picture = new Texture2D(64, 32);
            var pixels = new Color32[64 * 32];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32((byte)(i % 64 * 4), 80, 160, 255);
            picture.SetPixels32(pixels);
            string file = Path.Combine(Application.temporaryCachePath, "SavedStatesTest Picture.png");
            File.WriteAllBytes(file, picture.EncodeToPNG());
            Object.Destroy(picture);
            viewer.OpenTyped("file://" + file, ViewerMediaKind.Image);
        }
        panels = Now<BrowserPanel>();
        head = segments[0].transform;
        Call(first.GetComponentInChildren<TunnelSegmentActions>(true), "SpawnPanel", false, head.position + Vector3.up * 1.6f, -head.right);
        var browserPanel = Newest(panels);
        Check(viewerPanel != null && browserPanel != null, $"a Viewer{(entry != null ? $" showing {entry.name}" : " (no media in the library to show)")} and a browser");
        yield return null;
        yield return null;
        browserPanel?.SetFree(true);

        // A sign, a figure, a palette and the nozzle with the IgG on its cup.
        var signage = first.GetComponentInChildren<TunnelSignageMenu>(true);
        var style = SignStyle.Default;
        style.bold = true;
        var sign = signage.Place("Saved state test\nsecond line", style, "", 1.5f, ulong.MaxValue);
        sign?.CloseMenu();
        var figures = Now<TunnelFigure>();
        Call(first.GetComponentInChildren<TunnelMenu>(true), "PlaceFigure",
            new Pose(segments[0].transform.TransformPoint(new Vector3(0.1f, 0f, 0.9f)), segments[0].transform.rotation), null);
        var figure = Newest(figures);
        if (figure != null)
            figure.transform.localScale = Vector3.one * 1.3f;
        var palettes = Now<TunnelPalette>();
        Call(second.GetComponentInChildren<TunnelPaintMenu>(true), "PlacePalette");
        var palette = Newest(palettes);
        Call(moleculeMenu1, "SetNozzle", true, new Pose(segments[0].transform.TransformPoint(new Vector3(-0.2f, 1.0f, -0.2f)), segments[0].transform.rotation));
        yield return null;
        var nozzle = SpawnNozzle.current;
        if (nozzle != null)
        {
            nozzle.SetCount(17);
            nozzle.Attach(igg);
        }
        Check(sign != null && figure != null && palette != null && nozzle != null, "a sign, a New figure, a palette and the spawn nozzle");

        // Paint on the second segment's wall, and its colour.
        var paint = second.GetComponentInChildren<TunnelSegmentPaint>(true);
        var stroke = new List<Vector2>();
        for (int i = 0; i < 300; i++)
            stroke.Add(new Vector2(0.2f + i * 0.002f, 0.3f + 0.05f * Mathf.Sin(i * 0.1f)));
        paint.Draw(new PaintStyle(PaintMode.Paint, new Color32(200, 40, 40, 255), 0.02f), stroke);
        paint.EndStroke();
        var appearance = second.GetComponentInChildren<TunnelSegmentAppearance>(true);
        appearance.SetColor(new Vector4(0.6f, 0.5f, 0.8f, 1f));

        // A painting copy on the second segment's other wall, with a few marks of its own.
        var copies = Now<PaintingCopy>();
        var copyFrame = segments[1].transform;
        Call(second.GetComponentInChildren<TunnelPaintMenu>(true), "PlaceCopy",
            copyFrame.TransformPoint(new Vector3(-segments[1].width * 0.5f + 0.01f, 1.4f, -0.8f)),
            copyFrame.rotation * Quaternion.Euler(0f, -90f, 0f), new Vector2(0.6f, 0.4f), 0, 0ul);
        var copy = Newest(copies);
        yield return null;
        var copyPaint = copy != null ? copy.GetComponent<TunnelSegmentPaint>() : null;
        if (copyPaint != null)
        {
            var dots = new List<Vector2>();
            for (int i = 0; i < 120; i++)
                dots.Add(new Vector2(0.2f + i * 0.005f, 0.5f));
            copyPaint.Draw(new PaintStyle(PaintMode.Paint, new Color32(20, 120, 220, 255), 0.01f), dots);
            copyPaint.EndStroke();
        }
        Check(copyPaint != null, "a painting copy with marks");

        for (int i = 0; i < 20; i++)
            yield return null;

        // Save now.
        var saved = Describe(out string savedSegments);
        menu.ShowList(true);
        Field<Button>(menu, "m_SaveButton").onClick.Invoke();
        var ids = TunnelStateStore.Ids();
        Check(ids.Count == 1 && TunnelStateStore.Name(ids[0]) == "State 1", $"Save now kept State 1 ({string.Join(", ", ids)})");
        string firstId = ids.FirstOrDefault();
        var state = TunnelStateStore.Load(firstId);
        Check(state != null && state.segments.Count == 2, $"State 1 reads back: {state?.Summary()}");
        Check(state != null && state.segments[1].paint.ops.Count >= 2 && state.containers.Any(c => c.state.molecules.Count == 2),
            "it holds the paint and the box's two molecules");
        Check(menu.saved.Count == 1 && menu.saved[0].info.Contains("2 segments") && menu.saved[0].local,
            $"the list shows it, not uploaded (no token): \"{menu.saved.FirstOrDefault().info}\"");
        Field<Button>(menu, "m_SaveButton").onClick.Invoke();
        ids = TunnelStateStore.Ids();
        Check(ids.Count == 2 && TunnelStateStore.Name(ids[1]) == "State 2", "saving again keeps State 2 beside it");
        string secondId = ids.LastOrDefault();

        // Change everything.
        TunnelClearAll.Clear();
        appearance.SetColor(new Vector4(0.1f, 0.9f, 0.4f, 1f));
        paint.Clear();
        second.GetComponentInParent<TunnelSegmentActions>().AddSegmentAtEnd();
        for (int i = 0; i < 5; i++)
            yield return null;
        Check(TunnelSaveState.Segments().Count == 3, "changed: a third segment added, everything cleared, the colour and paint changed");

        // Restore State 1 (pressed twice).
        menu.CheckSaved(firstId);
        var restore = Field<Button>(menu, "m_RestoreButton");
        restore.onClick.Invoke();
        Check(!TunnelSaveState.busy, "a first press of Restore only asks for a second");
        restore.onClick.Invoke();
        Check(TunnelSaveState.busy, "the second press puts it back");
        float until = Time.realtimeSinceStartup + 30f;
        while (TunnelSaveState.busy && Time.realtimeSinceStartup < until)
            yield return null;
        Check(!TunnelSaveState.busy, "putting it back finished");
        for (int i = 0; i < 5; i++)
            yield return null;

        var restored = Describe(out string restoredSegments);
        Check(restoredSegments == savedSegments, $"segments as saved: {restoredSegments}" + (restoredSegments == savedSegments ? "" : $" (saved: {savedSegments})"));
        var unmatched = new List<Thing>(restored);
        int matched = 0;
        foreach (var thing in saved)
        {
            int best = -1;
            for (int i = 0; i < unmatched.Count; i++)
            {
                var other = unmatched[i];
                if (other.key != thing.key || other.segment != thing.segment || Vector3.Distance(other.position, thing.position) > thing.tolerance
                    || Quaternion.Angle(other.rotation, thing.rotation) > 1f || (other.scale - thing.scale).magnitude > 0.002f * Mathf.Max(1f, thing.scale.magnitude))
                    continue;
                best = i;
                break;
            }
            if (best >= 0)
            {
                unmatched.RemoveAt(best);
                matched++;
            }
            else
            {
                var near = unmatched.Where(u => u.key == thing.key).OrderBy(u => Vector3.Distance(u.position, thing.position)).Cast<Thing?>().FirstOrDefault();
                Check(false, $"not put back as saved: {thing}" + (near.HasValue ? $"; nearest of its kind: {near.Value}" : "; none of its kind"));
            }
        }
        foreach (var extra in unmatched)
            Check(false, $"put back but not saved: {extra}");
        Check(matched == saved.Count && unmatched.Count == 0, $"{matched} of {saved.Count} things back where and as they were");
        foreach (var thing in saved)
            report.AppendLine($"        {thing}");

        // Delete.
        menu.CheckSaved(secondId);
        Field<Button>(menu, "m_DeleteButton").onClick.Invoke();
        for (int i = 0; i < 3; i++)
            yield return null;
        Check(TunnelStateStore.Ids().SequenceEqual(new[] { firstId }), "Delete removed State 2 from the list");
        menu.ShowList(false);
    }

    /// <summary>
    /// Everything a state holds, as the test compares it: each thing's kind and settings, segment and pose in it, and
    /// the segments' number, colours, textures and paint (as one line).
    /// </summary>
    static List<Thing> Describe(out string segmentLine)
    {
        var segments = TunnelSaveState.Segments();
        var result = new List<Thing>();
        int SegmentOf(Vector3 world)
        {
            int best = 0;
            float bestOutside = float.MaxValue, bestSide = float.MaxValue;
            for (int i = 0; i < segments.Count; i++)
            {
                var t = segments[i].transform;
                var p = Quaternion.Inverse(t.rotation) * (world - t.position);
                float outside = Mathf.Max(0f, Mathf.Abs(p.z) - segments[i].length * 0.5f), side = Mathf.Abs(p.x);
                if (outside < bestOutside - 1e-4f || (Mathf.Abs(outside - bestOutside) <= 1e-4f && side < bestSide))
                {
                    best = i;
                    bestOutside = outside;
                    bestSide = side;
                }
            }
            return best;
        }
        void Add(string key, Transform t, float tolerance = 0.002f)
        {
            int s = SegmentOf(t.position);
            var frame = segments[s].transform;
            var inverse = Quaternion.Inverse(frame.rotation);
            result.Add(new Thing
            {
                key = key,
                segment = s,
                position = inverse * (t.position - frame.position),
                rotation = inverse * t.rotation,
                scale = t.localScale,
                tolerance = tolerance,
            });
        }

        foreach (var molecule in MoleculeGravity.all.Where(m => m != null && m.isActiveAndEnabled))
        {
            var inside = TunnelContainer.At(molecule.body != null ? molecule.body.worldCenterOfMass : molecule.transform.position);
            Add($"molecule {CapsidSites.MoleculeName(molecule.gameObject)} frozen {molecule.isFrozen} in {(inside != null ? inside.shape.ToString() : "nothing")}",
                molecule.transform, molecule.isFrozen || molecule.isParked ? 0.002f : 0.05f);
        }
        foreach (var c in Object.FindObjectsByType<TunnelContainer>(FindObjectsSortMode.None))
        {
            var movie = c.movie;
            Add($"container {c.shape} mixed reality {c.isMixedReality} set in wall {c.isSetInWall} front {c.frontFace} viscosity {c.viscosity:0.0} heat {c.temperature:0.00} motion {c.motion}" +
                (movie != null ? $" movie {movie.definition.id} binding {movie.bindingTime:0} radius {movie.captureRadius:0.0}" : ""), c.transform);
        }
        foreach (var p in Object.FindObjectsByType<BrowserPanel>(FindObjectsSortMode.None))
        {
            var viewer = p.GetComponentInChildren<ViewerPanel>(true);
            Add($"{(viewer != null ? $"Viewer showing {(viewer.hasContent ? viewer.source : "nothing")} content only {viewer.contentOnly}" : "browser")} free {p.isFree} width {p.width:0.000}",
                p.transform, 0.005f);
        }
        foreach (var s in TunnelSign.all.Where(s => s != null))
            Add($"sign \"{s.text.Replace('\n', '/')}\" bold {s.style.bold}", s.transform);
        foreach (var p in TunnelPalette.all.Where(p => p != null))
            Add("palette", p.transform);
        foreach (var c in PaintingCopy.all.Where(c => c != null))
        {
            var marks = c.GetComponent<TunnelSegmentPaint>().SaveMarks();
            Add($"painting copy {c.paintCanvasMeters.x:0.00} x {c.paintCanvasMeters.y:0.00} m, {marks.ops.Count(o => !o.endStroke)} ops/{marks.ops.Sum(o => o.stamps?.Length ?? 0)} chars", c.transform);
        }
        foreach (var f in TunnelFigure.all.Where(f => f != null))
            Add("figure", f.transform, 0.05f);
        foreach (var n in Object.FindObjectsByType<SpawnNozzle>(FindObjectsSortMode.None))
            Add($"nozzle copies {n.count} holding {(n.loaded != null ? CapsidSites.MoleculeName(n.loaded.gameObject) : "nothing")}", n.transform);

        segmentLine = $"{segments.Count}: " + string.Join("; ", segments.Select(g =>
        {
            var root = g.GetComponentInParent<TunnelSegmentLink>();
            var look = root.GetComponentInChildren<TunnelSegmentAppearance>(true);
            var texture = root.GetComponentInChildren<TunnelSegmentTexture>(true);
            var marks = root.GetComponentInChildren<TunnelSegmentPaint>(true).SaveMarks();
            int stamps = marks.ops.Sum(o => (o.stamps?.Length ?? 0));
            var hsva = look.colorHsva;
            return $"colour {hsva.x:0.00},{hsva.y:0.00},{hsva.z:0.00},{hsva.w:0.00} texture \"{texture?.textureName}\" paint {marks.ops.Count(o => !o.endStroke)} ops/{stamps} chars";
        }));
        return result;
    }
}
