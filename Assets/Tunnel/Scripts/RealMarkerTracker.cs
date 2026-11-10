using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// Paints with a real marker, seen by the headset's passthrough camera, in place of the palette's virtual marker.
/// The canvas is either a real white panel with a dark mark on each corner, or a tunnel wall that lies on a real wall.
/// Started by the palette's Real marker button:
///  1. Colour: the marker is held sideways with its coloured cap against something plain and white; the cap's colour
///     becomes the colour that's tracked, and its known length (1.5 inches) against its width gives its diameter.
///  1b. Aligning: the marker is moved around the view in the hand; matching the cap's place in the image to the
///     tracked hand holding it settles how the camera image is read and checks the cap's diameter.
///  2. Surface: the cap's tip is held still on the surface for 4 seconds; a dot appears there with a beep. Three such
///     points, apart from each other and not in a line, give the surface's plane.
///  3. Canvas: on a panel, the four dark corner marks (each a dark box with a letter) are found in the camera image,
///     seen from wherever the head was, and placed on the plane; the canvas reaches their outer corners and a dark
///     outline shows it (<see cref="WhiteboardSurface"/>). With no marks, a tunnel wall lying on the plane is the
///     canvas, and its paint is shared like the virtual marker's.
///  4. Painting: every camera image, the cap is found (colour lookup, then the biggest patch near where it was); its
///     tip is the end away from the hand holding it. Where the tip's ray meets the plane is where it paints, and its
///     height off the plane (its distance from the cap's apparent width) says whether it touches, with hysteresis.
///     The palette's marker paints from it (<see cref="TunnelMarker.SetRealTip"/>) with the palette's colour.
/// A disk at the tip shows the brush's colour and size. Pinching the disk with the other hand and moving away (or
/// back) sizes the brush, until the pinch opens.
/// A log and camera snapshots with what was found drawn on them are saved on the headset, in the app's files folder
/// under RealMarker (adb pull /sdcard/Android/data/edu.scripps.tunnel/files/RealMarker).
/// Each image is about 40 ms old when it arrives, so the head pose from then is used. Local to this headset, apart
/// from paint on a tunnel wall.
/// </summary>
[DefaultExecutionOrder(-1100)] // before the marker paints
public class RealMarkerTracker : MonoBehaviour
{
    public enum Stage { Off, Colour, Aligning, Surface, Canvas, Tracking }

    public enum CanvasKind { None, Panel, TunnelWall }

    [Header("The marker")]
    [SerializeField]
    [Tooltip("The cap's length (meters): 1.5 inches.")]
    float m_CapLength = 0.0381f;

    [SerializeField]
    [Tooltip("The cap's diameter (meters), used if it can't be measured when its colour is learned.")]
    float m_CapDiameter = 0.016f;

    [SerializeField]
    [Tooltip("Smallest cap patch tracked (image pixels).")]
    int m_MinPixels = 120;

    [SerializeField]
    [Tooltip("How old a camera image is when it arrives (seconds): the head pose from then is used.")]
    float m_CameraLatency = 0.04f;

    [SerializeField]
    [Tooltip("Lost from view this long (seconds), the marker stops painting.")]
    float m_LostAfter = 0.2f;

    [SerializeField]
    [Tooltip("The camera's image has red and blue swapped (only for naming the cap's colour).")]
    bool m_SwapRedBlue = true;

    [SerializeField]
    [Tooltip("At most this many camera snapshots are saved per run, for looking into problems.")]
    int m_MaxSnapshots = 40;

    [Header("The surface: three touches")]
    [SerializeField]
    [Tooltip("How long the tip is held still on the surface for a point (seconds).")]
    float m_HoldTime = 4f;

    [SerializeField]
    [Tooltip("The tip counts as still while it stays this close sideways (meters)...")]
    float m_HoldSideways = 0.01f;

    [SerializeField]
    [Tooltip("...and this close in depth, which one camera judges less well.")]
    float m_HoldDepth = 0.035f;

    [SerializeField]
    [Tooltip("The three points are at least this far apart (meters).")]
    float m_TouchSpacing = 0.1f;

    [SerializeField]
    [Tooltip("The third point is at least this far off the line through the other two (meters).")]
    float m_MinSpread = 0.05f;

    [SerializeField]
    [Tooltip("A surface within this angle of upright (degrees) is set exactly upright.")]
    float m_UprightSnap = 5f;

    [Header("The canvas")]
    [SerializeField]
    [Tooltip("Corner marks between these sizes (meters) are looked for.")]
    Vector2 m_MarkSize = new(0.015f, 0.2f);

    [SerializeField]
    [Tooltip("Sightings of a mark within this distance (meters) on the surface are the same mark.")]
    float m_MarkCluster = 0.03f;

    [SerializeField]
    [Tooltip("A mark must be seen this many times.")]
    int m_MinMarkSightings = 6;

    [SerializeField]
    [Tooltip("The panel's marks are at least this far apart (meters).")]
    float m_MinPanelSide = 0.15f;

    [SerializeField]
    [Tooltip("A tunnel wall is the canvas when the touched surface is within this angle (degrees)...")]
    float m_WallAngle = 15f;

    [SerializeField]
    [Tooltip("...and this distance (meters) of it.")]
    float m_WallDistance = 0.12f;

    [SerializeField]
    [Tooltip("Size of a canvas texel on the panel (meters).")]
    float m_PanelTexel = 0.0015f;

    [SerializeField]
    [Tooltip("With no marks and no tunnel wall, give up after this long (seconds).")]
    float m_CanvasTimeout = 90f;

    [Header("Painting")]
    [SerializeField]
    [Tooltip("The tip touches when its estimated height drops below this (meters)...")]
    float m_TouchDown = 0.012f;

    [SerializeField]
    [Tooltip("...and stops touching when it rises above this.")]
    float m_TouchUp = 0.022f;

    [Header("Brush size")]
    [SerializeField]
    [Tooltip("A pinch starts sizing the brush within this far of the disk's edge (meters).")]
    float m_PinchReach = 0.04f;

    [SerializeField]
    [Tooltip("Thumb and index tips this close (meters) are a pinch...")]
    float m_PinchClose = 0.015f;

    [SerializeField]
    [Tooltip("...and this far apart, the pinch has opened.")]
    float m_PinchOpen = 0.035f;

    const int k_Cell = 8;

    public static RealMarkerTracker instance { get; private set; }

    public Stage stage { get; private set; }
    public CanvasKind canvasKind { get; private set; }
    public TunnelPalette palette { get; private set; }
    public Color32 capColour { get; private set; }
    public string status { get; private set; } = "";

    /// <summary>The canvas on the real panel, once found (it stays after the real marker is turned off).</summary>
    public WhiteboardSurface board => m_Board;

    /// <summary>The paint on the panel's canvas, which can be copied onto a tunnel wall.</summary>
    public TunnelSegmentPaint panelPaint => m_BoardPaint;

    PassthroughCameraReader m_Camera;
    Material m_Overlay;
    Shader m_Brush;
    WhiteboardSurface m_Board;
    TunnelSegmentPaint m_BoardPaint;
    TunnelSegmentPaint m_Canvas;
    Coroutine m_Run;

    ColourModel m_Model;
    float m_Diameter; // the cap's, measured or the default

    // Head poses, for the pose when an image was taken.
    readonly (float time, Vector3 position, Quaternion rotation)[] m_HeadHistory = new (float, Vector3, Quaternion)[64];
    int m_HeadCount, m_HeadNext;

    // The cap.
    Blob m_LastBlob;
    float m_LastSeen = -10f;
    Vector2 m_TipPixel;
    Sighting m_Tip;
    bool m_TipShown;
    float m_NextLog;

    // The surface.
    readonly List<Vector3> m_Points = new();
    readonly List<GameObject> m_PointDots = new();
    readonly List<(float time, Vector3 point)> m_Hold = new();
    float m_HoldLast;
    float m_NextCount;
    bool m_NeedMove;
    bool m_HasPlane;
    Vector3 m_PlanePoint, m_PlaneNormal; // the normal looks out of the surface, toward whoever paints
    float m_CanvasSince;

    // Corner marks, as seen: rays and how big they look.
    readonly List<(Vector3 origin, Vector3 direction, float angle, float time)> m_MarkSightings = new();
    bool m_MarksChanged;
    float m_NextMarks;
    int m_MarksFound;

    // Painting.
    bool m_Touching;
    readonly float[] m_Heights = new float[3];
    int m_HeightCount;
    float m_NextStatus;

    // The disk at the tip, and sizing it.
    Transform m_Disk, m_DiskFace;
    LineRenderer m_DiskRing;
    Material m_DiskMaterial, m_DotMaterial, m_RingMaterial;
    Vector3 m_DiskNormal;
    bool m_Pinching;
    Handedness m_PinchHand;
    float m_PinchStartDistance, m_PinchStartDiameter;
    Handedness m_HoldingHand = Handedness.Invalid;

    AudioSource m_Audio;
    AudioClip m_Beep;

    // How the camera image is read: found by matching the cap to the hand holding it (see Align).
    bool m_FlipX, m_FlipY, m_AxisTurn = true;

    // A log and snapshots on the headset (Android/data/edu.scripps.tunnel/files/RealMarker).
    static System.IO.StreamWriter s_File;
    static string s_Folder;
    int m_Snapshots;
    float m_NextSnapshot;
    string m_SnapNext;
    List<MarkSeen> m_LastMarks;
    Texture2D m_SnapTexture;

    /// <summary>The tracker, made the first time it's needed.</summary>
    public static RealMarkerTracker Get()
    {
        if (instance == null)
            instance = new GameObject("Real Marker Tracker").AddComponent<RealMarkerTracker>();
        return instance;
    }

    /// <summary>Starts finding and tracking a real marker for this palette, or stops if it's already on.</summary>
    public void Toggle(TunnelPalette owner, Material overlay, Shader brush)
    {
        if (stage != Stage.Off)
        {
            Stop("Real marker off.");
            return;
        }

        palette = owner;
        m_Overlay = overlay;
        m_Brush = brush;
        m_Run = StartCoroutine(Run());
    }

    public void Stop(string message)
    {
        if (m_Run != null)
            StopCoroutine(m_Run);
        m_Run = null;
        var marker = Marker();
        if (marker != null)
        {
            marker.SetReal(false);
            marker.SetRealCanvas(null);
        }
        stage = Stage.Off;
        canvasKind = CanvasKind.None;
        m_Canvas = null;
        m_Touching = false;
        m_Pinching = false;
        ShowDisk(false);
        ClearPoints();
        var scanner = FindAnyObjectByType<QrFloorScanner>();
        if (m_Camera != null && (scanner == null || !scanner.isScanning))
            m_Camera.Stop();
        SetStatus(message);
    }

    void Awake()
    {
        instance = this;
        m_Audio = gameObject.AddComponent<AudioSource>();
        m_Audio.playOnAwake = false;
        m_Audio.spatialBlend = 0f;
        m_Beep = Tone("Real Marker Beep", 1000f, 0.18f);
    }

    void OnDestroy()
    {
        if (instance == this)
            instance = null;
        foreach (var o in new UnityEngine.Object[] { m_DiskMaterial, m_DotMaterial, m_RingMaterial, m_Beep })
            if (o != null)
                Destroy(o);
    }

    TunnelMarker Marker()
    {
        foreach (var marker in TunnelMarker.all)
            if (marker != null && palette != null && marker.palette == palette)
                return marker;
        return null;
    }

    void SetStatus(string text)
    {
        if (text == status)
            return;
        status = text;
        Log(text);
    }

    void Update()
    {
        var head = Camera.main != null ? Camera.main.transform : null;
        if (head != null)
        {
            m_HeadHistory[m_HeadNext] = (Time.unscaledTime, head.position, head.rotation);
            m_HeadNext = (m_HeadNext + 1) % m_HeadHistory.Length;
            m_HeadCount = Mathf.Min(m_HeadCount + 1, m_HeadHistory.Length);
        }

        if (stage != Stage.Off && palette == null)
        {
            Stop("The palette is gone: real marker off.");
            return;
        }

        if (stage >= Stage.Surface)
            SizeBrush();
    }

    // The head's pose when the current camera image was taken.
    Pose HeadAtCapture()
    {
        float when = Time.unscaledTime - m_CameraLatency;
        var latest = m_HeadHistory[(m_HeadNext - 1 + m_HeadHistory.Length) % m_HeadHistory.Length];
        for (int i = 1; i < m_HeadCount; i++)
        {
            var newer = m_HeadHistory[(m_HeadNext - i + m_HeadHistory.Length) % m_HeadHistory.Length];
            var older = m_HeadHistory[(m_HeadNext - i - 1 + m_HeadHistory.Length) % m_HeadHistory.Length];
            if (older.time <= when)
            {
                float t = newer.time > older.time ? Mathf.InverseLerp(older.time, newer.time, when) : 1f;
                return new Pose(Vector3.Lerp(older.position, newer.position, t), Quaternion.Slerp(older.rotation, newer.rotation, t));
            }
        }

        return new Pose(latest.position, latest.rotation);
    }

    IEnumerator Run()
    {
        m_Camera = FindAnyObjectByType<PassthroughCameraReader>();
        if (m_Camera == null)
            m_Camera = gameObject.AddComponent<PassthroughCameraReader>();
        var scanner = FindAnyObjectByType<QrFloorScanner>();
        if (scanner != null && scanner.isScanning)
        {
            Stop("The camera is busy aligning to the floor QR code; try again when that's done.");
            yield break;
        }

        var marker = Marker();
        if (marker == null)
        {
            Stop("The palette has no marker.");
            yield break;
        }

        stage = Stage.Colour;
        canvasKind = CanvasKind.None;
        m_Canvas = null;
        m_LastBlob = default;
        m_LastSeen = -10f;
        m_HeightCount = 0;
        m_Touching = false;
        m_HasPlane = false;
        m_MarkSightings.Clear();
        m_MarksFound = 0;
        m_HoldingHand = Handedness.Invalid;
        ClearPoints();
        OpenLog();
        m_Snapshots = 0;
        SetStatus("Starting the camera...");
        bool started = false;
        yield return m_Camera.StartCamera(ok => started = ok);
        if (!started)
        {
            Stop(m_Camera.error ?? "The camera didn't start.");
            yield break;
        }

        yield return FindColour();
        if (stage != Stage.Colour)
            yield break;

        stage = Stage.Aligning;
        yield return Align();
        if (stage != Stage.Aligning)
            yield break;

        // From here the real marker stands in for the virtual one, though it paints only once there's a canvas.
        marker.SetReal(true);
        marker.SetRealCanvas(null);
        stage = Stage.Surface;
        SetStatus("Touch the surface with the cap's tip and hold it still for 4 seconds, until it beeps. Then two more spots, apart and not in a line.");
        yield return Loop(marker);
    }

    #region Colour

    struct ColourModel
    {
        public float hue, tolerance, minSaturation, minValue;
        public bool[] lookup; // 32 x 32 x 32 RGB bins

        public bool Match(Color32 c) => lookup[(c.r >> 3) << 10 | (c.g >> 3) << 5 | c.b >> 3];

        public void Build()
        {
            lookup = new bool[32 * 32 * 32];
            for (int r = 0; r < 32; r++)
            for (int g = 0; g < 32; g++)
            for (int b = 0; b < 32; b++)
            {
                Hsv(new Color32((byte)(r * 8 + 4), (byte)(g * 8 + 4), (byte)(b * 8 + 4), 255), out float h, out float s, out float v);
                lookup[r << 10 | g << 5 | b] = s >= minSaturation && v >= minValue && HueDistance(h, hue) <= tolerance;
            }
        }
    }

    static void Hsv(Color32 c, out float hue, out float saturation, out float value)
    {
        int max = Math.Max(c.r, Math.Max(c.g, c.b)), min = Math.Min(c.r, Math.Min(c.g, c.b));
        float chroma = max - min;
        value = max / 255f;
        saturation = max > 0 ? chroma / max : 0f;
        if (chroma <= 0f)
        {
            hue = 0f;
            return;
        }

        float h;
        if (max == c.r)
            h = (c.g - c.b) / chroma;
        else if (max == c.g)
            h = 2f + (c.b - c.r) / chroma;
        else
            h = 4f + (c.r - c.g) / chroma;
        hue = (h * 60f + 360f) % 360f;
    }

    static float HueDistance(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    struct ColourSighting
    {
        public bool found;
        public ColourModel model;
        public Color32 colour;
        public int pixels;
        public float length, width;
        public string why;
    }

    IEnumerator FindColour()
    {
        SetStatus("Hold the marker sideways in the middle of your view, its coloured cap against something plain and white, and keep it still...");
        float deadline = Time.unscaledTime + 25f;
        var steady = new List<ColourSighting>();
        string lastWhy = null;
        while (Time.unscaledTime < deadline)
        {
            yield return NextFrame();
            var pixels = m_Camera.GetPixels();
            if (pixels == null)
                continue;
            int w = m_Camera.width, h = m_Camera.height;
            var task = Task.Run(() => SeeColour(pixels, w, h));
            while (!task.IsCompleted)
                yield return null;
            var seen = task.Result;
            if (!seen.found)
            {
                steady.Clear();
                if (seen.why != lastWhy)
                    Log("colour: " + seen.why);
                lastWhy = seen.why;
                continue;
            }

            if (steady.Count > 0 && HueDistance(steady[^1].model.hue, seen.model.hue) > 12f)
                steady.Clear();
            steady.Add(seen);
            SetStatus($"Seeing a {Describe(Shown(seen.colour))} cap <color=#{ColorUtility.ToHtmlStringRGB(Shown(seen.colour))}>■</color>. Hold still...");
            if (steady.Count < 8)
                continue;

            // The steadiest view: the middle one by hue.
            var byHue = new List<ColourSighting>(steady);
            byHue.Sort((a, b) => a.model.hue.CompareTo(b.model.hue));
            var chosen = byHue[byHue.Count / 2];
            m_Model = chosen.model;
            capColour = Shown(chosen.colour);

            // Its diameter, from its known length: the middle of the width-to-length ratios seen.
            var ratios = steady.ConvertAll(s => s.width / Mathf.Max(s.length, 1f));
            ratios.Sort();
            float diameter = m_CapLength * ratios[ratios.Count / 2];
            bool measured = diameter >= 0.008f && diameter <= 0.03f;
            m_Diameter = measured ? diameter : m_CapDiameter;
            Log($"cap colour {capColour} hue {m_Model.hue:0} ±{m_Model.tolerance:0}, saturation ≥ {m_Model.minSaturation:0.00}, value ≥ {m_Model.minValue:0.00}, {chosen.pixels} pixels; " +
                (measured ? $"diameter {m_Diameter * 1000f:0.0} mm from its length" : $"diameter measured {diameter * 1000f:0.0} mm (held end-on?), using {m_CapDiameter * 1000f:0} mm"));
            PlayerHudNotification.Instance?.ShowText($"Tracking the {Describe(capColour)} cap", 2f);
            yield break;
        }

        Stop("Couldn't find a coloured cap. Use a marker with a bright cap (blue, green, red...), hold it sideways in the middle of your view against white, and try again.");
    }

    // The colour as it really is (the camera's red and blue may be swapped).
    Color32 Shown(Color32 c) => m_SwapRedBlue ? new Color32(c.b, c.g, c.r, c.a) : c;

    static string Describe(Color32 c)
    {
        Hsv(c, out float h, out float s, out float v);
        if (s < 0.25f)
            return v < 0.3f ? "black" : "grey";
        string[] names = { "red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink" };
        float[] starts = { 15f, 45f, 70f, 165f, 200f, 260f, 290f, 335f };
        for (int i = 0; i < starts.Length; i++)
            if (h < starts[i])
                return names[i];
        return "red";
    }

    // Worker thread: the strongest saturated colour in the middle of the image, if it's one compact patch.
    static ColourSighting SeeColour(Color32[] pixels, int w, int h)
    {
        int x0 = w / 5, x1 = w - w / 5, y0 = h / 5, y1 = h - h / 5;
        var histogram = new int[36];
        for (int y = y0; y < y1; y += 2)
        for (int x = x0; x < x1; x += 2)
        {
            Hsv(pixels[y * w + x], out float hue, out float s, out float v);
            if (s >= 0.45f && v >= 0.2f && s * v >= 0.18f)
                histogram[(int)(hue / 10f) % 36]++;
        }

        int peak = 0, peakCount = 0;
        for (int i = 0; i < 36; i++)
        {
            int count = histogram[(i + 35) % 36] + histogram[i] + histogram[(i + 1) % 36];
            if (count > peakCount)
            {
                peakCount = count;
                peak = i;
            }
        }

        if (peakCount < 40)
            return new ColourSighting { why = $"no saturated colour in the middle ({peakCount} samples)" };

        // The colour's pixels: hue, saturation and brightness spread.
        float peakHue = peak * 10f + 5f;
        double sumSin = 0, sumCos = 0, r = 0, g = 0, b = 0;
        var saturations = new List<float>();
        var values = new List<float>();
        var hues = new List<float>();
        for (int y = y0; y < y1; y += 2)
        for (int x = x0; x < x1; x += 2)
        {
            var c = pixels[y * w + x];
            Hsv(c, out float hue, out float s, out float v);
            if (s < 0.45f || v < 0.2f || s * v < 0.18f || HueDistance(hue, peakHue) > 20f)
                continue;
            sumSin += Math.Sin(hue * Mathf.Deg2Rad);
            sumCos += Math.Cos(hue * Mathf.Deg2Rad);
            r += c.r;
            g += c.g;
            b += c.b;
            hues.Add(hue);
            saturations.Add(s);
            values.Add(v);
        }

        int n = hues.Count;
        float mean = ((float)(Math.Atan2(sumSin, sumCos) * Mathf.Rad2Deg) + 360f) % 360f;
        float spread = 0f;
        foreach (float hue in hues)
            spread += HueDistance(hue, mean) * HueDistance(hue, mean);
        spread = Mathf.Sqrt(spread / n);
        saturations.Sort();
        values.Sort();
        var model = new ColourModel
        {
            hue = mean,
            tolerance = Mathf.Clamp(spread * 2.5f, 10f, 22f),
            minSaturation = Mathf.Max(0.3f, saturations[n / 2] * 0.55f),
            minValue = Mathf.Max(0.12f, values[n / 2] * 0.45f),
        };
        model.Build();

        // It must be one compact patch (a cap), not colour scattered about the view.
        var blob = FindBlob(pixels, w, h, model, new RectInt(x0, y0, x1 - x0, y1 - y0), null, 60);
        if (!blob.found)
            return new ColourSighting { why = $"hue {mean:0} is scattered, not one patch" };
        if (blob.pixels < n * 4 * 0.5f)
            return new ColourSighting { why = $"hue {mean:0}: its biggest patch has {blob.pixels} of about {n * 4} pixels" };

        return new ColourSighting
        {
            found = true,
            model = model,
            colour = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255),
            pixels = blob.pixels,
            length = blob.length,
            width = blob.width,
        };
    }

    #endregion

    #region Aligning the camera image with the hand

    // The camera image can be read 8 ways (mirrored across, mirrored up and down, and Android's axes turned or not),
    // and only one puts the cap where it really is. The hand holding the marker is tracked in 3D, and the cap is
    // always right beside it: the way that keeps the cap's direction nearest the hand's joints, over many views with
    // the cap away from the middle of the image, is the right one. The hand's distance also checks the cap's
    // diameter (and so how far away it's judged to be).
    IEnumerator Align()
    {
        SetStatus("Now hold the marker as you would to draw and move it slowly around your view, out toward the edges too, for a few seconds. (Controllers down: your hands must be tracked.)");
        var angles = new List<float>[8];
        var diameters = new List<float>[8];
        for (int i = 0; i < 8; i++)
        {
            angles[i] = new List<float>();
            diameters[i] = new List<float>();
        }

        float f = m_Camera.imageFocalLength;
        float deadline = Time.unscaledTime + 30f;
        int counted = 0, chosen = -1;
        Blob blob = default;
        Pose lastPose = default;
        Color32[] lastPixels = null;
        while (Time.unscaledTime < deadline)
        {
            yield return NextFrame();
            var pixels = m_Camera.GetPixels();
            if (pixels == null)
                continue;
            var head = HeadAtCapture();
            int w = m_Camera.width, h = m_Camera.height;
            var model = m_Model;
            int minPixels = m_MinPixels;
            var task = Task.Run(() => FindBlob(pixels, w, h, model, new RectInt(0, 0, w, h), null, minPixels));
            while (!task.IsCompleted)
                yield return null;
            if (stage != Stage.Aligning)
                yield break;
            blob = task.Result;
            lastPixels = pixels;
            lastPose = m_Camera.GetCameraPose(head, m_AxisTurn);
            if (Time.unscaledTime >= m_NextSnapshot)
            {
                m_NextSnapshot = Time.unscaledTime + 3f;
                Snapshot("align", pixels, w, h, blob, lastPose);
            }
            if (!blob.found)
            {
                SetStatus("Aligning: can't see the cap. Hold the marker in view, its cap toward the camera's side.");
                continue;
            }

            var joints = new List<Vector3>();
            foreach (var hand in new[] { Handedness.Left, Handedness.Right })
                foreach (var joint in new[] { XRHandJointID.Palm, XRHandJointID.IndexTip, XRHandJointID.ThumbTip, XRHandJointID.IndexProximal, XRHandJointID.MiddleTip })
                    if (HandPalm.TryGetJoint(hand, joint, out var p))
                        joints.Add(p);
            if (joints.Count == 0)
            {
                SetStatus("Aligning: can't see your hands. Put the controllers down so the headset tracks your hands.");
                continue;
            }

            if (Vector2.Distance(blob.center, m_Camera.imageCenter) < 0.15f * Mathf.Min(w, h))
            {
                SetStatus($"Aligning ({counted} views): move the marker away from the middle of your view, toward the edges.");
                continue;
            }

            for (int o = 0; o < 8; o++)
            {
                var pose = m_Camera.GetCameraPose(head, (o & 4) == 0);
                var ray = m_Camera.PixelToRay(blob.center, pose, (o & 1) != 0, (o & 2) != 0);
                float best = 180f, depth = 0f;
                foreach (var joint in joints)
                {
                    float a = Vector3.Angle(ray.direction, joint - ray.origin);
                    if (a < best)
                    {
                        best = a;
                        depth = Vector3.Dot(joint - pose.position, pose.rotation * Vector3.forward);
                    }
                }
                angles[o].Add(best);
                diameters[o].Add(depth * blob.width / f);
            }

            counted++;
            SetStatus($"Aligning: {counted} views. Keep moving the marker slowly around your view...");
            if (counted < 20)
                continue;
            int first = -1, second = -1;
            for (int o = 0; o < 8; o++)
            {
                if (first < 0 || Middle(angles[o]) < Middle(angles[first]))
                {
                    second = first;
                    first = o;
                }
                else if (second < 0 || Middle(angles[o]) < Middle(angles[second]))
                {
                    second = o;
                }
            }

            if (Middle(angles[first]) < 10f && Middle(angles[second]) > 2.5f * Middle(angles[first]))
            {
                chosen = first;
                break;
            }
        }

        var table = new System.Text.StringBuilder();
        for (int o = 0; o < 8; o++)
            table.Append($"\n  flipX={(o & 1) != 0} flipY={(o & 2) != 0} axisTurn={(o & 4) == 0}: cap {Middle(angles[o]):0.0}° from the hand, diameter by the hand's distance {Middle(diameters[o]) * 1000f:0.0} mm");
        Log($"aligning: {counted} views{table}");

        if (chosen < 0)
        {
            Log("aligning: couldn't tell which way the image is read; keeping flipX=False flipY=False axisTurn=True");
            PlayerHudNotification.Instance?.ShowText("Couldn't align the camera with your hand; carrying on", 2f);
        }
        else
        {
            m_FlipX = (chosen & 1) != 0;
            m_FlipY = (chosen & 2) != 0;
            m_AxisTurn = (chosen & 4) == 0;
            float byHand = Middle(diameters[chosen]);
            string use = "";
            if (byHand >= 0.008f && byHand <= 0.03f && (m_Diameter > byHand * 1.25f || m_Diameter < byHand * 0.8f))
            {
                use = $"; using the hand's {byHand * 1000f:0.0} mm in place of {m_Diameter * 1000f:0.0} mm";
                m_Diameter = byHand;
            }
            Log($"aligned: flipX={m_FlipX} flipY={m_FlipY} axisTurn={m_AxisTurn}, cap {Middle(angles[chosen]):0.0}° from the hand; diameter {m_Diameter * 1000f:0.0} mm, by the hand's distance {byHand * 1000f:0.0} mm{use}");
            PlayerHudNotification.Instance?.ShowText("Camera aligned", 1.5f);
        }

        if (lastPixels != null)
            Snapshot("aligned", lastPixels, m_Camera.width, m_Camera.height, blob, m_Camera.GetCameraPose(HeadAtCapture(), m_AxisTurn));
    }

    static float Middle(List<float> values)
    {
        if (values.Count == 0)
            return float.MaxValue;
        var sorted = new List<float>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    #endregion

    #region Finding the cap

    struct Blob
    {
        public bool found;
        public int pixels;
        public Vector2 center, axis; // image pixels (x right, rows up from the bottom, as Unity reads them)
        public float length, width;

        public Vector2 End(int which) => center + axis * (which == 0 ? 0.5f * length : -0.5f * length);
    }

    // The patch of the cap's colour: a coarse grid of cells finds the patches, the one nearest `near` (or the biggest)
    // is measured precisely: its centre, long axis, length and width from its pixels' second moments.
    static Blob FindBlob(Color32[] pixels, int w, int h, ColourModel model, RectInt region, Vector2? near, int minPixels)
    {
        region.ClampToBounds(new RectInt(0, 0, w, h));
        int gw = region.width / k_Cell, gh = region.height / k_Cell;
        if (gw < 1 || gh < 1)
            return default;
        var counts = new int[gw * gh];
        for (int cy = 0; cy < gh; cy++)
        for (int cx = 0; cx < gw; cx++)
        {
            int count = 0;
            int baseX = region.x + cx * k_Cell, baseY = region.y + cy * k_Cell;
            for (int y = 1; y < k_Cell; y += 2)
            for (int x = 1; x < k_Cell; x += 2)
                if (model.Match(pixels[(baseY + y) * w + baseX + x]))
                    count++;
            counts[cy * gw + cx] = count;
        }

        // Patches of cells with a few matching samples (of 16).
        var label = new int[gw * gh];
        var queue = new Queue<int>();
        int bestLabel = 0;
        float bestScore = float.MinValue;
        int next = 0;
        for (int start = 0; start < counts.Length; start++)
        {
            if (counts[start] < 4 || label[start] != 0)
                continue;
            next++;
            int weight = 0;
            float sx = 0f, sy = 0f;
            label[start] = next;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int cx = i % gw, cy = i / gw;
                weight += counts[i];
                sx += cx * counts[i];
                sy += cy * counts[i];
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = cy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= gw || ny >= gh)
                        continue;
                    int j = ny * gw + nx;
                    if (counts[j] >= 4 && label[j] == 0)
                    {
                        label[j] = next;
                        queue.Enqueue(j);
                    }
                }
            }

            float size = weight * k_Cell * k_Cell / 16f; // pixels, roughly
            if (size < minPixels)
                continue;
            float score = size;
            if (near.HasValue)
            {
                var center = new Vector2(region.x + (sx / weight + 0.5f) * k_Cell, region.y + (sy / weight + 0.5f) * k_Cell);
                score -= Vector2.Distance(center, near.Value) * 8f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestLabel = next;
            }
        }

        if (bestLabel == 0)
            return default;

        // Its pixels, in its cells and the cells around them.
        double n = 0, mx = 0, my = 0, mxx = 0, myy = 0, mxy = 0;
        for (int cy = 0; cy < gh; cy++)
        for (int cx = 0; cx < gw; cx++)
        {
            bool include = false;
            for (int dy = -1; dy <= 1 && !include; dy++)
            for (int dx = -1; dx <= 1 && !include; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                include = nx >= 0 && ny >= 0 && nx < gw && ny < gh && label[ny * gw + nx] == bestLabel;
            }

            if (!include)
                continue;
            int baseX = region.x + cx * k_Cell, baseY = region.y + cy * k_Cell;
            for (int y = 0; y < k_Cell; y++)
            for (int x = 0; x < k_Cell; x++)
            {
                if (!model.Match(pixels[(baseY + y) * w + baseX + x]))
                    continue;
                double px = baseX + x + 0.5, py = baseY + y + 0.5;
                n++;
                mx += px;
                my += py;
                mxx += px * px;
                myy += py * py;
                mxy += px * py;
            }
        }

        if (n < minPixels)
            return default;
        mx /= n;
        my /= n;
        double a = mxx / n - mx * mx, c = myy / n - my * my, b = mxy / n - mx * my;
        double half = (a + c) * 0.5, root = Math.Sqrt(Math.Max(0, (a - c) * (a - c) * 0.25 + b * b));
        double major = half + root, minor = Math.Max(half - root, 0.25);
        double angle = 0.5 * Math.Atan2(2 * b, a - c);
        return new Blob
        {
            found = true,
            pixels = (int)n,
            center = new Vector2((float)mx, (float)my),
            axis = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)),
            length = (float)Math.Sqrt(12 * major), // as for a rectangle
            width = (float)Math.Sqrt(12 * minor),
        };
    }

    struct Sighting
    {
        public Blob blob;
        public Ray tipRay;
        public Vector3 tip;            // the tip's estimated position (its depth from the cap's width)
        public Vector3 cameraPosition;
    }

    // The cap's two ends in the world (both at the depth its width gives), and which is the tip: the end away from
    // the hand holding it; if no hand is seen, the end nearest the last tip, then the end nearer the surface.
    bool Measure(Blob blob, Pose cameraPose, out Sighting sighting)
    {
        sighting = default;
        if (!blob.found || blob.width < 2f)
            return false;

        float f = m_Camera.imageFocalLength;
        var forward = cameraPose.rotation * Vector3.forward;
        float depth = f * m_Diameter / blob.width; // along the camera's axis
        var rays = new Ray[2];
        var ends = new Vector3[2];
        for (int e = 0; e < 2; e++)
        {
            rays[e] = m_Camera.PixelToRay(blob.End(e), cameraPose, m_FlipX, m_FlipY);
            ends[e] = AtDepth(rays[e], forward, depth);
        }

        int tipEnd;
        string how;
        var center = (ends[0] + ends[1]) * 0.5f;
        if (HoldingHand(center, out var palm) && Mathf.Abs(Vector3.Distance(ends[0], palm) - Vector3.Distance(ends[1], palm)) > 0.01f)
        {
            tipEnd = Vector3.Distance(ends[0], palm) > Vector3.Distance(ends[1], palm) ? 0 : 1;
            how = $"hand ({m_HoldingHand})";
        }
        else if (m_LastBlob.found && Time.unscaledTime - m_LastSeen < 0.5f)
        {
            tipEnd = Vector2.Distance(blob.End(0), m_TipPixel) <= Vector2.Distance(blob.End(1), m_TipPixel) ? 0 : 1;
            how = "last";
        }
        else if (m_HasPlane)
        {
            tipEnd = Vector3.Dot(ends[0] - m_PlanePoint, m_PlaneNormal) <= Vector3.Dot(ends[1] - m_PlanePoint, m_PlaneNormal) ? 0 : 1;
            how = "surface";
        }
        else
        {
            tipEnd = ends[0].y <= ends[1].y ? 0 : 1; // the lower end
            how = "lower";
        }

        if (Time.unscaledTime >= m_NextLog)
        {
            m_NextLog = Time.unscaledTime + 1f;
            string height = m_HasPlane ? $", tip {Vector3.Dot(ends[tipEnd] - m_PlanePoint, m_PlaneNormal) * 100f:0.0} cm off the surface" : "";
            Log($"cap {blob.length:0} x {blob.width:0.0} px ({blob.pixels} px) at {blob.center:F0}, {depth:0.00} m away, tip end by {how}{height}, touching {m_Touching}, stage {stage}");
        }

        m_LastBlob = blob;
        m_TipPixel = blob.End(tipEnd);
        sighting = new Sighting { blob = blob, tipRay = rays[tipEnd], tip = ends[tipEnd], cameraPosition = cameraPose.position };
        return true;
    }

    static Vector3 AtDepth(Ray ray, Vector3 forward, float depth) =>
        ray.origin + ray.direction * (depth / Mathf.Max(0.2f, Vector3.Dot(ray.direction, forward)));

    // The tracked hand holding the marker: the one whose palm was nearest the cap when first seen, kept while it stays
    // near (so the other hand, coming to pinch the disk, isn't taken for it).
    bool HoldingHand(Vector3 cap, out Vector3 palm)
    {
        if (m_HoldingHand != Handedness.Invalid && HandPalm.TryGetPalm(m_HoldingHand, out palm, out _, out _) && Vector3.Distance(palm, cap) < 0.25f)
            return true;
        palm = default;
        float best = 0.2f;
        var found = Handedness.Invalid;
        foreach (var hand in new[] { Handedness.Left, Handedness.Right })
        {
            if (m_Pinching && hand == m_PinchHand)
                continue;
            if (!HandPalm.TryGetPalm(hand, out var p, out _, out _))
                continue;
            float d = Vector3.Distance(p, cap);
            if (d < best)
            {
                best = d;
                found = hand;
                palm = p;
            }
        }

        if (found != Handedness.Invalid)
            m_HoldingHand = found;
        return found != Handedness.Invalid;
    }

    #endregion

    #region The loop

    IEnumerator Loop(TunnelMarker marker)
    {
        while (stage != Stage.Off)
        {
            yield return null;
            bool seen = false;
            if (m_Camera.hasNewFrame)
            {
                var pixels = m_Camera.GetPixels();
                var head = HeadAtCapture();
                int w = m_Camera.width, h = m_Camera.height;
                var model = m_Model;
                var region = new RectInt(0, 0, w, h);
                Vector2? near = null;
                if (Time.unscaledTime - m_LastSeen < 0.3f)
                {
                    float reach = Mathf.Max(160f, m_LastBlob.length * 3f);
                    region = new RectInt((int)(m_LastBlob.center.x - reach), (int)(m_LastBlob.center.y - reach), (int)(2 * reach), (int)(2 * reach));
                    near = m_LastBlob.center;
                }

                bool marks = WantMarks() && Time.unscaledTime >= m_NextMarks;
                if (marks)
                    m_NextMarks = Time.unscaledTime + 0.33f;
                int minPixels = m_MinPixels;
                var task = Task.Run(() => (FindBlob(pixels, w, h, model, region, near, minPixels), marks ? FindMarks(pixels, w, h) : null));
                while (!task.IsCompleted)
                    yield return null;
                if (stage == Stage.Off)
                    yield break;
                var cameraPose = m_Camera.GetCameraPose(head, m_AxisTurn);
                if (task.Result.Item2 != null)
                {
                    AddMarkSightings(task.Result.Item2, cameraPose);
                    m_LastMarks = task.Result.Item2;
                }
                seen = Measure(task.Result.Item1, cameraPose, out var sighting);
                if (seen)
                {
                    m_LastSeen = Time.unscaledTime;
                    m_Tip = sighting;
                    OnSighting(marker, sighting);
                }

                // Snapshots: when a point is placed, and every few seconds while looking for the canvas or painting.
                bool due = (stage == Stage.Canvas || stage == Stage.Surface || stage == Stage.Tracking) && Time.unscaledTime >= m_NextSnapshot;
                if (m_SnapNext != null || due)
                {
                    m_NextSnapshot = Time.unscaledTime + (stage == Stage.Tracking ? 6f : 3f);
                    Snapshot(m_SnapNext ?? stage.ToString().ToLowerInvariant(), pixels, w, h, task.Result.Item1, cameraPose);
                    m_SnapNext = null;
                }
            }

            if (!seen && Time.unscaledTime - m_LastSeen > m_LostAfter)
            {
                if (m_Touching || m_TipShown)
                {
                    m_Touching = false;
                    marker.SetRealTip(false, default, default, false);
                    ShowDisk(false);
                }
                if (stage == Stage.Tracking && Time.unscaledTime >= m_NextStatus)
                {
                    m_NextStatus = Time.unscaledTime + 0.25f;
                    status = "Real marker: can't see the cap";
                }
            }

            if (stage == Stage.Canvas)
                ChooseCanvas(marker);
            else if (stage == Stage.Tracking && canvasKind == CanvasKind.TunnelWall && TryFindPanel(out var panel))
                UsePanel(marker, panel, "the panel's corner marks were found");
        }
    }

    void OnSighting(TunnelMarker marker, Sighting sighting)
    {
        switch (stage)
        {
            case Stage.Surface:
                Hold(sighting);
                ShowDisk(true, sighting.tip, (sighting.cameraPosition - sighting.tip).normalized, marker);
                break;
            case Stage.Canvas:
                ShowDisk(true, sighting.tip, m_PlaneNormal, marker);
                break;
            case Stage.Tracking:
                Follow(marker, sighting);
                break;
        }
    }

    bool WantMarks() => stage == Stage.Surface || stage == Stage.Canvas || (stage == Stage.Tracking && canvasKind == CanvasKind.TunnelWall);

    #endregion

    #region The surface: three touches

    // The tip held still (sideways within a centimetre, in depth within a few) for 4 seconds makes a point.
    void Hold(Sighting sighting)
    {
        float now = Time.unscaledTime;
        if (m_NeedMove)
        {
            // After a point, the tip must move away before the next one starts.
            if (m_Points.Count > 0 && Vector3.Distance(sighting.tip, m_Points[^1]) < 0.05f)
                return;
            m_NeedMove = false;
        }

        if (m_Hold.Count > 0 && (now - m_HoldLast > 0.3f || !StillNear(m_Hold[0].point, sighting)))
            m_Hold.Clear();
        m_Hold.Add((now, sighting.tip));
        m_HoldLast = now;
        float held = now - m_Hold[0].time;
        if (held >= m_HoldTime)
        {
            var points = m_Hold.ConvertAll(s => s.point);
            m_Hold.Clear();
            AddPoint(Median(points));
            return;
        }

        if (held >= 0.5f && now >= m_NextCount)
        {
            m_NextCount = now + 1f;
            int left = Mathf.CeilToInt(m_HoldTime - held);
            PlayerHudNotification.Instance?.ShowText($"Hold still: {left}", 0.9f);
            SetStatus($"Point {m_Points.Count + 1} of 3: holding still, {left} s more...");
        }
    }

    bool StillNear(Vector3 anchor, Sighting sighting)
    {
        var offset = sighting.tip - anchor;
        var along = sighting.tipRay.direction;
        float depth = Vector3.Dot(offset, along);
        return (offset - along * depth).magnitude <= m_HoldSideways && Mathf.Abs(depth) <= m_HoldDepth;
    }

    static Vector3 Median(List<Vector3> points)
    {
        float Middle(Func<Vector3, float> axis)
        {
            var values = points.ConvertAll(p => axis(p));
            values.Sort();
            return values[values.Count / 2];
        }

        return new Vector3(Middle(p => p.x), Middle(p => p.y), Middle(p => p.z));
    }

    void AddPoint(Vector3 point)
    {
        m_NeedMove = true;
        for (int i = 0; i < m_Points.Count; i++)
        {
            if (Vector3.Distance(point, m_Points[i]) < m_TouchSpacing)
            {
                SetStatus($"That's within {m_TouchSpacing * 100f:0} cm of point {i + 1}: touch a spot farther from the others.");
                PlayerHudNotification.Instance?.ShowText("Too near another point", 1.5f);
                return;
            }
        }

        if (m_Points.Count == 2)
        {
            var a = m_Points[0];
            var b = m_Points[1];
            float spread = Vector3.Cross(b - a, point - a).magnitude / Mathf.Max((b - a).magnitude, 1e-4f);
            if (spread < m_MinSpread)
            {
                SetStatus($"That's nearly in line with the other two ({spread * 100f:0.0} cm off): touch a spot farther from that line.");
                PlayerHudNotification.Instance?.ShowText("Too near the line through the others", 1.5f);
                return;
            }
        }

        m_Points.Add(point);
        m_PointDots.Add(MakeDot(point));
        m_SnapNext = $"point{m_Points.Count}";
        m_Audio.PlayOneShot(m_Beep);
        Log($"point {m_Points.Count} at {point:F3}");
        if (m_Points.Count < 3)
        {
            SetStatus($"Point {m_Points.Count} placed. Now hold the tip still on another spot, at least {m_TouchSpacing * 100f:0} cm away.");
            return;
        }

        SetPlane();
        stage = Stage.Canvas;
        m_CanvasSince = Time.unscaledTime;
        m_MarksChanged = true;
        SetStatus("Surface set. Look at the panel's four corner marks...");
    }

    void SetPlane()
    {
        Vector3 a = m_Points[0], b = m_Points[1], c = m_Points[2];
        var center = (a + b + c) / 3f;
        var normal = Vector3.Cross(b - a, c - a).normalized;
        var head = Camera.main != null ? Camera.main.transform.position : center + normal;
        if (Vector3.Dot(normal, head - center) < 0f)
            normal = -normal;
        string how = "";
        if (Mathf.Abs(normal.y) < Mathf.Sin(m_UprightSnap * Mathf.Deg2Rad))
        {
            normal = new Vector3(normal.x, 0f, normal.z).normalized;
            how = ", set upright";
        }

        m_PlanePoint = center;
        m_PlaneNormal = normal;
        m_HasPlane = true;
        float tilt = 90f - Vector3.Angle(normal, Vector3.up);
        Log($"surface through {a:F3} {b:F3} {c:F3}: normal {normal:F3}, {tilt:0.0}° from upright{how}");
    }

    GameObject MakeDot(Vector3 point)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Real Marker Point";
        Destroy(go.GetComponent<Collider>());
        if (m_DotMaterial == null)
            m_DotMaterial = UnlitMaterial("Real Marker Point", new Color(1f, 0.2f, 0.7f));
        go.GetComponent<MeshRenderer>().sharedMaterial = m_DotMaterial;
        go.transform.position = point;
        go.transform.localScale = Vector3.one * 0.01f;
        return go;
    }

    void ClearPoints()
    {
        foreach (var dot in m_PointDots)
            if (dot != null)
                Destroy(dot);
        m_PointDots.Clear();
        m_Points.Clear();
        m_Hold.Clear();
        m_NeedMove = false;
    }

    #endregion

    #region The canvas: corner marks or a tunnel wall

    struct MarkSeen
    {
        public Vector2 center; // image pixels, as the cap's
        public float size;     // pixels across
    }

    void AddMarkSightings(List<MarkSeen> marks, Pose cameraPose)
    {
        float f = m_Camera.imageFocalLength;
        float now = Time.unscaledTime;
        foreach (var mark in marks)
        {
            var ray = m_Camera.PixelToRay(mark.center, cameraPose, m_FlipX, m_FlipY);
            m_MarkSightings.Add((ray.origin, ray.direction, mark.size / f, now));
        }

        m_MarkSightings.RemoveAll(s => now - s.time > 60f);
        if (m_MarkSightings.Count > 800)
            m_MarkSightings.RemoveRange(0, m_MarkSightings.Count - 800);
        if (marks.Count > 0)
            m_MarksChanged = true;
    }

    // Worker thread: dark, square-ish boxes (filled, or outlined with a letter inside) with white all round them.
    static List<MarkSeen> FindMarks(Color32[] pixels, int w, int h)
    {
        int hw = w / 2, hh = h / 2;
        var lum = new byte[hw * hh];
        for (int y = 0; y < hh; y++)
        for (int x = 0; x < hw; x++)
        {
            var c = pixels[2 * y * w + 2 * x];
            lum[y * hw + x] = (byte)((c.r + 2 * c.g + c.b) >> 2); // the same whichever way red and blue come
        }

        // How white the paper is nearby: the brightest in the 3 x 3 blocks (16 pixels each) round each pixel.
        const int B = 16;
        int bw = (hw + B - 1) / B, bh = (hh + B - 1) / B;
        var blockMax = new byte[bw * bh];
        for (int y = 0; y < hh; y++)
        for (int x = 0; x < hw; x++)
        {
            int i = y / B * bw + x / B;
            if (lum[y * hw + x] > blockMax[i])
                blockMax[i] = lum[y * hw + x];
        }

        var white = new byte[bw * bh];
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            byte m = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = bx + dx, ny = by + dy;
                if (nx >= 0 && ny >= 0 && nx < bw && ny < bh && blockMax[ny * bw + nx] > m)
                    m = blockMax[ny * bw + nx];
            }
            white[by * bw + bx] = m;
        }

        byte WhiteAt(int x, int y) => white[y / B * bw + x / B];
        var dark = new bool[hw * hh];
        for (int y = 0; y < hh; y++)
        for (int x = 0; x < hw; x++)
        {
            byte paper = WhiteAt(x, y);
            dark[y * hw + x] = paper >= 60 && lum[y * hw + x] < paper / 2;
        }

        // Patches of dark pixels.
        var label = new int[hw * hh];
        var stack = new int[hw * hh];
        var found = new List<(int id, int minX, int minY, int maxX, int maxY)>();
        int next = 0;
        for (int start = 0; start < dark.Length; start++)
        {
            if (!dark[start] || label[start] != 0)
                continue;
            next++;
            int top = 0, count = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            stack[top++] = start;
            label[start] = next;
            while (top > 0)
            {
                int i = stack[--top];
                int x = i % hw, y = i / hw;
                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (x + 1 < hw && dark[i + 1] && label[i + 1] == 0) { label[i + 1] = next; stack[top++] = i + 1; }
                if (x > 0 && dark[i - 1] && label[i - 1] == 0) { label[i - 1] = next; stack[top++] = i - 1; }
                if (y + 1 < hh && dark[i + hw] && label[i + hw] == 0) { label[i + hw] = next; stack[top++] = i + hw; }
                if (y > 0 && dark[i - hw] && label[i - hw] == 0) { label[i - hw] = next; stack[top++] = i - hw; }
            }

            int width = maxX - minX + 1, height = maxY - minY + 1;
            if (width < 8 || height < 8 || Mathf.Max(width, height) > Mathf.Min(hw, hh) / 3)
                continue;
            if (Mathf.Max(width, height) > 1.6f * Mathf.Min(width, height))
                continue;
            if (count < 30 || count < 0.15f * width * height)
                continue;
            if (minX == 0 || minY == 0 || maxX == hw - 1 || maxY == hh - 1)
                continue;

            // A box's sides run all along its bounding square (a letter's or a circle's don't).
            int band = Mathf.Max(1, (int)(0.15f * Mathf.Min(width, height)));
            int hits = 0;
            for (int k = 0; k < 16; k++)
            {
                float t = (k + 0.5f) / 16f;
                int sx = minX + (int)(t * (width - 1)), sy = minY + (int)(t * (height - 1));
                bool Any(int x0, int y0, int dx, int dy)
                {
                    for (int s = 0; s < band; s++)
                        if (label[(y0 + dy * s) * hw + x0 + dx * s] == next)
                            return true;
                    return false;
                }

                if (Any(sx, maxY, 0, -1)) hits++;
                if (Any(sx, minY, 0, 1)) hits++;
                if (Any(minX, sy, 1, 0)) hits++;
                if (Any(maxX, sy, -1, 0)) hits++;
            }

            if (hits < 0.75f * 64)
                continue;

            // White paper all round it.
            int margin = Mathf.Max(2, (int)(0.3f * Mathf.Max(width, height)));
            int bright = 0, samples = 0;
            for (int k = 0; k < 32; k++)
            {
                float t = (k % 8 + 0.5f) / 8f;
                int x, y;
                switch (k / 8)
                {
                    case 0: x = minX - margin + (int)(t * (width + 2 * margin)); y = maxY + margin; break;
                    case 1: x = minX - margin + (int)(t * (width + 2 * margin)); y = minY - margin; break;
                    case 2: x = minX - margin; y = minY - margin + (int)(t * (height + 2 * margin)); break;
                    default: x = maxX + margin; y = minY - margin + (int)(t * (height + 2 * margin)); break;
                }
                if (x < 0 || y < 0 || x >= hw || y >= hh)
                    continue;
                samples++;
                if (!dark[y * hw + x] && lum[y * hw + x] >= WhiteAt(x, y) * 0.6f)
                    bright++;
            }

            if (samples < 20 || bright < 0.7f * samples)
                continue;
            found.Add((next, minX, minY, maxX, maxY));
        }

        // A letter inside an outlined box isn't another mark.
        var marks = new List<MarkSeen>();
        foreach (var a in found)
        {
            bool inside = false;
            foreach (var b in found)
            {
                if (b.id == a.id || (b.maxX - b.minX) <= (a.maxX - a.minX))
                    continue;
                float cx = (a.minX + a.maxX) * 0.5f, cy = (a.minY + a.maxY) * 0.5f;
                if (cx > b.minX && cx < b.maxX && cy > b.minY && cy < b.maxY)
                    inside = true;
            }

            if (!inside)
                marks.Add(new MarkSeen
                {
                    center = new Vector2((a.minX + a.maxX + 1) * 0.5f * 2f, (a.minY + a.maxY + 1) * 0.5f * 2f),
                    size = ((a.maxX - a.minX + 1) + (a.maxY - a.minY + 1)) * 0.5f * 2f,
                });
        }

        return marks;
    }

    struct PanelFit
    {
        public Vector3 center, up;
        public Vector2 size;
        public float markSize, error;
    }

    // The four corner marks on the surface: sightings placed on the plane, gathered into marks, and the four that
    // make the best rectangle.
    bool TryFindPanel(out PanelFit fit)
    {
        fit = default;
        if (!m_HasPlane || !m_MarksChanged)
            return false;
        m_MarksChanged = false;

        var clusters = new List<(Vector3 sum, int count, List<float> sizes, List<Vector3> points)>();
        foreach (var (origin, direction, angle, _) in m_MarkSightings)
        {
            float toward = Vector3.Dot(direction, m_PlaneNormal);
            if (toward > -0.15f)
                continue; // along the surface or from behind it
            float t = Vector3.Dot(m_PlanePoint - origin, m_PlaneNormal) / toward;
            if (t < 0.1f || t > 5f)
                continue;
            var p = origin + direction * t;
            float size = angle * t;
            if (size < m_MarkSize.x || size > m_MarkSize.y)
                continue;
            int best = -1;
            float nearest = m_MarkCluster;
            for (int i = 0; i < clusters.Count; i++)
            {
                float d = Vector3.Distance(clusters[i].sum / clusters[i].count, p);
                if (d < nearest)
                {
                    nearest = d;
                    best = i;
                }
            }

            if (best < 0)
            {
                clusters.Add((p, 1, new List<float> { size }, new List<Vector3> { p }));
            }
            else
            {
                var c = clusters[best];
                c.sizes.Add(size);
                c.points.Add(p);
                clusters[best] = (c.sum + p, c.count + 1, c.sizes, c.points);
            }
        }

        var marks = new List<(Vector3 point, float size, int count)>();
        foreach (var c in clusters)
        {
            if (c.count < m_MinMarkSightings)
                continue;
            var mean = c.sum / c.count;
            float rms = 0f;
            foreach (var p in c.points)
                rms += (p - mean).sqrMagnitude;
            if (Mathf.Sqrt(rms / c.count) > 0.015f)
                continue;
            c.sizes.Sort();
            marks.Add((mean, c.sizes[c.sizes.Count / 2], c.count));
        }

        m_MarksFound = marks.Count;
        if (marks.Count < 4)
            return false;
        marks.Sort((a, b) => b.count.CompareTo(a.count));
        if (marks.Count > 10)
            marks.RemoveRange(10, marks.Count - 10);

        // The in-surface directions: up is the world's up on an upright surface, away from the head on a flat one.
        var head = Camera.main != null ? Camera.main.transform.position : m_PlanePoint + m_PlaneNormal;
        var up = Mathf.Abs(m_PlaneNormal.y) < 0.7f
            ? Vector3.ProjectOnPlane(Vector3.up, m_PlaneNormal).normalized
            : Vector3.ProjectOnPlane(m_PlanePoint - head, m_PlaneNormal).normalized;
        var right = Vector3.Cross(up, -m_PlaneNormal);

        float bestError = 0.35f;
        bool any = false;
        var picked = new int[4];
        int n = marks.Count;
        for (int a = 0; a < n; a++)
        for (int b = a + 1; b < n; b++)
        for (int c = b + 1; c < n; c++)
        for (int d = c + 1; d < n; d++)
        {
            var four = new[] { a, b, c, d };
            var ordered = Order(four, marks, right, up);
            float error = RectangleError(ordered, marks);
            if (error < bestError)
            {
                bestError = error;
                any = true;
                ordered.CopyTo(picked, 0);
            }
        }

        if (!any)
            return false;

        Vector3 topLeft = marks[picked[0]].point, topRight = marks[picked[1]].point, bottomRight = marks[picked[2]].point, bottomLeft = marks[picked[3]].point;
        var across = ((topRight - topLeft) + (bottomRight - bottomLeft)).normalized;
        var upward = ((topLeft - bottomLeft) + (topRight - bottomRight));
        upward = (upward - across * Vector3.Dot(upward, across)).normalized;
        float markSize = 0f;
        foreach (int i in picked)
            markSize += marks[i].size * 0.25f;
        fit = new PanelFit
        {
            center = (topLeft + topRight + bottomRight + bottomLeft) * 0.25f,
            up = upward,
            size = new Vector2(
                0.5f * (Vector3.Distance(topLeft, topRight) + Vector3.Distance(bottomLeft, bottomRight)) + markSize,
                0.5f * (Vector3.Distance(topLeft, bottomLeft) + Vector3.Distance(topRight, bottomRight)) + markSize),
            markSize = markSize,
            error = bestError,
        };
        Log($"corner marks at {topLeft:F3} {topRight:F3} {bottomRight:F3} {bottomLeft:F3} (seen {marks[picked[0]].count}/{marks[picked[1]].count}/{marks[picked[2]].count}/{marks[picked[3]].count} times), " +
            $"marks {markSize * 100f:0.0} cm, rectangle error {bestError:0.00}, {marks.Count} marks in all");
        return true;
    }

    // Four marks in order: top-left, top-right, bottom-right, bottom-left.
    static int[] Order(int[] four, List<(Vector3 point, float size, int count)> marks, Vector3 right, Vector3 up)
    {
        var center = Vector3.zero;
        foreach (int i in four)
            center += marks[i].point * 0.25f;
        var byAngle = new List<(int index, float angle)>();
        foreach (int i in four)
        {
            var offset = marks[i].point - center;
            byAngle.Add((i, Mathf.Atan2(Vector3.Dot(offset, up), Vector3.Dot(offset, right)) * Mathf.Rad2Deg));
        }

        byAngle.Sort((a, b) => b.angle.CompareTo(a.angle)); // turning clockwise, as seen
        float[] wanted = { 135f, 45f, -45f, -135f };
        int bestShift = 0;
        float bestMiss = float.MaxValue;
        for (int shift = 0; shift < 4; shift++)
        {
            float miss = 0f;
            for (int k = 0; k < 4; k++)
                miss += Mathf.Abs(Mathf.DeltaAngle(byAngle[(k + shift) % 4].angle, wanted[k]));
            if (miss < bestMiss)
            {
                bestMiss = miss;
                bestShift = shift;
            }
        }

        var ordered = new int[4];
        for (int k = 0; k < 4; k++)
            ordered[k] = byAngle[(k + bestShift) % 4].index;
        return ordered;
    }

    // How far four marks (in order round) are from the corners of a rectangle, with marks of one size; large if
    // it's no rectangle at all.
    float RectangleError(int[] ordered, List<(Vector3 point, float size, int count)> marks)
    {
        var p = new Vector3[4];
        for (int k = 0; k < 4; k++)
            p[k] = marks[ordered[k]].point;
        var sides = new float[4];
        float error = 0f;
        for (int k = 0; k < 4; k++)
        {
            sides[k] = Vector3.Distance(p[k], p[(k + 1) % 4]);
            if (sides[k] < m_MinPanelSide)
                return float.MaxValue;
            var before = (p[(k + 3) % 4] - p[k]).normalized;
            var after = (p[(k + 1) % 4] - p[k]).normalized;
            float corner = Mathf.Abs(Vector3.Dot(before, after));
            if (corner > 0.15f)
                return float.MaxValue;
            error += corner;
        }

        float a = Mathf.Abs(sides[0] - sides[2]) / Mathf.Max(sides[0], sides[2]);
        float b = Mathf.Abs(sides[1] - sides[3]) / Mathf.Max(sides[1], sides[3]);
        if (a > 0.12f || b > 0.12f)
            return float.MaxValue;
        float smallest = float.MaxValue, largest = 0f;
        foreach (int i in ordered)
        {
            smallest = Mathf.Min(smallest, marks[i].size);
            largest = Mathf.Max(largest, marks[i].size);
        }

        if (largest > 1.6f * smallest)
            return float.MaxValue;
        return error + a + b + 0.5f * (largest / smallest - 1f);
    }

    // After the three touches: the panel if its marks are seen, otherwise a tunnel wall on the surface, if there is
    // one (a panel found later still takes over).
    void ChooseCanvas(TunnelMarker marker)
    {
        if (TryFindPanel(out var panel))
        {
            UsePanel(marker, panel, "found its corner marks");
            return;
        }

        float waited = Time.unscaledTime - m_CanvasSince;
        if (waited >= 1.5f)
        {
            var wall = MatchTunnelWall(out string where);
            if (wall != null)
            {
                UseCanvas(marker, wall, CanvasKind.TunnelWall);
                Log($"canvas: the tunnel wall {where}");
                SetStatus("Painting on the tunnel wall. (At a panel, look at its four corner marks to paint on the panel instead.)");
                PlayerHudNotification.Instance?.ShowText("Real marker: painting on the tunnel wall", 2f);
                return;
            }
        }

        if (waited > m_CanvasTimeout)
        {
            Stop($"Couldn't find the panel's four corner marks ({m_MarksFound} seen) or a tunnel wall on the touched surface. Turn on Real marker to try again.");
            return;
        }

        SetStatus($"Surface set. Look at the panel's four corner marks (seen {m_MarksFound} of 4); step back if they don't all fit in view.");
    }

    void UsePanel(TunnelMarker marker, PanelFit panel, string why)
    {
        if (m_Board == null)
        {
            var go = new GameObject("Canvas Panel");
            m_Board = go.AddComponent<WhiteboardSurface>();
            m_Board.Place(panel.center, m_PlaneNormal, panel.up, panel.size);
            m_BoardPaint = go.AddComponent<TunnelSegmentPaint>();
            m_BoardPaint.UseSurface(m_Board, m_Overlay, m_Brush, m_PanelTexel);
        }
        else
        {
            m_Board.Place(panel.center, m_PlaneNormal, panel.up, panel.size);
        }

        UseCanvas(marker, m_BoardPaint, CanvasKind.Panel);
        Log($"canvas: the panel ({why}), {panel.size.x * 100f:0.0} x {panel.size.y * 100f:0.0} cm, centre {panel.center:F3}");
        SetStatus($"Canvas found: {panel.size.x * 100f:0} x {panel.size.y * 100f:0} cm. Paint on it with the marker; pinch the disk at its tip to size the brush.");
        PlayerHudNotification.Instance?.ShowText("Canvas found", 2f);
    }

    void UseCanvas(TunnelMarker marker, TunnelSegmentPaint canvas, CanvasKind kind)
    {
        m_Canvas = canvas;
        canvasKind = kind;
        marker.SetRealCanvas(canvas);
        if (stage != Stage.Tracking)
        {
            stage = Stage.Tracking;
            m_Touching = false;
            m_HeightCount = 0;
            ClearPoints();
        }
    }

    // A flat tunnel wall the touched surface lies on (or near): the nearest within the angle and distance allowed.
    TunnelSegmentPaint MatchTunnelWall(out string where)
    {
        where = null;
        TunnelSegmentPaint best = null;
        float nearest = m_WallDistance;
        foreach (var paint in TunnelSegmentPaint.all)
        {
            var segment = paint != null ? paint.geometry : null;
            if (segment == null || !segment.isActiveAndEnabled || !paint.paintable)
                continue;
            var frame = segment.transform;
            var local = frame.InverseTransformPoint(m_PlanePoint);
            if (Mathf.Abs(local.z) > segment.length * 0.5f + 0.05f || local.y < -0.05f || local.y > segment.wallHeight + 0.05f)
                continue;
            float side = local.x < 0f ? -1f : 1f;
            float gap = Mathf.Abs(segment.width * 0.5f - Mathf.Abs(local.x));
            var inward = frame.TransformDirection(Vector3.right * -side);
            float angle = Vector3.Angle(m_PlaneNormal, inward);
            if (gap >= nearest || angle > m_WallAngle)
                continue;
            nearest = gap;
            best = paint;
            where = $"{(side < 0 ? "-X" : "+X")} of {segment.name} ({gap * 100f:0.0} cm and {angle:0}° from the touched surface)";
        }

        return best;
    }

    #endregion

    #region Painting

    // A new sighting while painting: where the tip's ray meets the surface, how high the tip is, touching or not
    // (with hysteresis), and the marker paints from it.
    void Follow(TunnelMarker marker, Sighting sighting)
    {
        var ray = sighting.tipRay;
        float toward = Vector3.Dot(ray.direction, m_PlaneNormal);
        if (toward > -0.05f || m_Canvas == null)
            return; // looking along the surface or away from it
        float along = Vector3.Dot(m_PlanePoint - ray.origin, m_PlaneNormal) / toward;
        if (along <= 0f)
            return;
        var onPlane = ray.origin + ray.direction * along;
        float height = SmoothHeight(Vector3.Dot(sighting.tip - m_PlanePoint, m_PlaneNormal));

        bool onCanvas = m_Canvas.surface.ToPaintCanvas(onPlane, out var uv, out _, out var onSurface) &&
            (canvasKind == CanvasKind.Panel || (uv.x >= 0f && uv.x <= 1f));
        if (!onCanvas)
        {
            onSurface = onPlane;
            m_Touching = false;
        }
        else if (!m_Touching && height < m_TouchDown)
        {
            m_Touching = true;
        }
        else if (m_Touching && height > m_TouchUp)
        {
            m_Touching = false;
        }

        var tip = m_Touching ? onSurface : onSurface + m_PlaneNormal * Mathf.Max(height, 0f);
        marker.SetRealTip(true, tip, -m_PlaneNormal, m_Touching);
        ShowDisk(true, tip, m_PlaneNormal, marker);

        if (Time.unscaledTime >= m_NextStatus)
        {
            m_NextStatus = Time.unscaledTime + 0.25f;
            string where = canvasKind == CanvasKind.Panel ? "the canvas" : "the tunnel wall";
            status = onCanvas
                ? (m_Touching ? $"Real marker: touching {where}" : $"Real marker: {height * 100f:0.0} cm off {where}")
                : $"Real marker: off {where}";
        }
    }

    // The median of the last three heights: one bad image doesn't lift or drop the tip.
    float SmoothHeight(float height)
    {
        m_Heights[m_HeightCount % 3] = height;
        m_HeightCount++;
        if (m_HeightCount < 3)
            return height;
        float a = m_Heights[0], b = m_Heights[1], c = m_Heights[2];
        return Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));
    }

    #endregion

    #region The disk at the tip, and sizing the brush

    void ShowDisk(bool show, Vector3 tip = default, Vector3 normal = default, TunnelMarker marker = null)
    {
        m_TipShown = show;
        if (!show)
        {
            if (m_Disk != null)
                m_Disk.gameObject.SetActive(false);
            return;
        }

        if (m_Disk == null)
        {
            m_Disk = new GameObject("Real Marker Disk").transform;
            var face = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            face.name = "Face";
            Destroy(face.GetComponent<Collider>());
            face.transform.SetParent(m_Disk, false);
            face.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // its round face along the disk's forward
            m_DiskFace = face.transform;
            m_DiskMaterial = UnlitMaterial("Real Marker Disk", Color.white);
            face.GetComponent<MeshRenderer>().sharedMaterial = m_DiskMaterial;

            var ring = new GameObject("Ring");
            ring.transform.SetParent(m_Disk, false);
            m_DiskRing = ring.AddComponent<LineRenderer>();
            m_DiskRing.useWorldSpace = false;
            m_DiskRing.loop = true;
            m_DiskRing.positionCount = 32;
            m_DiskRing.widthMultiplier = 0.0012f;
            m_DiskRing.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_RingMaterial = UnlitMaterial("Real Marker Disk Ring", new Color(0.08f, 0.08f, 0.09f));
            m_DiskRing.sharedMaterial = m_RingMaterial;
        }

        marker ??= Marker();
        float diameter = marker != null ? marker.realDiameter : 0.006f;
        Color colour = marker != null ? (Color)marker.color : Color.white;
        colour.a = 1f;
        m_DiskMaterial.SetColor("_BaseColor", colour);
        m_DiskMaterial.color = colour;

        m_Disk.gameObject.SetActive(true);
        m_DiskNormal = normal.sqrMagnitude > 0f ? normal.normalized : Vector3.up;
        m_Disk.SetPositionAndRotation(tip + m_DiskNormal * 0.002f, Quaternion.LookRotation(-m_DiskNormal));
        m_DiskFace.localScale = new Vector3(diameter, 0.0004f, diameter);
        for (int i = 0; i < 32; i++)
        {
            float a = i / 32f * Mathf.PI * 2f;
            float r = diameter * 0.5f + 0.0006f;
            m_DiskRing.SetPosition(i, new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, -0.0006f));
        }
    }

    // The other hand pinches the disk and moves away (or back) to size the brush, until the pinch opens.
    void SizeBrush()
    {
        var marker = Marker();
        if (marker == null || m_Disk == null || !m_TipShown)
        {
            m_Pinching = false;
            return;
        }

        var center = m_Disk.position;
        if (m_Pinching)
        {
            if (!Pinch(m_PinchHand, out var point, out float gap) || gap > m_PinchOpen)
            {
                m_Pinching = false;
                Log($"brush size {marker.realDiameter * 1000f:0.0} mm");
                PlayerHudNotification.Instance?.ShowText($"Brush {marker.realDiameter * 1000f:0} mm", 1.5f);
                return;
            }

            float distance = Vector3.Distance(ProjectOnDisk(point), center);
            marker.realDiameter = m_PinchStartDiameter + 2f * (distance - m_PinchStartDistance);
            return;
        }

        foreach (var hand in new[] { Handedness.Left, Handedness.Right })
        {
            if (hand == m_HoldingHand || !Pinch(hand, out var point, out float gap) || gap > m_PinchClose)
                continue;
            float distance = Vector3.Distance(ProjectOnDisk(point), center);
            if (distance > marker.realDiameter * 0.5f + m_PinchReach || Mathf.Abs(Vector3.Dot(point - center, m_DiskNormal)) > m_PinchReach)
                continue;
            m_Pinching = true;
            m_PinchHand = hand;
            m_PinchStartDistance = distance;
            m_PinchStartDiameter = marker.realDiameter;
            PlayerHudNotification.Instance?.ShowText("Sizing the brush: move away to enlarge, back to shrink, open to set", 2f);
            return;
        }
    }

    Vector3 ProjectOnDisk(Vector3 point) => point - m_DiskNormal * Vector3.Dot(point - m_Disk.position, m_DiskNormal);

    static bool Pinch(Handedness hand, out Vector3 point, out float gap)
    {
        point = default;
        gap = float.MaxValue;
        if (!HandPalm.TryGetJoint(hand, XRHandJointID.ThumbTip, out var thumb) || !HandPalm.TryGetJoint(hand, XRHandJointID.IndexTip, out var index))
            return false;
        point = (thumb + index) * 0.5f;
        gap = Vector3.Distance(thumb, index);
        return true;
    }

    #endregion

    static Material UnlitMaterial(string name, Color colour)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var material = new Material(shader != null ? shader : Shader.Find("Sprites/Default")) { name = name };
        material.SetColor("_BaseColor", colour);
        material.color = colour;
        return material;
    }

    static AudioClip Tone(string clipName, float frequency, float seconds)
    {
        const int rate = 44100;
        int count = Mathf.CeilToInt(rate * seconds);
        var data = new float[count];
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)rate;
            float envelope = Mathf.Min(1f, t * 200f) * Mathf.Clamp01((seconds - t) * 30f);
            data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.6f;
        }

        var clip = AudioClip.Create(clipName, count, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // Waits for the next camera image.
    IEnumerator NextFrame()
    {
        do
            yield return null;
        while (!m_Camera.hasNewFrame && m_Camera.isPlaying);
    }

    static void Log(string message)
    {
        Debug.Log("[RealMarker] " + message);
        try
        {
            s_File?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
            s_File?.Flush();
        }
        catch (Exception)
        {
            s_File = null;
        }
    }

    // A fresh log file and snapshot folder for this run, on the headset.
    static void OpenLog()
    {
        try
        {
            s_Folder = System.IO.Path.Combine(Application.persistentDataPath, "RealMarker");
            if (System.IO.Directory.Exists(s_Folder))
                foreach (var file in System.IO.Directory.GetFiles(s_Folder))
                    System.IO.File.Delete(file);
            System.IO.Directory.CreateDirectory(s_Folder);
            s_File?.Dispose();
            s_File = new System.IO.StreamWriter(System.IO.Path.Combine(s_Folder, "log.txt"), false);
            Log($"run started {DateTime.Now:yyyy-MM-dd HH:mm:ss}, version {Application.version}");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RealMarker] no log file: " + e.Message);
            s_File = null;
        }
    }

    // Saves the camera image as a JPEG with what was found drawn on it: the cap's ends (tip green, back red), the
    // corner marks seen (yellow), the hands' joints (magenta) and the placed points (cyan), as this headset maps
    // them into the image. Files are numbered in order.
    void Snapshot(string tag, Color32[] pixels, int w, int h, Blob blob, Pose cameraPose)
    {
        if (s_Folder == null || pixels == null || m_Snapshots >= m_MaxSnapshots)
            return;
        try
        {
            var image = (Color32[])pixels.Clone();
            void Dot(Vector2 p, int radius, Color32 colour)
            {
                for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = (int)p.x + dx, y = (int)p.y + dy;
                    if (x >= 0 && y >= 0 && x < w && y < h && (Mathf.Abs(dx) <= 1 || Mathf.Abs(dy) <= 1))
                        image[y * w + x] = colour;
                }
            }

            // The image's own colours come with red and blue swapped; draw in the same order.
            Color32 C(byte r, byte g, byte b) => m_SwapRedBlue ? new Color32(b, g, r, 255) : new Color32(r, g, b, 255);
            if (blob.found)
            {
                Dot(blob.End(0), 7, Vector2.Distance(blob.End(0), m_TipPixel) < 1f ? C(0, 255, 0) : C(255, 0, 0));
                Dot(blob.End(1), 7, Vector2.Distance(blob.End(1), m_TipPixel) < 1f ? C(0, 255, 0) : C(255, 0, 0));
            }
            if (m_LastMarks != null)
                foreach (var mark in m_LastMarks)
                {
                    float r = mark.size * 0.5f;
                    for (float t = -r; t <= r; t += 1f)
                    {
                        Dot(mark.center + new Vector2(t, r), 0, C(255, 230, 0));
                        Dot(mark.center + new Vector2(t, -r), 0, C(255, 230, 0));
                        Dot(mark.center + new Vector2(r, t), 0, C(255, 230, 0));
                        Dot(mark.center + new Vector2(-r, t), 0, C(255, 230, 0));
                    }
                }
            foreach (var hand in new[] { Handedness.Left, Handedness.Right })
                foreach (var joint in new[] { XRHandJointID.Palm, XRHandJointID.IndexTip, XRHandJointID.ThumbTip })
                    if (HandPalm.TryGetJoint(hand, joint, out var p) && m_Camera.WorldToPixel(p, cameraPose, m_FlipX, m_FlipY, out var pixel))
                        Dot(pixel, 5, C(255, 0, 255));
            foreach (var point in m_Points)
                if (m_Camera.WorldToPixel(point, cameraPose, m_FlipX, m_FlipY, out var pixel))
                    Dot(pixel, 9, C(0, 220, 255));

            if (m_SnapTexture == null || m_SnapTexture.width != w || m_SnapTexture.height != h)
                m_SnapTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            m_SnapTexture.SetPixels32(image);
            m_SnapTexture.Apply(false);
            var bytes = m_SnapTexture.EncodeToJPG(80);
            string file = $"{m_Snapshots:00}_{tag}.jpg";
            m_Snapshots++;
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(s_Folder, file), bytes);
            Log($"snapshot {file}: cap {(blob.found ? $"{blob.center:F0}, {blob.length:0} x {blob.width:0.0} px" : "not seen")}, {m_LastMarks?.Count ?? 0} marks in view, head {cameraPose.position:F2}");
        }
        catch (Exception e)
        {
            Log("snapshot failed: " + e.Message);
        }
    }
}
