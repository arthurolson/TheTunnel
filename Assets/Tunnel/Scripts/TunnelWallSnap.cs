using UnityEngine;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Where a flat panel let go of near a tunnel wall lies on that wall: within reach (25 cm for the signs and the
/// Antibody action menu) of one of the flat side walls, between the floor and the wall's top, it goes flat on the wall,
/// upright, its face toward the middle of the tunnel, where it was let go (moved along the wall only as far as needed
/// to keep it on the wall). The same rule as a sign's (see <see cref="TunnelSign.TrySnapToWall"/>), so panels and
/// signs line up alike. A panel's forward (+Z) points into the wall, so its face (-Z) looks into the tunnel.
/// </summary>
public static class TunnelWallSnap
{
    /// <summary>
    /// The pose on the nearest flat tunnel wall for a panel whose middle is at <paramref name="position"/>, of this half
    /// width and height (meters), whose back is <paramref name="back"/> behind its middle; false if no wall is within
    /// <paramref name="reach"/>.
    /// </summary>
    public static bool TryFind(Vector3 position, Vector2 halfSize, float back, float reach, out Pose pose, out TunnelSegmentGeometry wall)
    {
        pose = default;
        wall = null;
        int wallSide = 0;
        float nearest = float.MaxValue;
        Vector3 local = default;
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null || !segment.isActiveAndEnabled)
                continue;
            var p = segment.transform.InverseTransformPoint(position);
            if (Mathf.Abs(p.z) > segment.length * 0.5f || p.y < 0f || p.y > segment.wallHeight)
                continue;
            int side = p.x < 0f ? -1 : 1;
            float gap = segment.width * 0.5f - side * p.x;
            if (gap > reach || gap < -0.2f || gap >= nearest)
                continue;
            nearest = gap;
            wall = segment;
            wallSide = side;
            local = p;
        }
        if (wall == null)
            return false;

        var frame = wall.transform;
        float halfLength = wall.length * 0.5f;
        float z = halfSize.x < halfLength ? Mathf.Clamp(local.z, -halfLength + halfSize.x, halfLength - halfSize.x) : 0f;
        float y = halfSize.y * 2f + 0.02f < wall.wallHeight ? Mathf.Clamp(local.y, halfSize.y + 0.02f, wall.wallHeight - halfSize.y) : wall.wallHeight * 0.5f;
        pose = new Pose(frame.TransformPoint(new Vector3(wallSide * (wall.width * 0.5f - back), y, z)),
            Quaternion.LookRotation(frame.TransformDirection(Vector3.right * wallSide), frame.up));
        return true;
    }
}
