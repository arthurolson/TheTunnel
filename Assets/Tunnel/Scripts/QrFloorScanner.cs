using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ZXing;
using ZXing.Common;

/// <summary>
/// Finds the tunnel's QR code on the floor through a passthrough camera and measures where it lies: the point at its
/// centre and the direction its top edge faces, in this headset's world. Each camera image is decoded with ZXing on a
/// worker thread; the code's three corner finder patterns are turned into rays and dropped onto the floor plane.
/// Several sightings are averaged. Every headset measures the same printed code, so their measurements can be lined up.
/// The camera image's exact orientation isn't documented, so every sighting is placed under each possible
/// orientation; the right one is the one whose placements stay put as the head moves and come out at the printed size.
/// </summary>
public class QrFloorScanner : MonoBehaviour
{
    /// <summary>Only QR codes whose text starts with this are used, so other codes in the room are ignored.</summary>
    public const string CodePrefix = "TheTunnel";

    [SerializeField]
    PassthroughCameraReader m_Camera;

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
    float m_MaxSquarenessError = 0.35f;

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
        public Vector2[] points; // the three corner finder centres, in image pixels (y down)
        public int width, height;
    }

    /// <summary>One way of reading the camera image: mirrored or not along each axis, Android-to-Unity axis turn or not.</summary>
    readonly struct Orientation
    {
        public readonly bool flipX, flipY, flipAxes;

        public Orientation(int index)
        {
            flipX = (index & 1) != 0;
            flipY = (index & 2) != 0;
            flipAxes = (index & 4) == 0; // Meta's convention first
        }

        public override string ToString() => $"flipX={flipX} flipY={flipY} axisTurn={flipAxes}";
    }

    const int k_OrientationCount = 8;

    struct Placement
    {
        public Vector3 position, forward;
        public float size; // measured corner-square spacing (meters)
    }

    bool m_Scanning;
    byte[] m_Gray;
    int? m_KnownOrientation; // once found, later scans use it directly

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
        status("Starting the camera...");
        bool started = false;
        yield return m_Camera.StartCamera(ok => started = ok);
        if (!started)
        {
            Log("camera didn't start: " + m_Camera.error);
            status(m_Camera.error ?? "The camera didn't start.");
            m_Scanning = false;
            finished?.Invoke(null);
            done(null);
            yield break;
        }

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false, // QR detection handles any rotation itself
            Options = new DecodingOptions { PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }, TryHarder = true },
        };

        var placements = new List<Placement>[k_OrientationCount];
        for (int i = 0; i < k_OrientationCount; i++)
            placements[i] = new List<Placement>();

        int frames = 0, decodedCount = 0, otherCodes = 0;
        float deadline = Time.unscaledTime + m_Timeout;
        status("Look down at the QR code on the floor and hold still...");

        while (Time.unscaledTime < deadline && !Enough(placements))
        {
            var head = Camera.main.transform;
            var headPose = new Pose(head.position, head.rotation);
            var pixels = m_Camera.GetPixels();
            if (pixels == null)
            {
                yield return null;
                continue;
            }

            int w = m_Camera.width, h = m_Camera.height;
            if (m_Gray == null || m_Gray.Length != w * h)
                m_Gray = new byte[w * h];
            var gray = m_Gray;
            var task = Task.Run(() => Decode(reader, pixels, gray, w, h));
            while (!task.IsCompleted)
                yield return null;

            frames++;
            var decoded = task.Result;
            if (frames == 1 || (frames % 40 == 0 && decodedCount == 0))
                Log($"frame {frames}: {w}x{h}, mean brightness {MeanBrightness(gray):0}, {(decoded == null ? "no code" : "code: " + decoded.Value.text)}");
            if (decoded == null)
                continue;
            if (!decoded.Value.text.StartsWith(CodePrefix))
            {
                otherCodes++;
                continue;
            }

            decodedCount++;
            float floorY = FloorHeight(out string floorSource);
            if (floorY > headPose.position.y - 0.3f)
            {
                // A floor at head height can't be right (the code would be unreachable); assume a standing user.
                floorSource += $" at {floorY:0.00} is too high, using head height - 1.6 m";
                floorY = headPose.position.y - 1.6f;
            }
            int placedNow = 0;
            for (int i = 0; i < k_OrientationCount; i++)
            {
                if (m_KnownOrientation.HasValue && m_KnownOrientation.Value != i)
                    continue;
                if (TryPlaceOnFloor(decoded.Value, headPose, new Orientation(i), floorY, out var placement, out string why))
                {
                    placements[i].Add(placement);
                    placedNow++;
                }
                else if (decodedCount <= 2)
                {
                    Log($"sighting {decodedCount}, {new Orientation(i)}: {why}");
                }
            }

            // Show where it was seen, as placed under the most likely orientation so far.
            int leading = m_KnownOrientation ?? Leading(placements);
            if (leading >= 0 && placements[leading].Count > 0 && placedNow > 0)
            {
                var latest = placements[leading][placements[leading].Count - 1];
                sighted?.Invoke(latest.position, Mathf.Atan2(latest.forward.x, latest.forward.z) * Mathf.Rad2Deg);
            }

            if (decodedCount <= 3)
                Log($"sighting {decodedCount}: corners {decoded.Value.points[0]} {decoded.Value.points[1]} {decoded.Value.points[2]}, head {headPose.position} {headPose.rotation.eulerAngles}, floor y {floorY:0.00} ({floorSource}), placed under {placedNow} orientation(s)");

            status(placedNow > 0
                ? $"Seen the code {decodedCount} times. Hold still..."
                : $"Seen the code {decodedCount} times but can't place it on the floor yet. Look more steeply down at it...");
        }

        m_Camera.Stop();
        m_Scanning = false;

        int best = PickOrientation(placements, out string table);
        Log($"{frames} frames, {decodedCount} sightings of the code, {otherCodes} other codes.{table}");

        if (best < 0)
        {
            status(decodedCount == 0
                ? (otherCodes > 0
                    ? "Saw a QR code, but not the tunnel's. Use the code from Printables/TheTunnel-floor-QR.pdf."
                    : "Couldn't see the QR code. Stand next to it, look down at it from about 1 m, and try again.")
                : $"Saw the code {decodedCount} times but couldn't place it on the floor reliably. Hold still, look down at it, and try again.");
            finished?.Invoke(null);
            done(null);
            yield break;
        }

        m_KnownOrientation = best;
        var result = Average(placements[best]);
        Log($"measured {result.position} yaw {result.yaw:0.0} from {result.samples} sightings, spread {result.spread * 100f:0.0} cm, {new Orientation(best)}");
        finished?.Invoke(result);
        done(result);
    }

    static int Leading(List<Placement>[] placements)
    {
        int leading = -1;
        for (int i = 0; i < placements.Length; i++)
        {
            if (leading < 0 || placements[i].Count > placements[leading].Count)
                leading = i;
        }

        return leading;
    }

    bool Enough(List<Placement>[] placements)
    {
        // Enough sightings for a steady average, and (the first time) for telling the orientations apart.
        int needed = m_KnownOrientation.HasValue ? m_SamplesNeeded : m_SamplesNeeded + 4;
        foreach (var list in placements)
        {
            if (list.Count >= needed)
                return true;
        }

        return false;
    }

    /// <summary>The orientation whose placements agree best with each other and with the printed size; -1 if none.</summary>
    int PickOrientation(List<Placement>[] placements, out string table)
    {
        int best = -1;
        float bestScore = float.MaxValue;
        table = "";
        for (int i = 0; i < k_OrientationCount; i++)
        {
            var list = placements[i];
            if (list.Count == 0)
                continue;

            Vector3 mean = Vector3.zero;
            float size = 0f;
            foreach (var p in list)
            {
                mean += p.position;
                size += p.size;
            }

            mean /= list.Count;
            size /= list.Count;
            float rms = 0f;
            foreach (var p in list)
                rms += (p.position - mean).sqrMagnitude;
            rms = Mathf.Sqrt(rms / list.Count);
            float sizeError = Mathf.Abs(size - m_FinderSpacing) / m_FinderSpacing;
            float score = rms + 0.3f * sizeError;
            table += $"\n  {new Orientation(i)}: {list.Count} placed, spread {rms * 100f:0.0} cm, size {size * 100f:0.0} cm (printed {m_FinderSpacing * 100f:0.0})";

            if (list.Count >= Mathf.Max(3, m_SamplesNeeded / 2) && rms < 0.15f && score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Worker thread: grey image (rows top to bottom) and ZXing decode.</summary>
    static Decoded? Decode(BarcodeReaderGeneric reader, Color32[] pixels, byte[] gray, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int src = y * w, dst = (h - 1 - y) * w; // Unity's first row is the bottom one
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

        if (result == null || result.Text == null || result.ResultPoints == null || result.ResultPoints.Length < 3)
            return null;

        var points = new Vector2[3];
        for (int i = 0; i < 3; i++)
            points[i] = new Vector2(result.ResultPoints[i].X, result.ResultPoints[i].Y);
        return new Decoded { text = result.Text, points = points, width = w, height = h };
    }

    bool TryPlaceOnFloor(Decoded decoded, Pose headPose, Orientation orientation, float floorY, out Placement placement, out string why)
    {
        placement = default;
        var cameraPose = m_Camera.GetCameraPose(headPose, orientation.flipAxes);
        var corners = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            var ray = m_Camera.PixelToRay(decoded.points[i], cameraPose, orientation.flipX, orientation.flipY);
            if (!TryHitFloor(ray, floorY, out corners[i]))
            {
                why = $"corner ray doesn't reach the floor (direction {ray.direction})";
                return false;
            }
        }

        OrderCorners(corners);
        float squareness = SquarenessError(corners[0], corners[1], corners[2]);
        if (squareness > m_MaxSquarenessError)
        {
            why = $"not square on the floor (error {squareness:0.00})";
            return false;
        }

        Vector3 bottomLeft = corners[0], topLeft = corners[1], topRight = corners[2];
        placement = new Placement
        {
            position = (bottomLeft + topRight) * 0.5f,
            forward = Vector3.ProjectOnPlane(topLeft - bottomLeft, Vector3.up).normalized,
            size = ((topLeft - bottomLeft).magnitude + (topRight - topLeft).magnitude) * 0.5f,
        };
        why = null;
        return placement.forward.sqrMagnitude > 0.5f;
    }

    /// <summary>
    /// Sorts the three finder centres into bottom-left, top-left, top-right from where they lie on the floor (ZXing's
    /// order isn't reliable): top-left is the corner between the other two, and seen from above, top-right lies to
    /// the right of the bottom-left-to-top-left direction.
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

    static bool TryHitFloor(Ray ray, float floorY, out Vector3 hit)
    {
        hit = default;
        if (ray.direction.y > -0.05f)
            return false; // not looking down at the floor
        float distance = (floorY - ray.origin.y) / ray.direction.y;
        if (distance < 0.2f || distance > 5f)
            return false;
        hit = ray.origin + ray.direction * distance;
        return true;
    }

    /// <summary>0 for a perfect square corner with equal sides.</summary>
    static float SquarenessError(Vector3 bottomLeft, Vector3 topLeft, Vector3 topRight)
    {
        Vector3 a = bottomLeft - topLeft, b = topRight - topLeft;
        float la = a.magnitude, lb = b.magnitude;
        if (la < 0.02f || lb < 0.02f)
            return float.MaxValue;
        return Mathf.Abs(1f - Mathf.Min(la, lb) / Mathf.Max(la, lb)) + Mathf.Abs(Vector3.Dot(a / la, b / lb));
    }

    Measurement Average(List<Placement> samples)
    {
        Vector3 Mean(IEnumerable<Placement> set, out int count, out Vector3 forwardSum)
        {
            Vector3 sum = Vector3.zero;
            forwardSum = Vector3.zero;
            count = 0;
            foreach (var s in set)
            {
                sum += s.position;
                forwardSum += s.forward;
                count++;
            }

            return count > 0 ? sum / count : Vector3.zero;
        }

        var mean = Mean(samples, out _, out _);
        var kept = samples.FindAll(s => (s.position - mean).magnitude <= m_OutlierDistance);
        if (kept.Count < 3)
            kept = samples;
        mean = Mean(kept, out int n, out var forwardTotal);

        float spread = 0f;
        foreach (var s in kept)
            spread = Mathf.Max(spread, (s.position - mean).magnitude);

        return new Measurement
        {
            position = mean,
            yaw = Mathf.Atan2(forwardTotal.x, forwardTotal.z) * Mathf.Rad2Deg,
            samples = n,
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

    /// <summary>
    /// World height of the real floor: the room's floor plane from Space Setup (as the tunnel uses), or else the
    /// tracking space's floor level. Not the XR Origin itself, which the template's table seating moves around.
    /// </summary>
    static float FloorHeight(out string source)
    {
        var planes = FindAnyObjectByType<ARPlaneManager>();
        if (planes != null)
        {
            float? floor = null;
            foreach (var plane in planes.trackables)
            {
                if ((plane.classifications & PlaneClassifications.Floor) != 0)
                    floor = floor.HasValue ? Mathf.Min(floor.Value, plane.transform.position.y) : plane.transform.position.y;
            }

            if (floor.HasValue)
            {
                source = "floor plane";
                return floor.Value;
            }
        }

        var origin = FindAnyObjectByType<XROrigin>();
        if (origin != null && origin.CameraFloorOffsetObject != null)
        {
            source = "tracking floor";
            return origin.CameraFloorOffsetObject.transform.position.y;
        }

        source = "head height - 1.6 m";
        return Camera.main.transform.position.y - 1.6f;
    }

    static void Log(string message) => Debug.Log("[QR] " + message);
}
