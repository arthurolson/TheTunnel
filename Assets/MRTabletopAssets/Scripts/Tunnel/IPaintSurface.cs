namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// A surface markers paint on: a tunnel segment's walls and arch (<see cref="TunnelSegmentGeometry"/>), or a real
    /// whiteboard found by the headset's camera. Its paint is kept in a canvas texture whose coordinates (0..1 each way)
    /// are laid over the surface.
    /// </summary>
    public interface IPaintSurface
    {
        /// <summary>
        /// Where a point is over the surface, on the paint canvas, how far in front of the surface it is (meters;
        /// negative behind it) and the nearest point on the surface. False where the point isn't over the surface.
        /// </summary>
        bool ToPaintCanvas(Vector3 world, out Vector2 uv, out float depth, out Vector3 onSurface);

        /// <summary>The surface, set in front of it by <paramref name="inset"/> meters, with canvas coordinates as its UVs.</summary>
        Mesh BuildPaintMesh(float inset);

        /// <summary>Meters per unit of the canvas's u and v.</summary>
        Vector2 paintCanvasMeters { get; }

        /// <summary>Whether it can be painted on now.</summary>
        bool paintable { get; }

        /// <summary>The paint surface's mesh goes under this, in its coordinates.</summary>
        Transform paintParent { get; }

        /// <summary>Fires when its shape or size changes.</summary>
        event System.Action rebuilt;
    }
}
