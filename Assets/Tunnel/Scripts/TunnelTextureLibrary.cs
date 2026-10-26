using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// Wall textures for Tunnel Segments, from two places:
///  - Built in: images in Assets/Tunnel/Resources/TunnelTextures ship inside the app, so every headset has them.
///  - The host's Textures folder (Application.persistentDataPath/Textures, i.e. Android/data/&lt;package&gt;/files/Textures
///    on the headset), for adding images without a new build. The host shares these over the network as JPEG bytes.
///    TIFF can't be decoded at runtime: convert it before copying (the push-textures.sh helper does this).
/// Every headset keeps decoded textures in a shared cache.
/// </summary>
public static class TunnelTextureLibrary
{
    /// <summary>Longest side textures are scaled down to before use and sharing.</summary>
    public const int MaxSize = 2048;

    /// <summary>Resources path of the built-in textures (Assets/Tunnel/Resources/TunnelTextures).</summary>
    const string k_BuiltInPath = "TunnelTextures";

    static readonly string[] k_Extensions = { ".png", ".jpg", ".jpeg" };
    static readonly Dictionary<string, Texture2D> s_Textures = new();
    static readonly Dictionary<string, byte[]> s_Encoded = new();
    static Dictionary<string, Texture2D> s_BuiltIn;

    public static string folderPath => Path.Combine(Application.persistentDataPath, "Textures");

    /// <summary>Built-in textures by name (the image's file name without extension).</summary>
    static Dictionary<string, Texture2D> builtIn
    {
        get
        {
            if (s_BuiltIn == null)
            {
                s_BuiltIn = new Dictionary<string, Texture2D>();
                foreach (var texture in Resources.LoadAll<Texture2D>(k_BuiltInPath))
                    s_BuiltIn[texture.name] = texture;
            }

            return s_BuiltIn;
        }
    }

    /// <summary>Built-in texture names, sorted.</summary>
    public static List<string> ListBuiltIn() => builtIn.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Image file names in the Textures folder (creating the folder if needed), sorted.</summary>
    public static List<string> ListFiles()
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            return Directory.GetFiles(folderPath)
                .Where(f => k_Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Textures] Can't read {folderPath}: {e.Message}");
            return new List<string>();
        }
    }

    /// <summary>A built-in texture, or one already loaded from the folder or received from the host.</summary>
    public static bool TryGetCached(string name, out Texture2D texture) =>
        (s_Textures.TryGetValue(name, out texture) && texture != null) ||
        (!string.IsNullOrEmpty(name) && builtIn.TryGetValue(name, out texture) && texture != null);

    /// <summary>Host: loads an image from the folder (scaled to <see cref="MaxSize"/>), caching the texture and its shareable bytes.</summary>
    public static bool TryLoadFromFolder(string name, out Texture2D texture, out byte[] encoded)
    {
        encoded = null;
        if (TryGetCached(name, out texture) && s_Encoded.TryGetValue(name, out encoded))
            return true;

        // Names come over the network: only ever read plain file names inside the folder.
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name))
            return false;
        string path = Path.Combine(folderPath, name);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[Textures] {name} is not in {folderPath}");
            return false;
        }

        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!source.LoadImage(File.ReadAllBytes(path)))
        {
            UnityEngine.Object.Destroy(source);
            Debug.LogWarning($"[Textures] {name} isn't a PNG or JPEG image Unity can read");
            return false;
        }

        texture = Prepare(source);
        encoded = texture.EncodeToJPG(90);
        s_Textures[name] = texture;
        s_Encoded[name] = encoded;
        Debug.Log($"[Textures] loaded {name}: {texture.width}x{texture.height}, {encoded.Length / 1024} KB to share");
        return true;
    }

    /// <summary>Host: the shareable bytes of a loaded texture.</summary>
    public static bool TryGetEncoded(string name, out byte[] encoded)
    {
        if (s_Encoded.TryGetValue(name, out encoded))
            return true;
        return TryLoadFromFolder(name, out _, out encoded);
    }

    /// <summary>Client: decodes bytes received from the host and caches the texture.</summary>
    public static Texture2D AddFromBytes(string name, byte[] bytes)
    {
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!source.LoadImage(bytes))
        {
            UnityEngine.Object.Destroy(source);
            return null;
        }

        var texture = Prepare(source);
        s_Textures[name] = texture;
        return texture;
    }

    /// <summary>Scales to at most <see cref="MaxSize"/>, adds mipmaps and sets wrapping/filtering for walls.</summary>
    static Texture2D Prepare(Texture2D source)
    {
        float scale = Mathf.Min(1f, MaxSize / (float)Mathf.Max(source.width, source.height));
        int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
        int h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, rt);
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, true);
        texture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        texture.Apply(true);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        UnityEngine.Object.Destroy(source);

        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 4; // walls are often seen at grazing angles
        return texture;
    }
}
