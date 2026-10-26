namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// Whether this headset has been aligned to the shared floor QR code, i.e. its player shares a real room with
    /// the others. While aligned, the template's table seating must not move the player's XR Origin, or the virtual
    /// world would slide away from the real room.
    /// </summary>
    public static class CoLocationState
    {
        public static bool isAligned { get; set; }
    }
}
