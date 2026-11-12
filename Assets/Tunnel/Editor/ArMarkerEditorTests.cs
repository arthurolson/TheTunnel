using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor checks for the AR marker cube (<see cref="ArMarkerCube"/>), from Tunnel > Paint:
/// - Test AR Marker Stereo: renders the printed marker as both passthrough cameras would see it (known lenses, known
///   left-to-right transform), runs the images through artoolkitX's stereo solver exactly as the headset does, and
///   compares the cube's pose with the true one;
/// - Test AR Marker Handoff: two simulated headsets watch a moving marker and look away in turn; checks the host's
///   choice of source and that the drawn cube never jumps.
/// </summary>
static class ArMarkerEditorTests
{
    // The printed marker: artoolkitX 3x3 barcode, Hamming (6,3), id 1, as 12x12 cells ('#' black).
    static readonly string[] k_Barcode1 =
    {
        "############",
        "############",
        "############",
        "#####....###",
        "#####....###",
        "###..#######",
        "###..#######",
        "#######..###",
        "#######..###",
        "############",
        "############",
        "############",
    };

    const float k_Width = 0.06f;

    [MenuItem("Tunnel/Paint/Test AR Marker Stereo (rendered images)")]
    public static string TestStereo()
    {
        var left = new ArMarkerMath.CameraModel(1280, 960, 866f, 866f, 641.5f, 478.2f);
        var right = new ArMarkerMath.CameraModel(1280, 960, 871f, 869f, 636.8f, 482.9f);
        // Lenses on the head (Unity camera frames), roughly like a Quest 3's.
        var headFromLeft = new Pose(new Vector3(-0.032f, -0.012f, 0.06f), Quaternion.Euler(9f, -1f, 0.3f));
        var headFromRight = new Pose(new Vector3(0.033f, -0.011f, 0.06f), Quaternion.Euler(8.5f, 1.2f, -0.4f));
        var rightFromLeft = ArMarkerMath.RightFromLeft(headFromLeft, headFromRight);
        var head = new Pose(new Vector3(0.2f, 1.5f, -0.1f), Quaternion.Euler(25f, 10f, 0f));

        var report = new StringBuilder();
        report.AppendLine($"[ARMarker] stereo test, artoolkitX {ArMarkerStereoSolver.Version()}, baseline {rightFromLeft.position.magnitude * 1000f:0.0} mm");
        var solver = new ArMarkerStereoSolver();
        var nativeLog = new List<string>();
        void OnLog(string line) { lock (nativeLog) nativeLog.Add(line); }
        ArMarkerStereoSolver.log += OnLog;
        int passed = 0, total = 0;
        try
        {
            if (!solver.Start(left, right, rightFromLeft, 1, k_Width))
                return report.Append("solver didn't start: ").Append(solver.error).ToString();

            // Marker poses relative to the head: distance, sideways, up, tilt, turn on the table.
            var cases = new (string name, Vector3 offset, Vector3 tiltEuler)[]
            {
                ("ahead 40 cm, facing", new Vector3(0f, -0.05f, 0.40f), new Vector3(0f, 0f, 0f)),
                ("ahead 55 cm, tilted 35", new Vector3(0.05f, -0.1f, 0.55f), new Vector3(-35f, 20f, 0f)),
                ("ahead 70 cm, tilted 50, turned", new Vector3(-0.08f, -0.15f, 0.70f), new Vector3(-50f, -30f, 40f)),
                ("close 30 cm, turned 90", new Vector3(0.02f, 0f, 0.30f), new Vector3(10f, 0f, 90f)),
                ("at the left edge: left camera only", OneCameraOnly(true), new Vector3(0f, 0f, 0f)),
                ("at the right edge: right camera only", OneCameraOnly(false), new Vector3(0f, 0f, 0f)),
            };

            // Slides the marker sideways (40 cm ahead) until it is wholly in one camera's view and not in the other's.
            Vector3 OneCameraOnly(bool leftOnly)
            {
                for (float x = 0.1f; x < 0.5f; x += 0.002f)
                {
                    var offset = new Vector3(leftOnly ? -x : x, -0.05f, 0.40f);
                    var inHead = new Pose(offset, Quaternion.LookRotation(offset.normalized, Vector3.up));
                    // The seeing camera must show the marker with its white margin; the other must not show all of it.
                    var l = ArMarkerMath.CheckImage(left, null, ArMarkerMath.Mul(ArMarkerMath.Inverse(headFromLeft), inHead), leftOnly ? k_Width * 1.8f : k_Width);
                    var r = ArMarkerMath.CheckImage(right, null, ArMarkerMath.Mul(ArMarkerMath.Inverse(headFromRight), inHead), leftOnly ? k_Width : k_Width * 1.8f);
                    if (leftOnly ? l.inView && !r.inView : r.inView && !l.inView)
                        return offset;
                }
                return new Vector3(0f, -0.05f, 0.4f);
            }

            foreach (var c in cases)
            {
                total++;
                // The marker's face (local -z) toward the head, then tilted.
                var markerInHead = new Pose(c.offset, Quaternion.LookRotation(c.offset.normalized, Vector3.up) * Quaternion.Euler(c.tiltEuler));
                var markerWorld = ArMarkerMath.Mul(head, markerInHead);
                var leftWorld = ArMarkerMath.Mul(head, headFromLeft);
                var rightWorld = ArMarkerMath.Mul(head, headFromRight);
                var inLeft = ArMarkerMath.Mul(ArMarkerMath.Inverse(leftWorld), markerWorld);
                var inRight = ArMarkerMath.Mul(ArMarkerMath.Inverse(rightWorld), markerWorld);
                var imageL = Render(left, inLeft);
                var imageR = Render(right, inRight);

                ArMarkerStereoSolver.Result result = default;
                bool got = false;
                for (int i = 0; i < 80 && !got; i++)
                {
                    if (solver.Process(imageL, imageR, out result) && result.visible)
                        got = true;
                    else
                        Thread.Sleep(10);
                }
                if (!got)
                {
                    report.AppendLine($"  {c.name}: NOT FOUND (solver running {solver.running}, error {solver.error})");
                    continue;
                }

                var checkL = ArMarkerMath.CheckImage(left, imageL, result.markerInLeft, k_Width);
                var checkR = ArMarkerMath.CheckImage(right, imageR, ArMarkerMath.Mul(rightFromLeft, result.markerInLeft), k_Width);
                var cubeWorld = ArMarkerMath.Mul(leftWorld, ArMarkerMath.CubeFromMarker(result.markerInLeft));
                var trueCube = ArMarkerMath.CubeFromMarker(markerWorld);
                float positionError = Vector3.Distance(cubeWorld.position, trueCube.position) * 1000f;
                float upError = Vector3.Angle(cubeWorld.rotation * Vector3.up, trueCube.rotation * Vector3.up);
                float turnError = Quaternion.Angle(cubeWorld.rotation, trueCube.rotation);
                // The cube's up must point out of the printed face, toward the cameras.
                float upTowardHead = Vector3.Dot(cubeWorld.rotation * Vector3.up, (head.position - cubeWorld.position).normalized);
                float quality = ArMarkerMath.Quality(result.confidence, checkL, checkR);
                bool expectL = !c.name.Contains("right camera only"), expectR = !c.name.Contains("left camera only");
                bool ok = positionError < (expectL && expectR ? 5f : 12f) && upError < 3f && turnError < 3f && upTowardHead > 0f &&
                    checkL.seen == expectL && checkR.seen == expectR;
                if (ok)
                    passed++;
                report.AppendLine($"  {c.name}: {(ok ? "ok" : "FAIL")} position error {positionError:0.0} mm, up error {upError:0.0} deg, " +
                    $"turn error {turnError:0.0} deg, up faces the cameras {upTowardHead > 0f}, confidence {result.confidence:0.00}, " +
                    $"seen L {checkL.seen} ({checkL.edgeMatch:0.00}, {checkL.edgePixels:0} px) R {checkR.seen} ({checkR.edgeMatch:0.00}, {checkR.edgePixels:0} px), quality {quality:0.00}");
            }
        }
        finally
        {
            ArMarkerStereoSolver.log -= OnLog;
            solver.Dispose();
        }
        report.AppendLine($"  passed {passed} of {total}");
        lock (nativeLog)
        {
            foreach (var line in nativeLog)
                if (line.Contains("rror") || line.Contains("arning"))
                    report.AppendLine("  artoolkitX: " + line);
        }
        Debug.Log(report.ToString());
        return report.ToString();
    }

    [MenuItem("Tunnel/Paint/Test AR Marker Start Sequence (as on Android)")]
    public static string TestStartSequence()
    {
        // The headset's start, on a worker thread as there: artoolkitX's external video input needs a writable cache
        // folder. Android's default is read-only, which this reproduces with an unwritable folder; then a writable
        // one (the headset uses temporaryCachePath), then a stop and a second start.
        var left = new ArMarkerMath.CameraModel(1280, 1280, 854.6f, 854.6f, 639.4f, 642.9f);
        var right = new ArMarkerMath.CameraModel(1280, 1280, 852.6f, 852.6f, 640.3f, 640.0f);
        var headFromLeft = new Pose(new Vector3(-0.0331f, -0.0122f, 0.0751f), Quaternion.Euler(6.3f, 0.4f, -0.2f));
        var headFromRight = new Pose(new Vector3(0.0313f, -0.0125f, 0.0753f), Quaternion.Euler(6.3f, -0.4f, 0.5f));
        var rightFromLeft = ArMarkerMath.RightFromLeft(headFromLeft, headFromRight);
        var markerInHead = new Pose(new Vector3(0.02f, -0.08f, 0.45f), Quaternion.LookRotation(new Vector3(0.02f, -0.08f, 0.45f).normalized, Vector3.up) * Quaternion.Euler(-30f, 0f, 15f));
        var imageL = Render(left, ArMarkerMath.Mul(ArMarkerMath.Inverse(headFromLeft), markerInHead));
        var imageR = Render(right, ArMarkerMath.Mul(ArMarkerMath.Inverse(headFromRight), markerInHead));
        string writable = System.IO.Path.Combine(Application.temporaryCachePath, "artoolkitX");
        System.IO.Directory.CreateDirectory(writable);

        var report = new StringBuilder("[ARMarker] start sequence test (Quest 3S lenses, 1280x1280)\n");
        report.AppendLine($"  vconf: {ArMarkerStereoSolver.VideoConfiguration(writable)}");
        string Attempt(string label, string cache, bool expectStart)
        {
            string line = null;
            var log = new List<string>();
            var thread = new Thread(() =>
            {
                void OnLog(string l) { lock (log) log.Add(l); }
                ArMarkerStereoSolver.log += OnLog;
                var solver = new ArMarkerStereoSolver();
                try
                {
                    // Copies: the solver stamps its decoy square into the images it is given.
                    if (!solver.Start(left, right, rightFromLeft, 1, k_Width, ArMarkerStereoSolver.Threshold.Otsu, cache))
                    {
                        line = $"start failed: {solver.error}";
                        return;
                    }
                    for (int i = 0; i < 100; i++)
                    {
                        if (solver.Process((byte[])imageL.Clone(), (byte[])imageR.Clone(), out var result) && result.visible)
                        {
                            var truth = ArMarkerMath.Mul(ArMarkerMath.Inverse(headFromLeft), markerInHead);
                            line = $"started; found after {i + 1} pairs, {Vector3.Distance(result.markerInLeft.position, truth.position) * 1000f:0.0} mm from the truth";
                            return;
                        }
                        Thread.Sleep(10);
                    }
                    line = "started but never found the marker";
                }
                finally
                {
                    solver.Dispose();
                    ArMarkerStereoSolver.log -= OnLog;
                }
            });
            thread.Start();
            thread.Join(20000);
            bool started = line != null && line.StartsWith("started;");
            var keyLines = new List<string>();
            lock (log)
                foreach (var l in log)
                    if (l.Contains("[error]"))
                        keyLines.Add(l.Replace("\n", " "));
            return $"  {label}: {(started == expectStart ? "as expected" : "UNEXPECTED")} - {line}" +
                (keyLines.Count > 0 ? "\n      artoolkitX: " + string.Join("\n      artoolkitX: ", keyLines) : "");
        }

        report.AppendLine(Attempt("1. writable cache folder", writable, true));
        report.AppendLine(Attempt("2. after a stop, started again", writable, true));
        report.AppendLine(Attempt("3. unwritable cache folder (Android's case)", "/System/ARMarkerReadOnlyTest", false));
        report.AppendLine(Attempt("4. after that failed start, started again", writable, true));
        Debug.Log(report.ToString());
        return report.ToString();
    }

    // A headset's lens on the head from Android's camera characteristics, as the tracker reads it (Meta's turn).
    static Pose AndroidLens(float[] t, float[] r)
    {
        var lensFromHead = new Quaternion(-r[0], -r[1], r[2], r[3]);
        return new Pose(new Vector3(t[0], t[1], -t[2]), Quaternion.Inverse(lensFromHead) * Quaternion.Euler(180f, 0f, 0f));
    }

    /// <summary>
    /// Replays a headset's snapshots (files/ARMarker/snapshot-*.png: both camera images side by side at half size)
    /// through artoolkitX's own stereo fusion (old) and through each camera on its own plus our fusion (new), with that
    /// headset's lens calibration (Quest 3S 3497C10HC4004X's, from its log of 12 Nov 2026).
    /// </summary>
    static readonly List<(Pose left, Pose right)> s_ReplayPairs = new();

    public static string ReplaySnapshots(string folder)
    {
        var headFromLeft = AndroidLens(new[] { -0.0317f, -0.0127f, -0.0738f }, new[] { -0.9984f, -0.0014f, 0.0057f, 0.0566f });
        var headFromRight = AndroidLens(new[] { 0.0318f, -0.0129f, -0.0734f }, new[] { -0.9987f, 0.0026f, -0.0024f, 0.0518f });
        var rightFromLeft = ArMarkerMath.RightFromLeft(headFromLeft, headFromRight);
        // The snapshots are already half size: the lenses halved.
        var left = new ArMarkerMath.CameraModel(1728, 1728, 1174.3f, 1174.3f, 862.1f, 867.4f).Halved();
        var right = new ArMarkerMath.CameraModel(1728, 1728, 1171.3f, 1171.3f, 862.2f, 864.5f).Halved();
        var report = new StringBuilder($"[ARMarker] snapshot replay ({folder})\n");
        var solver = new ArMarkerStereoSolver();
        var fusion = new ArMarkerFusion();
        s_ReplayPairs.Clear();
        int oldFound = 0, oldBoth = 0, newFound = 0, newBoth = 0, files = 0;
        try
        {
            if (!solver.Start(left, right, rightFromLeft, 1, k_Width))
                return report.Append("solver didn't start: ").Append(solver.error).ToString();
            var paths = System.IO.Directory.GetFiles(folder, "snapshot-*.png");
            System.Array.Sort(paths);
            double time = 0;
            foreach (var path in paths)
            {
                var texture = new Texture2D(2, 2);
                texture.LoadImage(System.IO.File.ReadAllBytes(path));
                if (texture.width != left.width * 2 || texture.height != left.height)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    continue;
                }
                files++;
                var pixels = texture.GetPixels32();
                UnityEngine.Object.DestroyImmediate(texture);
                var imageL = new byte[left.width * left.height];
                var imageR = new byte[right.width * right.height];
                for (int y = 0; y < left.height; y++)
                    for (int x = 0; x < left.width; x++)
                    {
                        int source = (left.height - 1 - y) * left.width * 2; // the PNG's rows run from the bottom
                        imageL[y * left.width + x] = pixels[source + x].g;
                        imageR[y * left.width + x] = pixels[source + left.width + x].g;
                    }

                // Old: artoolkitX's stereo fusion through the lens calibration.
                ArMarkerStereoSolver.Result result = default;
                bool old = false;
                for (int i = 0; i < 4 && !old; i++)
                    old = solver.Process((byte[])imageL.Clone(), (byte[])imageR.Clone(), out result) && result.visible;
                string oldText = "nothing";
                if (old)
                {
                    oldFound++;
                    var cl = ArMarkerMath.CheckImage(left, imageL, result.markerInLeft, k_Width);
                    var cr = ArMarkerMath.CheckImage(right, imageR, ArMarkerMath.Mul(rightFromLeft, result.markerInLeft), k_Width);
                    oldText = cl.seen && cr.seen ? "L+R" : cl.seen ? "L only" : cr.seen ? "R only" : "pose fits neither image";
                    if (cl.seen && cr.seen)
                        oldBoth++;
                }

                // New: each camera on its own, joined here.
                ArMarkerStereoSolver.Views views = default;
                bool processed = false;
                for (int i = 0; i < 4 && !processed; i++)
                    processed = solver.ProcessEach((byte[])imageL.Clone(), (byte[])imageR.Clone(), out views);
                var checkL = views.left ? ArMarkerMath.CheckImage(left, imageL, views.leftInLeft, k_Width) : default;
                var checkR = views.right ? ArMarkerMath.CheckImage(right, imageR, views.rightInRight, k_Width) : default;
                time += 1.0; // snapshots are seconds apart: no "last pose" carried between them
                var fused = fusion.Fuse(views.left, views.leftInLeft, checkL.edgePixels, views.right, views.rightInRight, checkR.edgePixels, rightFromLeft, time);
                string newText = !fused.visible ? "nothing"
                    : fused.cameras == 3 ? $"L+R ({fused.apart * 1000f:0} mm, {fused.angle:0.0} deg apart)"
                    : fused.disagreed ? $"both found, disagreed ({fused.apart * 1000f:0} mm, {fused.angle:0.0} deg): took {(fused.cameras == 1 ? "L" : "R")}"
                    : fused.cameras == 1 ? "L only" : "R only";
                if (fused.visible)
                    newFound++;
                if (views.left && views.right)
                {
                    newBoth++;
                    s_ReplayPairs.Add((views.leftInLeft, views.rightInRight));
                }
                report.AppendLine($"  {System.IO.Path.GetFileName(path)}: old {oldText} | new: left {(views.left ? "found" : "-")}, right {(views.right ? "found" : "-")} -> {newText}" +
                    (fused.visible ? $", {fused.markerInLeft.position.magnitude * 100f:0} cm away" : ""));
            }
        }
        finally
        {
            solver.Dispose();
        }
        report.AppendLine($"  {files} pairs: old found {oldFound} (L+R {oldBoth}); new found {newFound} (both cameras found it in {newBoth}); {fusion}");
        // How far apart the two cameras' sightings are once the right camera's turn has been learnt (a second pass).
        var apart = new List<string>();
        foreach (var (l, r) in s_ReplayPairs)
        {
            var rightInLeft = fusion.RightInLeft(r, rightFromLeft);
            var raw = ArMarkerMath.Mul(ArMarkerMath.Inverse(rightFromLeft), r);
            apart.Add($"{Vector3.Distance(raw.position, l.position) * 1000f:0}->{Vector3.Distance(rightInLeft.position, l.position) * 1000f:0} mm");
        }
        report.AppendLine("  left and right sightings apart, before -> after the learnt turn: " + string.Join(", ", apart));
        Debug.Log(report.ToString());
        return report.ToString();
    }

    [MenuItem("Tunnel/Paint/Test AR Marker Miscalibrated and Distorted (rendered images)")]
    public static string TestMiscalibration()
    {
        // Rendered with the true rig; artoolkitX is given a calibration that is off by about a degree and a millimetre,
        // and the images carry a little barrel distortion the calibration doesn't model.
        var left = new ArMarkerMath.CameraModel(1728, 1728, 1174.3f, 1174.3f, 862.1f, 867.4f).Halved();
        var right = new ArMarkerMath.CameraModel(1728, 1728, 1171.3f, 1171.3f, 862.2f, 864.5f).Halved();
        var headFromLeft = new Pose(new Vector3(-0.0317f, -0.0127f, 0.0738f), Quaternion.Euler(6.3f, 0.3f, -0.2f));
        var headFromRight = new Pose(new Vector3(0.0318f, -0.0129f, 0.0734f), Quaternion.Euler(6.4f, -0.6f, 0.3f));
        var trueRightFromLeft = ArMarkerMath.RightFromLeft(headFromLeft, headFromRight);
        var givenRightFromLeft = ArMarkerMath.Mul(new Pose(new Vector3(0.001f, -0.0008f, 0.0005f), Quaternion.Euler(0.3f, -1.0f, 0.2f)), trueRightFromLeft);
        var head = new Pose(new Vector3(0f, 1.4f, 0f), Quaternion.Euler(20f, 0f, 0f));
        var report = new StringBuilder("[ARMarker] miscalibration test: calibration off by 1.1 deg (mostly a turn about the vertical, as measured on the headset) and 1.4 mm, barrel distortion k1 = -0.02\n");
        var solver = new ArMarkerStereoSolver();
        var fusion = new ArMarkerFusion();
        int oldFound = 0, newBoth = 0, total = 0;
        float worst = 0f;
        try
        {
            if (!solver.Start(left, right, givenRightFromLeft, 1, k_Width))
                return report.Append("solver didn't start: ").Append(solver.error).ToString();
            var random = new System.Random(3);
            for (int n = 0; n < 12; n++)
            {
                total++;
                var offset = new Vector3((float)random.NextDouble() * 0.3f - 0.15f, (float)random.NextDouble() * 0.2f - 0.15f, 0.35f + (float)random.NextDouble() * 0.5f);
                var tilt = Quaternion.Euler((float)random.NextDouble() * 70f - 35f, (float)random.NextDouble() * 60f - 30f, (float)random.NextDouble() * 360f);
                var markerWorld = ArMarkerMath.Mul(head, new Pose(offset, Quaternion.LookRotation(offset.normalized, Vector3.up) * tilt));
                var leftWorld = ArMarkerMath.Mul(head, headFromLeft);
                var rightWorld = ArMarkerMath.Mul(head, headFromRight);
                var imageL = Distort(Render(left, ArMarkerMath.Mul(ArMarkerMath.Inverse(leftWorld), markerWorld)), left, -0.02f);
                var imageR = Distort(Render(right, ArMarkerMath.Mul(ArMarkerMath.Inverse(rightWorld), markerWorld)), right, -0.02f);

                ArMarkerStereoSolver.Result result = default;
                bool old = false;
                for (int i = 0; i < 6 && !old; i++)
                    old = solver.Process((byte[])imageL.Clone(), (byte[])imageR.Clone(), out result) && result.visible;
                if (old)
                    oldFound++;

                ArMarkerStereoSolver.Views views = default;
                bool processed = false;
                for (int i = 0; i < 6 && !processed; i++)
                    processed = solver.ProcessEach((byte[])imageL.Clone(), (byte[])imageR.Clone(), out views);
                var cl = views.left ? ArMarkerMath.CheckImage(left, imageL, views.leftInLeft, k_Width) : default;
                var cr = views.right ? ArMarkerMath.CheckImage(right, imageR, views.rightInRight, k_Width) : default;
                var fused = fusion.Fuse(views.left, views.leftInLeft, cl.edgePixels, views.right, views.rightInRight, cr.edgePixels, givenRightFromLeft, n * 0.033);
                float error = fused.visible ? Vector3.Distance(ArMarkerMath.Apply(leftWorld, fused.markerInLeft.position), markerWorld.position) * 1000f : float.NaN;
                if (fused.cameras == 3)
                    newBoth++;
                if (fused.visible)
                    worst = Mathf.Max(worst, error);
                report.AppendLine($"  {offset.z * 100f:0} cm: old {(old ? "found" : "NOTHING")} | new {(fused.cameras == 3 ? "L+R" : fused.cameras == 1 ? "L only" : fused.cameras == 2 ? "R only" : "nothing")}" +
                    (fused.visible ? $", {error:0.0} mm from the truth, views {fused.apart * 1000f:0} mm apart" : ""));
            }
        }
        finally
        {
            solver.Dispose();
        }
        bool ok = newBoth == total && worst < 20f; // the distortion isn't modelled, so a few mm of error remain
        report.AppendLine($"  old (artoolkitX's stereo fusion) found {oldFound} of {total}; new found L+R in {newBoth} of {total}, worst {worst:0.0} mm; {fusion}; {(ok ? "PASS" : "FAIL")}");
        Debug.Log(report.ToString());
        return report.ToString();
    }

    // Barrel (k1 < 0) or pincushion distortion about the principal point, by resampling.
    static byte[] Distort(byte[] image, ArMarkerMath.CameraModel camera, float k1)
    {
        var result = new byte[image.Length];
        for (int y = 0; y < camera.height; y++)
            for (int x = 0; x < camera.width; x++)
            {
                float nx = (x - camera.cx) / camera.fx, ny = (y - camera.cy) / camera.fy;
                float r2 = nx * nx + ny * ny;
                // Where this distorted pixel's light comes from in the ideal image (one fixed-point step of the inverse).
                float scale = 1f / (1f + k1 * r2);
                int sx = Mathf.RoundToInt(nx * scale * camera.fx + camera.cx), sy = Mathf.RoundToInt(ny * scale * camera.fy + camera.cy);
                result[y * camera.width + x] = sx >= 0 && sy >= 0 && sx < camera.width && sy < camera.height ? image[sy * camera.width + sx] : (byte)120;
            }
        return result;
    }

    [MenuItem("Tunnel/Paint/Test AR Canvas Placement (rendered panel)")]
    public static string TestCanvasPlacement()
    {
        // The whole path: a white panel with the marker taped on it as the layout says, rendered for both cameras,
        // artoolkitX's stereo pose, then the canvas laid from that pose; its corners are compared with the panel's.
        var left = new ArMarkerMath.CameraModel(1280, 1280, 854.6f, 854.6f, 639.4f, 642.9f);
        var right = new ArMarkerMath.CameraModel(1280, 1280, 852.6f, 852.6f, 640.3f, 640.0f);
        var headFromLeft = new Pose(new Vector3(-0.0331f, -0.0122f, 0.0751f), Quaternion.Euler(6.3f, 0.4f, -0.2f));
        var headFromRight = new Pose(new Vector3(0.0313f, -0.0125f, 0.0753f), Quaternion.Euler(6.3f, -0.4f, 0.5f));
        var rightFromLeft = ArMarkerMath.RightFromLeft(headFromLeft, headFromRight);
        var head = new Pose(new Vector3(0f, 1.4f, 0f), Quaternion.Euler(20f, 0f, 0f));

        var layouts = new List<(string name, ArMarkerCanvasLayout layout, Pose canvasInHead)>();
        var standard = ArMarkerCanvasLayout.Default;
        // The panel 60 cm ahead, tilted back like an easel, the marker (top left) roughly in view.
        layouts.Add(("default: top left, 2 cm, upright; easel", standard,
            new Pose(new Vector3(0.2f, -0.12f, 0.6f), Quaternion.Euler(-25f, 8f, 0f))));
        var topRight = standard;
        topRight.corner = 1;
        topRight.offsetX = 0.0254f;
        topRight.offsetY = 0.0254f;
        layouts.Add(("top right, 1 in, upright; on a table", topRight,
            new Pose(new Vector3(-0.2f, -0.35f, 0.45f), Quaternion.Euler(70f, -10f, 0f))));
        var turned = standard;
        turned.rotation = 90f;
        layouts.Add(("top left, 2 cm, marker turned 90 deg", turned,
            new Pose(new Vector3(0.22f, -0.12f, 0.6f), Quaternion.Euler(-20f, 0f, 5f))));

        var report = new StringBuilder("[ARMarker] AR canvas placement test\n");
        var solver = new ArMarkerStereoSolver();
        int passed = 0;
        try
        {
            if (!solver.Start(left, right, rightFromLeft, 1, k_Width))
                return report.Append("solver didn't start: ").Append(solver.error).ToString();
            foreach (var (name, layout, canvasInHead) in layouts)
            {
                var canvasWorld = ArMarkerMath.Mul(head, canvasInHead);
                var leftWorld = ArMarkerMath.Mul(head, headFromLeft);
                var rightWorld = ArMarkerMath.Mul(head, headFromRight);
                var imageL = RenderPanel(left, ArMarkerMath.Mul(ArMarkerMath.Inverse(leftWorld), canvasWorld), layout);
                var imageR = RenderPanel(right, ArMarkerMath.Mul(ArMarkerMath.Inverse(rightWorld), canvasWorld), layout);
                ArMarkerStereoSolver.Result result = default;
                bool got = false;
                for (int i = 0; i < 60 && !got; i++)
                {
                    got = solver.Process((byte[])imageL.Clone(), (byte[])imageR.Clone(), out result) && result.visible;
                    if (!got)
                        Thread.Sleep(10);
                }
                if (!got)
                {
                    report.AppendLine($"  {name}: marker NOT FOUND");
                    continue;
                }
                // As the headset does: the cube's pose, then the marker's back from it, then the canvas.
                var cube = ArMarkerMath.Mul(leftWorld, ArMarkerMath.CubeFromMarker(result.markerInLeft));
                var canvas = layout.CanvasFromMarker(ArMarkerMath.MarkerFromCube(cube));
                float worst = 0f;
                var half = layout.size * 0.5f;
                foreach (var corner in new[] { new Vector3(-half.x, half.y), new Vector3(half.x, half.y), new Vector3(half.x, -half.y), new Vector3(-half.x, -half.y) })
                    worst = Mathf.Max(worst, Vector3.Distance(ArMarkerMath.Apply(canvas, corner), ArMarkerMath.Apply(canvasWorld, corner)) * 1000f);
                // Where the tracked marker's black square sits on the real panel: its edges' distances from the panel's edges.
                var markerOnLaid = ArMarkerMath.Mul(ArMarkerMath.Inverse(canvasWorld), ArMarkerMath.Mul(leftWorld, result.markerInLeft));
                float w = layout.markerWidth * 0.5f;
                float fromSide = layout.right ? half.x - (markerOnLaid.position.x + w) : (markerOnLaid.position.x - w) + half.x;
                float fromTop = half.y - (markerOnLaid.position.y + w);
                var markerUp = markerOnLaid.rotation * Vector3.up;
                float turn = Mathf.Atan2(-markerUp.x, markerUp.y) * Mathf.Rad2Deg; // counter-clockwise seen from the front
                bool ok = worst < 12f && Mathf.Abs(Mathf.DeltaAngle(turn, layout.rotation)) < 2f;
                if (ok)
                    passed++;
                report.AppendLine($"  {name}: {(ok ? "ok" : "FAIL")} canvas corners within {worst:0.0} mm of the panel's; marker square " +
                    $"{fromSide * 100f:0.00} cm from the {(layout.right ? "right" : "left")} edge, {fromTop * 100f:0.00} cm from the top, " +
                    $"marker up turned {turn:0.0} deg on the panel (layout {layout.rotation:0}); paint area {layout.paintArea.x * 100f:0.0} x {layout.paintArea.y * 100f:0.0} cm");
            }
        }
        finally
        {
            solver.Dispose();
        }
        report.AppendLine($"  passed {passed} of {layouts.Count}");
        Debug.Log(report.ToString());
        return report.ToString();
    }

    // A white panel with the marker on it where the layout puts it, over a mid-grey background.
    static byte[] RenderPanel(ArMarkerMath.CameraModel camera, Pose canvasInCamera, ArMarkerCanvasLayout layout)
    {
        var image = new byte[camera.width * camera.height];
        var inverse = Quaternion.Inverse(canvasInCamera.rotation);
        var normal = canvasInCamera.rotation * Vector3.forward;
        float planeDistance = Vector3.Dot(normal, canvasInCamera.position);
        var canvasToMarker = ArMarkerMath.Inverse(layout.markerOnCanvas);
        var half = layout.size * 0.5f;
        for (int y = 0; y < camera.height; y++)
        {
            for (int x = 0; x < camera.width; x++)
            {
                int sum = 0;
                for (int s = 0; s < 4; s++)
                {
                    var ray = ArMarkerMath.PixelRay(camera, new Vector2(x + 0.25f + 0.5f * (s & 1), y + 0.25f + 0.5f * (s >> 1)));
                    float along = Vector3.Dot(normal, ray);
                    int value = 110 + ((x / 40 + y / 40) % 2) * 25;
                    if (Mathf.Abs(along) > 1e-6f && planeDistance / along > 0f)
                    {
                        var onCanvas = inverse * (ray * (planeDistance / along) - canvasInCamera.position);
                        if (Mathf.Abs(onCanvas.x) <= half.x && Mathf.Abs(onCanvas.y) <= half.y)
                            value = Shade(ArMarkerMath.Apply(canvasToMarker, onCanvas), 215);
                    }
                    sum += value;
                }
                image[y * camera.width + x] = (byte)(sum / 4);
            }
        }
        return image;
    }

    // The camera's view of the marker on white paper (10 cm square) over a mid-grey background, 2x2 samples a pixel.
    static byte[] Render(ArMarkerMath.CameraModel camera, Pose markerInCamera)
    {
        var image = new byte[camera.width * camera.height];
        var inverse = Quaternion.Inverse(markerInCamera.rotation);
        var normal = markerInCamera.rotation * Vector3.forward;
        float planeDistance = Vector3.Dot(normal, markerInCamera.position);
        for (int y = 0; y < camera.height; y++)
        {
            for (int x = 0; x < camera.width; x++)
            {
                int sum = 0;
                for (int s = 0; s < 4; s++)
                {
                    var pixel = new Vector2(x + 0.25f + 0.5f * (s & 1), y + 0.25f + 0.5f * (s >> 1));
                    var ray = ArMarkerMath.PixelRay(camera, pixel);
                    float along = Vector3.Dot(normal, ray);
                    int value = 110 + ((x / 40 + y / 40) % 2) * 25; // background checks
                    if (Mathf.Abs(along) > 1e-6f)
                    {
                        float t = planeDistance / along;
                        if (t > 0f)
                        {
                            var local = inverse * (ray * t - markerInCamera.position);
                            value = Shade(local, value);
                        }
                    }
                    sum += value;
                }
                image[y * camera.width + x] = (byte)(sum / 4);
            }
        }
        return image;
    }

    static int Shade(Vector3 local, int background)
    {
        float half = k_Width * 0.5f;
        if (Mathf.Abs(local.x) <= half && Mathf.Abs(local.y) <= half)
        {
            int col = Mathf.Clamp(Mathf.FloorToInt((local.x + half) / k_Width * 12f), 0, 11);
            int row = Mathf.Clamp(Mathf.FloorToInt((half - local.y) / k_Width * 12f), 0, 11);
            return k_Barcode1[row][col] == '#' ? 35 : 215;
        }
        if (Mathf.Abs(local.x) <= 0.05f && Mathf.Abs(local.y) <= 0.05f)
            return 215; // the paper's white margin
        return background;
    }

    [MenuItem("Tunnel/Paint/Test AR Marker Handoff (simulated headsets)")]
    public static string TestHandoff()
    {
        // Two headsets: A (the host, id 0) and B (id 1). The marker moves and turns on a table for 12 s.
        // A sees it 0-6 s and 8-12 s (quality 0.85); B sees it 2-12 s (quality 0.65), with each its own small bias.
        // Images are 45 ms old when a sighting is made; a sighting reaches the host 30 ms later from B, and the shared
        // pose reaches B 30 ms after the host publishes it.
        var random = new System.Random(7);
        Vector3 Truth(double t) => new(0.15f * Mathf.Sin((float)t * 0.8f), 0.75f, 0.1f * Mathf.Cos((float)t * 0.6f));
        Quaternion TruthTurn(double t) => Quaternion.Euler(0f, (float)t * 25f, 0f);
        Vector3 Noise(float mm) => new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * (mm * 0.002f);

        var arbiter = new ArMarkerArbiter();
        var viewers = new[] { (id: 0ul, chooser: new ArMarkerRenderChooser(), display: new ArMarkerDisplay()), (id: 1ul, chooser: new ArMarkerRenderChooser(), display: new ArMarkerDisplay()) };
        var inFlight = new List<(double arrive, ArMarkerSample sample)>();     // to the host
        var sharedFlight = new List<(double arrive, ArMarkerSample sample)>(); // to B
        var report = new StringBuilder("[ARMarker] handoff test\n");
        var lastDrawn = new Vector3[2];
        var lastTruth = new Vector3[2];
        var hasDrawn = new bool[2];
        var maxJump = new float[2];
        var maxError = new float[2];
        var modes = new List<string>[] { new(), new() };
        var lastMode = new ArMarkerRenderChooser.Mode[2];
        ulong lastSource = ulong.MaxValue;
        var sources = new List<string>();
        ArMarkerSample sharedAtB = default;
        int hiddenFrames = 0;

        const double frame = 1.0 / 72.0, camera = 1.0 / 30.0;
        double nextCamera = 0;
        for (double t = 0; t < 12.0; t += frame)
        {
            if (t >= nextCamera)
            {
                nextCamera += camera;
                double taken = t - 0.045;
                var turn = TruthTurn(taken);
                if (t < 6.0 || t >= 8.0)
                {
                    var a = new ArMarkerSample { position = Truth(taken) + new Vector3(0.002f, 0, 0) + Noise(1f), rotation = turn, time = taken, quality = 0.85f, cameras = 3, source = 0, valid = true };
                    viewers[0].chooser.AddLocal(a);
                    arbiter.Submit(a);
                }
                if (t >= 2.0)
                {
                    var b = new ArMarkerSample { position = Truth(taken) + new Vector3(-0.002f, 0, 0.001f) + Noise(1f), rotation = turn, time = taken, quality = 0.65f, cameras = 3, source = 1, valid = true };
                    viewers[1].chooser.AddLocal(b);
                    inFlight.Add((t + 0.03, b));
                }
            }
            for (int i = inFlight.Count - 1; i >= 0; i--)
                if (inFlight[i].arrive <= t) { arbiter.Submit(inFlight[i].sample); inFlight.RemoveAt(i); }

            var published = arbiter.Choose(t, out string change);
            if (change != null)
                sources.Add($"{t:0.00}s {change}");
            if (published.valid && published.source != lastSource)
                lastSource = published.source;
            sharedFlight.Add((t + 0.03, published));
            for (int i = 0; i < sharedFlight.Count; i++)
                if (sharedFlight[i].arrive <= t) sharedAtB = sharedFlight[i].sample;
            sharedFlight.RemoveAll(s => s.arrive <= t);

            for (int v = 0; v < 2; v++)
            {
                var (id, chooser, display) = viewers[v];
                chooser.SetShared(v == 0 ? published : sharedAtB);
                var mode = chooser.Update(t, id, out var target);
                if (mode != lastMode[v])
                {
                    modes[v].Add($"{t:0.00}s {mode}");
                    lastMode[v] = mode;
                }
                if (mode == ArMarkerRenderChooser.Mode.Hidden)
                {
                    display.Reset();
                    hasDrawn[v] = false;
                    if (t > 0.2) // before the first sighting arrives it is rightly hidden
                        hiddenFrames++;
                    continue;
                }
                var drawn = display.Step(target, mode == ArMarkerRenderChooser.Mode.Local ? "local" : "shared " + (v == 0 ? published.source : sharedAtB.source), (float)frame);
                var truth = Truth(t);
                if (hasDrawn[v] && t > 0.5)
                {
                    // How much the drawn cube moved this frame beyond what the marker itself moved.
                    float jump = ((drawn.position - lastDrawn[v]) - (truth - lastTruth[v])).magnitude * 1000f;
                    maxJump[v] = Mathf.Max(maxJump[v], jump);
                    maxError[v] = Mathf.Max(maxError[v], Vector3.Distance(drawn.position, truth) * 1000f);
                }
                lastDrawn[v] = drawn.position;
                lastTruth[v] = truth;
                hasDrawn[v] = true;
            }
        }

        report.AppendLine("  shared source changes: " + string.Join("; ", sources));
        report.AppendLine($"  handoffs {arbiter.handoffs}");
        for (int v = 0; v < 2; v++)
            report.AppendLine($"  headset {(v == 0 ? "A (host)" : "B")}: drawing {string.Join(", ", modes[v])}; largest extra step in a frame {maxJump[v]:0.0} mm; " +
                $"largest distance from the marker {maxError[v]:0.0} mm");
        bool ok = arbiter.handoffs == 2 && maxJump[0] < 3f && maxJump[1] < 3f && maxError[0] < 15f && maxError[1] < 15f && hiddenFrames == 0;
        report.AppendLine(ok ? "  PASS: two handoffs (A to B when A looked away, back to A once clearly better), no visible jump"
            : "  FAIL (expected 2 handoffs, extra steps under 3 mm, never hidden)");

        // All stale: the cube hides.
        var hide = new ArMarkerRenderChooser();
        var lone = new ArMarkerArbiter();
        lone.Submit(new ArMarkerSample { position = Vector3.zero, rotation = Quaternion.identity, time = 0, quality = 0.8f, cameras = 3, source = 0, valid = true });
        hide.SetShared(lone.Choose(0.05, out _));
        var early = hide.Update(0.05, 1, out _);
        hide.SetShared(lone.Choose(1.0, out _));
        var late = hide.Update(1.0, 1, out _);
        report.AppendLine($"  stale check: shown {early} at 50 ms, {late} at 1 s ({(early == ArMarkerRenderChooser.Mode.Shared && late == ArMarkerRenderChooser.Mode.Hidden ? "ok" : "FAIL")})");

        Debug.Log(report.ToString());
        return report.ToString();
    }
}
