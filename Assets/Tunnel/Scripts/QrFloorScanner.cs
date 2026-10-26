using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using ZXing;
using ZXing.Common;

/// <summary>
/// Finds the tunnel's QR code on the floor through a passthrough camera and measures where it lies: the point at its
/// centre and the direction its top edge faces, in this headset's world. Each camera image is decoded with ZXing on a
/// worker thread; the code's three corner finder patterns are turned into rays and dropped onto the floor plane.
/// Several sightings are averaged. Every headset measures the same printed code, so their measurements can be lined up.
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
    }

    bool m_Scanning;
    byte[] m_Gray;
    bool? m_FlipY; // the camera's row order, found from the first good sightings

    public bool isScanning => m_Scanning;

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
            status(m_Camera.error ?? "The camera didn't start.");
            m_Scanning = false;
            done(null);
            yield break;
        }

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false, // QR detection handles any rotation itself
            Options = new DecodingOptions { PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }, TryHarder = true },
        };

        var samples = new List<(Vector3 position, Vector3 forward)>();
        float deadline = Time.unscaledTime + m_Timeout;
        status("Look at the QR code on the floor and hold still...");

        while (samples.Count < m_SamplesNeeded && Time.unscaledTime < deadline)
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

            var decoded = task.Result;
            if (decoded == null)
                continue;

            float floorY = FloorHeight();
            var cameraPose = m_Camera.GetCameraPose(headPose);
            if (TryPlaceOnFloor(decoded.Value, cameraPose, floorY, out var position, out var forward))
            {
                samples.Add((position, forward));
                status($"Seen the code {samples.Count}/{m_SamplesNeeded} times. Hold still...");
            }
        }

        m_Camera.Stop();
        m_Scanning = false;

        if (samples.Count < m_SamplesNeeded / 2)
        {
            status(samples.Count == 0
                ? "Couldn't see the QR code. Stand about 1 m from it, look down at it, and try again."
                : "Only saw the code a few times. Hold still while looking at it and try again.");
            done(null);
            yield break;
        }

        var result = Average(samples);
        Debug.Log($"[QR] measured {result.position} yaw {result.yaw:0.0} from {result.samples} sightings, spread {result.spread * 100f:0.0} cm, flipY={m_FlipY}");
        done(result);
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

        if (result == null || result.Text == null || !result.Text.StartsWith(CodePrefix) ||
            result.ResultPoints == null || result.ResultPoints.Length < 3)
            return null;

        var points = new Vector2[3];
        for (int i = 0; i < 3; i++)
            points[i] = new Vector2(result.ResultPoints[i].X, result.ResultPoints[i].Y);
        return new Decoded { text = result.Text, points = points };
    }

    bool TryPlaceOnFloor(Decoded decoded, Pose cameraPose, float floorY, out Vector3 position, out Vector3 forward)
    {
        position = forward = default;

        // Try the camera's row order both ways until one is known: the right one puts a square code on the floor.
        float bestScore = float.MaxValue;
        bool bestFlip = false;
        Vector3[] best = null;
        foreach (bool flip in m_FlipY.HasValue ? new[] { m_FlipY.Value } : new[] { false, true })
        {
            var corners = new Vector3[3];
            bool ok = true;
            for (int i = 0; i < 3 && ok; i++)
                ok = TryHitFloor(m_Camera.PixelToRay(decoded.points[i], cameraPose, flip), floorY, out corners[i]);
            if (!ok)
                continue;

            OrderCorners(corners);
            float score = SquarenessError(corners[0], corners[1], corners[2]);
            if (score < bestScore)
            {
                bestScore = score;
                bestFlip = flip;
                best = corners;
            }
        }

        if (best == null || bestScore > 0.25f)
            return false;
        if (!m_FlipY.HasValue)
        {
            m_FlipY = bestFlip;
            Debug.Log($"[QR] camera rows run {(bestFlip ? "bottom-up" : "top-down")} (squareness error {bestScore:0.000})");
        }

        Vector3 bottomLeft = best[0], topLeft = best[1], topRight = best[2];
        position = (bottomLeft + topRight) * 0.5f;
        forward = Vector3.ProjectOnPlane(topLeft - bottomLeft, Vector3.up).normalized;
        return forward.sqrMagnitude > 0.5f;
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

    Measurement Average(List<(Vector3 position, Vector3 forward)> samples)
    {
        Vector3 Mean(IEnumerable<(Vector3 position, Vector3 forward)> set, out int count, out Vector3 meanForward)
        {
            Vector3 sum = Vector3.zero, fsum = Vector3.zero;
            count = 0;
            foreach (var s in set)
            {
                sum += s.position;
                fsum += s.forward;
                count++;
            }

            meanForward = fsum;
            return count > 0 ? sum / count : Vector3.zero;
        }

        var mean = Mean(samples, out _, out _);
        var kept = samples.FindAll(s => (s.position - mean).magnitude <= m_OutlierDistance);
        if (kept.Count < 3)
            kept = samples;
        mean = Mean(kept, out int n, out var forwardSum);

        float spread = 0f;
        foreach (var s in kept)
            spread = Mathf.Max(spread, (s.position - mean).magnitude);

        return new Measurement
        {
            position = mean,
            yaw = Mathf.Atan2(forwardSum.x, forwardSum.z) * Mathf.Rad2Deg,
            samples = n,
            spread = spread,
        };
    }

    static float FloorHeight()
    {
        // The XR Origin sits on the floor (floor tracking), so its height is the floor's.
        var origin = FindAnyObjectByType<XROrigin>();
        return origin != null ? origin.Origin.transform.position.y : 0f;
    }
}
