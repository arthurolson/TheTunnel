using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using UnityEngine;
using UnityEngine.Networking;
using UnityMaterial = UnityEngine.Material;

/// <summary>
/// Loads an asset from the Tunnel's asset repository (see <see cref="TunnelAssetCatalog"/>) by its id and puts it in
/// the tunnel. Each file is downloaded once per headset and kept there by its checksum, so after the first time it
/// appears at once, offline too. A mesh is drawn as David Goodsell's portrait is: unlit, in its own vertex colours or
/// texture, exactly as captured. Meshes built into the app (the splat library's David Goodsell) need no download.
///
/// From other scripts:
/// <code>
/// // Everyone sees it: a splat-box with the asset, in front of whoever calls it (works offline too).
/// TunnelAssets.SpawnInFront("scripps_1924_splatlook_quest");
/// TunnelAssets.SpawnInFront("art_video_fullbody_glb", height: 1.8f); // 1.8 m tall
/// TunnelAssets.Spawn("goodsell_portrait_mesh", position, rotation);
///
/// // Only on this headset: the model itself, to place yourself. Destroying it frees its memory.
/// GameObject model = await TunnelAssets.Load("art_photo_mesh", parent, progress => Debug.Log(progress));
/// </code>
/// </summary>
public static class TunnelAssets
{
    /// <summary>Where the files are kept on the headset.</summary>
    public static string CacheFolder => Path.Combine(Application.persistentDataPath, "TunnelAssets");

    /// <summary>
    /// How tall some assets are made when they are put in the tunnel (meters), for those captured at no particular
    /// scale. Art's full-body capture is about 1.35 m tall as captured. Change these to suit.
    /// </summary>
    public static readonly Dictionary<string, float> targetHeights = new()
    {
        { "art_video_fullbody_glb", 1.75f },
    };

    /// <summary>The height an asset is given when put in the tunnel (meters), or 0 to keep its box's size.</summary>
    public static float TargetHeight(string assetId) => assetId != null && targetHeights.TryGetValue(assetId, out var h) ? h : 0f;

    static readonly Dictionary<string, SplatLibrary.Splat> s_BuiltIn = new();
    static UnityMaterial s_Template;

    /// <summary>
    /// Makes the library's meshes loadable by their asset id without a download, and takes its material (Tunnel/Splat
    /// Unlit) as the one every loaded mesh is drawn with. Done by each splat-box.
    /// </summary>
    public static void Register(SplatLibrary library)
    {
        if (library == null)
            return;
        if (library.material != null)
            s_Template = library.material;
        foreach (var splat in library.splats)
            if (splat != null && splat.prefab != null && !string.IsNullOrEmpty(splat.assetId))
                s_BuiltIn[splat.assetId] = splat;
    }

    /// <summary>The library entry built into the app for this asset id, or null.</summary>
    public static SplatLibrary.Splat BuiltIn(string assetId) => assetId != null && s_BuiltIn.TryGetValue(assetId, out var s) ? s : null;

    /// <summary>The name to show for an asset: its library name or its title in the list, else its id.</summary>
    public static string DisplayName(string assetId)
    {
        var builtIn = BuiltIn(assetId);
        if (builtIn != null)
            return builtIn.name;
        var asset = TunnelAssetCatalog.Get(assetId);
        return asset != null && !string.IsNullOrEmpty(asset.title) ? asset.title : assetId;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Putting one in the tunnel, for everyone

    /// <summary>
    /// Puts a splat-box showing this asset in the tunnel, for everyone (just here when not connected). Its height in
    /// meters, or 0 to keep the box's usual size, or -1 for the asset's own (<see cref="targetHeights"/>).
    /// Every headset then downloads it (or takes it from its own copy); the box shows a placeholder until then.
    /// </summary>
    public static bool Spawn(string assetId, Vector3 position, Quaternion rotation, float height = -1f)
    {
        var menu = TunnelContainerMenu.ForAssets();
        if (menu == null)
        {
            Debug.LogWarning($"[Assets] can't place {assetId}: no Containers menu in the scene");
            return false;
        }
        menu.PlaceAsset(assetId, position, rotation, height < 0f ? TargetHeight(assetId) : height);
        return true;
    }

    /// <summary>
    /// Puts a splat-box showing this asset in front of whoever calls it, this far away (meters), its front toward them.
    /// See <see cref="Spawn"/>.
    /// </summary>
    public static bool SpawnInFront(string assetId, float distance = 1f, float height = -1f)
    {
        var head = Camera.main != null ? Camera.main.transform : null;
        if (head == null)
            return Spawn(assetId, Vector3.forward * distance, Quaternion.identity, height);
        var forward = head.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
        var position = head.position + forward * distance;
        // A splat-box shows its asset toward its +Z: toward the player.
        return Spawn(assetId, position, Quaternion.LookRotation(-forward, Vector3.up), height);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Loading one on this headset

    /// <summary>Thrown by <see cref="Load"/> with a message to show the player.</summary>
    public class LoadException : Exception
    {
        public LoadException(string message) : base(message) { }
    }

    static readonly Dictionary<string, Task<GltfImport>> s_Imports = new();
    static readonly Dictionary<string, int> s_Users = new();

    /// <summary>
    /// The asset's model, under this parent, on this headset only: from the app itself, the copy on the headset, or
    /// downloaded (progress 0 to 1 while it downloads). On a headset an asset with a Quest version loads that instead.
    /// Each model loaded from the same file shares its meshes and textures; destroying the last one frees them.
    /// Throws <see cref="LoadException"/> if it can't be loaded.
    /// </summary>
    public static async Task<GameObject> Load(string assetId, Transform parent = null, Action<float> progress = null)
    {
        var builtIn = BuiltIn(assetId);
        if (builtIn != null)
        {
            var copy = UnityEngine.Object.Instantiate(builtIn.prefab, parent, false);
            copy.name = builtIn.name;
            copy.transform.localRotation = Quaternion.Euler(builtIn.rotation);
            return copy;
        }

        await TunnelAssetCatalog.Ready();
        var asset = TunnelAssetCatalog.Resolve(assetId);
        if (asset == null)
            throw new LoadException(TunnelAssetCatalog.ready ? $"{assetId} isn't in the asset list" : "the asset list can't be read (offline?)");
        if (!asset.isLoadableMesh)
            throw new LoadException($"{asset.title} is a {asset.kind} ({asset.format}); only glTF meshes can be shown so far");
        if (asset.id != assetId)
            Debug.Log($"[Assets] {assetId}: loading its Quest version, {asset.id}");
        if (asset.isHeavy)
            Debug.LogWarning($"[Assets] {asset.id} has {asset.faces:N0} triangles: more than {TunnelAssetCatalog.k_HeavyFaces:N0}, the headset may slow down");

        s_Users[asset.id] = (s_Users.TryGetValue(asset.id, out int n) ? n : 0) + 1;
        GltfImport import;
        try
        {
            // Each caller hears how the one download goes, whoever started it.
            if (!IsCached(asset))
                await Download(asset, progress);
            else
                progress?.Invoke(1f);
            if (!s_Imports.TryGetValue(asset.id, out var importing))
                s_Imports[asset.id] = importing = Import(asset);
            import = await importing;
        }
        catch
        {
            s_Imports.Remove(asset.id); // try again next time
            Release(asset.id);
            throw;
        }

        var root = new GameObject(asset.id);
        root.transform.SetParent(parent, false);
        var holder = root.AddComponent<TunnelAssetInstance>();
        holder.Bind(asset.id);
        if (!await import.InstantiateMainSceneAsync(root.transform))
        {
            UnityEngine.Object.Destroy(root);
            throw new LoadException($"{asset.title} couldn't be put together");
        }

        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            // A mesh without vertex colours takes its colour from its texture alone.
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null && !filter.sharedMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color))
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && material.HasProperty(s_VertexColor))
                        material.SetFloat(s_VertexColor, 0f);
        }
        Debug.Log($"[Assets] {asset.id} loaded: {asset.faces:N0} triangles");
        return root;
    }

    /// <summary>One fewer model uses this asset's file; the last one frees its meshes and textures.</summary>
    internal static void Release(string assetId)
    {
        if (!s_Users.TryGetValue(assetId, out int n))
            return;
        if (--n > 0)
        {
            s_Users[assetId] = n;
            return;
        }
        s_Users.Remove(assetId);
        if (s_Imports.Remove(assetId, out var importing))
            _ = DisposeWhenDone(importing);
    }

    static async Task DisposeWhenDone(Task<GltfImport> importing)
    {
        try
        {
            (await importing)?.Dispose();
        }
        catch
        {
            // It failed: nothing to free.
        }
    }

    static async Task<GltfImport> Import(TunnelAssetCatalog.Asset asset)
    {
        string path = await Download(asset);
        var import = new GltfImport(materialGenerator: new UnlitMaterials(Template()), logger: new ConsoleLogger());
        if (!await import.LoadFile(path, new Uri(path)))
        {
            import.Dispose();
            throw new LoadException($"{asset.title} couldn't be read");
        }
        return import;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The copies kept on the headset

    /// <summary>Where this asset's file is kept on the headset (named by its checksum).</summary>
    public static string CachedPath(TunnelAssetCatalog.Asset asset) =>
        Path.Combine(CacheFolder, (string.IsNullOrEmpty(asset.sha256) ? asset.id : asset.sha256) + ".glb");

    /// <summary>The headset has this asset's file already.</summary>
    public static bool IsCached(TunnelAssetCatalog.Asset asset)
    {
        var file = new FileInfo(CachedPath(asset));
        return file.Exists && (asset.bytes <= 0 || file.Length == asset.bytes);
    }

    static readonly Dictionary<string, Task<string>> s_Downloads = new();
    static readonly Dictionary<string, List<Action<float>>> s_Listeners = new();

    /// <summary>
    /// The file on the headset, downloaded first if it isn't there (progress 0 to 1). Each file downloads once even if
    /// asked for by several boxes at once, and is checked against its checksum.
    /// </summary>
    public static Task<string> Download(TunnelAssetCatalog.Asset asset, Action<float> progress = null)
    {
        string path = CachedPath(asset);
        if (IsCached(asset))
        {
            progress?.Invoke(1f);
            return Task.FromResult(path);
        }
        if (progress != null)
        {
            if (!s_Listeners.TryGetValue(path, out var listeners))
                s_Listeners[path] = listeners = new List<Action<float>>();
            listeners.Add(progress);
        }
        if (!s_Downloads.TryGetValue(path, out var downloading))
            s_Downloads[path] = downloading = DownloadNow(asset, path);
        return downloading;
    }

    static async Task<string> DownloadNow(TunnelAssetCatalog.Asset asset, string path)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            string partial = path + ".part";
            string lastError = "no address to download it from";
            // The faster copy first, when there is one; it can refuse large files, so then the repository itself.
            foreach (var url in new[] { asset.cdn_url, asset.url })
            {
                if (string.IsNullOrEmpty(url))
                    continue;
                Debug.Log($"[Assets] downloading {asset.id} ({asset.bytes / 1e6f:F1} MB) from {url}");
                float started = Time.realtimeSinceStartup;
                using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
                {
                    request.downloadHandler = new DownloadHandlerFile(partial) { removeFileOnAbort = true };
                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        float fraction = asset.bytes > 0 ? Mathf.Clamp01(request.downloadedBytes / (float)asset.bytes) : request.downloadProgress;
                        Report(path, fraction);
                        await Awaitable.NextFrameAsync();
                    }
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        lastError = $"download failed ({request.error})";
                        Debug.LogWarning($"[Assets] {asset.id}: {url}: {request.error}");
                        TryDelete(partial);
                        continue;
                    }
                }
                string hash = await Task.Run(() => Sha256(partial));
                if (!string.IsNullOrEmpty(asset.sha256) && !string.Equals(hash, asset.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    lastError = "the download doesn't match its checksum";
                    Debug.LogWarning($"[Assets] {asset.id}: {url} gave a file with checksum {hash}, not {asset.sha256}");
                    TryDelete(partial);
                    continue;
                }
                TryDelete(path);
                File.Move(partial, path);
                Report(path, 1f);
                Debug.Log($"[Assets] {asset.id} downloaded in {Time.realtimeSinceStartup - started:F1} s, kept as {path}");
                return path;
            }
            throw new LoadException($"{asset.title}: {lastError}");
        }
        finally
        {
            s_Downloads.Remove(path);
            s_Listeners.Remove(path);
        }
    }

    static void Report(string path, float fraction)
    {
        if (!s_Listeners.TryGetValue(path, out var listeners))
            return;
        foreach (var listener in listeners)
        {
            try
            {
                listener(fraction);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }

    static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Assets] couldn't delete {file}: {e.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Thumbnails

    static readonly Dictionary<string, Task<Texture2D>> s_Thumbnails = new();

    /// <summary>The asset's small picture (kept on the headset after the first time), or null if it can't be had.</summary>
    public static Task<Texture2D> Thumbnail(TunnelAssetCatalog.Asset asset)
    {
        if (asset == null || string.IsNullOrEmpty(asset.thumbnail))
            return Task.FromResult<Texture2D>(null);
        if (!s_Thumbnails.TryGetValue(asset.thumbnail, out var loading))
            s_Thumbnails[asset.thumbnail] = loading = LoadThumbnail(asset);
        return loading;
    }

    static async Task<Texture2D> LoadThumbnail(TunnelAssetCatalog.Asset asset)
    {
        string path = Path.Combine(CacheFolder, "thumbs", (string.IsNullOrEmpty(asset.sha256) ? asset.id : asset.sha256) + ".jpg");
        byte[] bytes = null;
        if (File.Exists(path))
            bytes = File.ReadAllBytes(path);
        else
        {
            using var request = UnityWebRequest.Get(asset.thumbnail);
            request.timeout = 20;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
                await Awaitable.NextFrameAsync();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Assets] no thumbnail for {asset.id}: {request.error}");
                s_Thumbnails.Remove(asset.thumbnail); // try again next time
                return null;
            }
            bytes = request.downloadHandler.data;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Assets] couldn't keep the thumbnail of {asset.id}: {e.Message}");
            }
        }
        var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = asset.id + " thumbnail", wrapMode = TextureWrapMode.Clamp };
        if (!texture.LoadImage(bytes, true))
        {
            UnityEngine.Object.Destroy(texture);
            return null;
        }
        return texture;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Materials

    static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int s_MainTex = Shader.PropertyToID("_MainTex");
    static readonly int s_VertexColor = Shader.PropertyToID("_VertexColor");
    static readonly int s_Cull = Shader.PropertyToID("_Cull");

    /// <summary>The material every loaded mesh starts from: Tunnel/Splat Unlit, as David Goodsell's portrait has.</summary>
    internal static UnityMaterial Template()
    {
        if (s_Template == null)
        {
            var shader = Shader.Find("Tunnel/Splat Unlit");
            if (shader == null)
                throw new LoadException("the Tunnel/Splat Unlit shader isn't in the app");
            s_Template = new UnityMaterial(shader) { name = "SplatUnlit (runtime)" };
            s_Template.SetFloat(s_Cull, (float)UnityEngine.Rendering.CullMode.Off);
        }
        return s_Template;
    }

    /// <summary>A material for a picture (the placeholder's thumbnail): Tunnel/Splat Unlit, its texture only.</summary>
    internal static UnityMaterial PictureMaterial(Texture texture)
    {
        var material = new UnityMaterial(Template()) { name = "Picture" };
        material.SetTexture(s_MainTex, texture != null ? texture : Texture2D.grayTexture);
        material.SetFloat(s_VertexColor, 0f);
        material.SetFloat(s_Cull, (float)UnityEngine.Rendering.CullMode.Off);
        return material;
    }

    /// <summary>
    /// Gives glTFast's meshes the Tunnel's unlit material instead of its own lit ones: unlit is how these captures
    /// look right (their light is in their colours), and the app then needs no extra shaders.
    /// </summary>
    class UnlitMaterials : IMaterialGenerator
    {
        readonly UnityMaterial m_Template;

        public UnlitMaterials(UnityMaterial template) => m_Template = template;

        public UnityMaterial GetDefaultMaterial(bool pointsSupport = false)
        {
            var material = new UnityMaterial(m_Template) { name = "Default" };
            material.SetFloat(s_VertexColor, 1f);
            material.SetFloat(s_Cull, (float)UnityEngine.Rendering.CullMode.Off);
            return material;
        }

        public UnityMaterial GenerateMaterial(GLTFast.Schema.MaterialBase gltfMaterial, IGltfReadable gltf, bool pointsSupport = false)
        {
            var material = new UnityMaterial(m_Template) { name = string.IsNullOrEmpty(gltfMaterial.name) ? "Material" : gltfMaterial.name };
            material.SetFloat(s_VertexColor, 1f);
            material.SetFloat(s_Cull, (float)(gltfMaterial.doubleSided ? UnityEngine.Rendering.CullMode.Off : UnityEngine.Rendering.CullMode.Back));
            var pbr = gltfMaterial.PbrMetallicRoughness;
            var info = pbr?.BaseColorTexture;
            var texture = info != null && info.index >= 0 ? gltf.GetTexture(info.index) : null;
            if (texture != null)
            {
                // The picture as captured (some exports darken it with a grey base colour meant for lit shading).
                material.SetTexture(s_MainTex, texture);
                material.SetColor(s_BaseColor, Color.white);
            }
            else
                material.SetColor(s_BaseColor, pbr != null ? pbr.BaseColor.gamma : Color.white);
            return material;
        }

        public void SetLogger(ICodeLogger logger) { }
    }
}
