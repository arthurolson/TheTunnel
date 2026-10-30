using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>The four kinds of media the Viewer shows, each with its own folder and menu list.</summary>
public enum ViewerMediaKind : byte
{
    Image,
    Video,
    SpatialImage,
    SpatialVideo,
}

/// <summary>
/// What the Viewer can show, from two places, one folder per kind of media:
///  - Built in: Assets/StreamingAssets/Viewer/&lt;Images | Videos | Spatial Images | Spatial Videos&gt;, shipped in every
///    build (Tunnel/Editor/ViewerMediaIndex.cs writes the list, index.json, since an APK's folders can't be listed).
///  - This headset: Android/data/&lt;package&gt;/files/Viewer/&lt;same folders&gt; (push-media.sh copies files there), so new
///    media needs no rebuild. Only headsets that have a file can show it, so prefer built-in media or links for sharing.
///  - The Quest's own photos and videos: only its screenshots and recordings (Oculus/Screenshots, Oculus/VideoShots;
///    other folders such as Pictures or Download are not listed), newest first; spatial ones (by their
///    name or what the file says, see <see cref="ViewerMediaProbe"/>) go into the Spatial lists. Reading them needs the
///    headset's photos-and-videos permission, asked for the first time a list is opened.
/// A folder can hold the media itself or a link to it: a .url, .webloc or .txt file whose text contains an http(s)
/// address. Either way the entry's name is the file name without its extension.
/// Spatial media is stereo: side by side (left eye on the left) unless the name says otherwise; see <see cref="StereoLayoutFor"/>.
/// </summary>
public static class ViewerLibrary
{
    public const string FolderName = "Viewer";
    public const string IndexFile = "index.json";

    public static readonly string[] KindFolders = { "Images", "Videos", "Spatial Images", "Spatial Videos" };
    public static readonly string[] KindLabels = { "Images", "Videos", "Spatial Images", "Spatial Videos" };

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".mpo" };
    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".webm", ".mkv" };
    public static readonly string[] LinkExtensions = { ".url", ".webloc", ".txt" };

    static readonly Regex k_Url = new(@"https?://[^\s""<>]+", RegexOptions.IgnoreCase);

    [Serializable]
    public class Entry
    {
        public string name;
        public ViewerMediaKind kind;
        [Tooltip("A URL, or a path relative to the kind's folder.")]
        public string location;
        public bool isLink;
        public bool onHeadset;
        [Tooltip("Set for the Quest's own photos and videos: where the file is.")]
        public string fullPath;
        public long modified;

        public bool isDeviceMedia => !string.IsNullOrEmpty(fullPath);
    }

    [Serializable]
    public class Index
    {
        public List<Entry> entries = new();
    }

    static List<Entry> s_BuiltIn;
    static readonly List<Entry> s_All = new();

    public static string BuiltInRoot => Path.Combine(Application.streamingAssetsPath, FolderName);
    public static string HeadsetRoot => Path.Combine(Application.persistentDataPath, FolderName);

    public static bool IsVideoKind(ViewerMediaKind kind) => kind is ViewerMediaKind.Video or ViewerMediaKind.SpatialVideo;
    public static bool IsSpatialKind(ViewerMediaKind kind) => kind is ViewerMediaKind.SpatialImage or ViewerMediaKind.SpatialVideo;
    public static bool IsUrl(string text) => text != null && Regex.IsMatch(text.Trim(), @"^(https?|file)://", RegexOptions.IgnoreCase);

    /// <summary>Every entry, built-in first then this headset's, sorted by name within each kind.</summary>
    public static IReadOnlyList<Entry> all => s_All;

    public static IEnumerable<Entry> OfKind(ViewerMediaKind kind) => s_All.Where(e => e.kind == kind);

    /// <summary>Reads the built-in list (once) and this headset's folders (every time, creating them if needed).</summary>
    /// <summary>The Quest's own media folders (shared storage), each with the kinds it may hold.</summary>
    static readonly (string path, bool images, bool videos)[] k_DeviceFolders =
    {
        ("/sdcard/Oculus/Screenshots", true, false),
        ("/sdcard/Oculus/VideoShots", false, true),
    };

    const string k_ReadImages = "android.permission.READ_MEDIA_IMAGES";
    const string k_ReadVideos = "android.permission.READ_MEDIA_VIDEO";
    static bool s_PermissionAsked;

    /// <param name="askPermission">Ask for the photos-and-videos permission if it hasn't been asked this session.</param>
    public static IEnumerator Refresh(bool askPermission = false)
    {
        if (askPermission)
            yield return AskForDeviceMedia();

        if (s_BuiltIn == null)
        {
            var builtIn = new List<Entry>();
            yield return LoadBuiltIn(builtIn);
            s_BuiltIn = builtIn;
        }

        s_All.Clear();
        s_All.AddRange(s_BuiltIn);
        foreach (var entry in ScanFolder(HeadsetRoot, true, true))
        {
            if (!s_All.Any(e => e.kind == entry.kind && string.Equals(e.name, entry.name, StringComparison.OrdinalIgnoreCase)))
                s_All.Add(entry);
        }

        s_All.AddRange(ScanDeviceMedia());

        // By kind; the app's own media by name, then the Quest's photos and videos, newest first.
        s_All.Sort((a, b) =>
        {
            if (a.kind != b.kind)
                return a.kind.CompareTo(b.kind);
            if (a.isDeviceMedia != b.isDeviceMedia)
                return a.isDeviceMedia ? 1 : -1;
            return a.isDeviceMedia ? b.modified.CompareTo(a.modified) : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        });
    }

    static IEnumerator LoadBuiltIn(List<Entry> into)
    {
        // Desktop and Editor can list the folders; an APK's can't, so it reads the list written at build time.
        if (Application.platform != RuntimePlatform.Android)
        {
            into.AddRange(ScanFolder(BuiltInRoot, false, false));
            yield break;
        }

        using var request = UnityWebRequest.Get(Path.Combine(BuiltInRoot, IndexFile));
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[Viewer] no built-in media list ({request.error})");
            yield break;
        }

        var index = JsonUtility.FromJson<Index>(request.downloadHandler.text);
        if (index?.entries != null)
            into.AddRange(index.entries);
    }

    /// <summary>The entries in one root's four kind folders (created when <paramref name="create"/> is set).</summary>
    public static List<Entry> ScanFolder(string root, bool create, bool onHeadset)
    {
        var entries = new List<Entry>();
        for (int k = 0; k < KindFolders.Length; k++)
        {
            string folder = Path.Combine(root, KindFolders[k]);
            try
            {
                if (create)
                    Directory.CreateDirectory(folder);
                if (!Directory.Exists(folder))
                    continue;

                foreach (string file in Directory.GetFiles(folder))
                {
                    var entry = EntryForFile(file, (ViewerMediaKind)k);
                    if (entry == null)
                        continue;
                    entry.onHeadset = onHeadset;
                    entries.Add(entry);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Viewer] can't read {folder}: {e.Message}");
            }
        }

        return entries;
    }

    /// <summary>A file in a kind's folder as an entry: media of that kind, or a link file; null for anything else.</summary>
    public static Entry EntryForFile(string file, ViewerMediaKind kind)
    {
        string extension = Path.GetExtension(file).ToLowerInvariant();
        string name = Path.GetFileNameWithoutExtension(file);
        if (name.StartsWith(".") || name.Length == 0 || string.Equals(name, "README", StringComparison.OrdinalIgnoreCase))
            return null;

        if (LinkExtensions.Contains(extension))
        {
            string url = ReadLink(file);
            return url == null ? null : new Entry { name = name, kind = kind, location = url, isLink = true };
        }

        var accepted = IsVideoKind(kind) ? VideoExtensions : ImageExtensions;
        return accepted.Contains(extension) ? new Entry { name = name, kind = kind, location = Path.GetFileName(file) } : null;
    }

    static string ReadLink(string file)
    {
        try
        {
            var match = k_Url.Match(File.ReadAllText(file));
            return match.Success ? match.Value.TrimEnd('.', ',', ';', ')') : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The address a loader opens for an entry: its URL, or its file in the right root.</summary>
    public static string AddressOf(Entry entry)
    {
        if (entry.isDeviceMedia)
            return "file://" + entry.fullPath;
        if (entry.isLink)
            return entry.location;
        string path = Path.Combine(entry.onHeadset ? HeadsetRoot : BuiltInRoot, KindFolders[(int)entry.kind], entry.location);
        // StreamingAssets on Android is already a jar: URL; everything else is a plain file.
        return path.Contains("://") ? path : "file://" + path;
    }

    static IEnumerator AskForDeviceMedia()
    {
        if (s_PermissionAsked || Application.platform != RuntimePlatform.Android
            || (UnityEngine.Android.Permission.HasUserAuthorizedPermission(k_ReadImages)
                && UnityEngine.Android.Permission.HasUserAuthorizedPermission(k_ReadVideos)))
            yield break;

        s_PermissionAsked = true;
        bool answered = false;
        var callbacks = new UnityEngine.Android.PermissionCallbacks();
        callbacks.PermissionGranted += _ => answered = true;
        callbacks.PermissionDenied += _ => answered = true;
        UnityEngine.Android.Permission.RequestUserPermissions(new[] { k_ReadImages, k_ReadVideos }, callbacks);

        // One answer covers both (the headset asks once for photos and videos); give up if the prompt is dismissed.
        float giveUp = Time.realtimeSinceStartup + 30f;
        while (!answered && Time.realtimeSinceStartup < giveUp)
            yield return null;
        yield return null;
        Debug.Log($"[Viewer] photos and videos permission: images {UnityEngine.Android.Permission.HasUserAuthorizedPermission(k_ReadImages)}, " +
            $"videos {UnityEngine.Android.Permission.HasUserAuthorizedPermission(k_ReadVideos)}");
    }

    /// <summary>The Quest's photos and videos it lets the app see (none in the Editor).</summary>
    static List<Entry> ScanDeviceMedia()
    {
        var entries = new List<Entry>();
        if (Application.platform != RuntimePlatform.Android)
            return entries;

        var seen = new HashSet<string>();
        foreach (var (path, images, videos) in k_DeviceFolders)
        {
            try
            {
                if (!Directory.Exists(path))
                    continue;
                // The folder and the one below it.
                var files = Directory.GetFiles(path).Concat(Directory.GetDirectories(path)
                    .Where(d => !Path.GetFileName(d).StartsWith("."))
                    .SelectMany(d => SafeFiles(d)));
                foreach (string file in files)
                {
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    bool isVideo = VideoExtensions.Contains(extension);
                    bool isImage = ImageExtensions.Contains(extension);
                    if (!(isVideo && videos) && !(isImage && images) || !seen.Add(file))
                        continue;

                    string name = Path.GetFileNameWithoutExtension(file);
                    // Spatial by name, or by what the file says it is (Quest 3D captures, MPO pairs, 3D tags).
                    bool stereo = StereoLayoutFor(name, 0, 0) != StereoLayout.Mono || ViewerMediaProbe.Probe(file).isSpatial;
                    var kind = isVideo
                        ? stereo ? ViewerMediaKind.SpatialVideo : ViewerMediaKind.Video
                        : stereo ? ViewerMediaKind.SpatialImage : ViewerMediaKind.Image;
                    entries.Add(new Entry
                    {
                        name = name,
                        kind = kind,
                        location = Path.GetFileName(file),
                        fullPath = file,
                        onHeadset = true,
                        modified = File.GetLastWriteTimeUtc(file).Ticks,
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Viewer] can't read {path}: {e.Message}");
            }
        }

        return entries;
    }

    static IEnumerable<string> SafeFiles(string folder)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public static Entry Find(ViewerMediaKind kind, string name) =>
        s_All.FirstOrDefault(e => e.kind == kind && string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What typed text names: an entry with exactly that name (the preferred kind first), else the only entry whose
    /// name starts with or contains it. Null when nothing or several things match.
    /// </summary>
    public static Entry Match(string text, ViewerMediaKind? preferred)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        IEnumerable<Entry> Ordered(IEnumerable<Entry> entries) =>
            preferred.HasValue ? entries.OrderBy(e => e.kind == preferred.Value ? 0 : 1) : entries;

        var exact = Ordered(s_All.Where(e => string.Equals(e.name, text, StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
        if (exact != null)
            return exact;

        foreach (var test in new Func<Entry, bool>[]
                 {
                     e => e.name.StartsWith(text, StringComparison.OrdinalIgnoreCase),
                     e => e.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0,
                 })
        {
            var hits = s_All.Where(test).ToList();
            if (preferred.HasValue && hits.Count(e => e.kind == preferred.Value) == 1)
                return hits.First(e => e.kind == preferred.Value);
            if (hits.Count == 1)
                return hits[0];
            if (hits.Count > 1)
                return null;
        }

        return null;
    }

    /// <summary>The kind a typed URL most likely is: video or image by its extension, spatial if the name says so.</summary>
    public static ViewerMediaKind KindForUrl(string url, ViewerMediaKind? preferred)
    {
        string path = url.Split('?', '#')[0].ToLowerInvariant();
        bool video = VideoExtensions.Any(path.EndsWith) || path.Contains(".m3u8");
        bool image = ImageExtensions.Any(path.EndsWith);
        bool spatial = StereoLayoutFor(path, 0, 0) != StereoLayout.Mono
            || (preferred.HasValue && IsSpatialKind(preferred.Value));

        if (!video && !image && preferred.HasValue)
            return preferred.Value;
        if (video)
            return spatial ? ViewerMediaKind.SpatialVideo : ViewerMediaKind.Video;
        return spatial ? ViewerMediaKind.SpatialImage : ViewerMediaKind.Image;
    }

    public enum StereoLayout
    {
        Mono,
        SideBySide,
        TopBottom,
        /// <summary>Two separate pictures, one per eye (an MPO stereo photo).</summary>
        Pair,
    }

    /// <summary>
    /// How a stereo frame is split. Names (file name or URL) decide when they say: "_tb", "_ou", "top-bottom",
    /// "over-under" mean top and bottom; "_sbs", "_lr", "_rl", "side-by-side" mean side by side. Otherwise a frame
    /// taller than wide is top and bottom, anything else side by side. Returns Mono for a name with no stereo tag when
    /// the size is unknown (0).
    /// </summary>
    public static StereoLayout StereoLayoutFor(string name, int width, int height)
    {
        string n = (name ?? string.Empty).ToLowerInvariant();
        if (Regex.IsMatch(n, @"(^|[^a-z])(tb|ou|tab|top-?bottom|over-?under)([^a-z]|$)"))
            return StereoLayout.TopBottom;
        if (Regex.IsMatch(n, @"(^|[^a-z])(sbs|hsbs|lr|rl|side-?by-?side)([^a-z]|$)"))
            return StereoLayout.SideBySide;
        if (width <= 0 || height <= 0)
            return StereoLayout.Mono;
        return height > width ? StereoLayout.TopBottom : StereoLayout.SideBySide;
    }

    /// <summary>Right eye first ("_rl").</summary>
    public static bool SwapsEyes(string name) => Regex.IsMatch((name ?? string.Empty).ToLowerInvariant(), @"(^|[^a-z])rl([^a-z]|$)");

    /// <summary>Half-width side by side ("_hsbs", "_half"): each eye was squeezed to half width.</summary>
    public static bool IsHalfStereo(string name) => Regex.IsMatch((name ?? string.Empty).ToLowerInvariant(), @"(^|[^a-z])(hsbs|htb|hou|half)([^a-z]|$)");
}
