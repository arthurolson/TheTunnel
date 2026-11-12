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
    public enum AxisTurn { Meta180X, None, Turn180Z }

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

    // Camera access
    ARCameraManager m_CameraManager;
    MetaOpenXRCameraSubsystem m_Subsystem;
    bool m_ImageSupportWasOn = true;
    ArMarkerMath.CameraModel m_LeftModel, m_RightModel;
    Pose m_HeadFromLeft, m_HeadFromRight, m_RightFromLeft;
    double m_LastPairTime = -1;
    int m_ClockChoice = -1; // 0 monotonic, 1 boot time, 2 neither (images taken as k_AssumedAge old)
    int m_ClockVotesMono, m_ClockVotesBoot, m_ClockChecks;
    const double k_AssumedAge = 0.045;

    // Head poses before rendering, in tracking space, with when they apply.
    struct HeadSample { public double mono, boot; public Pose pose; }
    readonly List<HeadSample> m_Heads = new();
    Transform m_TrackingSpace;
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
    int m_Pairs, m_Dropped, m_Unpaired, m_Found, m_Both, m_OnlyL, m_OnlyR, m_NoHead, m_NotReady;
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
        Application.onBeforeRender -= RecordHead;
        StopWorker();
        RestoreCamera();
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
        m_ClockChoice = -1;
        m_ClockVotesMono = m_ClockVotesBoot = m_ClockChecks = 0;
        m_LastPairTime = -1;
        m_Snapshots = 0;
        Log($"starting: artoolkitX {ArMarkerStereoSolver.Version()}, barcode 3x3 Hamming(6,3) id {m_BarcodeId}, " +
            $"{m_MarkerWidth * 1000f:0} mm, axis turn {m_AxisTurn}, head pose lead {m_HeadPoseLead * 1000f:0} ms, threshold {m_Threshold}, swap eyes {m_SwapEyes}");

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

        if (Application.platform != RuntimePlatform.Android)
        {
            Stop("This needs the headset's cameras; in the Editor the cube shows the shared pose only.");
            yield break;
        }

        // Camera permission (Horizon OS's headset camera permission).
        bool justGranted = false;
        if (!Permission.HasUserAuthorizedPermission(PassthroughCameraReader.HeadsetCameraPermission))
        {
            SetStatus("Asking for permission to use the headset cameras...");
            bool? answered = null;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => answered = true;
            callbacks.PermissionDenied += _ => answered = false;
            Permission.RequestUserPermission(PassthroughCameraReader.HeadsetCameraPermission, callbacks);
            float timeout = Time.unscaledTime + 60f;
            while (answered == null && Time.unscaledTime < timeout)
                yield return null;
            if (!Permission.HasUserAuthorizedPermission(PassthroughCameraReader.HeadsetCameraPermission))
            {
                Stop("Camera permission was not given (Settings > Apps > TheTunnel > Permissions).");
                yield break;
            }
            justGranted = true;
        }

        m_CameraManager = FindAnyObjectByType<ARCameraManager>();
        if (m_CameraManager == null)
        {
            Stop("No AR camera manager in the scene.");
            yield break;
        }

        // Camera images are turned on only while this runs (the QR alignment uses the camera its own way).
        var feature = OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<ARCameraFeature>() : null;
        if (feature == null)
        {
            Stop("The Meta Quest camera feature isn't enabled.");
            yield break;
        }
        m_ImageSupportWasOn = feature.cameraImageSupportEnabled;
        // The camera subsystem checks the setting and the permission when it starts, so it restarts after either changes.
        if (!feature.cameraImageSupportEnabled || justGranted)
        {
            SetStatus("Turning on the camera images (passthrough blinks)...");
            m_CameraManager.enabled = false;
            yield return null;
            feature.cameraImageSupportEnabled = true;
            m_CameraManager.enabled = true;
            yield return null;
            Log("camera images turned on (camera subsystem restarted)");
        }

        m_Subsystem = m_CameraManager.subsystem as MetaOpenXRCameraSubsystem;
        float wait = Time.unscaledTime + 5f;
        while ((m_Subsystem == null || !m_Subsystem.running) && Time.unscaledTime < wait)
        {
            yield return null;
            m_Subsystem = m_CameraManager.subsystem as MetaOpenXRCameraSubsystem;
        }
        if (m_Subsystem == null)
        {
            Stop("The Meta camera subsystem isn't running.");
            yield break;
        }

        var available = m_Subsystem.GetAvailableCameras();
        bool stereo = m_Subsystem.SupportsStereoImageCapture();
        Log($"cameras available {available}, stereo capture {stereo}");
        if (!stereo || available != MetaOpenXRCameraSubsystem.AvailableCameras.Both)
        {
            Stop($"This headset gives only {available} camera images (stereo {stereo}); both are needed.");
            yield break;
        }
        MetaOpenXRCameraSubsystem.SetMaxStereoSyncAttempts(5);

        // Lenses: Android's camera characteristics give each lens's pose on the head and its calibration.
        if (!ReadLens(0, out m_HeadFromLeft, out var leftLens, out string leftInfo) ||
            !ReadLens(1, out m_HeadFromRight, out var rightLens, out string rightInfo))
        {
            Stop("Couldn't read the passthrough cameras' lenses.");
            yield break;
        }
        if (m_SwapEyes)
        {
            (m_HeadFromLeft, m_HeadFromRight) = (m_HeadFromRight, m_HeadFromLeft);
            (leftLens, rightLens) = (rightLens, leftLens);
        }
        Log($"left lens (Camera2): {leftInfo}");
        Log($"right lens (Camera2): {rightInfo}");
        if (m_HeadFromLeft.position.x > m_HeadFromRight.position.x)
            Log("WARNING: the 'left' lens sits to the right of the 'right' one; try swapEyes=true in settings.txt");

        // Wait for the first pair to learn the image size.
        SetStatus("Waiting for the camera images...");
        int width = 0, height = 0;
        wait = Time.unscaledTime + 8f;
        while (width == 0 && Time.unscaledTime < wait)
        {
            if (m_Subsystem.TryAcquireLatestStereoCpuImagePair(out var pair))
            {
                using var l = new XRCpuImage(m_Subsystem.cpuImageApi, pair.leftEyeImageCinfo);
                using var r = new XRCpuImage(m_Subsystem.cpuImageApi, pair.rightEyeImageCinfo);
                width = l.width;
                height = l.height;
                Log($"first pair: left {l.width}x{l.height} {l.format} t={l.timestamp:F4}, right {r.width}x{r.height} {r.format} t={r.timestamp:F4}, planes {l.planeCount}");
                if (r.width != width || r.height != height)
                {
                    Stop($"The two cameras' images differ in size ({width}x{height}, {r.width}x{r.height}).");
                    yield break;
                }
            }
            yield return null;
        }
        if (width == 0)
        {
            Stop("No camera images arrived (is another app or the QR alignment using the camera?).");
            yield break;
        }

        // Intrinsics: the subsystem's for each eye when they fit the images, else Android's scaled to them.
        m_LeftModel = Intrinsics(MetaOpenXRCameraSubsystem.CameraPosition.LeftEye, leftLens, width, height);
        m_RightModel = Intrinsics(MetaOpenXRCameraSubsystem.CameraPosition.RightEye, rightLens, width, height);
        if (m_SwapEyes)
            (m_LeftModel, m_RightModel) = (m_RightModel, m_LeftModel);
        m_RightFromLeft = ArMarkerMath.RightFromLeft(CameraOnHead(m_HeadFromLeft), CameraOnHead(m_HeadFromRight));
        Log($"left model {m_LeftModel}, right model {m_RightModel}");
        Log($"right camera from left: {m_RightFromLeft.position * 1000f} mm, {m_RightFromLeft.rotation.eulerAngles} deg (baseline {m_RightFromLeft.position.magnitude * 1000f:0.0} mm)");

        var main = Camera.main;
        m_TrackingSpace = main != null ? main.transform.parent : null;
        var table = FindAnyObjectByType<TableTop>();
        m_Table = table != null ? table.transform : null;
        Log(m_Table != null
            ? $"shared frame: TableTop '{m_Table.name}' at {m_Table.position:F3}, yaw {m_Table.eulerAngles.y:0.0}"
            : "shared frame: no TableTop in the scene, using world coordinates");

        m_Heads.Clear();
        Application.onBeforeRender += RecordHead;
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
        RestoreCamera();
    }

    void RestoreCamera()
    {
        if (Application.platform != RuntimePlatform.Android || m_CameraManager == null || m_ImageSupportWasOn)
            return;
        var feature = OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<ARCameraFeature>() : null;
        if (feature == null || !feature.cameraImageSupportEnabled)
            return;
        // Give the cameras back so the QR alignment can use them.
        m_CameraManager.enabled = false;
        feature.cameraImageSupportEnabled = false;
        m_CameraManager.enabled = true;
        m_ImageSupportWasOn = true;
        Log("camera images turned off (camera subsystem restarted)");
    }

    ArMarkerMath.CameraModel Intrinsics(MetaOpenXRCameraSubsystem.CameraPosition eye, ArMarkerMath.CameraModel android, int width, int height)
    {
        if (m_Subsystem.TryGetIntrinsicsForPosition(eye, out XRCameraIntrinsics intrinsics))
        {
            var model = new ArMarkerMath.CameraModel(intrinsics.resolution.x, intrinsics.resolution.y,
                intrinsics.focalLength.x, intrinsics.focalLength.y, intrinsics.principalPoint.x, intrinsics.principalPoint.y);
            Log($"{eye} intrinsics (subsystem): {model}; Android's: {android}");
            if (model.width > 0 && model.fx > 1f)
                return model.ForImage(width, height);
        }
        else
        {
            Log($"{eye} intrinsics: the subsystem gave none; using Android's {android}");
        }
        return android.ForImage(width, height);
    }

    Pose CameraOnHead(Pose headFromLens) => m_AxisTurn switch
    {
        AxisTurn.Meta180X => new Pose(headFromLens.position, headFromLens.rotation * Quaternion.Euler(180f, 0f, 0f)),
        AxisTurn.Turn180Z => new Pose(headFromLens.position, headFromLens.rotation * Quaternion.Euler(0f, 0f, 180f)),
        _ => headFromLens,
    };

    #endregion

    #region Every frame

    void RecordHead()
    {
        var main = Camera.main;
        if (main == null)
            return;
        if (m_TrackingSpace == null)
            m_TrackingSpace = main.transform.parent;
        var head = new Pose(main.transform.localPosition, main.transform.localRotation);
        if (m_TrackingSpace != null && main.transform.parent != m_TrackingSpace)
            head = ArMarkerMath.Mul(ArMarkerMath.Inverse(new Pose(m_TrackingSpace.position, m_TrackingSpace.rotation)),
                new Pose(main.transform.position, main.transform.rotation));
        m_Heads.Add(new HeadSample { mono = Clock.Monotonic() + m_HeadPoseLead, boot = Clock.Boot() + m_HeadPoseLead, pose = head });
        if (m_Heads.Count > 240)
            m_Heads.RemoveRange(0, m_Heads.Count - 240);
    }

    void Update()
    {
        if (!active || m_Subsystem == null || m_Thread == null)
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
        if (!m_Subsystem.TryAcquireLatestStereoCpuImagePair(out var pair))
            return;
        using var left = new XRCpuImage(m_Subsystem.cpuImageApi, m_SwapEyes ? pair.rightEyeImageCinfo : pair.leftEyeImageCinfo);
        using var right = new XRCpuImage(m_Subsystem.cpuImageApi, m_SwapEyes ? pair.leftEyeImageCinfo : pair.rightEyeImageCinfo);
        if (left.timestamp == m_LastPairTime)
            return;
        m_LastPairTime = left.timestamp;
        m_Pairs++;
        double leftTime = Seconds(left.timestamp), rightTime = Seconds(right.timestamp);
        if (Math.Abs(leftTime - rightTime) > 0.004)
        {
            m_Unpaired++;
            return;
        }

        // The images' clock: whichever of the two system clocks puts them a little in the past.
        double mono = Clock.Monotonic(), boot = Clock.Boot();
        double ageMono = mono - leftTime, ageBoot = boot - leftTime;
        if (m_ClockChoice < 0)
        {
            if (ageMono >= 0 && ageMono < 1.0) m_ClockVotesMono++;
            if (ageBoot >= 0 && ageBoot < 1.0) m_ClockVotesBoot++;
            if (++m_ClockChecks >= 30 && m_ClockVotesMono + m_ClockVotesBoot < 10)
            {
                m_ClockChoice = 2;
                Log($"image clock: neither system clock fits (image t={leftTime:F4} s, monotonic {mono:F4}, boot {boot:F4}); " +
                    $"taking images as {k_AssumedAge * 1000:0} ms old");
            }
            else if (m_ClockVotesMono + m_ClockVotesBoot >= 10)
            {
                m_ClockChoice = m_ClockVotesBoot > m_ClockVotesMono ? 1 : 0;
                Log($"image clock: {(m_ClockChoice == 1 ? "boot time" : "monotonic")} (votes mono {m_ClockVotesMono}, boot {m_ClockVotesBoot}; " +
                    $"image t={leftTime:F4} s, monotonic {mono:F4}, boot {boot:F4})");
            }
            else if (m_Pairs <= 3)
            {
                Log($"clock check: image t={leftTime:F4} s (raw {left.timestamp:F0}), monotonic {mono:F4} (age {ageMono * 1000:0} ms), boot {boot:F4} (age {ageBoot * 1000:0} ms)");
            }
            if (m_ClockChoice < 0)
                return;
        }

        if (m_SolverState != SolverRunning)
        {
            m_NotReady++; // artoolkitX is still starting (or failed: Update stops tracking)
            return;
        }
        lock (m_Lock)
        {
            if (m_Busy || m_Pending != null)
            {
                m_Dropped++;
                return;
            }
        }

        double capture = m_ClockChoice == 2 ? Clock.Monotonic() - k_AssumedAge : leftTime;
        if (!HeadAt(capture, out var headTracking))
        {
            m_NoHead++;
            return;
        }
        var trackingSpace = m_TrackingSpace != null ? new Pose(m_TrackingSpace.position, m_TrackingSpace.rotation) : Pose.identity;
        var headWorld = ArMarkerMath.Mul(trackingSpace, headTracking);
        var leftWorld = ArMarkerMath.Mul(headWorld, CameraOnHead(m_HeadFromLeft));
        var table = m_Table != null ? new Pose(m_Table.position, m_Table.rotation) : Pose.identity;

        var job = new Job
        {
            left = Rent(),
            right = Rent(),
            captureClock = capture,
            tableFromLeft = ArMarkerMath.Mul(ArMarkerMath.Inverse(table), leftWorld),
        };
        if (!CopyLuminance(left, job.left) || !CopyLuminance(right, job.right))
        {
            Return(job.left);
            Return(job.right);
            return;
        }
        if (Time.unscaledTime >= m_NextSnapshot && m_Snapshots < m_MaxSnapshots)
        {
            job.snapshot = true;
            m_NextSnapshot = Time.unscaledTime + m_SnapshotEvery;
        }
        lock (m_Lock)
            m_Pending = job;
        m_Wake.Set();
    }

    bool CopyLuminance(XRCpuImage image, byte[] into)
    {
        if (image.planeCount < 1 || image.width != m_LeftModel.width || image.height != m_LeftModel.height)
            return false;
        var plane = image.GetPlane(0);
        int w = image.width, h = image.height;
        if (plane.pixelStride != 1)
            return false;
        if (plane.rowStride == w && plane.data.Length >= w * h)
        {
            NativeArray<byte>.Copy(plane.data, 0, into, 0, w * h);
            return true;
        }
        for (int y = 0; y < h; y++)
            NativeArray<byte>.Copy(plane.data, y * plane.rowStride, into, y * w, w);
        return true;
    }

    // The head pose (tracking space) at a moment on the images' clock, between the poses recorded around it.
    bool HeadAt(double time, out Pose pose)
    {
        pose = default;
        if (m_Heads.Count == 0)
            return false;
        for (int i = m_Heads.Count - 1; i > 0; i--)
        {
            double t1 = Stamp(m_Heads[i]), t0 = Stamp(m_Heads[i - 1]);
            if (t0 <= time && time <= t1)
            {
                float k = t1 > t0 ? (float)((time - t0) / (t1 - t0)) : 0f;
                pose = new Pose(Vector3.Lerp(m_Heads[i - 1].pose.position, m_Heads[i].pose.position, k),
                    Quaternion.Slerp(m_Heads[i - 1].pose.rotation, m_Heads[i].pose.rotation, k));
                return true;
            }
        }
        // Newer than every pose recorded (shouldn't happen with images ~40 ms old): use the latest.
        if (time > Stamp(m_Heads[^1]))
        {
            pose = m_Heads[^1].pose;
            return true;
        }
        return false;
    }

    double Stamp(HeadSample sample) => m_ClockChoice == 1 ? sample.boot : sample.mono;

    double ClockNow() => m_ClockChoice == 1 ? Clock.Boot() : Clock.Monotonic();

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
            $"(L+R {m_Both}, L only {m_OnlyL}, R only {m_OnlyR}), quality {(m_Found > 0 ? m_SumQuality / m_Found : 0f):0.00}, " +
            $"image age {m_SumAge / processed * 1000f:0} ms, artoolkitX {m_SumProcess / processed:0.0} ms" +
            (head != null ? $", head {head.position:F2} yaw {head.eulerAngles.y:0}" : ""));
        if (m_SolverState == SolverStarting)
            status = "Starting artoolkitX...";
        else if (m_Found > 0 || m_Pairs > 0)
            status = $"Tracking: {m_Pairs} pairs/s, marker in {m_Found} (L+R {m_Both}, L {m_OnlyL}, R {m_OnlyR}), images {m_SumAge / processed * 1000f:0} ms old.";
        m_Pairs = m_Dropped = m_Unpaired = m_Found = m_Both = m_OnlyL = m_OnlyR = m_NoHead = m_NotReady = 0;
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
        try
        {
            if (!solver.Start(setup.left, setup.right, setup.rightFromLeft, setup.id, setup.width, setup.threshold, cacheFolder))
            {
                Log("artoolkitX: " + solver.error);
                m_SolverError = solver.error;
                m_SolverState = SolverFailed;
                return;
            }
            m_SolverState = SolverRunning;
            Log($"artoolkitX {ArMarkerStereoSolver.Version()} stereo started (cache {cacheFolder})");
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
                    done.processed = solver.Process(job.left, job.right, out var result);
                    done.processMs = (float)watch.Elapsed.TotalMilliseconds;
                    if (solver.error != null)
                        done.error = solver.error;
                    if (done.processed && result.visible)
                    {
                        done.visible = true;
                        done.markerInLeft = result.markerInLeft;
                        done.checkL = ArMarkerMath.CheckImage(setup.left, job.left, result.markerInLeft, setup.width);
                        var markerInRight = ArMarkerMath.Mul(setup.rightFromLeft, result.markerInLeft);
                        done.checkR = ArMarkerMath.CheckImage(setup.right, job.right, markerInRight, setup.width);
                        byte cameras = (byte)((done.checkL.seen ? ArMarkerSample.LeftCamera : 0) | (done.checkR.seen ? ArMarkerSample.RightCamera : 0));
                        done.sighting = new Sighting
                        {
                            cubeInTable = ArMarkerMath.Mul(job.tableFromLeft, ArMarkerMath.CubeFromMarker(result.markerInLeft)),
                            quality = ArMarkerMath.Quality(result.confidence, done.checkL, done.checkR),
                            cameras = cameras,
                            confidence = result.confidence,
                        };
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

    #region Lenses (Android camera characteristics)

    const string k_SourceKey = "com.meta.extra_metadata.camera_source";
    const string k_PositionKey = "com.meta.extra_metadata.position";

    // A passthrough lens's pose on the head (Android's axes turned into Unity's as Meta's samples do) and its
    // calibration, for Meta's camera position 0 (left) or 1 (right).
    static bool ReadLens(int wantedPosition, out Pose headFromLens, out ArMarkerMath.CameraModel model, out string info)
    {
        headFromLens = Pose.identity;
        model = default;
        info = null;
        try
        {
            using var playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = playerClass.GetStatic<AndroidJavaObject>("currentActivity");
            using var manager = activity.Call<AndroidJavaObject>("getSystemService", "camera");
            var ids = manager.Call<string[]>("getCameraIdList");
            foreach (var id in ids)
            {
                using var characteristics = manager.Call<AndroidJavaObject>("getCameraCharacteristics", id);
                int source = -1, position = -1;
                using (var keys = characteristics.Call<AndroidJavaObject>("getKeys"))
                {
                    int count = keys.Call<int>("size");
                    for (int k = 0; k < count; k++)
                    {
                        using var key = keys.Call<AndroidJavaObject>("get", k);
                        string name = key.Call<string>("getName");
                        if (string.Equals(name, k_SourceKey, StringComparison.OrdinalIgnoreCase))
                            source = FirstByte(characteristics, key);
                        else if (string.Equals(name, k_PositionKey, StringComparison.OrdinalIgnoreCase))
                            position = FirstByte(characteristics, key);
                    }
                }
                if (source != 0 || position != wantedPosition)
                    continue;

                var intrinsics = Get<float[]>(characteristics, "LENS_INTRINSIC_CALIBRATION");
                using var sensor = Get<AndroidJavaObject>(characteristics, "SENSOR_INFO_PRE_CORRECTION_ACTIVE_ARRAY_SIZE");
                var translation = Get<float[]>(characteristics, "LENS_POSE_TRANSLATION");
                var rotation = Get<float[]>(characteristics, "LENS_POSE_ROTATION");
                float[] distortion = null;
                try { distortion = Get<float[]>(characteristics, "LENS_DISTORTION"); } catch (Exception) { }

                model = new ArMarkerMath.CameraModel(sensor.Get<int>("right"), sensor.Get<int>("bottom"),
                    intrinsics[0], intrinsics[1], intrinsics[2], intrinsics[3]);
                var lensFromHead = new Quaternion(-rotation[0], -rotation[1], rotation[2], rotation[3]);
                headFromLens = new Pose(new Vector3(translation[0], translation[1], -translation[2]), Quaternion.Inverse(lensFromHead));
                info = $"camera id {id}, sensor {model}, skew {(intrinsics.Length > 4 ? intrinsics[4] : 0):F2}, " +
                    $"translation ({translation[0]:F4}, {translation[1]:F4}, {translation[2]:F4}) m, " +
                    $"rotation ({rotation[0]:F4}, {rotation[1]:F4}, {rotation[2]:F4}, {rotation[3]:F4}), " +
                    $"distortion [{(distortion != null ? string.Join(", ", Array.ConvertAll(distortion, d => d.ToString("F4", CultureInfo.InvariantCulture))) : "none")}]";
                return true;
            }
        }
        catch (Exception e)
        {
            info = e.Message;
            Debug.LogWarning("[ARMarker] reading the lenses: " + e.Message);
        }
        return false;
    }

    static T Get<T>(AndroidJavaObject characteristics, string keyName)
    {
        using var key = characteristics.GetStatic<AndroidJavaObject>(keyName);
        return characteristics.Call<T>("get", key);
    }

    static int FirstByte(AndroidJavaObject characteristics, AndroidJavaObject key)
    {
        var values = characteristics.Call<sbyte[]>("get", key);
        return values != null && values.Length == 1 ? values[0] : -1;
    }

    /// <summary>The two system clocks camera images may be stamped with (seconds).</summary>
    static class Clock
    {
        static IntPtr s_System, s_NanoTime, s_SystemClock, s_Elapsed;
        static bool s_Ready, s_Failed;

        static bool Ready()
        {
            if (s_Ready || s_Failed)
                return s_Ready;
            try
            {
                s_System = AndroidJNI.NewGlobalRef(AndroidJNI.FindClass("java/lang/System"));
                s_NanoTime = AndroidJNI.GetStaticMethodID(s_System, "nanoTime", "()J");
                s_SystemClock = AndroidJNI.NewGlobalRef(AndroidJNI.FindClass("android/os/SystemClock"));
                s_Elapsed = AndroidJNI.GetStaticMethodID(s_SystemClock, "elapsedRealtimeNanos", "()J");
                s_Ready = s_NanoTime != IntPtr.Zero && s_Elapsed != IntPtr.Zero;
                s_Failed = !s_Ready;
            }
            catch (Exception)
            {
                s_Failed = true;
            }
            return s_Ready;
        }

        static readonly jvalue[] s_NoArgs = Array.Empty<jvalue>();

        /// <summary>CLOCK_MONOTONIC (Java's System.nanoTime).</summary>
        public static double Monotonic() => Ready() ? AndroidJNI.CallStaticLongMethod(s_System, s_NanoTime, s_NoArgs) * 1e-9 : Time.realtimeSinceStartupAsDouble;

        /// <summary>CLOCK_BOOTTIME (SystemClock.elapsedRealtimeNanos).</summary>
        public static double Boot() => Ready() ? AndroidJNI.CallStaticLongMethod(s_SystemClock, s_Elapsed, s_NoArgs) * 1e-9 : Time.realtimeSinceStartupAsDouble;
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

    /// <summary>Image timestamps arrive in nanoseconds (the Android camera's clock); older code paths may give seconds.</summary>
    static double Seconds(double timestamp) => timestamp > 1e7 ? timestamp * 1e-9 : timestamp;

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
