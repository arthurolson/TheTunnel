using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Fine-tunes a joining player's room alignment from the AR marker: when this headset and the host both see the
/// marker, their sightings taken at the same moment (on the shared network clock) should coincide in the shared
/// tabletop frame; any steady difference is this headset's alignment error. Once they have differed steadily for
/// about a second, it gives the correction: a turn about the vertical (gravity is kept) and a shift that bring this
/// headset's sighting onto the host's. <see cref="CoLocation"/> moves the XR Origin by it.
/// Only the host's own sightings are used, never the shared pose this headset may itself have supplied.
/// </summary>
public class ArMarkerRoomAlignment
{
    [Tooltip("Own and host sightings this close in time (seconds) are compared.")]
    public float pairWithin = 0.04f;
    [Tooltip("Pairs gathered over at least this long (seconds), and at least minPairs of them, make a correction.")]
    public float window = 1f;
    public int minPairs = 20;
    [Tooltip("The pairs must agree this well (RMS) for the correction to be used.")]
    public float maxSpreadMm = 12f;
    public float maxSpreadDeg = 1.5f;
    [Tooltip("The marker must stay this still (meters, host's sightings over the window).")]
    public float maxMarkerMotion = 0.03f;
    [Tooltip("Corrections smaller than this are left alone.")]
    public float minShift = 0.005f;
    public float minTurn = 0.3f;

    readonly List<ArMarkerSample> m_Own = new();
    readonly List<(double time, Pose own, Pose host)> m_Pairs = new();

    public int pairs => m_Pairs.Count;

    /// <summary>The correction: turn (degrees about the vertical) and shift, in the shared tabletop frame.</summary>
    public struct Correction
    {
        public float turn;
        public Vector3 pivot, target;
        public int pairs;
        public float spreadMm, spreadDeg;

        public Vector3 shift => target - pivot;

        /// <summary>Where a point of this headset's world goes (in the frame the correction was found in).</summary>
        public Vector3 Apply(Vector3 point) => Quaternion.AngleAxis(turn, Vector3.up) * (point - pivot) + target;

        public override string ToString() =>
            $"shift {shift.magnitude * 1000f:0} mm ({shift.x * 1000f:0}, {shift.y * 1000f:0}, {shift.z * 1000f:0}), turn {turn:0.00} deg, " +
            $"from {pairs} pairs, spread {spreadMm:0.0} mm, {spreadDeg:0.00} deg";
    }

    /// <summary>Forget everything (after the XR Origin moved, earlier sightings are in the old frame).</summary>
    public void Clear()
    {
        m_Own.Clear();
        m_Pairs.Clear();
    }

    /// <summary>This headset's own sighting.</summary>
    public void AddOwn(ArMarkerSample sample)
    {
        if (!sample.valid)
            return;
        m_Own.Add(sample);
        while (m_Own.Count > 0 && sample.time - m_Own[0].time > 2.0)
            m_Own.RemoveAt(0);
    }

    /// <summary>The host's own sighting; pairs it with this headset's taken at the same moment.</summary>
    public void AddHost(ArMarkerSample host)
    {
        if (!host.valid || m_Own.Count < 2)
            return;
        // This headset's pose at the host's moment, between its sightings around it.
        for (int i = m_Own.Count - 1; i > 0; i--)
        {
            var a = m_Own[i - 1];
            var b = m_Own[i];
            if (a.time - pairWithin <= host.time && host.time <= b.time + pairWithin && b.time - a.time <= 0.1)
            {
                float k = b.time > a.time ? Mathf.Clamp01((float)((host.time - a.time) / (b.time - a.time))) : 0f;
                var own = new Pose(Vector3.Lerp(a.position, b.position, k), Quaternion.Slerp(a.rotation, b.rotation, k));
                m_Pairs.Add((host.time, own, host.pose));
                break;
            }
        }
        while (m_Pairs.Count > 0 && host.time - m_Pairs[0].time > window * 1.5)
            m_Pairs.RemoveAt(0);
    }

    /// <summary>A steady correction, if the pairs gathered so far give one.</summary>
    public bool TryCorrection(out Correction correction, out string why)
    {
        correction = default;
        why = null;
        if (m_Pairs.Count < minPairs || m_Pairs[^1].time - m_Pairs[0].time < window)
        {
            why = $"{m_Pairs.Count} pairs";
            return false;
        }

        // The marker must be still (so small timing differences don't count).
        var first = m_Pairs[0].host.position;
        float motion = 0f;
        foreach (var p in m_Pairs)
            motion = Mathf.Max(motion, Vector3.Distance(p.host.position, first));
        if (motion > maxMarkerMotion)
        {
            why = $"the marker moved {motion * 100f:0} cm";
            m_Pairs.RemoveRange(0, m_Pairs.Count / 2);
            return false;
        }

        // Each pair's turn about the vertical, then each pair's shift after that turn.
        var turns = new List<float>();
        foreach (var p in m_Pairs)
            turns.Add(Twist(p.host.rotation * Quaternion.Inverse(p.own.rotation)));
        float turn = Median(turns);
        var rotation = Quaternion.AngleAxis(turn, Vector3.up);
        Vector3 pivot = Vector3.zero, target = Vector3.zero;
        foreach (var p in m_Pairs)
        {
            pivot += p.own.position;
            target += p.host.position;
        }
        pivot /= m_Pairs.Count;
        target /= m_Pairs.Count;

        float sumMm = 0f, sumDeg = 0f;
        foreach (var p in m_Pairs)
        {
            var moved = rotation * (p.own.position - pivot) + target;
            float mm = Vector3.Distance(moved, p.host.position) * 1000f;
            float deg = Mathf.DeltaAngle(turn, Twist(p.host.rotation * Quaternion.Inverse(p.own.rotation)));
            sumMm += mm * mm;
            sumDeg += deg * deg;
        }
        correction = new Correction
        {
            turn = turn,
            pivot = pivot,
            target = target,
            pairs = m_Pairs.Count,
            spreadMm = Mathf.Sqrt(sumMm / m_Pairs.Count),
            spreadDeg = Mathf.Sqrt(sumDeg / m_Pairs.Count),
        };
        if (correction.spreadMm > maxSpreadMm || correction.spreadDeg > maxSpreadDeg)
        {
            why = $"not steady ({correction})";
            m_Pairs.RemoveRange(0, m_Pairs.Count / 2);
            return false;
        }
        if (correction.shift.magnitude < minShift && Mathf.Abs(turn) < minTurn)
        {
            why = $"already aligned ({correction})";
            m_Pairs.Clear();
            return false;
        }
        return true;
    }

    /// <summary>The part of a rotation about the vertical (degrees).</summary>
    public static float Twist(Quaternion q)
    {
        float angle = 2f * Mathf.Atan2(q.y, q.w) * Mathf.Rad2Deg;
        return Mathf.DeltaAngle(0f, angle);
    }

    static float Median(List<float> values)
    {
        var sorted = new List<float>(values);
        sorted.Sort();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : 0.5f * (sorted[middle - 1] + sorted[middle]);
    }
}
