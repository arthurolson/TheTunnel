using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>
/// Finds the printed square marker in a pair of passthrough camera images with artoolkitX (arunityx's native
/// library, used through its C API): both images are pushed into artoolkitX's external video input as two video
/// sources, and its stereo pose solver uses both cameras' lenses and the left-to-right camera transform. The marker is
/// an artoolkitX 3x3 barcode with Hamming (6,3) error correction (see Printables/).
/// artoolkitX keeps one tracking session per app, so only one solver runs at a time. All calls must come from the
/// same thread (the AR marker tracker's worker thread on the headset).
/// </summary>
public sealed class ArMarkerStereoSolver : IDisposable
{
    // artoolkitX options (ARX_c.h, ar.h).
    const int k_OptionThresholdMode = 2, k_OptionPatternDetectionMode = 4, k_OptionMatrixCodeType = 6;
    const int k_MatrixCodeDetection = 2, k_MatrixCode3x3Hamming63 = 0x03 | 0x200;
    const int k_TrackableOptionConfidence = 5;

    /// <summary>Threshold modes: 0 manual, 1 median, 2 Otsu, 3 adaptive, 4 bracketing.</summary>
    public enum Threshold { Manual = 0, Median = 1, Otsu = 2, Adaptive = 3, Bracketing = 4 }

    static ArMarkerStereoSolver s_Running;
    static PluginFunctionsLogCallback s_LogCallback;

    ArMarkerMath.CameraModel m_Left, m_Right;
    int m_Trackable = -1;
    bool m_Started, m_PushInited;
    readonly float[] m_MatrixL = new float[16];
    readonly float[] m_MatrixR = new float[16];

    /// <summary>artoolkitX's own log lines (debug level and up are forwarded, tagged [ARMarker]).</summary>
    public static event Action<string> log;

    public float markerWidthMeters { get; private set; }
    public string error { get; private set; }
    public bool started => m_Started;

    /// <summary>artoolkitX has both video streams and is tracking.</summary>
    public bool running => m_Started && ARX_pinvoke.arwIsRunning();

    /// <summary>
    /// Starts artoolkitX with two external video sources (8-bit luminance images of the given sizes), each camera's
    /// lens and the transform from the left camera to the right one, and the barcode marker to look for.
    /// </summary>
    public bool Start(ArMarkerMath.CameraModel left, ArMarkerMath.CameraModel right, Pose rightFromLeft,
        int barcodeId, float markerWidthMeters, Threshold threshold = Threshold.Otsu)
    {
        error = null;
        if (s_Running != null && s_Running != this)
            s_Running.Dispose();

        try
        {
            if (s_LogCallback == null)
            {
                s_LogCallback = OnNativeLog;
                ARX_pinvoke.arwRegisterLogCallback(s_LogCallback);
                ARX_pinvoke.arwSetLogLevel(1); // info and up
            }
            if (!arwIsInited() && !ARX_pinvoke.arwInitialiseAR())
                return Fail("artoolkitX didn't initialise");

            m_Left = left;
            m_Right = right;
            this.markerWidthMeters = markerWidthMeters;
            var paramL = ArMarkerMath.CameraParamFile(left);
            var paramR = ArMarkerMath.CameraParamFile(right);
            var l2r = ArMarkerMath.TransL2RFile(rightFromLeft);
            const string vconf = "-module=External";
            if (!ARX_pinvoke.arwStartRunningStereoB(vconf, paramL, paramL.Length, vconf, paramR, paramR.Length, l2r, l2r.Length))
                return Fail($"artoolkitX didn't start the stereo video (error {ARX_pinvoke.arwGetError()})");
            m_Started = true;
            s_Running = this;

            if (ARX_pinvoke.arwVideoPushInit(0, left.width, left.height, "MONO", 0, 0) < 0 ||
                ARX_pinvoke.arwVideoPushInit(1, right.width, right.height, "MONO", 1, 0) < 0)
                return Fail("artoolkitX didn't accept the camera images");
            m_PushInited = true;

            ARX_pinvoke.arwSetTrackerOptionInt(k_OptionThresholdMode, (int)threshold);
            ARX_pinvoke.arwSetTrackerOptionInt(k_OptionPatternDetectionMode, k_MatrixCodeDetection);
            ARX_pinvoke.arwSetTrackerOptionInt(k_OptionMatrixCodeType, k_MatrixCode3x3Hamming63);
            m_Trackable = ARX_pinvoke.arwAddTrackable($"single_barcode;{barcodeId};{markerWidthMeters * 1000f:0.###}");
            if (m_Trackable < 0)
                return Fail("artoolkitX didn't accept the marker");
            return true;
        }
        catch (DllNotFoundException e)
        {
            return Fail("artoolkitX's library isn't in this build: " + e.Message);
        }
        catch (EntryPointNotFoundException e)
        {
            return Fail("artoolkitX's library is missing a function: " + e.Message);
        }
    }

    bool Fail(string message)
    {
        error = message;
        Dispose();
        return false;
    }

    /// <summary>The result of one stereo pair.</summary>
    public struct Result
    {
        public bool visible;
        /// <summary>The marker in the left camera's Unity frame (metres; its face is local -z).</summary>
        public Pose markerInLeft;
        /// <summary>artoolkitX's match confidence (0-1).</summary>
        public float confidence;
    }

    /// <summary>
    /// Pushes one pair of images taken at the same moment (8-bit luminance, rows from the top, no padding) and returns
    /// the marker's pose if artoolkitX found it. Returns false while artoolkitX is still starting.
    /// </summary>
    public bool Process(byte[] left, byte[] right, out Result result)
    {
        result = default;
        if (!m_Started || !m_PushInited)
            return false;

        StampDecoy(left, m_Left.width, m_Left.height);
        StampDecoy(right, m_Right.width, m_Right.height);
        var handleL = GCHandle.Alloc(left, GCHandleType.Pinned);
        var handleR = GCHandle.Alloc(right, GCHandleType.Pinned);
        try
        {
            // Copied by artoolkitX (no release callback needed).
            int pushL = ARX_pinvoke.arwVideoPush(0, handleL.AddrOfPinnedObject(), m_Left.width * m_Left.height, 1, m_Left.width,
                IntPtr.Zero, 0, 0, 0, IntPtr.Zero, 0, 0, 0, IntPtr.Zero, 0, 0, 0, null, IntPtr.Zero);
            int pushR = ARX_pinvoke.arwVideoPush(1, handleR.AddrOfPinnedObject(), m_Right.width * m_Right.height, 1, m_Right.width,
                IntPtr.Zero, 0, 0, 0, IntPtr.Zero, 0, 0, 0, IntPtr.Zero, 0, 0, 0, null, IntPtr.Zero);
            if (pushL < 0 || pushR < 0)
            {
                error = "artoolkitX refused a camera image";
                return false;
            }
        }
        finally
        {
            handleL.Free();
            handleR.Free();
        }

        if (!ARX_pinvoke.arwCapture())
            return false;
        if (!ARX_pinvoke.arwUpdateAR())
            return false;

        result.visible = ARX_pinvoke.arwQueryTrackableVisibilityAndTransformationStereo(m_Trackable, m_MatrixL, m_MatrixR);
        if (result.visible)
        {
            result.markerInLeft = ArMarkerMath.MarkerPoseFromGl(m_MatrixL);
            result.confidence = ARX_pinvoke.arwGetTrackableOptionFloat(m_Trackable, k_TrackableOptionConfidence);
        }
        return true;
    }

    /// <summary>
    /// Draws a small black square on white in the image's top left corner. artoolkitX 1.1.21's stereo update skips the
    /// marker entirely when either image has no square-looking shape at all (arGetMarker gives no list), so without
    /// this a marker seen by only one camera would be lost; the decoy keeps every image's list non-empty. It reads as
    /// a different (or no) barcode, so it is never taken for the marker.
    /// </summary>
    public static void StampDecoy(byte[] image, int width, int height)
    {
        const int margin = 6, paper = 56, square = 30;
        if (width < paper * 4 || height < paper * 4)
            return;
        int inset = (paper - square) / 2;
        for (int y = 0; y < paper; y++)
        {
            int row = (margin + y) * width + margin;
            bool insideY = y >= inset && y < inset + square;
            for (int x = 0; x < paper; x++)
                image[row + x] = insideY && x >= inset && x < inset + square ? (byte)20 : (byte)235;
        }
    }

    public void Dispose()
    {
        if (m_Started)
        {
            try
            {
                if (m_PushInited)
                {
                    ARX_pinvoke.arwVideoPushFinal(0);
                    ARX_pinvoke.arwVideoPushFinal(1);
                }
                ARX_pinvoke.arwRemoveAllTrackables();
                ARX_pinvoke.arwStopRunning();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ARMarker] stopping artoolkitX: " + e.Message);
            }
        }
        m_Started = false;
        m_PushInited = false;
        m_Trackable = -1;
        if (s_Running == this)
            s_Running = null;
    }

    // Not in arunityx's bindings.
    [DllImport("ARX", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool arwIsInited();

    [AOT.MonoPInvokeCallback(typeof(PluginFunctionsLogCallback))]
    static void OnNativeLog(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;
        log?.Invoke(message.TrimEnd());
    }

    /// <summary>artoolkitX's version, for the log.</summary>
    public static string Version()
    {
        try
        {
            var text = new StringBuilder(64);
            return ARX_pinvoke.arwGetARToolKitVersion(text, text.Capacity) ? text.ToString() : "?";
        }
        catch (Exception e)
        {
            return "unavailable (" + e.Message + ")";
        }
    }
}
