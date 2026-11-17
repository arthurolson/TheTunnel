using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// The list of assets in the Tunnel's asset repository (by default github.com/arthurolson/TheTunnelAsset): portraits of
/// David Goodsell, Art and the Scripps building in 1924 as meshes and splats, and images, videos, molecules and viruses.
/// Each headset reads the repository's assets.json when the app starts, so an asset pushed to the repository shows up
/// in the app (on a splat-box's menu) without a new build. The last list read is kept on the headset, so the list is
/// there offline too. Which list is read is set in <see cref="TunnelAssetSettings"/>, can be changed on a headset
/// without a build (catalog-url.txt), and in a session follows the host's.
/// Reads schema thetunnel-assets/1 and /2; kinds and fields it doesn't know are skipped, not errors.
/// See <see cref="TunnelAssets"/> for loading one and putting it in the tunnel.
/// </summary>
public static class TunnelAssetCatalog
{
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
        public string kind;      // "mesh", "splat", "image", "video", "molecule", "virus"
        public string subject;
        public string title;
        public string[] tags;
        public string format;    // "gltf-binary", "jpeg", "png", "mp4", "pdb", "mmcif", "instance-list-json", ...
        public string path;
        public string url;       // the file itself
        public string cdn_url;   // a faster copy, for files under 20 MB (empty otherwise)
        public long bytes;
        public string sha256;
        public string thumbnail; // a small JPG
        public long vertices;
        public long faces;
        public string color;     // "vertex", "texture" or "material"
        public int width;        // images and videos
        public int height;
        public float duration;   // videos (seconds)
        public float fps;
        public string codec;
        public string projection; // "equirect" for 360 images and videos
        public string stereo;     // "mono" or "top-bottom"
        public int atoms;         // molecules
        public int chains;
        public string surface;    // a molecule's ready-made glb (an asset id)
        public int instances;     // a virus instance list
        public string[] proteins;
        public Coordinates coordinates;
        public License license;

        public bool isGlb => format == "gltf-binary";

        /// <summary>Shown all round the viewer, on a sphere: a 360 image or video.</summary>
        public bool isSurround => projection == "equirect" || HasTag("equirect");

        /// <summary>A 360 video with the left eye above the right.</summary>
        public bool isTopBottom => stereo == "top-bottom" || HasTag("stereo-tb");

        /// <summary>Something the app can show: glTF meshes, molecules and viruses; images; videos; atomic models with
        /// a ready-made surface; virus instance lists. Splats and .usdz files are not (yet).</summary>
        public bool isLoadable
        {
            get
            {
                if (string.IsNullOrEmpty(url))
                    return false;
                switch (kind)
                {
                    case "mesh": return isGlb;
                    case "image": return format == "jpeg" || format == "png";
                    case "video": return format == "mp4";
                    case "molecule": return isGlb || ((format == "pdb" || format == "mmcif") && !string.IsNullOrEmpty(surface));
                    case "virus": return isGlb || format == "instance-list-json";
                    default: return false;
                }
            }
        }

        /// <summary>Few enough triangles to draw smoothly on a Quest.</summary>
        public bool isQuestSafe => faces <= k_QuestSafeFaces;

        /// <summary>So many triangles that the headset may slow down while it is shown.</summary>
        public bool isHeavy => faces > k_HeavyFaces;

        /// <summary>Meters per unit of its model (1e-10 for molecules in Angstroms), 1 if not given.</summary>
        public double metersPerUnit => coordinates != null && coordinates.meters_per_unit > 0 ? coordinates.meters_per_unit : 1;

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
        public double meters_per_unit;
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
        public string repo;
        public string base_url;
        public string updated;
        public Asset[] assets;
    }

    static Asset[] s_Assets = Array.Empty<Asset>();
    static Task<bool> s_Reading;
    static string s_Source;

    /// <summary>Every asset in the list (empty until it has been read).</summary>
    public static IReadOnlyList<Asset> all => s_Assets;

    /// <summary>The list has been read (from its repository, or the copy kept on the headset).</summary>
    public static bool ready { get; private set; }

    /// <summary>When the list was last changed in its repository (its "updated" date), or "".</summary>
    public static string updated { get; private set; } = "";

    /// <summary>Called on the main thread whenever a new list has been read.</summary>
    public static event Action changed;

    /// <summary>The headset's own choice of list, if it has one (one URL, kept on the headset).</summary>
    static string OverridePath => Path.Combine(TunnelAssets.CacheFolder, "catalog-url.txt");

    /// <summary>
    /// The URL of the asset list in use: the one chosen with <see cref="UseSource"/> (in a session, the host's), else
    /// the headset's catalog-url.txt, else <see cref="TunnelAssetSettings.catalogUrl"/>.
    /// </summary>
    public static string source
    {
        get
        {
            if (string.IsNullOrEmpty(s_Source))
            {
                string chosen = null;
                try
                {
                    if (File.Exists(OverridePath))
                        chosen = File.ReadAllText(OverridePath).Trim();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Assets] couldn't read {OverridePath}: {e.Message}");
                }
                s_Source = IsUrl(chosen) ? chosen : TunnelAssetSettings.current.catalogUrl;
                if (IsUrl(chosen))
                    Debug.Log($"[Assets] using the headset's own asset list: {chosen}");
            }
            return s_Source;
        }
    }

    static bool IsUrl(string s) => !string.IsNullOrEmpty(s) && (s.StartsWith("https://") || s.StartsWith("http://") || s.StartsWith("file://"));

    /// <summary>
    /// Reads the asset list from this URL from now on (an assets.json), and keeps it as the headset's own choice if
    /// <paramref name="remember"/> (until changed again; null or "" goes back to the default). Returns the reading.
    /// </summary>
    public static Task<bool> UseSource(string url, bool remember = false)
    {
        if (remember)
        {
            try
            {
                Directory.CreateDirectory(TunnelAssets.CacheFolder);
                if (IsUrl(url))
                    File.WriteAllText(OverridePath, url);
                else if (File.Exists(OverridePath))
                    File.Delete(OverridePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Assets] couldn't keep the asset list's URL: {e.Message}");
            }
        }
        string next = IsUrl(url) ? url : null;
        if (next == s_Source && s_Reading != null)
            return s_Reading;
        s_Source = next; // null: worked out again from the file and the settings
        Debug.Log($"[Assets] asset list now {source}");
        return Refresh();
    }

    /// <summary>The copy of the list kept on the headset (one per list URL).</summary>
    static string CachedIndexPath(string url)
    {
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url)), 0, 6).Replace("-", "").ToLowerInvariant();
        return Path.Combine(TunnelAssets.CacheFolder, $"assets-{hash}.json");
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ReadAtStart() => _ = Refresh();

    /// <summary>
    /// Reads the list once (later calls wait for the same reading). True if there is a list to use, from its repository
    /// or, offline, from the copy kept on the headset.
    /// </summary>
    public static Task<bool> Ready() => s_Reading ??= Read(source);

    /// <summary>Reads the list again, for assets added since the app started.</summary>
    public static Task<bool> Refresh()
    {
        s_Reading = Read(source);
        return s_Reading;
    }

    static async Task<bool> Read(string url)
    {
        string json = null;
        using (var request = UnityWebRequest.Get(url))
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
                    File.WriteAllText(CachedIndexPath(url), json);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Assets] couldn't keep the asset list on the headset: {e.Message}");
                }
            }
            else
                Debug.LogWarning($"[Assets] couldn't read {url}: {request.error}");
        }

        bool fromCache = false;
        if (json == null && File.Exists(CachedIndexPath(url)))
        {
            json = File.ReadAllText(CachedIndexPath(url));
            fromCache = true;
        }
        if (url != source)
            return ready; // another list was chosen meanwhile
        if (json == null)
        {
            Debug.LogWarning("[Assets] no asset list: offline, and none kept from before");
            return ready;
        }

        try
        {
            var index = JsonUtility.FromJson<Index>(json);
            if (index?.schema != null && !index.schema.StartsWith("thetunnel-assets/"))
                Debug.LogWarning($"[Assets] {url} has schema {index.schema}; reading it anyway");
            s_Assets = (index?.assets ?? Array.Empty<Asset>()).Where(a => a != null && !string.IsNullOrEmpty(a.id)).ToArray();
            updated = index?.updated ?? "";
            Debug.Log($"[Assets] {s_Assets.Length} assets ({s_Assets.Count(a => a.isLoadable)} the app can show) from {url}, schema {index?.schema}, updated {updated}{(fromCache ? ", from the copy on the headset" : "")}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Assets] couldn't read the asset list: {e.Message}");
            return ready;
        }
        ready = true;
        changed?.Invoke();
        return true;
    }

    /// <summary>The asset with this id, or null.</summary>
    public static Asset Get(string id) => string.IsNullOrEmpty(id) ? null : s_Assets.FirstOrDefault(a => a.id == id);

    /// <summary>The assets of one subject, such as "art", "david-goodsell" or "scripps-1924".</summary>
    public static IEnumerable<Asset> BySubject(string subject) => s_Assets.Where(a => a.subject == subject);

    /// <summary>The assets with this tag (any case).</summary>
    public static IEnumerable<Asset> ByTag(string tag) => s_Assets.Where(a => a.HasTag(tag));

    /// <summary>The assets of one kind: "mesh", "image", "video", "molecule", "virus" or "splat".</summary>
    public static IEnumerable<Asset> ByKind(string kind) => s_Assets.Where(a => a.kind == kind);

    /// <summary>Every glTF mesh the headset can load.</summary>
    public static IEnumerable<Asset> meshes => s_Assets.Where(a => a.kind == "mesh" && a.isLoadable);

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
        return quest != null && quest.isLoadable ? quest : asset;
    }

    /// <summary>
    /// The assets to offer on a menu: everything the app can show, meshes first (lighter ones first), then images,
    /// videos, molecules and viruses. Left out: a mesh with a Quest version when on a headset (that version is offered
    /// instead), and a molecule's surface when its atomic model is listed (the model shows it).
    /// </summary>
    public static IEnumerable<Asset> MenuAssets()
    {
        var surfaces = new HashSet<string>(s_Assets.Where(a => a.isLoadable && !string.IsNullOrEmpty(a.surface)).Select(a => a.surface));
        string[] order = { "mesh", "image", "video", "molecule", "virus" };
        return s_Assets
            .Where(a => a.isLoadable && !surfaces.Contains(a.id))
            .Where(a => !Application.isMobilePlatform || a.id.EndsWith(k_QuestSuffix) || Get(a.id + k_QuestSuffix) == null)
            .OrderBy(a => Array.IndexOf(order, a.kind))
            .ThenBy(a => a.isQuestSafe ? 0 : a.isHeavy ? 2 : 1);
    }
}
