using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using AxisTurn = StereoPassthroughCamera.AxisTurn;

/// <summary>
/// This headset's own tracking of the printed AR marker, with both passthrough cameras at once: Unity OpenXR Meta's
/// camera subsystem hands over synchronized left/right image pairs (Meta's Passthrough Camera API underneath), their
/// luminance goes into artoolkitX's stereo solver (<see cref="ArMarkerStereoSolver"/>) on a worker thread, and the
/// marker's pose is placed in the room with the left camera's pose at the moment the images were taken (the head pose
/// then, from a short history, and the lens's pose on the head from Android's camera characteristics). Sightings are
/// given in the shared tabletop frame (<see cref="TableTop"/>) for <see cref="ArMarkerCube"/> to share.
/// Writes a log and, now and then, annotated camera pictures to files/ARMarker on the headset (pull with adb).
/// Settings can be changed without a new build by putting key=value lines in files/ARMarker/settings.txt.
/// </summary>
public class ArMarkerTracker : MonoBehaviour
{
    [SerializeField] int m_BarcodeId = 1;
    [SerializeField] float m_MarkerWidth = 0.06f;
    [SerializeField]
    [Tooltip("How a lens's Android pose becomes a Unity camera (Meta's samples turn it 180 degrees about x).")]
    AxisTurn m_AxisTurn = AxisTurn.Meta180X;
    [SerializeField]
    [Tooltip("The head pose read before rendering is predicted this far (seconds) ahead of the moment it's read.")]
    float m_HeadPoseLead = 0.03f;
    [SerializeField] ArMarkerStereoSolver.Threshold m_Threshold = ArMarkerStereoSolver.Threshold.Otsu;
    [SerializeField] float m_SnapshotEvery = 15f;
    [SerializeField] int m_MaxSnapshots = 24;
    [SerializeField] bool m_SwapEyes;
    [SerializeField]
    [Tooltip("Search each camera's image at half size (four times faster; the marker is still found from about 1.5 m).")]
    bool m_HalveImages = true;

    /// <summary>One sighting, ready to share.</summary>
    public struct Sighting
    {
        public Pose cubeInTable;
        public float quality;
        public byte cameras;
        public float confidence;
        /// <summary>How old the camera images were when this sighting reached the main thread (seconds).</summary>
        public double age;
    }

    public static ArMarkerTracker instance { get; private set; }

    public static ArMarkerTracker Get()
    {
        if (instance == null)
            instance = new GameObject("AR Marker Tracker").AddComponent<ArMarkerTracker>();
        return instance;
    }

    public event Action<Sighting> sighted;

    public bool active { get; private set; }
    public string status { get; private set; } = "Off.";
    /// <summary>Which cameras showed the marker in the latest pair: L+R, L only, R only or none.</summary>
    public string camerasText { get; private set; } = "none";
    public float markerWidth => m_MarkerWidth;
    public float lastQuality { get; private set; }

    // Both cameras (shared with the floor QR alignment, one user at a time).
    StereoPassthroughCamera m_Camera;
    const string k_Owner = "AR marker tracker";
    ArMarkerMath.CameraModel m_LeftModel, m_RightModel;
    Pose m_RightFromLeft;
    Transform m_Table;

    // Worker
    class Job
    {
        public byte[] left, right;
        public double captureClock;
        public Pose tableFromLeft;
        public bool snapshot;
    }
    struct Done
    {
        public bool started, processed, visible;
        public string error;
        public double captureClock;
        public Sighting sighting;
        public ArMarkerMath.ImageCheck checkL, checkR;
        public Pose markerInLeft;
        public float processMs;
        public bool disagreed;
        public float apart;
    }
    Thread m_Thread;
    readonly object m_Lock = new();
    readonly AutoResetEvent m_Wake = new(false);
    Job m_Pending;
    readonly Queue<Done> m_Done = new();
    readonly Stack<byte[]> m_Buffers = new();
    volatile bool m_Stop;
    bool m_Busy;
    const int SolverStarting = 0, SolverRunning = 1, SolverFailed = 2;
    volatile int m_SolverState;
    volatile string m_SolverError;

    // Counts for the once-a-second log line.
    int m_Pairs, m_Dropped, m_Unpaired, m_Found, m_Both, m_OnlyL, m_OnlyR, m_NoHead, m_NotReady, m_Disagreed;
    float m_SumAge, m_SumProcess, m_SumQuality;
    float m_NextReport;
    float m_NextSnapshot;
    int m_Snapshots;
    string m_Folder;
    StreamWriter m_Log;
    static readonly object s_LogLock = new();

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    #region Starting and stopping

    /// <summary>Starts tracking on this headset (asks for camera permission first if needed).</summary>
    public void Begin()
    {
        if (active)
            return;
        active = true;
        StartCoroutine(Starting());
    }

    /// <summary>Stops tracking and gives the cameras back.</summary>
    public void End()
    {
        if (!active)
            return;
        active = false;
        StopAllCoroutines();
        StopWorker();
        CloseCamera();
        SetStatus("Off.");
        Log("stopped");
        CloseLog();
    }

    void OnDestroy()
    {
        End();
        if (instance == this)
            instance = null;
    }

    IEnumerator Starting()
    {
        OpenLog();
        LoadSettings();
        m_Snapshots = 0;
        Log($"starting: artoolkitX {ArMarkerStereoSolver.Version()}, barcode 3x3 Hamming(6,3) id {m_BarcodeId}, " +
            $"{m_MarkerWidth * 1000f:0} mm, axis turn {m_AxisTurn}, head pose lead {m_HeadPoseLead * 1000f:0} ms, threshold {m_Threshold}, swap eyes {m_SwapEyes}, halve images {m_HalveImages}");

        if (RealMarkerTracker.instance != null && RealMarkerTracker.instance.stage != RealMarkerTracker.Stage.Off)
        {
            Stop("The real marker is using the camera: stop it on the palette first.");
            yield break;
        }
        var scanner = FindAnyObjectByType<QrFloorScanner>();
        while (scanner != null && scanner.isScanning)
        {
            SetStatus("Waiting for the QR alignment to finish with the camera...");
            yield return null;
        }

        m_Camera = StereoPassthroughCamera.Get();
        m_Camera.axisTurn = m_AxisTurn;
        m_Camera.headPoseLead = m_HeadPoseLead;
        m_Camera.swapEyes = m_SwapEyes;
        yield return m_Camera.Open(k_Owner, SetStatus, Log);
        if (!m_Camera.isOpen)
        {
            Stop(m_Camera.error ?? "The cameras didn't open.");
            yield break;
        }
        m_LeftModel = m_Camera.leftModel;
        m_RightModel = m_Camera.rightModel;
        m_RightFromLeft = m_Camera.rightFromLeft;

        var table = FindAnyObjectByType<TableTop>();
        m_Table = table != null ? table.transform : null;
        Log(m_Table != null
            ? $"shared frame: TableTop '{m_Table.name}' at {m_Table.position:F3}, yaw {m_Table.eulerAngles.y:0.0}"
            : "shared frame: no TableTop in the scene, using world coordinates");

        StartWorker();
        m_NextReport = Time.unscaledTime + 1f;
        m_NextSnapshot = Time.unscaledTime + 3f;
        SetStatus("Tracking: show the marker to this headset.");
    }

    void Stop(string message)
    {
        SetStatus(message);
        Log("not tracking: " + message);
        active = false;
        StopWorker();
        CloseCamera();
    }

    void CloseCamera()
    {
        if (m_Camera != null)
            m_Camera.Close(k_Owner);
    }

    #endregion

    #region Every frame

    void Update()
    {
        if (!active || m_Camera == null || !m_Camera.isOpen || m_Thread == null)
            return;
        if (m_SolverState == SolverFailed)
        {
            Stop("artoolkitX didn't start: " + m_SolverError + " (press AR marker cube twice to retry)");
            return;
        }
        AcquirePair();
        DrainResults();
        if (Time.unscaledTime >= m_NextReport)
            Report();
    }

    void AcquirePair()
    {
        // The worker takes one pair at a time; pairs arriving while it is busy are only counted.
        bool free;
        lock (m_Lock)
            free = !m_Busy && m_Pending == null && m_SolverState == SolverRunning;
        byte[] left = free ? Rent() : null, right = free ? Rent() : null;
        var result = m_Camera.TryAcquire(free, left, right, out var pair);
        if (result != StereoPassthroughCamera.Result.New || !free)
        {
            if (free)
            {
                Return(left);
                Return(right);
            }
            switch (result)
            {
                case StereoPassthroughCamera.Result.Nothing:
                    return;
                case StereoPassthroughCamera.Result.Unsynced: m_Unpaired++; break;
                case StereoPassthroughCamera.Result.NoHeadPose: m_NoHead++; break;
                case StereoPassthroughCamera.Result.New:
                    if (m_SolverState != SolverRunning) m_NotReady++; else m_Dropped++;
                    break;
            }
            m_Pairs++;
            return;
        }
        m_Pairs++;

        var table = m_Table != null ? new Pose(m_Table.position, m_Table.rotation) : Pose.identity;
        var job = new Job
        {
            left = left,
            right = right,
            captureClock = pair.captureClock,
            tableFromLeft = ArMarkerMath.Mul(ArMarkerMath.Inverse(table), pair.leftWorld),
        };
        if (Time.unscaledTime >= m_NextSnapshot && m_Snapshots < m_MaxSnapshots)
        {
            job.snapshot = true;
            m_NextSnapshot = Time.unscaledTime + m_SnapshotEvery;
        }
        lock (m_Lock)
            m_Pending = job;
        m_Wake.Set();
    }

    double ClockNow() => m_Camera != null ? m_Camera.ClockNow() : Time.realtimeSinceStartupAsDouble;

    void DrainResults()
    {
        while (true)
        {
            Done done;
            lock (m_Lock)
            {
                if (m_Done.Count == 0)
                    break;
                done = m_Done.Dequeue();
            }
            if (done.error != null)
            {
                Log("artoolkitX: " + done.error);
                SetStatus("artoolkitX: " + done.error);
                continue;
            }
            if (!done.processed)
                continue;

            double age = ClockNow() - done.captureClock;
            m_SumAge += (float)age;
            m_SumProcess += done.processMs;
            if (!done.visible)
            {
                camerasText = "none";
                lastQuality = 0f;
                continue;
            }
            var sighting = done.sighting;
            sighting.age = age;
            camerasText = new ArMarkerSample { cameras = sighting.cameras }.CamerasText();
            lastQuality = sighting.quality;
            if (sighting.cameras == 0)
                continue; // artoolkitX's pose isn't borne out by either image
            m_Found++;
            if (done.disagreed)
                m_Disagreed++;
            m_SumQuality += sighting.quality;
            if (sighting.cameras == 3) m_Both++;
            else if (sighting.cameras == 1) m_OnlyL++;
            else m_OnlyR++;
            sighted?.Invoke(sighting);
        }
    }

    void Report()
    {
        m_NextReport = Time.unscaledTime + 1f;
        int processed = Mathf.Max(1, m_Pairs - m_Dropped - m_Unpaired - m_NoHead - m_NotReady);
        var head = Camera.main != null ? Camera.main.transform : null;
        Log($"1 s: pairs {m_Pairs} (artoolkitX not ready {m_NotReady}, busy-dropped {m_Dropped}, unsynced {m_Unpaired}, no head pose {m_NoHead}), found {m_Found} " +
            $"(L+R {m_Both}, L only {m_OnlyL}, R only {m_OnlyR}; both found but disagreed {m_Disagreed}), quality {(m_Found > 0 ? m_SumQuality / m_Found : 0f):0.00}, " +
            $"image age {m_SumAge / processed * 1000f:0} ms, artoolkitX {m_SumProcess / processed:0.0} ms" +
            (head != null ? $", head {head.position:F2} yaw {head.eulerAngles.y:0}" : ""));
        if (m_SolverState == SolverStarting)
            status = "Starting artoolkitX...";
        else if (m_Found > 0 || m_Pairs > 0)
            status = $"Tracking: {m_Pairs} pairs/s, marker in {m_Found} (L+R {m_Both}, L {m_OnlyL}, R {m_OnlyR}), images {m_SumAge / processed * 1000f:0} ms old.";
        m_Pairs = m_Dropped = m_Unpaired = m_Found = m_Both = m_OnlyL = m_OnlyR = m_NoHead = m_NotReady = m_Disagreed = 0;
        m_SumAge = m_SumProcess = m_SumQuality = 0f;
    }

    #endregion

    #region Worker thread

    void StartWorker()
    {
        StopWorker();
        m_Stop = false;
        m_Buffers.Clear();
        m_SolverState = SolverStarting;
        m_SolverError = null;
        var models = (m_LeftModel, m_RightModel, m_RightFromLeft, m_BarcodeId, m_MarkerWidth, m_Threshold, m_Folder);
        string cache = CacheFolder();
        m_Thread = new Thread(() => Work(models, cache)) { IsBackground = true, Name = "AR marker" };
        m_Thread.Start();
    }

    void StopWorker()
    {
        if (m_Thread == null)
            return;
        m_Stop = true;
        m_Wake.Set();
        if (!m_Thread.Join(2000))
            Log("the worker thread didn't stop in time");
        m_Thread = null;
        lock (m_Lock)
        {
            m_Pending = null;
            m_Done.Clear();
            m_Busy = false;
        }
    }

    byte[] Rent()
    {
        lock (m_Lock)
            if (m_Buffers.Count > 0)
                return m_Buffers.Pop();
        return new byte[m_LeftModel.width * m_LeftModel.height];
    }

    void Return(byte[] buffer)
    {
        lock (m_Lock)
            if (buffer != null && m_Buffers.Count < 6)
                m_Buffers.Push(buffer);
    }

    void Work((ArMarkerMath.CameraModel left, ArMarkerMath.CameraModel right, Pose rightFromLeft, int id, float width,
        ArMarkerStereoSolver.Threshold threshold, string folder) setup, string cacheFolder)
    {
        var solver = new ArMarkerStereoSolver();
        ArMarkerStereoSolver.log += Log;
        // Each camera's image, halved (2x2 averaged) unless halveImages=false: four times fewer pixels to search, and
        // the marker is still found from about a metre and a half.
        bool halve = m_HalveImages;
        var left = halve ? setup.left.Halved() : setup.left;
        var right = halve ? setup.right.Halved() : setup.right;
        var smallL = halve ? new byte[left.width * left.height] : null;
        var smallR = halve ? new byte[right.width * right.height] : null;
        var fusion = new ArMarkerFusion();
        string offsetFile = setup.folder != null ? Path.Combine(setup.folder, "stereo-offset.txt") : null;
        if (fusion.Load(offsetFile))
            Log("loaded " + fusion);
        double nextOffsetLog = 0;
        try
        {
            if (!solver.Start(left, right, setup.rightFromLeft, setup.id, setup.width, setup.threshold, cacheFolder))
            {
                Log("artoolkitX: " + solver.error);
                m_SolverError = solver.error;
                m_SolverState = SolverFailed;
                return;
            }
            m_SolverState = SolverRunning;
            Log($"artoolkitX {ArMarkerStereoSolver.Version()} started: each camera's {left.width}x{left.height} image searched on its own, " +
                $"poses joined here (cache {cacheFolder})");
            int snapshots = 0;
            var watch = new System.Diagnostics.Stopwatch();
            while (!m_Stop)
            {
                m_Wake.WaitOne(200);
                Job job;
                lock (m_Lock)
                {
                    job = m_Pending;
                    m_Pending = null;
                    m_Busy = job != null;
                }
                if (job == null)
                    continue;

                var done = new Done { captureClock = job.captureClock };
                try
                {
                    watch.Restart();
                    var imageL = job.left;
                    var imageR = job.right;
                    if (halve)
                    {
                        ArMarkerStereoSolver.Halve(job.left, setup.left.width, setup.left.height, smallL);
                        ArMarkerStereoSolver.Halve(job.right, setup.right.width, setup.right.height, smallR);
                        imageL = smallL;
                        imageR = smallR;
                    }
                    done.processed = solver.ProcessEach(imageL, imageR, out var views);
                    done.processMs = (float)watch.Elapsed.TotalMilliseconds;
                    if (solver.error != null)
                        done.error = solver.error;
                    if (done.processed && (views.left || views.right))
                    {
                        // Each camera checked against its own image with its own pose.
                        done.checkL = views.left ? ArMarkerMath.CheckImage(left, imageL, views.leftInLeft, setup.width) : default;
                        done.checkR = views.right ? ArMarkerMath.CheckImage(right, imageR, views.rightInRight, setup.width) : default;
                        var fused = fusion.Fuse(views.left, views.leftInLeft, done.checkL.edgePixels * Mathf.Max(done.checkL.edgeMatch, 0.1f),
                            views.right, views.rightInRight, done.checkR.edgePixels * Mathf.Max(done.checkR.edgeMatch, 0.1f),
                            setup.rightFromLeft, job.captureClock);
                        done.visible = fused.visible;
                        done.markerInLeft = fused.markerInLeft;
                        done.disagreed = fused.disagreed;
                        done.apart = fused.apart;
                        bool l = (fused.cameras & ArMarkerSample.LeftCamera) != 0, r = (fused.cameras & ArMarkerSample.RightCamera) != 0;
                        float pixels = l && r ? (done.checkL.edgePixels + done.checkR.edgePixels) * 0.5f : l ? done.checkL.edgePixels : done.checkR.edgePixels;
                        float facing = Mathf.Max(l ? done.checkL.facing : 0f, r ? done.checkR.facing : 0f);
                        float match = l && r ? (done.checkL.edgeMatch + done.checkR.edgeMatch) * 0.5f : l ? done.checkL.edgeMatch : done.checkR.edgeMatch;
                        float confidence = Mathf.Max(views.left ? views.confidenceLeft : 0f, views.right ? views.confidenceRight : 0f);
                        done.sighting = new Sighting
                        {
                            cubeInTable = ArMarkerMath.Mul(job.tableFromLeft, ArMarkerMath.CubeFromMarker(fused.markerInLeft)),
                            quality = ArMarkerMath.Quality(confidence, l, r, pixels, facing, match),
                            cameras = fused.cameras,
                            confidence = confidence,
                        };
                    }
                    if (job.captureClock >= nextOffsetLog && fusion.learnt > 0)
                    {
                        nextOffsetLog = job.captureClock + 5.0;
                        Log(fusion.ToString());
                    }
                    if (job.snapshot && setup.folder != null)
                    {
                        Snapshot(setup, job, done, ++snapshots);
                    }
                }
                catch (Exception e)
                {
                    done.error = e.GetType().Name + ": " + e.Message;
                }
                Return(job.left);
                Return(job.right);
                lock (m_Lock)
                {
                    m_Done.Enqueue(done);
                    m_Busy = false;
                }
            }
        }
        finally
        {
            fusion.Save(offsetFile);
            ArMarkerStereoSolver.log -= Log;
            solver.Dispose();
        }
    }

    // Both images side by side at half size, with the marker's outline (green) and the cube's top (yellow) drawn
    // where the pose puts them; no marker found draws nothing.
    void Snapshot((ArMarkerMath.CameraModel left, ArMarkerMath.CameraModel right, Pose rightFromLeft, int id, float width,
        ArMarkerStereoSolver.Threshold threshold, string folder) setup, Job job, Done done, int index)
    {
        int w = setup.left.width / 2, h = setup.left.height / 2;
        int stride = w * 2 * 3;
        var rgb = new byte[stride * h];
        for (int side = 0; side < 2; side++)
        {
            var source = side == 0 ? job.left : job.right;
            int full = setup.left.width;
            for (int y = 0; y < h; y++)
            {
                int row = (h - 1 - y) * stride; // PNG rows from the bottom in Unity's encoder
                for (int x = 0; x < w; x++)
                {
                    byte v = source[(y * 2) * full + x * 2];
                    int at = row + (side * w + x) * 3;
                    rgb[at] = rgb[at + 1] = rgb[at + 2] = v;
                }
            }
        }
        if (done.visible)
        {
            float half = setup.width * 0.5f;
            Vector3[] square = { new(-half, half, 0), new(half, half, 0), new(half, -half, 0), new(-half, -half, 0) };
            for (int side = 0; side < 2; side++)
            {
                var model = side == 0 ? setup.left : setup.right;
                var marker = side == 0 ? done.markerInLeft : ArMarkerMath.Mul(setup.rightFromLeft, done.markerInLeft);
                for (int i = 0; i < 4; i++)
                {
                    var a = ArMarkerMath.Apply(marker, square[i]);
                    var b = ArMarkerMath.Apply(marker, square[(i + 1) % 4]);
                    DrawLine(rgb, w, h, side, model, a, b, 0, 255, 0);
                    var up = marker.rotation * Vector3.back * setup.width;
                    DrawLine(rgb, w, h, side, model, a + up, b + up, 255, 220, 0);
                    DrawLine(rgb, w, h, side, model, a, a + up, 255, 220, 0);
                }
            }
        }
        try
        {
            var png = ImageConversion.EncodeArrayToPNG(rgb, GraphicsFormat.R8G8B8_SRGB, (uint)(w * 2), (uint)h);
            string name = $"snapshot-{index:00}-{DateTime.Now:HHmmss}-{(done.visible ? $"L{(done.checkL.seen ? 1 : 0)}R{(done.checkR.seen ? 1 : 0)}" : "none")}.png";
            File.WriteAllBytes(Path.Combine(setup.folder, name), png);
            Log($"snapshot {name}: found {done.visible}, left edge match {done.checkL.edgeMatch:0.00} ({done.checkL.edgePixels:0} px), " +
                $"right {done.checkR.edgeMatch:0.00} ({done.checkR.edgePixels:0} px), marker in left camera {done.markerInLeft.position * 1000f} mm");
            m_Snapshots = index;
        }
        catch (Exception e)
        {
            Log("snapshot failed: " + e.Message);
        }
    }

    static void DrawLine(byte[] rgb, int w, int h, int side, ArMarkerMath.CameraModel model, Vector3 a, Vector3 b, byte r, byte g, byte bl)
    {
        if (!ArMarkerMath.Project(model, a, out var pa) || !ArMarkerMath.Project(model, b, out var pb))
            return;
        pa *= 0.5f;
        pb *= 0.5f;
        int steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(pa, pb)), 1, 4000);
        for (int i = 0; i <= steps; i++)
        {
            var p = Vector2.Lerp(pa, pb, i / (float)steps);
            int x = Mathf.RoundToInt(p.x), y = Mathf.RoundToInt(p.y);
            if (x < 0 || y < 0 || x >= w || y >= h)
                continue;
            int at = (h - 1 - y) * w * 2 * 3 + (side * w + x) * 3;
            rgb[at] = r;
            rgb[at + 1] = g;
            rgb[at + 2] = bl;
        }
    }

    #endregion


    #region Log, status and settings

    void SetStatus(string text) => status = text;

    // A folder artoolkitX may write its camera-calibration cache to (its default on Android is the read-only working
    // directory, which stops its video input from opening).
    static string CacheFolder()
    {
        try
        {
            string folder = Path.Combine(Application.temporaryCachePath, "artoolkitX");
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ARMarker] no cache folder for artoolkitX: " + e.Message);
            return null;
        }
    }


    void OpenLog()
    {
        try
        {
            m_Folder = Path.Combine(Application.persistentDataPath, "ARMarker");
            Directory.CreateDirectory(m_Folder);
            lock (s_LogLock)
            {
                m_Log?.Dispose();
                m_Log = new StreamWriter(Path.Combine(m_Folder, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), false) { AutoFlush = true };
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ARMarker] can't write the log file: " + e.Message);
            m_Folder = null;
        }
    }

    void CloseLog()
    {
        lock (s_LogLock)
        {
            m_Log?.Dispose();
            m_Log = null;
        }
    }

    /// <summary>A line in the headset log (tagged [ARMarker]) and in files/ARMarker/log-*.txt. Any thread.</summary>
    public void Log(string line)
    {
        Debug.Log("[ARMarker] " + line);
        lock (s_LogLock)
        {
            try
            {
                m_Log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
            catch (Exception)
            {
                // the log is best effort
            }
        }
    }

    // key=value lines in files/ARMarker/settings.txt: axisTurn (Meta180X, None, Turn180Z), headPoseLead (seconds),
    // threshold (Manual, Median, Otsu, Adaptive, Bracketing), swapEyes, barcodeId, markerWidth (metres), snapshotEvery.
    // The AR canvas's keys (canvasCorner, canvasOffsetX/Y, canvasWidth/Height, canvasMarkerRotation, canvasPaintInset,
    // canvasTouchAllowance) are read by ArMarkerCanvasLayout on the host.
    void LoadSettings()
    {
        if (m_Folder == null)
            return;
        string path = Path.Combine(m_Folder, "settings.txt");
        if (!File.Exists(path))
            return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            int equals = line.IndexOf('=');
            if (line.StartsWith("#") || equals < 0)
                continue;
            string key = line[..equals].Trim(), value = line[(equals + 1)..].Trim();
            var culture = CultureInfo.InvariantCulture;
            switch (key)
            {
                case "axisTurn" when Enum.TryParse(value, true, out AxisTurn turn): m_AxisTurn = turn; break;
                case "headPoseLead" when float.TryParse(value, NumberStyles.Float, culture, out float lead): m_HeadPoseLead = lead; break;
                case "threshold" when Enum.TryParse(value, true, out ArMarkerStereoSolver.Threshold t): m_Threshold = t; break;
                case "swapEyes" when bool.TryParse(value, out bool swap): m_SwapEyes = swap; break;
                case "halveImages" when bool.TryParse(value, out bool halveImages): m_HalveImages = halveImages; break;
                case "barcodeId" when int.TryParse(value, out int id): m_BarcodeId = id; break;
                case "markerWidth" when float.TryParse(value, NumberStyles.Float, culture, out float width): m_MarkerWidth = width; break;
                case "snapshotEvery" when float.TryParse(value, NumberStyles.Float, culture, out float every): m_SnapshotEvery = every; break;
                case var canvas when canvas.StartsWith("canvas"): break; // read by ArMarkerCanvasLayout (the host's apply)
                default: Log($"settings.txt: ignored '{line}'"); continue;
            }
            Log($"settings.txt: {key} = {value}");
        }
    }

    #endregion
}
