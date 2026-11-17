using UnityEngine;

/// <summary>
/// Where the app finds its asset list (see <see cref="TunnelAssetCatalog"/>), and how videos are handled. The asset
/// in Assets/Tunnel/Resources/TunnelAssetSettings sets the default for every build. A headset can use another list
/// without a new build: put its URL in Android/data/edu.scripps.tunnel/files/TunnelAssets/catalog-url.txt, for example
/// <code>adb shell "echo https://raw.githubusercontent.com/someone/TheTunnelAsset/dev/assets.json > /sdcard/Android/data/edu.scripps.tunnel/files/TunnelAssets/catalog-url.txt"</code>
/// or call <see cref="TunnelAssetCatalog.UseSource"/>. In a session everyone uses the host's list.
/// </summary>
[CreateAssetMenu(menuName = "Tunnel/Asset Settings")]
public class TunnelAssetSettings : ScriptableObject
{
    public const string k_DefaultCatalogUrl = "https://raw.githubusercontent.com/arthurolson/TheTunnelAsset/main/assets.json";

    [Tooltip("The asset list (assets.json) the app reads at startup, unless the headset has its own (catalog-url.txt).")]
    public string catalogUrl = k_DefaultCatalogUrl;

    [Tooltip("Videos up to this size (bytes) are downloaded and kept on the headset; larger ones stream from their URL.")]
    public long cacheVideosUpTo = 20_000_000;

    [Tooltip("Radius of the sphere a 360 image or video is shown on around its box (meters).")]
    public float surroundRadius = 8f;

    static TunnelAssetSettings s_Current;

    /// <summary>The settings in Resources, or the defaults if there are none.</summary>
    public static TunnelAssetSettings current
    {
        get
        {
            if (s_Current == null)
                s_Current = Resources.Load<TunnelAssetSettings>("TunnelAssetSettings");
            if (s_Current == null)
                s_Current = CreateInstance<TunnelAssetSettings>();
            return s_Current;
        }
    }
}
