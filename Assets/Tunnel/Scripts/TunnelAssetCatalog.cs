using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// The list of 3D assets in the Tunnel's asset repository (github.com/autinlab/TheTunnelAsset): portraits of David
/// Goodsell, Art and the Scripps building in 1924, as meshes and Gaussian splats. Each headset reads the repository's
/// assets.json when the app starts, so an asset added to the repository shows up in the app (on a splat-box's menu)
/// without a new build. The last list read is kept on the headset, so the list is there offline too.
/// Only meshes in glTF binary (.glb) can be shown for now; splats and .usdz files are listed but skipped.
/// See <see cref="TunnelAssets"/> for loading one and putting it in the tunnel.
/// </summary>
public static class TunnelAssetCatalog
{
    /// <summary>Where the list is read from.</summary>
    public const string k_IndexUrl = "https://raw.githubusercontent.com/autinlab/TheTunnelAsset/main/assets.json";

    /// <summary>The most triangles a mesh can have and still draw smoothly on a Quest next to everything else.</summary>
    public const long k_QuestSafeFaces = 300_000;

    /// <summary>Above this many triangles, loading a mesh logs a warning and its menu row says it is heavy.</summary>
    public const long k_HeavyFaces = 500_000;

    /// <summary>The suffix of a mesh's lighter version made for the Quest, which the headset loads in its place.</summary>
    public const string k_QuestSuffix = "_quest";

    /// <summary>One asset in the list, as assets.json describes it.</summary>
    [Serializable]
    public class Asset
    {
        public string id;
        public string kind;      // "mesh" or "splat"
        public string subject;   // "art", "david-goodsell", "scripps-1924"
        public string title;
        public string[] tags;
        public string format;    // "gltf-binary", "usdz", "gaussian-splat-ply"
        public string path;
        public string url;       // the file itself
        public string cdn_url;   // a faster copy, for files under 20 MB (empty otherwise)
        public long bytes;
        public string sha256;
        public string thumbnail; // a small JPG
        public long vertices;
        public long faces;
        public string color;     // "vertex" or "texture"
        public Coordinates coordinates;
        public License license;

        /// <summary>A mesh the headset can load (glTF binary).</summary>
        public bool isLoadableMesh => kind == "mesh" && format == "gltf-binary" && !string.IsNullOrEmpty(url);

        /// <summary>Few enough triangles to draw smoothly on a Quest.</summary>
        public bool isQuestSafe => faces <= k_QuestSafeFaces;

        /// <summary>So many triangles that the headset may slow down while it is shown.</summary>
        public bool isHeavy => faces > k_HeavyFaces;

        public bool HasTag(string tag) => tags != null && tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

        /// <summary>The credit to show beside it, or "" when its license asks for none.</summary>
        public string attribution => license == null ? ""
            : !string.IsNullOrEmpty(license.attribution) ? license.attribution
            : !string.IsNullOrEmpty(license.name) ? $"{title}, {license.name}{(string.IsNullOrEmpty(license.author) ? "" : " by " + license.author)}" : "";
    }

    [Serializable]
    public class Coordinates
    {
        public string convention;
        public string x, y, z;
        public string units;
    }

    [Serializable]
    public class License
    {
        public string name;
        public string url;
        public string author;
        public string attribution;
    }

    [Serializable]
    class Index
    {
        public string schema;
        public string base_url;
        public string updated;
        public Asset[] assets;
    }

    static Asset[] s_Assets = Array.Empty<Asset>();
    static Task<bool> s_Reading;

    /// <summary>Every asset in the list (empty until it has been read).</summary>
    public static IReadOnlyList<Asset> all => s_Assets;

    /// <summary>The list has been read (from the repository, or the copy kept on the headset).</summary>
    public static bool ready { get; private set; }

    /// <summary>When the list was last changed in the repository (its "updated" date), or "".</summary>
    public static string updated { get; private set; } = "";

    /// <summary>Called on the main thread whenever a new list has been read.</summary>
    public static event Action changed;

    /// <summary>The copy of the list kept on the headset.</summary>
    static string CachedIndexPath => Path.Combine(TunnelAssets.CacheFolder, "assets.json");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ReadAtStart() => _ = Refresh();

    /// <summary>
    /// Reads the list once (later calls wait for the same reading). True if there is a list to use, from the repository
    /// or, offline, from the copy kept on the headset.
    /// </summary>
    public static Task<bool> Ready() => s_Reading ??= Read();

    /// <summary>Reads the list again from the repository, for assets added since the app started.</summary>
    public static Task<bool> Refresh()
    {
        s_Reading = Read();
        return s_Reading;
    }

    static async Task<bool> Read()
    {
        string json = null;
        using (var request = UnityWebRequest.Get(k_IndexUrl))
        {
            request.timeout = 20;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
                await Awaitable.NextFrameAsync();
            if (request.result == UnityWebRequest.Result.Success)
            {
                json = request.downloadHandler.text;
                try
                {
                    Directory.CreateDirectory(TunnelAssets.CacheFolder);
                    File.WriteAllText(CachedIndexPath, json);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Assets] couldn't keep the asset list on the headset: {e.Message}");
                }
            }
            else
                Debug.LogWarning($"[Assets] couldn't read {k_IndexUrl}: {request.error}");
        }

        bool fromCache = false;
        if (json == null && File.Exists(CachedIndexPath))
        {
            json = File.ReadAllText(CachedIndexPath);
            fromCache = true;
        }
        if (json == null)
        {
            Debug.LogWarning("[Assets] no asset list: offline, and none kept from before");
            return ready;
        }

        try
        {
            var index = JsonUtility.FromJson<Index>(json);
            s_Assets = (index?.assets ?? Array.Empty<Asset>()).Where(a => a != null && !string.IsNullOrEmpty(a.id)).ToArray();
            updated = index?.updated ?? "";
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Assets] couldn't read the asset list: {e.Message}");
            return ready;
        }
        ready = true;
        Debug.Log($"[Assets] {s_Assets.Length} assets ({meshes.Count()} loadable meshes), updated {updated}{(fromCache ? ", from the copy on the headset" : "")}");
        changed?.Invoke();
        return true;
    }

    /// <summary>The asset with this id, or null.</summary>
    public static Asset Get(string id) => string.IsNullOrEmpty(id) ? null : s_Assets.FirstOrDefault(a => a.id == id);

    /// <summary>The assets of one subject: "art", "david-goodsell" or "scripps-1924".</summary>
    public static IEnumerable<Asset> BySubject(string subject) => s_Assets.Where(a => a.subject == subject);

    /// <summary>The assets with this tag (any case).</summary>
    public static IEnumerable<Asset> ByTag(string tag) => s_Assets.Where(a => a.HasTag(tag));

    /// <summary>Every mesh the headset can load (glTF binary).</summary>
    public static IEnumerable<Asset> meshes => s_Assets.Where(a => a.isLoadableMesh);

    /// <summary>Only those with few enough triangles for a Quest (<see cref="k_QuestSafeFaces"/>).</summary>
    public static IEnumerable<Asset> QuestSafe(this IEnumerable<Asset> assets) => assets.Where(a => a.isQuestSafe);

    /// <summary>
    /// What to load for this id: on a headset, its lighter Quest version (the same id ending in "_quest") when there is
    /// one; otherwise the asset itself. Null if the id isn't in the list.
    /// </summary>
    public static Asset Resolve(string id)
    {
        var asset = Get(id);
        if (asset == null || !Application.isMobilePlatform || id.EndsWith(k_QuestSuffix))
            return asset;
        var quest = Get(id + k_QuestSuffix);
        return quest != null && quest.isLoadableMesh ? quest : asset;
    }

    /// <summary>
    /// The meshes to offer on a menu: every loadable mesh, except one with a Quest version when on a headset (that
    /// version is offered instead), lighter ones first.
    /// </summary>
    public static IEnumerable<Asset> MenuMeshes() => meshes
        .Where(a => !Application.isMobilePlatform || a.id.EndsWith(k_QuestSuffix) || Get(a.id + k_QuestSuffix) == null)
        .OrderBy(a => a.isQuestSafe ? 0 : a.isHeavy ? 2 : 1);
}
