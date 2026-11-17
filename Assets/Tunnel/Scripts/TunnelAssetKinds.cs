using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// How each kind of asset that isn't a glTF model is shown (see <see cref="TunnelAssets.Load"/>):
/// - an image on a quad of its own proportions, or, a 360 panorama ("equirect"), all round on a sphere;
/// - a video the same way, with its sound, playing from the headset's copy if the file is small, else streaming;
/// - an atomic model (.pdb, .cif) as its ready-made surface (another asset, a glb);
/// - a virus instance list as its proteins (glb assets) put together at their positions and rotations.
/// The front of a quad, and the centre of a 360 picture, face the box's front (+Z) as a model's do: a 360 picture's
/// centre is straight ahead of whoever placed it.
/// </summary>
public static partial class TunnelAssets
{
    static Task<GameObject> LoadKind(TunnelAssetCatalog.Asset asset, Transform parent, Action<float> progress)
    {
        switch (asset.kind)
        {
            case "image": return LoadImage(asset, parent, progress);
            case "video": return LoadVideo(asset, parent, progress);
            case "molecule": return Load(asset.surface, parent, progress); // its ready-made surface
            case "virus": return LoadInstances(asset, parent, progress);
            default: throw new LoadException($"{asset.title} is a {asset.kind}, which the app can't show yet");
        }
    }

    static async Task<GameObject> LoadImage(TunnelAssetCatalog.Asset asset, Transform parent, Action<float> progress)
    {
        string path = await Download(asset, progress);
        var bytes = await File.ReadAllBytesAsync(path);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = asset.id, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
        if (!texture.LoadImage(bytes, true))
        {
            UnityEngine.Object.Destroy(texture);
            throw new LoadException($"{asset.title} isn't an image the app can read");
        }
        if (asset.isSurround)
            texture.wrapModeU = TextureWrapMode.Repeat; // the seam behind
        var root = Picture(asset, texture, texture.width, texture.height, parent);
        root.GetComponent<TunnelAssetInstance>().Own(texture);
        Debug.Log($"[Assets] {asset.id} loaded: {texture.width} x {texture.height} image{(asset.isSurround ? ", 360" : "")}");
        return root;
    }

    static async Task<GameObject> LoadVideo(TunnelAssetCatalog.Asset asset, Transform parent, Action<float> progress)
    {
        // Small files are kept on the headset; large ones stream.
        string url = asset.url;
        if (IsCached(asset) || (asset.bytes > 0 && asset.bytes <= TunnelAssetSettings.current.cacheVideosUpTo))
            url = new Uri(await Download(asset, progress)).AbsoluteUri;
        else
            progress?.Invoke(1f);

        int width = asset.width > 0 ? asset.width : 1920, height = asset.height > 0 ? asset.height : 1080;
        float scale = Mathf.Min(1f, 4096f / Mathf.Max(width, height));
        var target = new RenderTexture(Mathf.RoundToInt(width * scale), Mathf.RoundToInt(height * scale), 0, RenderTextureFormat.ARGB32)
        {
            name = asset.id, useMipMap = false, wrapMode = asset.isSurround ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
        };
        target.Create();
        var root = Picture(asset, target, width, height, parent);
        var instance = root.GetComponent<TunnelAssetInstance>();
        instance.Own(target);
        var video = root.AddComponent<VideoPlayer>().gameObject.AddComponent<TunnelVideo>();
        instance.video = video;
        video.Begin(url, target);

        // Wait until it can play (or says why it can't), so the box shows the picture only when there is one.
        float started = Time.realtimeSinceStartup;
        while (!video.isPrepared && string.IsNullOrEmpty(video.error) && Time.realtimeSinceStartup - started < 60f && root != null)
            await Awaitable.NextFrameAsync();
        if (root == null)
            throw new LoadException($"{asset.title} was closed while it loaded");
        if (!video.isPrepared)
        {
            UnityEngine.Object.Destroy(root);
            throw new LoadException($"{asset.title} won't play: {(string.IsNullOrEmpty(video.error) ? "it took too long to start" : video.error)}");
        }
        Debug.Log($"[Assets] {asset.id} ready: {width} x {height} video, {video.length:F1} s{(asset.isSurround ? ", 360" : "")}, {(url.StartsWith("file:") ? "from the headset" : "streaming")}");
        return root;
    }

    /// <summary>
    /// A picture (image or video frame) on a quad of its proportions, 1 unit wide, facing +Z; or on a sphere around the
    /// centre for a 360 one (its top half only, for top-bottom stereo).
    /// </summary>
    static GameObject Picture(TunnelAssetCatalog.Asset asset, Texture texture, int width, int height, Transform parent)
    {
        var root = new GameObject(asset.id);
        root.transform.SetParent(parent, false);
        var instance = root.AddComponent<TunnelAssetInstance>();
        instance.Name(asset.id);
        instance.surround = asset.isSurround;

        var material = instance.Own(PictureMaterial(texture));
        material.name = asset.id;
        Mesh mesh;
        if (asset.isSurround)
        {
            mesh = instance.Own(SurroundSphere(TunnelAssetSettings.current.surroundRadius));
            if (asset.isTopBottom)
                material.SetTextureScale("_MainTex", new Vector2(1f, 0.5f)); // the left eye (top) for both
            if (asset.isTopBottom)
                material.SetTextureOffset("_MainTex", new Vector2(0f, 0.5f));
        }
        else
            mesh = instance.Own(FrontQuad(height / (float)Mathf.Max(1, width)));
        root.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = root.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        return root;
    }

    /// <summary>A quad 1 wide and this tall, read from +Z (the box's front): left and right as the viewer sees them.</summary>
    static Mesh FrontQuad(float aspect)
    {
        float w = 0.5f, h = aspect * 0.5f;
        var mesh = new Mesh { name = "Picture" };
        // Seen from +Z looking toward -Z, the viewer's right is -X.
        mesh.vertices = new[] { new Vector3(w, -h, 0f), new Vector3(-w, -h, 0f), new Vector3(w, h, 0f), new Vector3(-w, h, 0f) };
        mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
        mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// A sphere seen from inside, mapped equirectangularly: the picture's centre straight ahead of whoever faces the
    /// box's front from in front of it (-Z), its right to their right (-X), its top up.
    /// </summary>
    static Mesh SurroundSphere(float radius, int columns = 96, int rows = 48)
    {
        var vertices = new Vector3[(columns + 1) * (rows + 1)];
        var uv = new Vector2[vertices.Length];
        for (int r = 0; r <= rows; r++)
        {
            float v = r / (float)rows;
            float lat = (v - 0.5f) * Mathf.PI;
            for (int c = 0; c <= columns; c++)
            {
                float u = c / (float)columns;
                float lon = (u - 0.5f) * 2f * Mathf.PI;
                int i = r * (columns + 1) + c;
                vertices[i] = radius * new Vector3(-Mathf.Sin(lon) * Mathf.Cos(lat), Mathf.Sin(lat), -Mathf.Cos(lon) * Mathf.Cos(lat));
                uv[i] = new Vector2(u, v);
            }
        }
        var triangles = new int[columns * rows * 6];
        int t = 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                int a = r * (columns + 1) + c, b = a + 1, d = a + columns + 1, e = d + 1;
                triangles[t++] = a; triangles[t++] = e; triangles[t++] = b;
                triangles[t++] = a; triangles[t++] = d; triangles[t++] = e;
            }
        var mesh = new Mesh { name = "360 sphere", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = vertices, uv = uv, triangles = triangles };
        mesh.RecalculateBounds();
        return mesh;
    }

    [Serializable]
    class InstanceList
    {
        public string schema;
        public string units;
        public double meters_per_unit;
        public Instance[] instances;
    }

    [Serializable]
    class Instance
    {
        public string protein;
        public float[] position;
        public float[] rotation;
    }

    /// <summary>
    /// A virus (or any assembly) from an instance list: each protein's glb, loaded once and placed at each instance's
    /// position and rotation (glTF frame, the list's units, which its proteins share).
    /// </summary>
    static async Task<GameObject> LoadInstances(TunnelAssetCatalog.Asset asset, Transform parent, Action<float> progress)
    {
        string path = await Download(asset);
        InstanceList list;
        try
        {
            list = JsonUtility.FromJson<InstanceList>(await File.ReadAllTextAsync(path));
        }
        catch (Exception e)
        {
            throw new LoadException($"{asset.title}: its instance list can't be read ({e.Message})");
        }
        if (list?.instances == null || list.instances.Length == 0)
            throw new LoadException($"{asset.title}: its instance list is empty");

        var root = new GameObject(asset.id);
        root.transform.SetParent(parent, false);
        root.AddComponent<TunnelAssetInstance>().Name(asset.id);
        int done = 0;
        foreach (var instance in list.instances)
        {
            GameObject model;
            try
            {
                model = await Load(instance.protein, root.transform, p => progress?.Invoke(p * 0.9f));
            }
            catch
            {
                if (root != null)
                    UnityEngine.Object.Destroy(root);
                throw;
            }
            if (root == null)
            {
                UnityEngine.Object.Destroy(model);
                throw new LoadException($"{asset.title} was closed while it loaded");
            }
            // glTF to Unity, as glTFast turns the proteins' own meshes: x mirrored.
            var p = instance.position is { Length: 3 } ? instance.position : new float[3];
            var q = instance.rotation is { Length: 4 } ? instance.rotation : new[] { 0f, 0f, 0f, 1f };
            model.transform.SetLocalPositionAndRotation(new Vector3(-p[0], p[1], p[2]), new Quaternion(q[0], -q[1], -q[2], q[3]));
            done++;
            progress?.Invoke(0.9f + 0.1f * done / list.instances.Length);
        }
        Debug.Log($"[Assets] {asset.id} loaded: {done} instances of {string.Join(", ", asset.proteins ?? Array.Empty<string>())}");
        return root;
    }
}
