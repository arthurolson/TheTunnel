using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Lets what is set on or into the tunnel's walls go with the tunnel when someone moves it (by its handle, or its
/// height): mixed reality boxes (and the molecules inside), signs, palettes, painting copies and a movie menu laid on
/// a wall. Each of them asks,
/// once a frame, whether the segment it's on moved since the last frame and by how much, and moves itself by the
/// same amount. The browser and Viewer panels keep to their wall their own way (BrowserPanel).
/// </summary>
public static class TunnelWallRide
{
    static readonly Dictionary<TunnelSegmentGeometry, (Vector3 position, Quaternion rotation)> s_Last = new();
    static readonly Dictionary<TunnelSegmentGeometry, Matrix4x4> s_Moves = new();
    static int s_Frame = -1;

    /// <summary>
    /// Whether this segment moved since the last frame; if so, <paramref name="move"/> takes a point where it was on
    /// the segment to where it is now (<c>move.MultiplyPoint3x4</c>) and a rotation likewise (<c>move.rotation *</c>).
    /// </summary>
    public static bool Moved(TunnelSegmentGeometry segment, out Matrix4x4 move)
    {
        Refresh();
        move = Matrix4x4.identity;
        return segment != null && s_Moves.TryGetValue(segment, out move);
    }

    /// <summary>A segment's pose in the world, without scale.</summary>
    public static Matrix4x4 PoseOf(TunnelSegmentGeometry segment) =>
        Matrix4x4.TRS(segment.transform.position, segment.transform.rotation, Vector3.one);

    /// <summary>Moves a transform (and its body, if it has one) the way a segment moved.</summary>
    public static void Apply(Matrix4x4 move, Transform target, Rigidbody body = null)
    {
        // From the body when it has one: an interpolated body's transform lags a frame behind where it was put.
        var position = move.MultiplyPoint3x4(body != null ? body.position : target.position);
        var rotation = move.rotation * (body != null ? body.rotation : target.rotation);
        target.SetPositionAndRotation(position, rotation);
        if (body != null)
        {
            body.position = position;
            body.rotation = rotation;
        }
    }

    /// <summary>
    /// For something that lies flat on a wall (its forward into the wall), called once a frame on the headset that
    /// moves it: while it's <paramref name="free"/> (not in a hand), keeps which wall it lies on, and when that segment
    /// moves, moves it the same way. <paramref name="gap"/> is how far its transform sits in from the wall's surface.
    /// </summary>
    public static void Ride(ref TunnelSegmentGeometry wall, Transform target, Rigidbody body, float gap, bool free)
    {
        if (!free)
        {
            wall = null;
            return;
        }
        if (wall != null && Moved(wall, out var move))
        {
            Apply(move, target, body);
            return;
        }
        wall = WallUnder(target, gap);
    }

    /// <summary>The flat tunnel wall this lies flat on (forward into it, <paramref name="gap"/> in from it), or null.</summary>
    public static TunnelSegmentGeometry WallUnder(Transform target, float gap)
    {
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null || !segment.isActiveAndEnabled)
                continue;
            var frame = segment.transform;
            var p = frame.InverseTransformPoint(target.position);
            if (Mathf.Abs(p.z) > segment.length * 0.5f + 0.01f || p.y < 0f || p.y > segment.wallHeight)
                continue;
            int side = p.x < 0f ? -1 : 1;
            float off = Mathf.Abs(segment.width * 0.5f - side * p.x - gap);
            if (off < 0.01f && Vector3.Angle(target.forward, frame.TransformDirection(Vector3.right * side)) < 2f)
                return segment;
        }
        return null;
    }

    // Once a frame, the first time anyone asks: how each segment moved since the frame before.
    static void Refresh()
    {
        if (s_Frame == Time.frameCount)
            return;
        // Nobody asked last frame: the poses kept are old, so this frame only catches up (no move is reported).
        bool lastFrame = s_Frame == Time.frameCount - 1;
        s_Frame = Time.frameCount;
        s_Moves.Clear();
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null)
                continue;
            var t = segment.transform;
            var position = t.position;
            var rotation = t.rotation;
            if (lastFrame && s_Last.TryGetValue(segment, out var last)
                && ((last.position - position).sqrMagnitude > 1e-10f || Quaternion.Angle(last.rotation, rotation) > 0.001f))
            {
                s_Moves[segment] = Matrix4x4.TRS(position, rotation, Vector3.one) * Matrix4x4.TRS(last.position, last.rotation, Vector3.one).inverse;
            }
            s_Last[segment] = (position, rotation);
        }
    }
}
