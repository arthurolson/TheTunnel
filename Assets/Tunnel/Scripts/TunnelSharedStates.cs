using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Saved states shared between headsets through the asset repository (by default github.com/arthurolson/TheTunnelAsset,
/// in its saved-states folder): a state saved on one headset can be restored on any other, in a session or not.
/// <para>Each headset reads the repository's list of saved states when the app starts and when the Saved states list
/// is opened (at most once a minute), and keeps a copy of the list and of every state on the headset, so they can be
/// restored offline too. The repository is the one the asset list comes from (<see cref="TunnelAssetCatalog.source"/>,
/// its same branch), so in a session it follows the host's.</para>
/// <para>Save now keeps a state on the headset first (<see cref="TunnelStateStore"/>), then puts its .json and a small
/// .txt (name, date, summary) in the repository through GitHub's Contents API. If the headset is offline or GitHub
/// refuses, the state stays on the headset, marked "not uploaded", and is tried again on the next start or the next
/// time the list is opened; it is only moved out once GitHub has it. Writing needs a GitHub token in github-token.txt
/// in the app's own storage (Android/data/edu.scripps.tunnel/files/github-token.txt), put there by hand; it is never in
/// the app or the repository, and never logged. Without one the headset still reads and restores every shared state.</para>
/// <para>Deleting a shared state deletes it from the repository (so from every headset), which also needs the token.
/// The repository is public, so a saved state holds nothing about who made it or on which headset.</para>
/// </summary>
public static class TunnelSharedStates
{
    /// <summary>The repository's folder of saved states.</summary>
    public const string k_Folder = "saved-states";

    const string k_DefaultOwner = "arthurolson";
    const string k_DefaultRepository = "TheTunnelAsset";
    const string k_DefaultBranch = "main";
    const double k_RefreshEverySeconds = 60;

    /// <summary>One state in the repository, as the list read last describes it.</summary>
    [Serializable]
    public class Entry
    {
        public string id;
        public string name;
        public string info;
        public string jsonSha;
        public string txtSha;
        public string jsonUrl;
        public string txtUrl;
        [Tooltip("The jsonSha of the copy kept on the headset, or empty if there's none yet.")]
        public string cachedSha;
    }

    /// <summary>One row of the Saved states list: shared, or saved here and not uploaded yet.</summary>
    public struct Item
    {
        public string id;
        public string name;
        public string info;
        public bool local;
    }

    /// <summary>What a request to GitHub came back with (code 0: no answer, e.g. offline).</summary>
    public struct Reply
    {
        public long code;
        public string text;
        public string error;
        public bool ok => code >= 200 && code < 300;
    }

    [Serializable]
    class CachedList
    {
        public string repository;
        public List<Entry> entries = new();
    }

    // GitHub's description of a file (Contents API).
    [Serializable]
    class FileInfo
    {
        public string name;
        public string path;
        public string sha;
        public string type;
        public string download_url;
    }

    [Serializable]
    class FileList
    {
        public FileInfo[] items;
    }

    [Serializable]
    class PutReply
    {
        public FileInfo content;
    }

    /// <summary>
    /// Sends one request: method, URL, JSON body (or null) and token (or null; only ever sent to api.github.com).
    /// The Editor tests put a fake GitHub here; the app uses <see cref="SendWithUnity"/>.
    /// </summary>
    public static Func<string, string, string, string, Task<Reply>> send = SendWithUnity;

    /// <summary>The token to use in place of github-token.txt ("" for none), for the Editor tests; null reads the file.</summary>
    public static string tokenOverride;

    static List<Entry> s_Entries;
    static string s_EntriesFolder;
    static Task s_Refreshing;
    static Task s_Uploading;
    static bool s_UploadAgain;
    static DateTime s_LastRefresh = DateTime.MinValue;
    static string s_UploadProblem; // why the last upload failed, until one works
    static readonly Dictionary<string, DateTime> s_Uploaded = new(); // when this headset uploaded each, this run

    /// <summary>Called on the main thread when the shared list changes, or a state moves from this headset to it.</summary>
    public static event Action changed;

    /// <summary>How sharing stands, in a line for the Saved states list.</summary>
    public static string status { get; private set; } = "Reading the shared saved states...";

    /// <summary>Where the token goes on a headset: github-token.txt in the app's own storage.</summary>
    public static string TokenPath => Path.Combine(Application.persistentDataPath, "github-token.txt");

    static string CacheFolder => Path.Combine(TunnelStateStore.Folder, "Shared");

    static string CachedListPath => Path.Combine(CacheFolder, "list.json");

    static string CachedStatePath(string id) => Path.Combine(CacheFolder, id + ".json");

    #region Where

    /// <summary>
    /// The repository and branch the shared states are in: those of the asset list in use
    /// (raw.githubusercontent.com/owner/repository/branch/assets.json), else github.com/arthurolson/TheTunnelAsset, main.
    /// </summary>
    public static (string owner, string repository, string branch) repository
    {
        get
        {
            if (Uri.TryCreate(TunnelAssetCatalog.source, UriKind.Absolute, out var uri) && uri.Host == "raw.githubusercontent.com")
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/');
                if (parts.Length >= 4)
                {
                    string branch = parts[2] == "refs" && parts.Length >= 6 && parts[3] == "heads" ? parts[4] : parts[2];
                    return (parts[0], parts[1], Uri.UnescapeDataString(branch));
                }
            }
            return (k_DefaultOwner, k_DefaultRepository, k_DefaultBranch);
        }
    }

    /// <summary>"owner/repository" (and "@branch" when it isn't main), for messages.</summary>
    public static string repositoryName
    {
        get
        {
            var (owner, name, branch) = repository;
            return $"{owner}/{name}" + (branch == k_DefaultBranch ? "" : "@" + branch);
        }
    }

    static string ApiFile(string file)
    {
        var (owner, name, _) = repository;
        return $"https://api.github.com/repos/{owner}/{name}/contents/{k_Folder}/{Uri.EscapeDataString(file)}";
    }

    static string Ref => "?ref=" + Uri.EscapeDataString(repository.branch);

    static string ListUrl
    {
        get
        {
            var (owner, name, _) = repository;
            return $"https://api.github.com/repos/{owner}/{name}/contents/{k_Folder}{Ref}";
        }
    }

    /// <summary>The token on this headset, or null. Never logged.</summary>
    static string Token()
    {
        if (tokenOverride != null)
            return tokenOverride.Length > 0 ? tokenOverride : null;
        try
        {
            if (!File.Exists(TokenPath))
                return null;
            string token = File.ReadAllText(TokenPath).Replace("\r", "").Split('\n')[0].Trim().Trim('\uFEFF', ' ', '"');
            return token.Length > 0 ? token : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>This headset can upload and delete shared states (it has a token; whether GitHub takes it shows on use).</summary>
    public static bool hasToken => Token() != null;

    #endregion

    #region The list

    /// <summary>The shared states as last read (or kept from before, offline), oldest first.</summary>
    public static IReadOnlyList<Entry> entries
    {
        get
        {
            LoadCachedList();
            return s_Entries;
        }
    }

    /// <summary>Every saved state this headset can restore, oldest first: the shared ones, and those saved here and not uploaded yet.</summary>
    public static List<Item> Everything()
    {
        var items = new Dictionary<string, Item>();
        foreach (var entry in entries)
            items[entry.id] = new Item { id = entry.id, name = entry.name, info = entry.info, local = false };
        foreach (var id in TunnelStateStore.Ids())
            items[id] = new Item { id = id, name = TunnelStateStore.Name(id), info = TunnelStateStore.Info(id), local = true };
        // By when they were saved; within one second, by their number (State 3 before State 4).
        return items.Values.OrderBy(i => i.id.Substring(0, 15), StringComparer.Ordinal).ThenBy(i => Number(i.name))
            .ThenBy(i => i.id, StringComparer.Ordinal).ToList();
    }

    static int Number(string name)
    {
        int space = name?.LastIndexOf(' ') ?? -1;
        return space >= 0 && int.TryParse(name.Substring(space + 1), out int n) ? n : 0;
    }

    /// <summary>A state's name for messages (State 3), or its id.</summary>
    public static string NameOf(string id)
    {
        if (TunnelStateStore.Exists(id))
            return TunnelStateStore.Name(id);
        var entry = Find(id);
        return entry != null && !string.IsNullOrEmpty(entry.name) ? entry.name : id;
    }

    /// <summary>The state is in the repository (as last read).</summary>
    public static bool IsShared(string id) => Find(id) != null;

    static Entry Find(string id) => entries.FirstOrDefault(e => e.id == id);

    static void LoadCachedList()
    {
        if (s_Entries != null && s_EntriesFolder == CacheFolder)
            return;
        s_EntriesFolder = CacheFolder;
        s_Entries = new List<Entry>();
        try
        {
            if (File.Exists(CachedListPath))
            {
                var cached = JsonUtility.FromJson<CachedList>(File.ReadAllText(CachedListPath));
                if (cached != null && cached.repository == repositoryName)
                    s_Entries = cached.entries.Where(e => TunnelStateStore.IsId(e.id)).ToList();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't read the shared list kept on the headset: {e.Message}");
        }
    }

    static void SaveCachedList()
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(CachedListPath, JsonUtility.ToJson(new CachedList { repository = repositoryName, entries = s_Entries }));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't keep the shared list on the headset: {e.Message}");
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AtStart() => _ = Refresh(true);

    /// <summary>
    /// Uploads the states saved here that aren't shared yet (if there's a token), then reads the repository's list and
    /// downloads the states not kept here yet. At most once a minute unless <paramref name="force"/>; a refresh already
    /// running is waited for.
    /// </summary>
    public static Task Refresh(bool force = false)
    {
        if (s_Refreshing != null && !s_Refreshing.IsCompleted)
            return s_Refreshing;
        if (!force && (DateTime.UtcNow - s_LastRefresh).TotalSeconds < k_RefreshEverySeconds)
            return Task.CompletedTask;
        s_LastRefresh = DateTime.UtcNow;
        s_Refreshing = Guard(RefreshNow(), "reading the shared list");
        return s_Refreshing;
    }

    static async Task RefreshNow()
    {
        await UploadPending();
        if (await ReadList())
            await Prefetch();
    }

    /// <summary>Reads the repository's saved-states folder. False if it couldn't (offline): the list from before stays.</summary>
    static async Task<bool> ReadList()
    {
        LoadCachedList();
        string token = Token();
        var reply = await send("GET", ListUrl, null, token);
        if (reply.code == 401 && token != null)
            reply = await send("GET", ListUrl, null, null); // a refused token still lets the public list be read
        FileInfo[] files;
        if (reply.code == 404)
            files = Array.Empty<FileInfo>(); // no state shared yet: the folder comes with the first
        else if (!reply.ok)
        {
            Debug.LogWarning($"[SaveState] couldn't read the shared saved states in {repositoryName}: {Describe(reply)}");
            status = reply.code == 0 ? $"Offline: showing the shared states read before{(TunnelStateStore.Ids().Count > 0 ? "; the others upload later" : "")}."
                : reply.code == 403 ? "GitHub is busy (too many reads): showing the shared states read before."
                : $"Couldn't read the shared states ({reply.code}): showing those read before.";
            changed?.Invoke();
            return false;
        }
        else
        {
            try
            {
                files = JsonUtility.FromJson<FileList>("{\"items\":" + reply.text + "}")?.items ?? Array.Empty<FileInfo>();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SaveState] GitHub's list of saved states didn't read: {e.Message}");
                return false;
            }
        }

        var texts = files.Where(f => f.type == "file" && f.name.EndsWith(".txt")).ToDictionary(f => Path.GetFileNameWithoutExtension(f.name));
        var next = new List<Entry>();
        foreach (var file in files.Where(f => f.type == "file" && f.name.EndsWith(".json")))
        {
            string id = Path.GetFileNameWithoutExtension(file.name);
            if (!TunnelStateStore.IsId(id))
                continue;
            texts.TryGetValue(id, out var text);
            var before = s_Entries.FirstOrDefault(e => e.id == id);
            var entry = new Entry
            {
                id = id,
                jsonSha = file.sha,
                jsonUrl = file.download_url,
                txtSha = text?.sha ?? "",
                txtUrl = text?.download_url ?? "",
                cachedSha = before != null && before.cachedSha == file.sha ? before.cachedSha : "",
            };
            if (before != null && before.txtSha == entry.txtSha && !string.IsNullOrEmpty(before.name))
            {
                entry.name = before.name;
                entry.info = before.info;
            }
            else if (text != null)
            {
                var got = await send("GET", text.download_url, null, null);
                if (got.ok)
                    (entry.name, entry.info) = TunnelStateStore.ParseInfo(got.text, id);
            }
            if (string.IsNullOrEmpty(entry.name))
            {
                entry.name = id;
                entry.info = "";
                entry.txtSha = ""; // read again next time
            }
            next.Add(entry);
        }

        // States deleted from the repository (by another headset) go from here too; but one this headset uploaded in
        // the last two minutes may just be newer than the list GitHub sent.
        foreach (var gone in s_Entries.Where(e => next.All(n => n.id != e.id)).ToList())
        {
            if (s_Uploaded.TryGetValue(gone.id, out var when) && (DateTime.UtcNow - when).TotalMinutes < 2)
                next.Add(gone);
            else
                TryDelete(CachedStatePath(gone.id));
        }
        s_Entries = next.OrderBy(e => e.id, StringComparer.Ordinal).ToList();
        SaveCachedList();
        status = StatusAfterReading();
        Debug.Log($"[SaveState] {s_Entries.Count} shared saved state{(s_Entries.Count == 1 ? "" : "s")} in {repositoryName}{(hasToken ? "" : " (no token on this headset: reading only)")}");
        changed?.Invoke();
        return true;
    }

    // How sharing stands once the list is read: an upload's trouble stays shown while saves wait here.
    static string StatusAfterReading()
    {
        int waiting = TunnelStateStore.Ids().Count;
        if (!hasToken)
            return waiting > 0 ? $"No GitHub token on this headset: {waiting} saved state{(waiting == 1 ? " stays" : "s stay")} here (not uploaded)."
                : $"Reading {repositoryName}. No GitHub token here, so new saves stay on this headset.";
        if (waiting > 0 && s_UploadProblem != null)
            return s_UploadProblem;
        return $"Shared with every headset through {repositoryName}.";
    }

    // Downloads the shared states not kept here yet, so they can be restored offline.
    static async Task Prefetch()
    {
        foreach (var entry in s_Entries.ToList())
            if (!IsCached(entry))
                await Download(entry);
    }

    static bool IsCached(Entry entry) => !string.IsNullOrEmpty(entry.cachedSha) && entry.cachedSha == entry.jsonSha && File.Exists(CachedStatePath(entry.id));

    static async Task<string> Download(Entry entry)
    {
        if (string.IsNullOrEmpty(entry.jsonUrl))
            return "it has no address in the repository";
        var reply = await send("GET", entry.jsonUrl, null, null);
        if (!reply.ok)
        {
            Debug.LogWarning($"[SaveState] couldn't download the shared state {entry.id}: {Describe(reply)}");
            return reply.code == 0 ? "this headset is offline" : $"GitHub said {reply.code}";
        }
        var state = TunnelStateStore.Parse(reply.text, entry.id);
        if (state == null)
            return "its file doesn't read";
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(CachedStatePath(entry.id), reply.text);
            entry.cachedSha = entry.jsonSha;
            if (string.IsNullOrEmpty(entry.info))
            {
                (entry.name, entry.info) = TunnelStateStore.ParseInfo(TunnelStateStore.InfoText(state), entry.id);
                changed?.Invoke();
            }
            SaveCachedList();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't keep the shared state {entry.id} on the headset: {e.Message}");
        }
        return null;
    }

    #endregion

    #region Loading

    /// <summary>A state saved here or a shared one already kept here, or null (not saved, or not downloaded yet).</summary>
    public static TunnelSavedState LoadNow(string id)
    {
        if (TunnelStateStore.Exists(id))
            return TunnelStateStore.Load(id);
        var entry = Find(id);
        if (entry == null || !File.Exists(CachedStatePath(id)))
            return null;
        try
        {
            return TunnelStateStore.Parse(File.ReadAllText(CachedStatePath(id)), id);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't read the shared state {id} kept on the headset: {e.Message}");
            return null;
        }
    }

    /// <summary>Any state this headset can restore, downloading a shared one first if need be; or why it can't.</summary>
    public static async Task<(TunnelSavedState state, string error)> Load(string id)
    {
        var state = LoadNow(id);
        if (state != null)
            return (state, null);
        var entry = Find(id);
        if (entry == null)
            return (null, $"{NameOf(id)} isn't saved any more.");
        string error = await Download(entry);
        state = error == null ? LoadNow(id) : null;
        return state != null ? (state, null) : (null, $"Couldn't download {NameOf(id)}: {error ?? "its file doesn't read"}.");
    }

    #endregion

    #region Uploading

    /// <summary>
    /// Puts the states saved here into the repository, oldest first, each moving into the shared list once GitHub has
    /// both its files. Stops at the first that fails (offline, or the token refused), keeping it and the rest here for
    /// next time. Does nothing without a token. If an upload is running, this waits for it and goes again after.
    /// </summary>
    public static Task UploadPending()
    {
        if (s_Uploading != null && !s_Uploading.IsCompleted)
        {
            s_UploadAgain = true;
            return s_Uploading;
        }
        s_Uploading = Guard(UploadLoop(), "uploading");
        return s_Uploading;
    }

    static async Task UploadLoop()
    {
        do
        {
            s_UploadAgain = false;
            await UploadOnce();
        }
        while (s_UploadAgain);
    }

    static async Task UploadOnce()
    {
        var ids = TunnelStateStore.Ids();
        if (ids.Count == 0)
            return;
        string token = Token();
        if (token == null)
        {
            status = $"No GitHub token on this headset: {ids.Count} saved state{(ids.Count == 1 ? " stays" : "s stay")} here (not uploaded).";
            return;
        }
        LoadCachedList();
        int uploaded = 0;
        foreach (var id in ids)
        {
            string json, txt;
            try
            {
                json = File.ReadAllText(TunnelStateStore.PathOf(id));
                txt = File.Exists(TunnelStateStore.InfoPathOf(id)) ? File.ReadAllText(TunnelStateStore.InfoPathOf(id)) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SaveState] couldn't read {id} to upload it: {e.Message}");
                continue;
            }
            var state = TunnelStateStore.Parse(json, id);
            if (state == null)
                continue;
            txt ??= TunnelStateStore.InfoText(state);
            var (name, info) = TunnelStateStore.ParseInfo(txt, id);
            string message = $"Saved state {name} ({state.Summary()}), from The Tunnel {state.savedFrom}";

            var (jsonReply, jsonFile) = await Put(id + ".json", json, message, token);
            if (!jsonReply.ok)
            {
                UploadFailed(jsonReply, name);
                break;
            }
            var (txtReply, txtFile) = await Put(id + ".txt", txt, message, token);
            if (!txtReply.ok)
            {
                UploadFailed(txtReply, name);
                break;
            }

            // GitHub has it: it moves from this headset's own saves to the shared list (and its copy kept here).
            try
            {
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(CachedStatePath(id), json);
                File.Delete(TunnelStateStore.PathOf(id));
                TryDelete(TunnelStateStore.InfoPathOf(id));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SaveState] {name} is uploaded, but couldn't be moved to the shared list here: {e.Message}");
            }
            s_Entries.RemoveAll(e => e.id == id);
            s_Entries.Add(new Entry
            {
                id = id,
                name = name,
                info = info,
                jsonSha = jsonFile?.sha ?? "",
                jsonUrl = jsonFile?.download_url ?? "",
                txtSha = txtFile?.sha ?? "",
                txtUrl = txtFile?.download_url ?? "",
                cachedSha = jsonFile?.sha ?? "",
            });
            s_Entries.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            SaveCachedList();
            s_Uploaded[id] = DateTime.UtcNow;
            s_UploadProblem = null;
            uploaded++;
            Debug.Log($"[SaveState] uploaded {name} ({id}) to {repositoryName}/{k_Folder}");
        }
        if (uploaded > 0)
        {
            if (TunnelStateStore.Ids().Count == 0)
                status = $"Shared with every headset through {repositoryName}.";
            TunnelStateStore.NotifyChanged();
            changed?.Invoke();
        }
    }

    static void UploadFailed(Reply reply, string name)
    {
        Debug.LogWarning($"[SaveState] couldn't upload {name} to {repositoryName}: {Describe(reply)}; it stays on this headset and is tried again later");
        status = s_UploadProblem = reply.code == 0 ? "Offline: new saves stay on this headset (not uploaded) and upload later."
            : reply.code == 401 ? "GitHub refused the token on this headset (expired or mistyped?): new saves stay here."
            : reply.code == 403 || reply.code == 404 ? $"The token can't write to {repositoryName} (it needs Contents: read and write): new saves stay here."
            : $"Couldn't upload ({reply.code}): new saves stay on this headset and upload later.";
        changed?.Invoke();
    }

    // Creates or replaces a file in the saved-states folder. A file already there (an upload cut short before) is
    // replaced, with the sha GitHub asks for.
    static async Task<(Reply reply, FileInfo file)> Put(string file, string text, string message, string token)
    {
        string url = ApiFile(file);
        string content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var reply = await send("PUT", url, Body(message, content, repository.branch, null), token);
        if (reply.code == 422 || reply.code == 409)
        {
            var existing = await send("GET", url + Ref, null, token);
            string sha = existing.ok ? Parse<FileInfo>(existing.text)?.sha : null;
            if (!string.IsNullOrEmpty(sha))
                reply = await send("PUT", url, Body(message, content, repository.branch, sha), token);
        }
        return (reply, reply.ok ? Parse<PutReply>(reply.text)?.content : null);
    }

    #endregion

    #region Deleting

    /// <summary>
    /// Deletes a saved state: one saved here and not uploaded, from this headset; a shared one, from the repository (so
    /// from every headset) and the copy here, which needs the token. Returns whether it went, and a message saying so.
    /// </summary>
    public static async Task<(bool ok, string message)> Delete(string id)
    {
        string name = NameOf(id);
        if (TunnelStateStore.Exists(id))
            return TunnelStateStore.Delete(id) ? (true, $"Deleted {name} (it was only on this headset).") : (false, $"Couldn't delete {name}.");
        var entry = Find(id);
        if (entry == null)
            return (false, $"{name} isn't saved any more.");
        string token = Token();
        if (token == null)
            return (false, $"{name} is shared in {repositoryName}: deleting it there needs a GitHub token on this headset.");

        string message = $"Deleted saved state {name}";
        foreach (var file in new[] { id + ".json", id + ".txt" })
        {
            string url = ApiFile(file);
            var existing = await send("GET", url + Ref, null, token);
            if (existing.code == 404)
                continue; // gone already
            string sha = existing.ok ? Parse<FileInfo>(existing.text)?.sha : null;
            if (string.IsNullOrEmpty(sha))
            {
                Debug.LogWarning($"[SaveState] couldn't find {file} in {repositoryName} to delete it: {Describe(existing)}");
                return (false, existing.code == 0 ? $"Offline: couldn't delete {name} from {repositoryName}; try again when online."
                    : $"Couldn't delete {name} from {repositoryName} ({existing.code}).");
            }
            var reply = await send("DELETE", url, Body(message, null, repository.branch, sha), token);
            if (!reply.ok && reply.code != 404)
            {
                Debug.LogWarning($"[SaveState] couldn't delete {file} from {repositoryName}: {Describe(reply)}");
                return (false, reply.code == 401 ? $"GitHub refused the token: {name} is still shared."
                    : reply.code == 403 ? $"The token can't write to {repositoryName}: {name} is still shared."
                    : reply.code == 0 ? $"Offline: couldn't delete {name}; try again when online."
                    : $"Couldn't delete {name} from {repositoryName} ({reply.code}).");
            }
        }
        s_Entries.RemoveAll(e => e.id == id);
        TryDelete(CachedStatePath(id));
        SaveCachedList();
        Debug.Log($"[SaveState] deleted {name} ({id}) from {repositoryName}/{k_Folder}");
        changed?.Invoke();
        return (true, $"Deleted {name} from every headset.");
    }

    #endregion

    /// <summary>For the Editor tests: forgets the list read and what this run uploaded, as if the app had just started.</summary>
    public static void ResetForTests()
    {
        s_Entries = null;
        s_EntriesFolder = null;
        s_Uploaded.Clear();
        s_UploadProblem = null;
        s_LastRefresh = DateTime.MinValue;
        status = "Reading the shared saved states...";
    }

    #region Requests

    /// <summary>Sends a request with UnityWebRequest. The token goes only to api.github.com, and is never logged.</summary>
    public static async Task<Reply> SendWithUnity(string method, string url, string body, string token)
    {
        using var request = new UnityWebRequest(url, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 30 };
        if (body != null)
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.SetRequestHeader("Content-Type", "application/json");
        }
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
        {
            request.SetRequestHeader("Accept", "application/vnd.github+json");
            request.SetRequestHeader("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                }
                catch (Exception)
                {
                    // Its message might quote the token: say only that it's unusable.
                    return new Reply { code = 401, text = "{\"message\":\"the token in github-token.txt has characters a request can't carry\"}" };
                }
            }
        }
        try
        {
            request.SetRequestHeader("User-Agent", "TheTunnel");
        }
        catch (Exception)
        {
            // Some platforms set it themselves.
        }
        var operation = request.SendWebRequest();
        var done = new TaskCompletionSource<bool>();
        operation.completed += _ => done.TrySetResult(true);
        if (operation.isDone)
            done.TrySetResult(true);
        await done.Task;
        bool answered = request.result != UnityWebRequest.Result.ConnectionError;
        return new Reply
        {
            code = answered ? request.responseCode : 0,
            text = request.downloadHandler?.text ?? "",
            error = request.result == UnityWebRequest.Result.Success ? null : request.error,
        };
    }

    static string Body(string message, string content, string branch, string sha)
    {
        var body = new StringBuilder("{\"message\":").Append(Quote(message));
        if (content != null)
            body.Append(",\"content\":").Append(Quote(content));
        body.Append(",\"branch\":").Append(Quote(branch));
        if (!string.IsNullOrEmpty(sha))
            body.Append(",\"sha\":").Append(Quote(sha));
        return body.Append('}').ToString();
    }

    static string Quote(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in text ?? "")
        {
            switch (c)
            {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\t': quoted.Append("\\t"); break;
                default:
                    if (c < ' ')
                        quoted.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        quoted.Append(c);
                    break;
            }
        }
        return quoted.Append('"').ToString();
    }

    static T Parse<T>(string json) where T : class
    {
        try
        {
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<T>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A reply for the log: its code and GitHub's message (never the token, which GitHub doesn't send back).
    static string Describe(Reply reply)
    {
        string message = null;
        if (!string.IsNullOrEmpty(reply.text) && reply.text.Contains("\"message\""))
            message = Parse<GitHubError>(reply.text)?.message;
        return reply.code == 0 ? $"no answer ({reply.error})" : $"{reply.code}{(string.IsNullOrEmpty(message) ? "" : " " + message)}";
    }

    [Serializable]
    class GitHubError
    {
        public string message;
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    static async Task Guard(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] sharing: {what} failed: {e.Message}");
        }
    }

    #endregion
}
