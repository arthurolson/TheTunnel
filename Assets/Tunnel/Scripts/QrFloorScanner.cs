using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using ZXing;
using ZXing.Common;

/// <summary>
/// Finds the tunnel's QR code on the floor with both passthrough cameras and measures where it lies: the point at its
/// centre and the direction its top edge faces, in this headset's world. It reads the cameras the same way the AR
/// marker does (<see cref="StereoPassthroughCamera"/>: synchronized pairs, each camera's lens, each camera's pose at
/// the moment the images were taken), decodes each camera's image with ZXing on worker threads, and places the code
/// from each camera on its own: the rays through its three corner squares are dropped onto a level floor at the
/// height where the squares come out at their printed spacing (so the floor's height is measured, not assumed). Where
/// both cameras see the code their two placements are compared (logged) and averaged. Several sightings are averaged,
/// leaving out stray ones. Every headset measures the same printed code, so their measurements can be lined up
/// (<see cref="CoLocation"/>). While it scans, AR marker tracking pauses and gets the cameras back after.
/// </summary>
public class QrFloorScanner : MonoBehaviour
{
    /// <summary>Only QR codes whose text starts with this are used, so other codes in the room are ignored.</summary>
    public const string CodePrefix = "TheTunnel";

    [SerializeField]
    [Tooltip("Sightings averaged into one measurement.")]
    int m_SamplesNeeded = 12;

    [SerializeField]
    [Tooltip("Give up after this many seconds.")]
    float m_Timeout = 40f;

    [SerializeField]
    [Tooltip("Sightings further than this from the average (meters) are left out.")]
    float m_OutlierDistance = 0.08f;

    [SerializeField]
    [Tooltip("Distance between the centres of two corner squares on the printed code (meters): 14 of its 25 modules " +
        "for the 19 cm print in Printables/.")]
    float m_FinderSpacing = 0.1064f;

    [SerializeField]
    [Tooltip("Placements whose corner squares are this far from a right-angled, equal-sided corner are rejected.")]
    float m_MaxSquarenessError = 0.2f;

    const string k_Owner = "floor QR alignment";

    public struct Measurement
    {
        public Vector3 position;
        /// <summary>Compass direction (degrees about world up) of the code's top edge.</summary>
        public float yaw;
        public int samples;
        public float spread;
    }

    struct Decoded
    {
        public string text;
        public Vector2[] points; // the three corner finder centres, in image pixels from the top left
    }

    struct Placement
    {
        public Vector3 position, forward;
        public float floorY;
    }

    bool m_Scanning;

    public bool isScanning => m_Scanning;

    /// <summary>The code was just seen here (centre on the floor, yaw of its top edge), for on-floor feedback.</summary>
    public event Action<Vector3, float> sighted;

    /// <summary>Scanning ended with this measurement (null if it failed).</summary>
    public event Action<Measurement?> finished;

    public void Scan(Action<string> status, Action<Measurement?> done)
    {
        if (!m_Scanning)
            StartCoroutine(ScanRoutine(status, done));
    }

    IEnumerator ScanRoutine(Action<string> status, Action<Measurement?> done)
    {
        m_Scanning = true;

        // AR marker tracking gives the cameras up while the code is scanned, and gets them back after.
        var tracker = ArMarkerTracker.instance;
        bool resumeTracking = tracker != null && tracker.active;
        if (resumeTracking)
        {
            Log("pausing AR marker tracking while scanning");
            tracker.End();
        }

        var camera = StereoPassthroughCamera.Get();
        float wait = Time.unscaledTime + 3f;
        while (camera.owner != null && Time.unscaledTime < wait)
            yield return null;
        status("Starting the cameras...");
        yield return camera.Open(k_Owner, status, Log);
        if (!camera.isOpen)
        {
            Log("cameras didn't open: " + camera.error);
            Finish(status, done, null, camera.error ?? "The cameras didn't start.", resumeTracking);
            yield break;
        }

        // The right camera's small turn learnt by the AR marker (files/ARMarker/stereo-offset.txt), if any.
        var fusion = new ArMarkerFusion();
        bool learnt = fusion.Load(Path.Combine(Application.persistentDataPath, "ARMarker", "stereo-offset.txt"));
        var rightOnLeft = new Pose(Vector3.zero, fusion.offsetRotation);
        Log($"scanning with both cameras: {camera.leftModel.width}x{camera.leftModel.height}, right camera {(learnt ? fusion.ToString() : "as calibrated (no learnt turn yet)")}");

        var left = new byte[camera.leftModel.width * camera.leftModel.height];
        var right = new byte[camera.rightModel.width * camera.rightModel.height];
        var readers = new[] { MakeReader(), MakeReader() };
        var placements = new List<Placement>();
        int pairs = 0, seenL = 0, seenR = 0, seenBoth = 0, otherCodes = 0, unplaced = 0;
        float deadline = Time.unscaledTime + m_Timeout;
        status("Look down at the QR code on the floor and hold still...");

        while (Time.unscaledTime < deadline && placements.Count < m_SamplesNeeded)
        {
            if (camera.TryAcquire(true, left, right, out var pair) != StereoPassthroughCamera.Result.New)
            {
                yield return null;
                continue;
            }
            pairs++;
            var lm = camera.leftModel;
            var rm = camera.rightModel;
            var taskL = Task.Run(() => Decode(readers[0], left, lm.width, lm.height));
            var taskR = Task.Run(() => Decode(readers[1], right, rm.width, rm.height));
            while (!taskL.IsCompleted || !taskR.IsCompleted)
                yield return null;

            var decodedL = Ours(taskL.Result, ref otherCodes);
            var decodedR = Ours(taskR.Result, ref otherCodes);
            if (pairs == 1 || (pairs % 20 == 0 && placements.Count == 0))
                Log($"pair {pairs}: left {(decodedL != null ? "code" : "-")}, right {(decodedR != null ? "code" : "-")}, mean brightness {MeanBrightness(left):0}");
            if (decodedL == null && decodedR == null)
                continue;

            // The right camera in the world, with the learnt turn about the left camera.
            var rightWorld = ArMarkerMath.Mul(pair.leftWorld, ArMarkerMath.Mul(rightOnLeft, ArMarkerMath.Inverse(camera.rightFromLeft)));
            string whyL = null, whyR = null;
            Placement pl = default, pr = default;
            bool placedL = decodedL != null && TryPlace(decodedL.Value, lm, pair.leftWorld, out pl, out whyL);
            bool placedR = decodedR != null && TryPlace(decodedR.Value, rm, rightWorld, out pr, out whyR);
            if (decodedL != null) seenL++;
            if (decodedR != null) seenR++;
            if (!placedL && !placedR)
            {
                unplaced++;
                if (unplaced <= 3)
                    Log($"pair {pairs}: seen but not placed (left: {whyL ?? "not seen"}; right: {whyR ?? "not seen"})");
                status("Seen the code but can't place it on the floor yet. Look more steeply down at it...");
                continue;
            }

            Placement placement;
            if (placedL && placedR)
            {
                seenBoth++;
                float apart = Vector3.Distance(pl.position, pr.position) * 100f;
                float turn = Vector3.SignedAngle(pl.forward, pr.forward, Vector3.up);
                Log($"sighting {placements.Count + 1}: both cameras; left and right placements {apart:0.0} cm and {turn:0.0} deg apart, " +
                    $"floor at {pl.floorY:0.000} / {pr.floorY:0.000} m; head {pair.headWorld.position:F2}");
                placement = new Placement
                {
                    position = (pl.position + pr.position) * 0.5f,
                    forward = (pl.forward + pr.forward).normalized,
                    floorY = (pl.floorY + pr.floorY) * 0.5f,
                };
            }
            else
            {
                placement = placedL ? pl : pr;
                Log($"sighting {placements.Count + 1}: {(placedL ? "left" : "right")} camera only, floor at {placement.floorY:0.000} m; head {pair.headWorld.position:F2}");
            }
            placements.Add(placement);
            sighted?.Invoke(placement.position, Mathf.Atan2(placement.forward.x, placement.forward.z) * Mathf.Rad2Deg);
            status($"Seen the code {placements.Count} times. Hold still...");
        }

        camera.Close(k_Owner);
        Log($"{pairs} pairs: code seen by the left camera {seenL} times, right {seenR}, placed by both {seenBoth}; " +
            $"{placements.Count} sightings, {unplaced} seen but not placed, {otherCodes} other codes");

        if (placements.Count < 3)
        {
            Finish(status, done, null, seenL + seenR == 0
                ? (otherCodes > 0
                    ? "Saw a QR code, but not the tunnel's. Use the code from Printables/TheTunnel-floor-QR.pdf."
                    : "Couldn't see the QR code. Stand next to it, look down at it from about 1 m, and try again.")
                : $"Saw the code but couldn't place it on the floor reliably. Hold still, look down at it, and try again.", resumeTracking);
            yield break;
        }

        var result = Average(placements, out float floor);
        float trackingFloor = TrackingFloor();
        Log($"measured {result.position:F3} yaw {result.yaw:0.0} from {result.samples} sightings, spread {result.spread * 100f:0.0} cm; " +
            $"floor measured at {floor:0.000} m (tracking floor {trackingFloor:0.000} m)");
        Finish(status, done, result, null, resumeTracking);
    }

    void Finish(Action<string> status, Action<Measurement?> done, Measurement? result, string failure, bool resumeTracking)
    {
        m_Scanning = false;
        if (failure != null)
            status(failure);
        if (resumeTracking)
            ArMarkerTracker.Get().Begin();
        finished?.Invoke(result);
        done(result);
    }

    static BarcodeReaderGeneric MakeReader() => new()
    {
        AutoRotate = false, // QR detection handles any rotation itself
        Options = new DecodingOptions { PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }, TryHarder = true },
    };

    /// <summary>Decodes a camera's luminance (for the Editor tests): the code's text and its three corner squares' centres.</summary>
    public static bool DecodeImage(byte[] luminance, int width, int height, out string text, out Vector2[] points)
    {
        var decoded = Decode(MakeReader(), luminance, width, height);
        text = decoded?.text;
        points = decoded?.points;
        return decoded != null;
    }

    static Decoded? Ours(Decoded? decoded, ref int otherCodes)
    {
        if (decoded == null)
            return null;
        if (decoded.Value.text.StartsWith(CodePrefix))
            return decoded;
        otherCodes++;
        return null;
    }

    /// <summary>Worker thread: ZXing on a camera's luminance (rows from the top, as ZXing reads images).</summary>
    static Decoded? Decode(BarcodeReaderGeneric reader, byte[] luminance, int w, int h)
    {
        Result result;
        try
        {
            result = reader.Decode(new RGBLuminanceSource(luminance, w, h, RGBLuminanceSource.BitmapFormat.Gray8));
        }
        catch (Exception)
        {
            return null;
        }
        if (result == null || result.Text == null || result.ResultPoints == null || result.ResultPoints.Length < 3)
            return null;
        var points = new Vector2[3];
        for (int i = 0; i < 3; i++)
            points[i] = new Vector2(result.ResultPoints[i].X, result.ResultPoints[i].Y);
        // ZXing's corner centres are found from run lengths along a few lines; the dark centre square of each corner
        // pattern, measured directly, puts them about twice as close to their true places.
        float module = ((points[0] - points[1]).magnitude + (points[1] - points[2]).magnitude) * 0.5f / 14f;
        for (int i = 0; i < 3; i++)
            points[i] = RefineCentre(luminance, w, h, points[i], module);
        return new Decoded { text = result.Text, points = points };
    }

    /// <summary>
    /// The centroid of a corner pattern's dark 3x3-module centre square around <paramref name="guess"/>: the dark
    /// pixels connected to the guess within two modules of it (the white ring around the square stops them), weighted
    /// by how dark they are.
    /// </summary>
    public static Vector2 RefineCentre(byte[] luminance, int w, int h, Vector2 guess, float module)
    {
        int radius = Mathf.CeilToInt(module * 2.2f);
        int cx = Mathf.RoundToInt(guess.x), cy = Mathf.RoundToInt(guess.y);
        if (module < 2f || cx - radius < 0 || cy - radius < 0 || cx + radius >= w || cy + radius >= h)
            return guess;
        // Threshold halfway between the darkest and lightest pixels around (the ring's white is within 2.5 modules).
        int outer = Mathf.CeilToInt(module * 2.6f);
        int min = 255, max = 0;
        for (int y = Mathf.Max(0, cy - outer); y <= Mathf.Min(h - 1, cy + outer); y++)
            for (int x = Mathf.Max(0, cx - outer); x <= Mathf.Min(w - 1, cx + outer); x++)
            {
                int v = luminance[y * w + x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        if (max - min < 40)
            return guess;
        int threshold = (min + max) / 2;
        int size = radius * 2 + 1;
        var visited = new bool[size * size];
        var stack = new Stack<(int x, int y)>();
        if (luminance[cy * w + cx] >= threshold)
            return guess;
        stack.Push((cx, cy));
        visited[radius * size + radius] = true;
        double sx = 0, sy = 0, sw = 0;
        int count = 0;
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            float weight = (threshold - luminance[y * w + x]) / (float)(threshold - min + 1);
            sx += x * weight;
            sy += y * weight;
            sw += weight;
            count++;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                int lx = nx - cx + radius, ly = ny - cy + radius;
                if (lx < 0 || ly < 0 || lx >= size || ly >= size || visited[ly * size + lx])
                    continue;
                visited[ly * size + lx] = true;
                if (luminance[ny * w + nx] < threshold)
                    stack.Push((nx, ny));
            }
        }
        // The flood must have stayed inside the centre square (about 9 square modules), not leaked into the ring.
        if (sw <= 0 || count > module * module * 14f || count < module * module * 4f)
            return guess;
        return new Vector2((float)(sx / sw) + 0.5f, (float)(sy / sw) + 0.5f);
    }

    /// <summary>
    /// The code placed on a level floor from one camera: each corner square's ray, scaled so the squares come out at
    /// their printed spacing; the floor's height follows.
    /// </summary>
    bool TryPlace(Decoded decoded, ArMarkerMath.CameraModel model, Pose cameraWorld, out Placement placement, out string why)
    {
        bool ok = PlaceOnFloor(decoded.points, model, cameraWorld, m_FinderSpacing, m_MaxSquarenessError, out var position, out var forward, out float floorY, out why);
        placement = new Placement { position = position, forward = forward, floorY = floorY };
        return ok;
    }

    /// <summary>
    /// Places the code from one camera (public for the Editor tests): the rays through its three corner squares
    /// (pixels from the top left) meet a level floor <c>k</c> metres below the camera at <c>camera + k * u</c>, where
    /// <c>u</c> is each ray scaled to drop one metre; <c>k</c> is chosen so the squares are the printed spacing apart.
    /// </summary>
    public static bool PlaceOnFloor(Vector2[] points, ArMarkerMath.CameraModel model, Pose cameraWorld, float spacing, float maxSquareness,
        out Vector3 position, out Vector3 forward, out float floorY, out string why)
    {
        position = forward = default;
        floorY = 0f;
        var u = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            var direction = cameraWorld.rotation * ArMarkerMath.PixelRay(model, points[i]);
            if (direction.y > -0.05f * direction.magnitude)
            {
                why = "a corner's ray doesn't point down at the floor";
                return false;
            }
            u[i] = direction / -direction.y;
        }
        OrderCorners(u);
        float sides = ((u[1] - u[0]).magnitude + (u[2] - u[1]).magnitude) * 0.5f;
        float squareness = SquarenessError(u[0], u[1], u[2]);
        if (sides < 1e-4f || squareness > maxSquareness)
        {
            why = $"not square on a level floor (error {squareness:0.00}): look more steeply down at it";
            return false;
        }
        float drop = spacing / sides;
        if (drop < 0.3f || drop > 3f)
        {
            why = $"the floor would be {drop:0.00} m below the camera";
            return false;
        }
        var bottomLeft = cameraWorld.position + u[0] * drop;
        var topLeft = cameraWorld.position + u[1] * drop;
        var topRight = cameraWorld.position + u[2] * drop;
        position = (bottomLeft + topRight) * 0.5f;
        forward = Vector3.ProjectOnPlane(topLeft - bottomLeft, Vector3.up).normalized;
        floorY = cameraWorld.position.y - drop;
        why = null;
        return forward.sqrMagnitude > 0.5f;
    }

    /// <summary>
    /// Sorts the three finder centres into bottom-left, top-left, top-right from where they lie on the floor: top-left
    /// is the corner between the other two, and seen from above, top-right lies to the right of the bottom-left-to-
    /// top-left direction.
    /// </summary>
    static void OrderCorners(Vector3[] p)
    {
        int corner = 0;
        float bestCos = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            Vector3 a = (p[(i + 1) % 3] - p[i]).normalized, b = (p[(i + 2) % 3] - p[i]).normalized;
            float cos = Mathf.Abs(Vector3.Dot(a, b));
            if (cos < bestCos)
            {
                bestCos = cos;
                corner = i;
            }
        }

        Vector3 topLeft = p[corner], bottomLeft = p[(corner + 1) % 3], topRight = p[(corner + 2) % 3];
        Vector3 up = topLeft - bottomLeft, right = topRight - topLeft;
        if (Vector3.Dot(Vector3.Cross(Vector3.up, up), right) < 0f)
            (bottomLeft, topRight) = (topRight, bottomLeft);
        p[0] = bottomLeft;
        p[1] = topLeft;
        p[2] = topRight;
    }

    /// <summary>0 for a perfect square corner with equal sides.</summary>
    static float SquarenessError(Vector3 bottomLeft, Vector3 topLeft, Vector3 topRight)
    {
        Vector3 a = bottomLeft - topLeft, b = topRight - topLeft;
        float la = a.magnitude, lb = b.magnitude;
        if (la < 1e-6f || lb < 1e-6f)
            return float.MaxValue;
        return Mathf.Abs(1f - Mathf.Min(la, lb) / Mathf.Max(la, lb)) + Mathf.Abs(Vector3.Dot(a / la, b / lb));
    }

    Measurement Average(List<Placement> samples, out float floor)
    {
        Vector3 Mean(List<Placement> set, out Vector3 forwardSum, out float floorSum)
        {
            Vector3 sum = Vector3.zero;
            forwardSum = Vector3.zero;
            floorSum = 0f;
            foreach (var s in set)
            {
                sum += s.position;
                forwardSum += s.forward;
                floorSum += s.floorY;
            }
            return set.Count > 0 ? sum / set.Count : Vector3.zero;
        }

        var mean = Mean(samples, out _, out _);
        var kept = samples.FindAll(s => (s.position - mean).magnitude <= m_OutlierDistance);
        if (kept.Count < 3)
            kept = samples;
        mean = Mean(kept, out var forwardTotal, out float floorTotal);
        floor = floorTotal / kept.Count;

        float spread = 0f;
        foreach (var s in kept)
            spread = Mathf.Max(spread, (s.position - mean).magnitude);

        return new Measurement
        {
            position = mean,
            yaw = Mathf.Atan2(forwardTotal.x, forwardTotal.z) * Mathf.Rad2Deg,
            samples = kept.Count,
            spread = spread,
        };
    }

    static float MeanBrightness(byte[] gray)
    {
        long sum = 0;
        for (int i = 0; i < gray.Length; i += 97)
            sum += gray[i];
        return sum / (float)((gray.Length + 96) / 97);
    }

    // The tracking space's floor level, for comparison with the measured floor.
    static float TrackingFloor()
    {
        var origin = FindAnyObjectByType<XROrigin>();
        return origin != null && origin.CameraFloorOffsetObject != null ? origin.CameraFloorOffsetObject.transform.position.y : float.NaN;
    }

    static void Log(string message) => Debug.Log("[QR] " + message);
}
