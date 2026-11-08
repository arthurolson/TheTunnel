using Unity.Netcode;
using UnityEngine;

/// <summary>What a marker does to the tunnel's walls (chosen on its palette).</summary>
public enum PaintMode : byte
{
    /// <summary>Solid paint, freehand while the marker touches the wall.</summary>
    Paint,
    /// <summary>Pencil lines: straight from touch to touch, or (Curve) a Bezier curve with the touches as its control points.</summary>
    Pencil,
    /// <summary>Soft grainy spray that builds up the longer it is held there.</summary>
    Airbrush,
    /// <summary>Takes marks off where it touches.</summary>
    Eraser,
}

/// <summary>How a batch of brush stamps is drawn on a paint canvas.</summary>
public struct PaintStyle : INetworkSerializable
{
    public PaintMode mode;
    public Color32 color;   // its alpha is the opacity
    public float diameter;  // meters

    public PaintStyle(PaintMode mode, Color32 color, float diameter)
    {
        this.mode = mode;
        this.color = color;
        this.diameter = diameter;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref mode);
        serializer.SerializeValue(ref color);
        serializer.SerializeValue(ref diameter);
    }
}
