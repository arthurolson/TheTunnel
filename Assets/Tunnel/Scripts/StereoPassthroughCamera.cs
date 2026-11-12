using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;

/// <summary>
/// Both of the headset's passthrough cameras at once, for whoever needs them (the AR marker tracker, the floor QR
/// alignment), one user at a time: Unity OpenXR Meta's camera subsystem hands over synchronized left/right image pairs
/// (Meta's Passthrough Camera API underneath). For each pair it gives each camera's 8-bit luminance (rows from the
/// top), each camera's lens (in pixels, principal point from the top left) and each camera's pose in the world at the
/// moment the images were taken: the head pose then (from a short history of the poses rendered) and the lens's pose
/// on the head from Android's camera characteristics, turned into Unity's axes as Meta's samples do. These are the
/// conventions checked with the AR marker on the headset.
/// Camera images are switched on only while someone has the cameras open, and off again after (passthrough blinks).
/// </summary>
public class StereoPassthroughCamera : MonoBehaviour
{
    public enum AxisTurn { Meta180X, None, Turn180Z }

    /// <summary>One pair of images: when they were taken (on the camera clock, seconds) and where the cameras were.</summary>
    public struct Pair
    {
        public double captureClock;
        public Pose headWorld, leftWorld, rightWorld;
    }

    /// <summary>What a call to <see cref="TryAcquire"/> got.</summary>
    public enum Result { Nothing, New, Unsynced, ClockUnknown, NoHeadPose, CopyFailed }

    static StereoPassthroughCamera s_Instance;

    public static StereoPassthroughCamera Get()
    {
        if (s_Instance == null)
        {
            s_Instance = new GameObject("Stereo Passthrough Camera").AddComponent<StereoPassthroughCamera>();
            DontDestroyOnLoad(s_Instance.gameObject);
        }
        return s_Instance;
    }

    /// <summary>Settings applied when the cameras are opened.</summary>
    public AxisTurn axisTurn = AxisTurn.Meta180X;
    public float headPoseLead = 0.03f;
    public bool swapEyes;

    /// <summary>Who has the cameras open (null when nobody has).</summary>
    public string owner { get; private set; }
    public bool isOpen => owner != null && m_Subsystem != null && m_LeftModel.width > 0;
    public string error { get; private set; }

    public ArMarkerMath.CameraModel leftModel => m_LeftModel;
    public ArMarkerMath.CameraModel rightModel => m_RightModel;
    /// <summary>Each camera's Unity frame on the head (x right, y up, z forward).</summary>
    public Pose headFromLeft => CameraOnHead(m_HeadFromLeft);
    public Pose headFromRight => CameraOnHead(m_HeadFromRight);
    public Pose rightFromLeft => m_RightFromLeft;

    ARCameraManager m_CameraManager;
    MetaOpenXRCameraSubsystem m_Subsystem;
    bool m_ImageSupportWasOn = true;
    ArMarkerMath.CameraModel m_LeftModel, m_RightModel;
    Pose m_HeadFromLeft, m_HeadFromRight, m_RightFromLeft;
    double m_LastPairTime = -1;
    int m_ClockChoice = -1; // 0 monotonic, 1 boot time, 2 neither (images taken as k_AssumedAge old)
    int m_ClockVotesMono, m_ClockVotesBoot, m_ClockChecks, m_Pairs;
    const double k_AssumedAge = 0.045;
    Action<string> m_Log;

    struct HeadSample { public double mono, boot; public Pose pose; }
    readonly List<HeadSample> m_Heads = new();
    Transform m_TrackingSpace;

    void OnDestroy()
    {
        if (owner != null)
            Close(owner);
        if (s_Instance == this)
            s_Instance = null;
    }

    #region Opening and closing

    /// <summary>
    /// Opens both cameras for <paramref name="who"/> (asking for camera permission first if needed). Afterwards
    /// <see cref="isOpen"/> says whether it worked and <see cref="error"/> why not.
    /// </summary>
    public IEnumerator Open(string who, Action<string> status, Action<string> log)
    {
        error = null;
        if (owner != null && owner != who)
        {
            error = $"The cameras are in use by the {owner}.";
            yield break;
        }
        owner = who;
        m_Log = log;
        m_LeftModel = default;
        m_ClockChoice = -1;
        m_ClockVotesMono = m_ClockVotesBoot = m_ClockChecks = m_Pairs = 0;
        m_LastPairTime = -1;

        if (Application.platform != RuntimePlatform.Android)
        {
            Fail("This needs the headset's cameras; in the Editor the cube shows the shared pose only.");
            yield break;
        }

        bool justGranted = false;
        if (!Permission.HasUserAuthorizedPermission(PassthroughCameraReader.HeadsetCameraPermission))
        {
            status?.Invoke("Asking for permission to use the headset cameras...");
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
                Fail("Camera permission was not given (Settings > Apps > TheTunnel > Permissions).");
                yield break;
            }
            justGranted = true;
        }

        m_CameraManager = FindAnyObjectByType<ARCameraManager>();
        if (m_CameraManager == null)
        {
            Fail("No AR camera manager in the scene.");
            yield break;
        }

        var feature = OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<ARCameraFeature>() : null;
        if (feature == null)
        {
            Fail("The Meta Quest camera feature isn't enabled.");
            yield break;
        }
        m_ImageSupportWasOn = feature.cameraImageSupportEnabled;
        // The camera subsystem checks the setting and the permission when it starts, so it restarts after either changes.
        if (!feature.cameraImageSupportEnabled || justGranted)
        {
            status?.Invoke("Turning on the camera images (passthrough blinks)...");
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
            Fail("The Meta camera subsystem isn't running.");
            yield break;
        }

        var available = m_Subsystem.GetAvailableCameras();
        bool stereo = m_Subsystem.SupportsStereoImageCapture();
        Log($"cameras available {available}, stereo capture {stereo}");
        if (!stereo || available != MetaOpenXRCameraSubsystem.AvailableCameras.Both)
        {
            Fail($"This headset gives only {available} camera images (stereo {stereo}); both are needed.");
            yield break;
        }
        MetaOpenXRCameraSubsystem.SetMaxStereoSyncAttempts(5);

        if (!ReadLens(0, out m_HeadFromLeft, out var leftLens, out string leftInfo) ||
            !ReadLens(1, out m_HeadFromRight, out var rightLens, out string rightInfo))
        {
            Fail("Couldn't read the passthrough cameras' lenses.");
            yield break;
        }
        if (swapEyes)
        {
            (m_HeadFromLeft, m_HeadFromRight) = (m_HeadFromRight, m_HeadFromLeft);
            (leftLens, rightLens) = (rightLens, leftLens);
        }
        Log($"left lens (Camera2): {leftInfo}");
        Log($"right lens (Camera2): {rightInfo}");
        if (m_HeadFromLeft.position.x > m_HeadFromRight.position.x)
            Log("WARNING: the 'left' lens sits to the right of the 'right' one; try swapEyes=true in settings.txt");

        status?.Invoke("Waiting for the camera images...");
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
                    Fail($"The two cameras' images differ in size ({width}x{height}, {r.width}x{r.height}).");
                    yield break;
                }
            }
            yield return null;
        }
        if (width == 0)
        {
            Fail("No camera images arrived (is another app using the camera?).");
            yield break;
        }

        var leftCamera = Intrinsics(MetaOpenXRCameraSubsystem.CameraPosition.LeftEye, leftLens, width, height);
        var rightCamera = Intrinsics(MetaOpenXRCameraSubsystem.CameraPosition.RightEye, rightLens, width, height);
        if (swapEyes)
            (leftCamera, rightCamera) = (rightCamera, leftCamera);
        m_RightFromLeft = ArMarkerMath.RightFromLeft(CameraOnHead(m_HeadFromLeft), CameraOnHead(m_HeadFromRight));
        Log($"left model {leftCamera}, right model {rightCamera}");
        Log($"right camera from left: {m_RightFromLeft.position * 1000f} mm, {m_RightFromLeft.rotation.eulerAngles} deg (baseline {m_RightFromLeft.position.magnitude * 1000f:0.0} mm)");

        var main = Camera.main;
        m_TrackingSpace = main != null ? main.transform.parent : null;
        m_Heads.Clear();
        Application.onBeforeRender -= RecordHead;
        Application.onBeforeRender += RecordHead;
        m_RightModel = rightCamera;
        m_LeftModel = leftCamera; // last: isOpen
    }

    void Fail(string message)
    {
        error = message;
        Close(owner);
    }

    /// <summary>Gives the cameras back (camera images off again, so other users of the camera can have it).</summary>
    public void Close(string who)
    {
        if (owner == null || who != owner)
            return;
        Application.onBeforeRender -= RecordHead;
        owner = null;
        m_LeftModel = default;
        if (Application.platform != RuntimePlatform.Android || m_CameraManager == null || m_ImageSupportWasOn)
            return;
        var feature = OpenXRSettings.Instance != null ? OpenXRSettings.Instance.GetFeature<ARCameraFeature>() : null;
        if (feature == null || !feature.cameraImageSupportEnabled)
            return;
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

    Pose CameraOnHead(Pose headFromLens) => axisTurn switch
    {
        AxisTurn.Meta180X => new Pose(headFromLens.position, headFromLens.rotation * Quaternion.Euler(180f, 0f, 0f)),
        AxisTurn.Turn180Z => new Pose(headFromLens.position, headFromLens.rotation * Quaternion.Euler(0f, 0f, 180f)),
        _ => headFromLens,
    };

    void Log(string line) => m_Log?.Invoke(line);

    #endregion

    #region Pairs

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
        m_Heads.Add(new HeadSample { mono = Clock.Monotonic() + headPoseLead, boot = Clock.Boot() + headPoseLead, pose = head });
        if (m_Heads.Count > 240)
            m_Heads.RemoveRange(0, m_Heads.Count - 240);
    }

    /// <summary>
    /// The latest pair, if it is new: where the cameras were, and (when <paramref name="copy"/>) each camera's
    /// luminance copied into <paramref name="left"/> and <paramref name="right"/> (width x height bytes each).
    /// </summary>
    public Result TryAcquire(bool copy, byte[] left, byte[] right, out Pair pair)
    {
        pair = default;
        if (!isOpen || !m_Subsystem.TryAcquireLatestStereoCpuImagePair(out var images))
            return Result.Nothing;
        using var imageL = new XRCpuImage(m_Subsystem.cpuImageApi, swapEyes ? images.rightEyeImageCinfo : images.leftEyeImageCinfo);
        using var imageR = new XRCpuImage(m_Subsystem.cpuImageApi, swapEyes ? images.leftEyeImageCinfo : images.rightEyeImageCinfo);
        if (imageL.timestamp == m_LastPairTime)
            return Result.Nothing;
        m_LastPairTime = imageL.timestamp;
        m_Pairs++;
        double leftTime = Seconds(imageL.timestamp), rightTime = Seconds(imageR.timestamp);
        if (Math.Abs(leftTime - rightTime) > 0.004)
            return Result.Unsynced;

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
                Log($"clock check: image t={leftTime:F4} s (raw {imageL.timestamp:F0}), monotonic {mono:F4} (age {ageMono * 1000:0} ms), boot {boot:F4} (age {ageBoot * 1000:0} ms)");
            }
            if (m_ClockChoice < 0)
                return Result.ClockUnknown;
        }

        double capture = m_ClockChoice == 2 ? Clock.Monotonic() - k_AssumedAge : leftTime;
        pair.captureClock = capture;
        if (!copy)
            return Result.New;
        if (!HeadAt(capture, out var headTracking))
            return Result.NoHeadPose;
        var trackingSpace = m_TrackingSpace != null ? new Pose(m_TrackingSpace.position, m_TrackingSpace.rotation) : Pose.identity;
        pair.headWorld = ArMarkerMath.Mul(trackingSpace, headTracking);
        pair.leftWorld = ArMarkerMath.Mul(pair.headWorld, headFromLeft);
        pair.rightWorld = ArMarkerMath.Mul(pair.headWorld, headFromRight);
        if (!CopyLuminance(imageL, left, m_LeftModel) || !CopyLuminance(imageR, right, m_RightModel))
            return Result.CopyFailed;
        return Result.New;
    }

    static bool CopyLuminance(XRCpuImage image, byte[] into, ArMarkerMath.CameraModel model)
    {
        if (into == null || image.planeCount < 1 || image.width != model.width || image.height != model.height ||
            into.Length < model.width * model.height)
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
        if (time > Stamp(m_Heads[^1]))
        {
            pose = m_Heads[^1].pose;
            return true;
        }
        return false;
    }

    double Stamp(HeadSample sample) => m_ClockChoice == 1 ? sample.boot : sample.mono;

    /// <summary>Now, on the camera images' clock (seconds).</summary>
    public double ClockNow() => m_ClockChoice == 1 ? Clock.Boot() : Clock.Monotonic();

    /// <summary>Image timestamps arrive in nanoseconds (the Android camera's clock); older code paths may give seconds.</summary>
    public static double Seconds(double timestamp) => timestamp > 1e7 ? timestamp * 1e-9 : timestamp;

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
            if (Application.platform != RuntimePlatform.Android)
            {
                s_Failed = true;
                return false;
            }
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
}
