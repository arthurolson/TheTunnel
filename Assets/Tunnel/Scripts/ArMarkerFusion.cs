using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// Joins what the two passthrough cameras each found on their own (<see cref="ArMarkerStereoSolver.ProcessEach"/>)
/// into one pose of the marker, in the left camera's frame:
/// - both found it and agree: a weighted average (both cameras contribute);
/// - they disagree (a single small marker can look tilted one way or the other): the one nearer the last pose, or the
///   one that sees the marker larger;
/// - only one found it: that one.
/// The right camera's pose is carried into the left camera's frame with the headset's lens calibration. On the
/// headset that disagrees with the images by about a degree (the marker's centre lands 10 pixels apart at half size,
/// the same way in every pair), so the turn between where the right camera and the left camera see the marker is
/// learnt while both find it (a slowly updated small rotation about the left camera) and taken off; then the two
/// agree, average well, and the pose doesn't step when one camera loses the marker. The turn is kept per headset in
/// files/ARMarker/stereo-offset.txt for the next start.
/// </summary>
public class ArMarkerFusion
{
    public float agreeDistance = 0.03f;
    public float agreeAngle = 12f;
    [Tooltip("How fast the learnt right-camera offset follows each agreeing pair (0-1).")]
    public float learnRate = 0.05f;
    [Tooltip("Pairs whose directions to the marker differ by at most this (degrees) teach the right camera's turn.")]
    public float learnBearing = 5f;
    [Tooltip("A sighting older than this (seconds) no longer counts as the last pose.")]
    public float memory = 0.3f;

    Vector3 m_OffsetPosition;
    Quaternion m_OffsetRotation = Quaternion.identity;
    Pose m_Last;
    double m_LastTime = double.NegativeInfinity;

    /// <summary>Pairs that agreed and taught the offset.</summary>
    public int learnt { get; private set; }

    public Vector3 offsetPosition => m_OffsetPosition;
    public Quaternion offsetRotation => m_OffsetRotation;

    public struct Fused
    {
        public bool visible;
        public Pose markerInLeft;
        public byte cameras;
        /// <summary>Both cameras found it but their poses disagreed, so one was chosen.</summary>
        public bool disagreed;
        public float apart, angle;
    }

    /// <summary>The right camera's own pose of the marker, in the left camera's frame, with the learnt offset taken off.</summary>
    public Pose RightInLeft(Pose rightInRight, Pose rightFromLeft) =>
        Correct(ArMarkerMath.Mul(ArMarkerMath.Inverse(rightFromLeft), rightInRight));

    Pose Correct(Pose raw) => new(m_OffsetRotation * raw.position + m_OffsetPosition, m_OffsetRotation * raw.rotation);

    /// <summary>
    /// One pair: each camera's own pose (if found) with how much to trust it (for instance the marker's size in the
    /// image), at time <paramref name="time"/> (seconds).
    /// </summary>
    public Fused Fuse(bool hasLeft, Pose leftInLeft, float weightLeft, bool hasRight, Pose rightInRight, float weightRight,
        Pose rightFromLeft, double time)
    {
        var fused = new Fused();
        var raw = hasRight ? ArMarkerMath.Mul(ArMarkerMath.Inverse(rightFromLeft), rightInRight) : default;
        bool recent = time - m_LastTime <= memory;

        // Learn from every pair where both cameras plainly see the same marker (their directions to it within a few
        // degrees, its face within 25 degrees), before judging whether they agree.
        if (hasLeft && hasRight && Vector3.Angle(raw.position, leftInLeft.position) <= learnBearing &&
            Vector3.Angle(raw.rotation * Vector3.forward, leftInLeft.rotation * Vector3.forward) <= 25f)
            Learn(leftInLeft, raw);
        var right = hasRight ? Correct(raw) : default;

        if (hasLeft && hasRight)
        {
            fused.apart = Vector3.Distance(leftInLeft.position, right.position);
            fused.angle = Vector3.Angle(leftInLeft.rotation * Vector3.forward, right.rotation * Vector3.forward);
            // Each camera judges the marker's distance from its size, to within a few percent; sideways they agree
            // to a few millimetres once the right camera's turn is learnt.
            var d = right.position - leftInLeft.position;
            var towards = leftInLeft.position.normalized;
            float along = Mathf.Abs(Vector3.Dot(d, towards)), across = (d - towards * Vector3.Dot(d, towards)).magnitude;
            float range = leftInLeft.position.magnitude;
            if (across <= agreeDistance * 0.5f + 0.01f * range && along <= agreeDistance + 0.04f * range && fused.angle <= agreeAngle)
            {
                float t = weightRight / Mathf.Max(weightLeft + weightRight, 1e-6f);
                fused.markerInLeft = new Pose(Vector3.Lerp(leftInLeft.position, right.position, t),
                    Quaternion.Slerp(leftInLeft.rotation, right.rotation, t));
                fused.cameras = ArMarkerSample.LeftCamera | ArMarkerSample.RightCamera;
            }
            else
            {
                fused.disagreed = true;
                bool takeLeft = recent
                    ? Distance(leftInLeft, m_Last) <= Distance(right, m_Last)
                    : weightLeft >= weightRight;
                fused.markerInLeft = takeLeft ? leftInLeft : right;
                fused.cameras = takeLeft ? ArMarkerSample.LeftCamera : ArMarkerSample.RightCamera;
            }
        }
        else if (hasLeft)
        {
            fused.markerInLeft = leftInLeft;
            fused.cameras = ArMarkerSample.LeftCamera;
        }
        else if (hasRight)
        {
            fused.markerInLeft = right;
            fused.cameras = ArMarkerSample.RightCamera;
        }

        fused.visible = fused.cameras != 0;
        if (fused.visible)
        {
            m_Last = fused.markerInLeft;
            m_LastTime = time;
        }
        return fused;
    }

    // Distance between poses: position plus orientation of the marker's face (a degree counted as 2 mm).
    static float Distance(Pose a, Pose b) =>
        Vector3.Distance(a.position, b.position) + Vector3.Angle(a.rotation * Vector3.forward, b.rotation * Vector3.forward) * 0.002f;

    // The small turn about the left camera that takes the right camera's sighting onto the left camera's: learnt
    // from the directions to the marker, which each camera measures well (its distance and tilt less so).
    void Learn(Pose left, Pose rawRight)
    {
        float rate = learnt < 20 ? 0.2f : learnRate; // quicker at first
        var turn = Quaternion.FromToRotation(rawRight.position, left.position);
        m_OffsetRotation = Quaternion.Slerp(m_OffsetRotation, turn, rate);
        m_OffsetPosition = Vector3.zero;
        learnt++;
    }

    public override string ToString()
    {
        m_OffsetRotation.ToAngleAxis(out float angle, out _);
        return $"right camera's learnt turn {Mathf.DeltaAngle(0f, angle):0.00} deg ({m_OffsetRotation.eulerAngles.x:0.00}, {m_OffsetRotation.eulerAngles.y:0.00}, " +
            $"{m_OffsetRotation.eulerAngles.z:0.00}) from {learnt} agreeing pairs";
    }

    /// <summary>Loads a saved offset (files/ARMarker/stereo-offset.txt); false if there is none.</summary>
    public bool Load(string file)
    {
        try
        {
            if (file == null || !File.Exists(file))
                return false;
            var parts = File.ReadAllText(file).Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7)
                return false;
            var v = Array.ConvertAll(parts, p => float.Parse(p, CultureInfo.InvariantCulture));
            m_OffsetPosition = new Vector3(v[0], v[1], v[2]);
            m_OffsetRotation = new Quaternion(v[3], v[4], v[5], v[6]).normalized;
            learnt = 20; // trusted, but still updated
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Saves the offset once it has been learnt from enough pairs.</summary>
    public void Save(string file)
    {
        if (file == null || learnt < 100)
            return;
        try
        {
            var c = CultureInfo.InvariantCulture;
            var p = m_OffsetPosition;
            var q = m_OffsetRotation;
            File.WriteAllText(file, string.Join(" ", new[] { p.x, p.y, p.z, q.x, q.y, q.z, q.w }.Select(f => f.ToString("R", c))));
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
