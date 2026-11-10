using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using ZXing;
using ZXing.Common;
using XRMultiplayer;

/// <summary>
/// Paints with a real marker, seen by the headset's passthrough camera, in place of the palette's virtual marker,
/// on a real whiteboard in place of the tunnel's walls (an experiment). Started by the palette's Real marker button:
///  1. Colour: the marker is held up with its coloured cap against something plain and white; the cap's colour
///     (the strongest saturated colour in the middle of the view, seen steadily) becomes the colour that's tracked.
///  2. Board: any QR code in the whiteboard's top-right corner. It is seen from several places as the head moves
///     side to side, and its three corner squares are triangulated, which gives where it is, its size and the board's
///     plane (set upright when nearly so, or onto a Space Setup wall it lies on). The board (3 x 2 ft) extends left
///     and down from the code's top-right corner; a thin outline shows it (<see cref="WhiteboardSurface"/>).
///  3. Touch: the cap is held on the board for a moment, which calibrates how far the cap is from its apparent width.
///  4. Tracking: every camera image, the cap is found (colour lookup, then the biggest patch near where it was), its
///     two ends turned into rays, the end nearer the board is the tip; where its ray meets the board is where it
///     paints, and its estimated height off the board (from the cap's width) says whether it touches, with some
///     hysteresis. The palette's marker paints from it (<see cref="TunnelMarker.SetRealTip"/>) with its own colour,
///     size and kind of mark, on the whiteboard only. A dot on the board shows where the tip is seen, green while
///     touching.
/// Each image is about 40 ms old when it arrives, so the head pose from then is used. Local to this headset.
/// </summary>
[DefaultExecutionOrder(-1100)] // before the marker paints
public class RealMarkerTracker : MonoBehaviour
{
    public enum Stage { Off, Colour, Board, Touch, Tracking }

    [Header("Whiteboard")]
    [SerializeField]
    Vector2 m_BoardSize = new(0.9144f, 0.6096f);

    [SerializeField]
    [Tooltip("How far the QR code's top-right corner is in from the board's (meters, left and down).")]
    Vector2 m_QrInset = Vector2.zero;

    [SerializeField]
    [Tooltip("Parallax needed to triangulate the QR code (degrees between the farthest views of it).")]
    float m_MinParallax = 5f;

    [SerializeField]
    int m_MinQrSightings = 20;

    [SerializeField]
    [Tooltip("A board within this angle of upright (degrees) is set exactly upright.")]
    float m_UprightSnap = 12f;

    [Header("Tracking")]
    [SerializeField]
    [Tooltip("How old a camera image is when it arrives (seconds): the head pose from then is used.")]
    float m_CameraLatency = 0.04f;

    [SerializeField]
    [Tooltip("The cap's diameter (meters), used until the touch calibration measures it.")]
    float m_CapDiameter = 0.019f;

    [SerializeField]
    [Tooltip("The tip touches the board when its estimated height drops below this (meters)...")]
    float m_TouchDown = 0.012f;

    [SerializeField]
    [Tooltip("...and stops touching when it rises above this.")]
    float m_TouchUp = 0.022f;

    [SerializeField]
    [Tooltip("Lost from view this long (seconds), the marker stops painting.")]
    float m_LostAfter = 0.2f;

    [SerializeField]
    [Tooltip("Smallest cap patch tracked (image pixels).")]
    int m_MinPixels = 120;

    const int k_Cell = 8;

    public static RealMarkerTracker instance { get; private set; }

    public Stage stage { get; private set; }
    public TunnelPalette palette { get; private set; }
    public Color32 capColour { get; private set; }
    public string status { get; private set; } = "";
    public WhiteboardSurface board => m_Board;

    PassthroughCameraReader m_Camera;
    Material m_Overlay;
    Shader m_Brush;
    WhiteboardSurface m_Board;
    Coroutine m_Run;

    ColourModel m_Model;
    float m_K; // the cap's apparent width (pixels) times its distance (meters)

    // Head poses, for the pose when an image was taken.
    readonly (float time, Vector3 position, Quaternion rotation)[] m_HeadHistory = new (float, Vector3, Quaternion)[64];
    int m_HeadCount, m_HeadNext;

    // Tracking.
    Blob m_LastBlob;
    float m_LastSeen = -10f;
    bool m_Touching;
    Vector2 m_TipPixel;
    readonly float[] m_Heights = new float[3];
    int m_HeightCount;
    Transform m_Dot;
    Material m_DotMaterial;
    float m_NextStatus;

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
            marker.SetReal(false);
        stage = Stage.Off;
        m_Touching = false;
        ShowDot(false, default, false);
        var scanner = FindAnyObjectByType<QrFloorScanner>();
        if (m_Camera != null && (scanner == null || !scanner.isScanning))
            m_Camera.Stop();
        SetStatus(message);
    }

    void Awake() => instance = this;

    void OnDestroy()
    {
        if (instance == this)
            instance = null;
        if (m_DotMaterial != null)
            Destroy(m_DotMaterial);
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
            Stop("The palette is gone: real marker off.");
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

        stage = Stage.Colour;
        m_LastBlob = default;
        m_LastSeen = -10f;
        m_HeightCount = 0;
        m_Touching = false;
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

        stage = Stage.Board;
        yield return FindBoard();
        if (stage != Stage.Board)
            yield break;

        stage = Stage.Touch;
        var marker = Marker();
        if (marker == null)
        {
            Stop("The palette has no marker.");
            yield break;
        }

        yield return CalibrateTouch();
        if (stage != Stage.Touch)
            yield break;

        stage = Stage.Tracking;
        marker.SetReal(true);
        SetStatus($"Real marker on: draw on the whiteboard with the capped marker. It paints with the marker's colour; select a disk to change it.");
        PlayerHudNotification.Instance?.ShowText("Real marker tracking", 2f);
        yield return Track(marker);
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
        public string why;
    }

    IEnumerator FindColour()
    {
        SetStatus("Hold the marker up in front of you with its coloured cap toward you, against something plain and white, and keep it still...");
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
            SetStatus($"Seeing a {Describe(seen.colour)} cap <color=#{ColorUtility.ToHtmlStringRGB(seen.colour)}>■</color>. Hold still...");
            if (steady.Count < 6)
                continue;

            // The steadiest view: the middle one by hue.
            steady.Sort((a, b) => a.model.hue.CompareTo(b.model.hue));
            var chosen = steady[steady.Count / 2];
            m_Model = chosen.model;
            capColour = chosen.colour;
            Log($"cap colour {capColour} hue {m_Model.hue:0} ±{m_Model.tolerance:0}, saturation ≥ {m_Model.minSaturation:0.00}, value ≥ {m_Model.minValue:0.00}, {chosen.pixels} pixels");
            PlayerHudNotification.Instance?.ShowText($"Tracking the {Describe(capColour)} cap", 2f);
            yield break;
        }

        Stop("Couldn't find a coloured cap. Use a marker with a bright cap (blue, green, red...), hold it in the middle of your view against white, and try again.");
    }

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
        };
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
            int cells = 0, weight = 0;
            float sx = 0f, sy = 0f;
            label[start] = next;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int cx = i % gw, cy = i / gw;
                cells++;
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

    #endregion

    #region The board

    IEnumerator FindBoard()
    {
        SetStatus("Now look at the QR code in the whiteboard's top-right corner, and slowly move your head side to side (about 20 cm)...");
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions { PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }, TryHarder = true },
        };

        var rays = new List<Ray>[3];
        for (int i = 0; i < 3; i++)
            rays[i] = new List<Ray>();
        byte[] gray = null;
        float deadline = Time.unscaledTime + 60f;
        float parallax = 0f;
        while (Time.unscaledTime < deadline)
        {
            yield return NextFrame();
            var head = HeadAtCapture();
            var pixels = m_Camera.GetPixels();
            if (pixels == null)
                continue;
            int w = m_Camera.width, h = m_Camera.height;
            if (gray == null || gray.Length != w * h)
                gray = new byte[w * h];
            var buffer = gray;
            var task = Task.Run(() => DecodeQr(reader, pixels, buffer, w, h));
            while (!task.IsCompleted)
                yield return null;
            var points = task.Result;
            if (points == null)
                continue;

            var cameraPose = m_Camera.GetCameraPose(head);
            for (int i = 0; i < 3; i++)
                rays[i].Add(m_Camera.PixelToRay(points[i], cameraPose, false, true));
            parallax = Parallax(rays[0]);
            int seen = rays[0].Count;
            if (seen >= m_MinQrSightings && parallax >= m_MinParallax)
                break;
            SetStatus(parallax < m_MinParallax
                ? $"Seen the QR code {seen} times. Keep moving your head slowly side to side ({parallax:0.0}° of {m_MinParallax:0}°)..."
                : $"Seen the QR code {seen} times. Hold on...");
        }

        if (rays[0].Count < m_MinQrSightings || parallax < m_MinParallax)
        {
            Stop(rays[0].Count == 0
                ? "Couldn't see a QR code. Stand about 1 m from the whiteboard, look at the code in its top-right corner and try again."
                : $"Saw the QR code but couldn't measure it (seen {rays[0].Count} times, {parallax:0.0}° of head movement). Move your head more side to side and try again.");
            yield break;
        }

        var corners = new Vector3[3];
        float worst = 0f;
        for (int i = 0; i < 3; i++)
        {
            corners[i] = Triangulate(rays[i], out float rms);
            worst = Mathf.Max(worst, rms);
        }

        if (!PlaceBoard(corners, worst, out string why))
        {
            Stop(why);
            yield break;
        }

        PlayerHudNotification.Instance?.ShowText("Whiteboard found", 2f);
    }

    // Worker thread: the QR code's three corner squares, in image pixels (x right, y down), the corner one first, the
    // other two in the same turning order every time.
    static Vector2[] DecodeQr(BarcodeReaderGeneric reader, Color32[] pixels, byte[] gray, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int src = y * w, dst = (h - 1 - y) * w;
            for (int x = 0; x < w; x++)
            {
                var c = pixels[src + x];
                gray[dst + x] = (byte)((c.r * 77 + c.g * 150 + c.b * 29) >> 8);
            }
        }

        Result result;
        try
        {
            result = reader.Decode(new RGBLuminanceSource(gray, w, h, RGBLuminanceSource.BitmapFormat.Gray8));
        }
        catch (Exception)
        {
            return null;
        }

        if (result?.ResultPoints == null || result.ResultPoints.Length < 3)
            return null;
        var p = new Vector2[3];
        for (int i = 0; i < 3; i++)
            p[i] = new Vector2(result.ResultPoints[i].X, result.ResultPoints[i].Y);

        int corner = 0;
        float bestCos = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            Vector2 a = (p[(i + 1) % 3] - p[i]).normalized, b = (p[(i + 2) % 3] - p[i]).normalized;
            float cos = Mathf.Abs(Vector2.Dot(a, b));
            if (cos < bestCos)
            {
                bestCos = cos;
                corner = i;
            }
        }

        Vector2 c0 = p[corner], c1 = p[(corner + 1) % 3], c2 = p[(corner + 2) % 3];
        var d1 = c1 - c0;
        var d2 = c2 - c0;
        if (d1.x * d2.y - d1.y * d2.x < 0f)
            (c1, c2) = (c2, c1);
        return new[] { c0, c1, c2 };
    }

    // The widest angle between two of the rays (degrees).
    static float Parallax(List<Ray> rays)
    {
        float widest = 0f;
        int step = Mathf.Max(1, rays.Count / 40);
        for (int i = 0; i < rays.Count; i += step)
        for (int j = i + step; j < rays.Count; j += step)
            widest = Mathf.Max(widest, Vector3.Angle(rays[i].direction, rays[j].direction));
        return widest;
    }

    // The point nearest all the rays (least squares), and the rays' RMS distance from it.
    static Vector3 Triangulate(List<Ray> rays, out float rms)
    {
        double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
        foreach (var ray in rays)
        {
            Vector3 d = ray.direction.normalized, o = ray.origin;
            double m00 = 1 - d.x * d.x, m01 = -d.x * d.y, m02 = -d.x * d.z, m11 = 1 - d.y * d.y, m12 = -d.y * d.z, m22 = 1 - d.z * d.z;
            a00 += m00; a01 += m01; a02 += m02; a11 += m11; a12 += m12; a22 += m22;
            b0 += m00 * o.x + m01 * o.y + m02 * o.z;
            b1 += m01 * o.x + m11 * o.y + m12 * o.z;
            b2 += m02 * o.x + m12 * o.y + m22 * o.z;
        }

        double det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
        var p = Vector3.zero;
        if (Math.Abs(det) > 1e-12)
        {
            double x = (b0 * (a11 * a22 - a12 * a12) - a01 * (b1 * a22 - a12 * b2) + a02 * (b1 * a12 - a11 * b2)) / det;
            double y = (a00 * (b1 * a22 - a12 * b2) - b0 * (a01 * a22 - a12 * a02) + a02 * (a01 * b2 - b1 * a02)) / det;
            double z = (a00 * (a11 * b2 - b1 * a12) - a01 * (a01 * b2 - b1 * a02) + b0 * (a01 * a12 - a11 * a02)) / det;
            p = new Vector3((float)x, (float)y, (float)z);
        }

        double sum = 0;
        foreach (var ray in rays)
        {
            var d = ray.direction.normalized;
            var v = p - ray.origin;
            sum += (v - d * Vector3.Dot(v, d)).sqrMagnitude;
        }

        rms = (float)Math.Sqrt(sum / Math.Max(1, rays.Count));
        return p;
    }

    bool PlaceBoard(Vector3[] corners, float rms, out string why)
    {
        why = null;
        Vector3 topLeft = corners[0], b = corners[1], c = corners[2];
        var center = (b + c) * 0.5f;
        var head = Camera.main.transform.position;
        // Seen from the front, the code's right (to its top-right square) crossed with its up points away from us.
        Vector3 topRight = b, bottomLeft = c;
        var facingAway = Vector3.Cross(topRight - topLeft, topLeft - bottomLeft);
        if (Vector3.Dot(facingAway, center - head) < 0f)
        {
            (topRight, bottomLeft) = (bottomLeft, topRight);
            facingAway = -facingAway;
        }

        float right = (topRight - topLeft).magnitude, up = (topLeft - bottomLeft).magnitude;
        float spacing = (right + up) * 0.5f;
        float squareness = Mathf.Abs(1f - Mathf.Min(right, up) / Mathf.Max(right, up)) +
            Mathf.Abs(Vector3.Dot((topRight - topLeft).normalized, (topLeft - bottomLeft).normalized));
        Log($"QR corner squares at {topLeft} {topRight} {bottomLeft}: {right * 100f:0.0} x {up * 100f:0.0} cm apart, squareness error {squareness:0.00}, ray RMS {rms * 1000f:0.0} mm");
        if (spacing < 0.02f || spacing > 0.6f || squareness > 0.3f || rms > 0.02f)
        {
            why = $"The QR code didn't measure up (corner squares {right * 100f:0.0} x {up * 100f:0.0} cm apart). Hold still more between movements and try again.";
            return false;
        }

        var forward = facingAway.normalized; // into the board
        string how = "from the QR code";
        if (Mathf.Abs(forward.y) < Mathf.Sin(m_UprightSnap * Mathf.Deg2Rad))
        {
            forward = new Vector3(forward.x, 0f, forward.z).normalized;
            how = "set upright";
        }

        // On a wall from Space Setup: the wall's plane is steadier than the code's three points.
        var planes = FindAnyObjectByType<ARPlaneManager>();
        if (planes != null)
        {
            foreach (var plane in planes.trackables)
            {
                if (plane.alignment != PlaneAlignment.Vertical)
                    continue;
                var wallNormal = plane.normal; // out of the wall
                float off = Vector3.Dot(center - plane.center, wallNormal);
                if (Vector3.Angle(-forward, wallNormal) < 12f && off > -0.03f && off < 0.08f)
                {
                    forward = -wallNormal;
                    how = $"on a Space Setup wall ({off * 100f:0.0} cm in front of it)";
                    break;
                }
            }
        }

        var boardUp = Mathf.Abs(forward.y) < 0.7f
            ? Vector3.ProjectOnPlane(Vector3.up, forward).normalized
            : Vector3.ProjectOnPlane(topLeft - bottomLeft, forward).normalized;
        var boardRight = Vector3.Cross(boardUp, forward);
        var onPlane = (topLeft + topRight + bottomLeft) / 3f;
        var qrCenter = center - forward * Vector3.Dot(center - onPlane, forward);
        // The code's outer corner: about 3.5 modules beyond its corner squares' centres (0.2 of their spacing).
        float half = spacing * 0.7f;
        var boardCorner = qrCenter + (boardRight + boardUp) * half + boardRight * m_QrInset.x + boardUp * m_QrInset.y;
        var boardCenter = boardCorner - boardRight * (m_BoardSize.x * 0.5f) - boardUp * (m_BoardSize.y * 0.5f);

        if (m_Board == null)
        {
            var go = new GameObject("Whiteboard");
            m_Board = go.AddComponent<WhiteboardSurface>();
            m_Board.Place(boardCenter, -forward, boardUp, m_BoardSize);
            var paint = go.AddComponent<TunnelSegmentPaint>();
            paint.UseSurface(m_Board, m_Overlay, m_Brush, 0.0015f);
        }
        else
        {
            m_Board.Place(boardCenter, -forward, boardUp, m_BoardSize);
        }

        Log($"whiteboard {m_BoardSize.x * 100f:0} x {m_BoardSize.y * 100f:0} cm, centre {boardCenter}, facing {-forward} ({how}); QR code about {spacing * 1.4f * 100f:0.0} cm wide");
        return true;
    }

    #endregion

    #region Touch calibration

    IEnumerator CalibrateTouch()
    {
        float f = m_Camera.imageFocalLength;
        m_K = f * m_CapDiameter;
        SetStatus("Touch the whiteboard with the marker's cap (keep the cap on) and hold it there for a moment...");
        var still = new List<(Vector3 point, float k, float time)>();
        float deadline = Time.unscaledTime + 30f;
        while (Time.unscaledTime < deadline)
        {
            yield return NextFrame();
            if (!See(out var sighting))
            {
                still.Clear();
                continue;
            }

            if (still.Count > 0 && Vector3.Distance(still[0].point, sighting.boardPoint) > 0.006f)
                still.Clear();
            // Its distance, if its tip is on the board, times its width.
            float k = Vector3.Distance(sighting.cameraPosition, sighting.boardPoint) * sighting.blob.width;
            still.Add((sighting.boardPoint, k, Time.unscaledTime));
            if (still.Count >= 10 && still[^1].time - still[0].time >= 0.7f)
            {
                var ks = still.ConvertAll(s => s.k);
                ks.Sort();
                float measured = ks[ks.Count / 2];
                float diameter = measured / f;
                if (diameter > 0.006f && diameter < 0.05f)
                {
                    m_K = measured;
                    Log($"touch calibrated: the cap looks {diameter * 1000f:0.0} mm wide ({ks.Count} images)");
                    PlayerHudNotification.Instance?.ShowText("Touch calibrated", 1.5f);
                }
                else
                {
                    Log($"touch calibration gave a {diameter * 1000f:0.0} mm cap; keeping {m_CapDiameter * 1000f:0} mm");
                }

                yield break;
            }

            SetStatus("Holding... keep the cap still on the board.");
        }

        Log($"touch calibration timed out; using a {m_CapDiameter * 1000f:0} mm cap");
    }

    #endregion

    #region Tracking

    struct Sighting
    {
        public Blob blob;
        public Vector3 cameraPosition;
        public Vector3 boardPoint; // where the tip's ray meets the board
        public float height;       // the tip's estimated height off the board (meters)
    }

    IEnumerator Track(TunnelMarker marker)
    {
        while (stage == Stage.Tracking)
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

                int minPixels = m_MinPixels;
                var task = Task.Run(() => FindBlob(pixels, w, h, model, region, near, minPixels));
                while (!task.IsCompleted)
                    yield return null;
                if (stage != Stage.Tracking)
                    yield break;
                seen = Measure(task.Result, head, out var sighting);
                if (seen)
                    Follow(marker, sighting);
            }

            if (!seen && Time.unscaledTime - m_LastSeen > m_LostAfter)
            {
                m_Touching = false;
                marker.SetRealTip(false, default, default, false);
                ShowDot(false, default, false);
                status = "Real marker: can't see the cap";
            }
        }
    }

    // A new sighting: touching or not (with hysteresis), and the marker paints from it.
    void Follow(TunnelMarker marker, Sighting sighting)
    {
        m_LastSeen = Time.unscaledTime;
        float height = SmoothHeight(sighting.height);
        bool onBoard = m_Board.ToPaintCanvas(sighting.boardPoint, out _, out _, out _);
        if (!onBoard)
            m_Touching = false;
        else if (!m_Touching && height < m_TouchDown)
            m_Touching = true;
        else if (m_Touching && height > m_TouchUp)
            m_Touching = false;

        var normal = m_Board.normal;
        var tip = m_Touching ? sighting.boardPoint : sighting.boardPoint + normal * Mathf.Max(height, 0f);
        marker.SetRealTip(true, tip, -normal, m_Touching);
        ShowDot(onBoard, sighting.boardPoint, m_Touching);

        if (Time.unscaledTime >= m_NextStatus)
        {
            m_NextStatus = Time.unscaledTime + 0.25f;
            status = onBoard
                ? (m_Touching ? "Real marker: touching the board" : $"Real marker: {height * 100f:0.0} cm off the board")
                : "Real marker: off the board";
        }
    }

    bool See(out Sighting sighting)
    {
        var pixels = m_Camera.GetPixels();
        sighting = default;
        if (pixels == null)
            return false;
        var blob = FindBlob(pixels, m_Camera.width, m_Camera.height, m_Model, new RectInt(0, 0, m_Camera.width, m_Camera.height), m_LastBlob.found ? m_LastBlob.center : (Vector2?)null, m_MinPixels);
        return Measure(blob, HeadAtCapture(), out sighting);
    }

    // Which end of the cap is its tip (the one nearer the board), where its ray meets the board, and how high it is.
    bool Measure(Blob blob, Pose head, out Sighting sighting)
    {
        sighting = default;
        if (!blob.found || blob.width < 2f || m_Board == null)
            return false;

        var cameraPose = m_Camera.GetCameraPose(head);
        var normal = m_Board.normal;
        var origin = m_Board.transform.position;
        float distance = m_K / blob.width;
        int tipEnd;
        var heights = new float[2];
        var rays = new Ray[2];
        for (int e = 0; e < 2; e++)
        {
            rays[e] = m_Camera.PixelToRay(blob.End(e), cameraPose, false, false);
            heights[e] = Vector3.Dot(rays[e].origin + rays[e].direction.normalized * distance - origin, normal);
        }

        if (Mathf.Abs(heights[0] - heights[1]) < 0.01f && m_LastBlob.found)
            tipEnd = Vector2.Distance(blob.End(0), m_TipPixel) <= Vector2.Distance(blob.End(1), m_TipPixel) ? 0 : 1;
        else
            tipEnd = heights[0] <= heights[1] ? 0 : 1;
        float best = heights[tipEnd];

        var ray = rays[tipEnd];
        float toward = Vector3.Dot(ray.direction, -normal);
        if (toward < 0.05f)
            return false; // looking along the board or away from it
        float along = Vector3.Dot(origin - ray.origin, -normal) / toward;
        if (along <= 0f)
            return false;

        m_LastBlob = blob;
        m_TipPixel = blob.End(tipEnd);
        sighting = new Sighting
        {
            blob = blob,
            cameraPosition = cameraPose.position,
            boardPoint = ray.origin + ray.direction * along,
            height = best,
        };
        return true;
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

    void ShowDot(bool show, Vector3 point, bool touching)
    {
        if (!show)
        {
            if (m_Dot != null)
                m_Dot.gameObject.SetActive(false);
            return;
        }

        if (m_Dot == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Real Marker Tip";
            Destroy(go.GetComponent<Collider>());
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            m_DotMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            go.GetComponent<MeshRenderer>().sharedMaterial = m_DotMaterial;
            go.transform.localScale = Vector3.one * 0.008f;
            m_Dot = go.transform;
        }

        m_Dot.gameObject.SetActive(true);
        m_Dot.position = point + m_Board.normal * 0.005f;
        var colour = touching ? new Color(0.2f, 1f, 0.35f) : new Color(1f, 1f, 1f, 0.8f);
        m_DotMaterial.SetColor("_BaseColor", colour);
        m_DotMaterial.color = colour;
    }

    #endregion

    // Waits for the next camera image.
    IEnumerator NextFrame()
    {
        do
            yield return null;
        while (!m_Camera.hasNewFrame && m_Camera.isPlaying);
    }

    static void Log(string message) => Debug.Log("[RealMarker] " + message);
}
