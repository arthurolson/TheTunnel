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

    [MenuItem("Tunnel/Paint/Test Floor QR (rendered stereo pairs)")]
    public static string TestFloorQr()
    {
        // The printed floor code (Printables/TheTunnel-floor-QR.png: 25 modules including the quiet zone = 19 cm)
        // rendered on the floor for both cameras of 4004X's lens rig, decoded with ZXing and placed as the scanner does.
        var texture = new Texture2D(2, 2);
        texture.LoadImage(System.IO.File.ReadAllBytes("Printables/TheTunnel-floor-QR.png"));
        var modules = new bool[25, 25]; // [row from the top, column]
        for (int row = 0; row < 25; row++)
            for (int col = 0; col < 25; col++)
                modules[row, col] = texture.GetPixelBilinear((col + 0.5f) / 25f, 1f - (row + 0.5f) / 25f).grayscale < 0.5f;
        UnityEngine.Object.DestroyImmediate(texture);
        const float size = 0.19f, module = size / 25f;

        var headFromLeft = AndroidLens(new[] { -0.0317f, -0.0127f, -0.0738f }, new[] { -0.9984f, -0.0014f, 0.0057f, 0.0566f });
        var headFromRight = AndroidLens(new[] { 0.0318f, -0.0129f, -0.0734f }, new[] { -0.9987f, 0.0026f, -0.0024f, 0.0518f });
        var left = new ArMarkerMath.CameraModel(1728, 1728, 1174.3f, 1174.3f, 862.1f, 867.4f);
        var right = new ArMarkerMath.CameraModel(1728, 1728, 1171.3f, 1171.3f, 862.2f, 864.5f);
        var report = new StringBuilder("[QR] floor code test (both cameras, Quest 3S 4004X's lenses, 1728x1728)\n");
        var cases = new (string name, Vector3 head, float headYaw, float pitch, Vector3 code, float codeYaw)[]
        {
            ("standing, looking down 55 deg", new Vector3(0f, 1.62f, -0.75f), 0f, 55f, new Vector3(0.05f, 0f, 0.15f), 20f),
            ("standing beside it, turned", new Vector3(0.6f, 1.55f, 0.4f), -100f, 60f, new Vector3(-0.1f, 0f, 0.2f), 137f),
            ("further, shallower", new Vector3(-0.3f, 1.7f, -1.1f), 15f, 45f, new Vector3(0f, 0f, 0.2f), -60f),
        };
        int passed = 0;
        foreach (var c in cases)
        {
            var head = new Pose(c.head, Quaternion.Euler(c.pitch, c.headYaw, 0f));
            var forward = Quaternion.Euler(0f, c.codeYaw, 0f) * Vector3.forward;
            var rightAxis = Vector3.Cross(Vector3.up, forward);
            byte[] Render(ArMarkerMath.CameraModel model, Pose camera)
            {
                var image = new byte[model.width * model.height];
                for (int y = 0; y < model.height; y++)
                    for (int x = 0; x < model.width; x++)
                    {
                        int sum = 0;
                        for (int s = 0; s < 4; s++)
                        {
                            var ray = camera.rotation * ArMarkerMath.PixelRay(model, new Vector2(x + 0.25f + 0.5f * (s & 1), y + 0.25f + 0.5f * (s >> 1)));
                            int value = 150; // grey carpet
                            if (ray.y < -1e-4f)
                            {
                                var hit = camera.position + ray * (-camera.position.y / ray.y) - c.code;
                                float u = Vector3.Dot(hit, rightAxis), v = Vector3.Dot(hit, forward);
                                int col = Mathf.FloorToInt((u + size * 0.5f) / module), row = Mathf.FloorToInt((size * 0.5f - v) / module);
                                if (col >= 0 && col < 25 && row >= 0 && row < 25)
                                    value = modules[row, col] ? 30 : 225;
                                else if (Mathf.Abs(u) < 0.13f && Mathf.Abs(v) < 0.13f)
                                    value = 225; // the paper
                            }
                            sum += value;
                        }
                        image[y * model.width + x] = (byte)(sum / 4);
                    }
                return image;
            }

            var leftWorld = ArMarkerMath.Mul(head, headFromLeft);
            var rightWorld = ArMarkerMath.Mul(head, headFromRight);
            var results = new List<string>();
            var found = new List<(Vector3 position, Vector3 forward, float floor)>();
            foreach (var (label, model, camera) in new[] { ("left", left, leftWorld), ("right", right, rightWorld) })
            {
                var image = Render(model, camera);
                if (!QrFloorScanner.DecodeImage(image, model.width, model.height, out string text, out var points))
                {
                    results.Add($"{label}: not decoded");
                    continue;
                }
                if (!QrFloorScanner.PlaceOnFloor(points, model, camera, 0.1064f, 0.2f, out var position, out var codeForward, out float floorY, out string why))
                {
                    results.Add($"{label}: decoded '{text}' but not placed ({why})");
                    continue;
                }
                found.Add((position, codeForward, floorY));
                results.Add($"{label}: {Vector3.Distance(position, c.code) * 1000f:0.0} mm, {Vector3.SignedAngle(forward, codeForward, Vector3.up):0.00} deg off, floor at {floorY * 1000f:0.0} mm");
            }
            // One camera alone is enough (the scanner uses whichever see the code); both are averaged.
            bool ok = found.Count == 1 && Vector3.Distance(found[0].position, c.code) < 0.01f;
            if (found.Count == 2)
            {
                var mean = (found[0].position + found[1].position) * 0.5f;
                float error = Vector3.Distance(mean, c.code) * 1000f;
                float yawError = Vector3.SignedAngle(forward, (found[0].forward + found[1].forward).normalized, Vector3.up);
                ok = error < 10f && Mathf.Abs(yawError) < 1f;
                results.Add($"both: {error:0.0} mm, {yawError:0.00} deg off; left and right {Vector3.Distance(found[0].position, found[1].position) * 1000f:0.0} mm apart");
            }
            if (ok)
                passed++;
            report.AppendLine($"  {c.name}: {(ok ? "ok" : "FAIL")} - " + string.Join("; ", results));
        }
        report.AppendLine($"  passed {passed} of {cases.Length}");
        Debug.Log(report.ToString());
        return report.ToString();
    }

    [MenuItem("Tunnel/Paint/Test Room Alignment from the Marker (simulated host and client)")]
    public static string TestRoomAlignment()
    {
        // The client's world is off from the host's by 24 cm and 3 deg (as after the floor QR on 12 Nov). Both see a
        // still marker; each sighting has a little jitter, and the client's clock pairs are within a frame.
        var random = new System.Random(4);
        float Gauss() => (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble()));
        var marker = new Pose(new Vector3(0.4f, 0.78f, 0.3f), Quaternion.Euler(-90f, 35f, 0f));
        // Client's error: its world is the host's turned 3 deg about (0.2, 0, -0.1) and shifted (0.2, 0, 0.13).
        var errorTurn = Quaternion.AngleAxis(3f, Vector3.up);
        var errorPivot = new Vector3(0.2f, 0f, -0.1f);
        var errorShift = new Vector3(0.2f, 0.01f, 0.13f);
        Pose ClientSees(Pose truth) => new(errorTurn * (truth.position - errorPivot) + errorPivot + errorShift, errorTurn * truth.rotation);
        var alignment = new ArMarkerRoomAlignment();
        var report = new StringBuilder("[QR] room alignment from the marker: client off by 24 cm and 3 deg\n");
        ArMarkerRoomAlignment.Correction correction = default;
        bool got = false;
        double t = 0;
        int frames = 0;
        for (; frames < 200 && !got; frames++, t += 1.0 / 30.0)
        {
            Pose Jitter(Pose p) => new(p.position + new Vector3(Gauss(), Gauss(), Gauss()) * 0.002f, Quaternion.Euler(Gauss() * 0.7f, Gauss() * 0.7f, Gauss() * 0.7f) * p.rotation);
            var own = Jitter(ClientSees(marker));
            alignment.AddOwn(new ArMarkerSample { position = own.position, rotation = own.rotation, time = t + 0.01 * Gauss(), valid = true, quality = 0.9f, cameras = 3 });
            var host = Jitter(marker);
            alignment.AddHost(new ArMarkerSample { position = host.position, rotation = host.rotation, time = t, valid = true, quality = 0.9f, cameras = 3 });
            got = alignment.TryCorrection(out correction, out _);
        }
        if (!got)
            return report.Append("  FAIL: no correction").ToString();
        // Apply it to points of the client's world: they should land on the host's.
        float worst = 0f;
        foreach (var p in new[] { marker.position, new Vector3(-1f, 0f, 2f), new Vector3(2f, 1.5f, -1f) })
        {
            var clientPoint = errorTurn * (p - errorPivot) + errorPivot + errorShift;
            worst = Mathf.Max(worst, Vector3.Distance(correction.Apply(clientPoint), p) * 1000f);
        }
        bool ok = Mathf.Abs(correction.turn + 3f) < 0.5f && worst < 40f;
        report.AppendLine($"  after {frames} frames ({frames / 30f:0.0} s): {correction}");
        report.AppendLine($"  the client's world corrected: worst point {worst:0.0} mm from the host's (within 2 m of the marker); turn {correction.turn:0.00} deg (true -3)");
        report.AppendLine(ok ? "  PASS" : "  FAIL");
        Debug.Log(report.ToString());
        return report.ToString();
    }

    [MenuItem("Tunnel/Paint/Test Copy to Wall Matches the Canvas (in Play mode)")]
    public static string TestCopyMatches()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Paint] enter Play mode (any scene) first, then run Tunnel > Paint > Test Copy to Wall Matches the Canvas");
            return "needs Play mode";
        }
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Call(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, flags).Invoke(target, args.Length == 0 ? null : args);

        // The AR canvas, locked (sightings pushed in), a palette away from it, and its marker, held.
        var cubePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Tunnel/Paint/ARMarker_Cube_Networked.prefab");
        if (ArMarkerCube.instance != null)
            UnityEngine.Object.DestroyImmediate(ArMarkerCube.instance.gameObject);
        ArMarkerCube.Toggle(cubePrefab, false, ArMarkerCube.Shows.Canvas);
        var cube = ArMarkerCube.instance;
        Call(cube, "Start");
        var panel = new Pose(new Vector3(0f, 1.1f, 0.6f), Quaternion.Euler(30f, 0f, 0f));
        var cubePose = ArMarkerMath.CubeFromMarker(ArMarkerMath.Mul(panel, cube.layout.markerOnCanvas));
        for (int i = 0; i < 40 && !cube.canvasLocked; i++)
        {
            Call(cube, "OnSighted", new ArMarkerTracker.Sighting { cubeInTable = cubePose, quality = 0.9f, cameras = 3, confidence = 1f, age = -1000.0 - i * 0.033 });
            Call(cube, "Update");
        }
        Call(cube, "Update");
        var canvas = cube.GetComponentInChildren<ArMarkerCanvas>(true);
        var source = cube.canvasPaint;
        var palette = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Tunnel/Paint/Paint_Palette_Networked.prefab"),
            new Vector3(2f, 1.2f, 0f), Quaternion.LookRotation(Vector3.left)).GetComponent<TunnelPalette>();
        var marker = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Tunnel/Paint/Paint_Marker_Networked.prefab")).GetComponent<TunnelMarker>();
        marker.SetPalette(palette);
        typeof(TunnelMarker).GetField("m_LocalDocked", flags).SetValue(marker, false);
        marker.heldForTests = true;
        var report = new StringBuilder("[Paint] copy-to-wall test: the AR canvas painted through the virtual marker, copied, compared\n");

        // One frame of painting: the marker's tip at a point of the canvas (u, v over the paint area; lift above the
        // real panel's surface), pointing into it.
        var area = canvas.layout.paintArea;
        Vector3 OnCanvas(float u, float v, float above) => canvas.transform.TransformPoint((u - 0.5f) * area.x, (v - 0.5f) * area.y, canvas.layout.lift - above);
        void Frame(Vector3 tip)
        {
            marker.transform.rotation = canvas.transform.rotation; // its tip (+Z) into the panel
            marker.transform.position = tip - marker.transform.forward * (marker.transform.lossyScale.z * 0.5f);
            Call(marker, "Update");
            Call(source, "LateUpdate");
        }
        void Lift() => Frame(OnCanvas(0.5f, 0.5f, 0.2f));
        void Stroke(float u0, float v0, float u1, float v1, int frames = 30, float above = 0f)
        {
            for (int i = 0; i <= frames; i++)
                Frame(OnCanvas(Mathf.Lerp(u0, u1, i / (float)frames), Mathf.Lerp(v0, v1, i / (float)frames), above));
            Lift();
        }
        void Mode(PaintMode mode) => Call(palette, "SetMode", mode);
        void Size(float diameter) => marker.transform.localScale = new Vector3(diameter, diameter, marker.transform.localScale.z);
        void Disk(int index)
        {
            // Touch a palette disk with the tip, as a painter does to change colour.
            var disks = (UnityEngine.UI.Button[])typeof(TunnelPalette).GetField("m_Disks", flags).GetValue(palette);
            Frame(disks[index].transform.position);
            Lift();
        }

        var scenarios = new (string name, Action paint)[]
        {
            ("colours (touching disks) crossing each other", () =>
            {
                Mode(PaintMode.Paint); Size(0.01f);
                Disk(0); Stroke(0.1f, 0.2f, 0.9f, 0.8f);
                Disk(2); Stroke(0.1f, 0.8f, 0.9f, 0.2f);
                Disk(3); Stroke(0.5f, 0.05f, 0.5f, 0.95f);
            }),
            ("sizes", () =>
            {
                Mode(PaintMode.Paint); Disk(1);
                Size(0.004f); Stroke(0.1f, 0.3f, 0.9f, 0.3f);
                Size(0.02f); Stroke(0.1f, 0.6f, 0.9f, 0.6f);
                Size(0.01f);
            }),
            ("pencil: straight and curve", () =>
            {
                Mode(PaintMode.Pencil); Disk(4);
                foreach (var p in new[] { new Vector2(0.1f, 0.1f), new Vector2(0.4f, 0.5f), new Vector2(0.2f, 0.8f) }) { Frame(OnCanvas(p.x, p.y, 0f)); Lift(); }
                Frame(OnCanvas(0.2f, 0.8f, 0f)); Lift(); // the last point again finishes it
                Call(palette, "SetCurve", true);
                foreach (var p in new[] { new Vector2(0.5f, 0.2f), new Vector2(0.7f, 0.9f), new Vector2(0.9f, 0.3f) }) { Frame(OnCanvas(p.x, p.y, 0f)); Lift(); }
                Call(palette, "RequestEndLine"); Lift();
                Call(palette, "SetCurve", false);
            }),
            ("airbrush", () =>
            {
                Mode(PaintMode.Airbrush); Disk(2);
                Stroke(0.1f, 0.5f, 0.9f, 0.5f, 40, 0.05f);
                Stroke(0.3f, 0.2f, 0.3f, 0.8f, 20, 0f);
            }),
            ("paint, erase, paint again", () =>
            {
                Mode(PaintMode.Paint); Size(0.012f); Disk(0);
                Stroke(0.1f, 0.5f, 0.9f, 0.5f);
                Mode(PaintMode.Eraser); Size(0.02f); Stroke(0.5f, 0.2f, 0.5f, 0.8f);
                Mode(PaintMode.Paint); Size(0.008f); Disk(3); Stroke(0.4f, 0.3f, 0.6f, 0.7f);
            }),
            ("across the dashed edge", () =>
            {
                Mode(PaintMode.Paint); Size(0.012f); Disk(1);
                Stroke(-0.1f, 0.4f, 0.3f, 0.6f);
                Stroke(0.8f, 1.1f, 0.95f, 0.5f);
            }),
            ("Re-find between strokes", () =>
            {
                Mode(PaintMode.Paint); Size(0.01f); Disk(2);
                Stroke(0.2f, 0.2f, 0.8f, 0.3f);
                ArMarkerCube.Toggle(cubePrefab, false, ArMarkerCube.Shows.Canvas); // Re-find canvas
                Relock();
                Disk(0); Stroke(0.2f, 0.7f, 0.8f, 0.6f);
            }),
            ("pencil lines with a colour change (a disk) between them", () =>
            {
                Mode(PaintMode.Pencil); Disk(0);
                foreach (var p in new[] { new Vector2(0.1f, 0.6f), new Vector2(0.45f, 0.9f) }) { Frame(OnCanvas(p.x, p.y, 0f)); Lift(); }
                Disk(2); // the red line goes on the canvas; the next touches start a green one
                foreach (var p in new[] { new Vector2(0.55f, 0.9f), new Vector2(0.9f, 0.6f) }) { Frame(OnCanvas(p.x, p.y, 0f)); Lift(); }
                Call(palette, "RequestEndLine"); Lift();
            }),
            ("a pencil line still being drawn when copied", () =>
            {
                Mode(PaintMode.Pencil); Disk(3);
                foreach (var p in new[] { new Vector2(0.15f, 0.15f), new Vector2(0.85f, 0.2f), new Vector2(0.6f, 0.85f) }) { Frame(OnCanvas(p.x, p.y, 0f)); Lift(); }
                // not finished: it shows on the canvas (as a preview) while the painter goes to Copy to wall
            }),
        };

        void Relock()
        {
            for (int i = 0; i < 40 && !cube.canvasLocked; i++)
            {
                Call(cube, "OnSighted", new ArMarkerTracker.Sighting { cubeInTable = cubePose, quality = 0.9f, cameras = 3, confidence = 1f, age = -2000.0 - s_Sightings++ * 0.033 });
                Call(cube, "Update");
            }
            Call(cube, "Update");
        }

        int ok = 0;
        foreach (var (name, paint) in scenarios)
        {
            source.Clear();
            Call(source, "LateUpdate");
            paint();
            Call(source, "LateUpdate");
            report.AppendLine($"  {name}: {Compare(source, Copy(source, cube.canvasPaintMeters), out bool same)}");
            if (same)
                ok++;
        }

        // Everything at once, without clearing in between.
        source.Clear();
        Call(source, "LateUpdate");
        foreach (var (_, paint) in scenarios)
            paint();
        Call(source, "LateUpdate");
        string all = Compare(source, Copy(source, cube.canvasPaintMeters), out bool allSame);
        report.AppendLine($"  all of it on one canvas: {all}");
        if (allSame)
            ok++;
        report.AppendLine(ok == scenarios.Length + 1 ? "  PASS" : $"  FAIL ({ok} of {scenarios.Length + 1} match)");

        UnityEngine.Object.Destroy(marker.gameObject);
        UnityEngine.Object.Destroy(palette.gameObject);
        UnityEngine.Object.Destroy(cube.gameObject);
        Debug.Log(report.ToString());
        return report.ToString();

        // A copy as Copy to wall makes it (the canvas's record replayed into it), drawn.
        TunnelSegmentPaint Copy(TunnelSegmentPaint from, Vector2 meters)
        {
            TunnelPalette.FinishPencilLines(); // as Copy to wall does first
            Call(from, "LateUpdate");
            var holder = new GameObject("Copy Holder");
            holder.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Tunnel/Paint/Paint_Copy_Networked.prefab"),
                new Vector3(-2f, 1.2f, 0f), Quaternion.identity, holder.transform);
            copy.GetComponent<PaintingCopy>().Setup(meters, 0, 0);
            copy.transform.SetParent(null, true);
            UnityEngine.Object.Destroy(holder);
            var paint = copy.GetComponent<TunnelSegmentPaint>();
            var replay = from.ReplayInto(paint);
            while (replay.MoveNext())
                Call(paint, "LateUpdate");
            Call(paint, "LateUpdate");
            return paint;
        }
    }

    // The most painted of a cell's neighbourhood (by alpha).
    static Color Darkest(Texture2D t, float u, float v, float du, float dv)
    {
        var best = new Color(0, 0, 0, 0);
        for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                var c = t.GetPixelBilinear(u + i * du, v + j * dv);
                if (c.a > best.a) best = c;
            }
        return best;
    }

    // Whether a colour is found in a cell's neighbourhood (two cells round: crossings and airbrush grain fall on
    // different pixels at the two texel sizes).
    static bool Near(Texture2D t, Color colour, float u, float v, float du, float dv)
    {
        for (int j = -2; j <= 2; j++)
            for (int i = -2; i <= 2; i++)
            {
                var c = t.GetPixelBilinear(u + i * du, v + j * dv);
                if (Mathf.Abs(c.r - colour.r) + Mathf.Abs(c.g - colour.g) + Mathf.Abs(c.b - colour.b) + Mathf.Abs(c.a - colour.a) < 0.2f)
                    return true;
            }
        return false;
    }

    static int s_Sightings;

    /// <summary>When set, Compare saves both pictures there (for looking at differences).</summary>
    public static string dumpFolder;
    static int s_Dumps;

    // Both canvases resampled to the same grid: cells painted on the canvas but not on the copy (or the other way),
    // and colours that differ.
    static string Compare(TunnelSegmentPaint source, TunnelSegmentPaint copy, out bool same)
    {
        var a = source.ReadCanvas();
        var b = copy.ReadCanvas();
        same = false;
        if (a == null || b == null)
        {
            if (b != null) UnityEngine.Object.Destroy(b);
            if (a != null) UnityEngine.Object.Destroy(a);
            UnityEngine.Object.Destroy(copy.gameObject);
            return $"nothing to compare (canvas {(a != null)}, copy {(b != null)})";
        }
        if (dumpFolder != null)
        {
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dumpFolder, $"compare-{++s_Dumps}-canvas.png"), a.EncodeToPNG());
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dumpFolder, $"compare-{s_Dumps}-copy.png"), b.EncodeToPNG());
        }
        int w = 200, h = Mathf.RoundToInt(200f * a.height / a.width);
        int painted = 0, missing = 0, extra = 0, colour = 0;
        var where = new List<string>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
                var ca = a.GetPixelBilinear(u, v);
                var cb = b.GetPixelBilinear(u, v);
                if (ca.a > 0.3f) painted++;
                // The two canvases have different texel sizes, so a stroke's edge falls on different pixels: a cell
                // counts as missing (or extra, or another colour) only where the other picture has nothing like it
                // anywhere within a cell around.
                // (The cheap test first: the neighbourhood is looked at only where the cell itself differs.)
                if (ca.a > 0.5f && cb.a < 0.1f && Darkest(b, u, v, 1f / w, 1f / h).a < 0.1f) { missing++; if (where.Count < 4) where.Add($"({u:0.00}, {v:0.00})"); }
                else if (cb.a > 0.5f && ca.a < 0.1f && Darkest(a, u, v, 1f / w, 1f / h).a < 0.1f) extra++;
                else if (ca.a > 0.9f && cb.a > 0.9f && Mathf.Abs(ca.r - cb.r) + Mathf.Abs(ca.g - cb.g) + Mathf.Abs(ca.b - cb.b) + Mathf.Abs(ca.a - cb.a) >= 0.2f &&
                    !Near(b, ca, u, v, 1f / w, 1f / h)) colour++;
            }
        UnityEngine.Object.Destroy(a);
        UnityEngine.Object.Destroy(b);
        UnityEngine.Object.Destroy(copy.gameObject);
        float total = Mathf.Max(painted, 1);
        same = painted > 0 && (missing + extra + colour) / total < 0.01f;
        return $"{(same ? "match" : "DIFFER")}: painted {painted} cells; missing on the copy {missing} ({missing / total * 100f:0.0}%){(where.Count > 0 ? " e.g. at " + string.Join(" ", where) : "")}, " +
            $"extra {extra}, other colour {colour}";
    }

    [MenuItem("Tunnel/Paint/Test AR Canvas Lock (jittery sightings)")]
    public static string TestCanvasLock()
    {
        // 30 sightings a second of a still marker: 3 mm and 1.5 deg of jitter, and one in eight wildly off (a flipped
        // tilt or a bad corner). The locker must settle within about a second, near the truth.
        var random = new System.Random(11);
        float Gauss() => (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble()));
        var truth = new Pose(new Vector3(0.3f, 0.82f, 0.45f), Quaternion.Euler(-30f, 20f, 5f));
        var report = new StringBuilder("[ARMarker] canvas lock test\n");
        bool allOk = true;
        foreach (var (jitterMm, jitterDeg, outliers) in new[] { (3f, 1.5f, 8), (1f, 0.5f, 0), (6f, 3f, 5) })
        {
            var locker = new ArMarkerCanvasLocker();
            ArMarkerCanvasLocker.Result result = default;
            double t = 0;
            bool locked = false;
            int fed = 0;
            for (; fed < 150 && !locked; fed++, t += 1.0 / 30.0)
            {
                var position = truth.position + new Vector3(Gauss(), Gauss(), Gauss()) * jitterMm * 0.001f;
                var rotation = Quaternion.Euler(Gauss() * jitterDeg, Gauss() * jitterDeg, Gauss() * jitterDeg) * truth.rotation;
                if (outliers > 0 && fed % outliers == 3)
                {
                    position += new Vector3(0.03f, -0.02f, 0.05f);
                    rotation = Quaternion.Euler(25f, 0f, 0f) * rotation;
                }
                locker.Add(t, new Pose(position, rotation));
                locked = locker.TryLock(out result);
            }
            float error = Vector3.Distance(result.pose.position, truth.position) * 1000f, turn = Quaternion.Angle(result.pose.rotation, truth.rotation);
            // At the canvas's far corner (about 55 cm from the marker) a turn error moves the canvas this much.
            float corner = turn * Mathf.Deg2Rad * 550f;
            bool ok = locked && error < 2f && turn < 0.8f;
            allOk &= ok || jitterMm >= 6f; // very noisy sightings may rightly never settle
            report.AppendLine($"  jitter {jitterMm} mm / {jitterDeg} deg, {(outliers > 0 ? $"1 in {outliers} wild" : "no wild ones")}: " +
                (locked ? $"locked after {fed} sightings ({fed / 30f:0.0} s): {result}; {error:0.0} mm and {turn:0.00} deg from the truth (far corner ~{corner:0.0} mm)"
                    : $"not locked after {fed} sightings ({result}), as it should not with this much jitter")
                + (ok || jitterMm >= 6f ? "" : "  FAIL"));
        }
        report.AppendLine(allOk ? "  PASS" : "  FAIL");
        Debug.Log(report.ToString());
        return report.ToString();
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
