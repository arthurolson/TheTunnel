using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Everything set up in the tunnel at one moment, as the TS Menu's Saved states list keeps it (see
/// <see cref="TunnelSaveState"/>): how many segments there are, each one's colour, texture and paint, and every thing
/// in them (molecules, containers with their medium, movie and contents, Viewers and browsers with what they show,
/// signs, palettes, painting copies, the New figure and the spawn nozzle), where it is, how big and how it is set up.
/// Poses are kept in the frame of the segment they are in (counted along the tunnel from its first segment), so a
/// state comes back in the same place in the tunnel even if the tunnel stands somewhere else in the room today.
/// A container and the molecules in it are kept as a 3D movie's saved starting state is
/// (<see cref="MovieSnapshot"/>, molecules in the container's own frame), so both use one format.
/// </summary>
[Serializable]
public class TunnelSavedState
{
    public int format = 1;
    [Tooltip("When it was saved (UTC) and three random letters, such as 20261122-140531-k7q: its file name, here and in the asset repository.")]
    public string id;
    public string name;
    public string saved;
    public string savedFrom; // the app's version
    [Tooltip("The Molecules list's magnification when it was saved.")]
    public int magnification;
    [Tooltip("The segments in order along the tunnel, from its first.")]
    public List<SegmentState> segments = new();
    [Tooltip("Molecules not inside a container (those inside are kept with their container).")]
    public List<PlacedMolecule> molecules = new();
    public List<PlacedContainer> containers = new();
    [Tooltip("Viewers and browsers.")]
    public List<PlacedPanel> panels = new();
    public List<PlacedSign> signs = new();
    [Tooltip("Palettes; each one's marker comes back in its holder.")]
    public List<Placement> palettes = new();
    public List<PlacedCopy> paintingCopies = new();
    [Tooltip("New figures.")]
    public List<Placement> figures = new();
    public List<PlacedNozzle> nozzles = new();

    /// <summary>How many things it holds, counting the molecules inside containers.</summary>
    public int thingCount => molecules.Count + containers.Sum(c => 1 + (c.state?.molecules.Count ?? 0)) + panels.Count + signs.Count
        + palettes.Count + paintingCopies.Count + figures.Count + nozzles.Count;

    static readonly string[] k_SecretParameters =
    {
        "token", "access_token", "id_token", "refresh_token", "auth", "authorization", "key", "apikey", "api_key", "sig",
        "signature", "password", "pass", "pwd", "secret", "session", "sessionid", "sid", "code", "ticket",
        "x-amz-signature", "x-amz-credential", "x-amz-security-token", "x-goog-signature", "x-goog-credential",
    };

    /// <summary>
    /// Takes private bits out of the web addresses it holds (a browser's page, a Viewer's URL), as a shared state is
    /// public: a user name and password in the address, and query parameters such as token, key, sig or session.
    /// </summary>
    public void Scrub()
    {
        foreach (var panel in panels)
        {
            panel.url = ScrubUrl(panel.url);
            if (panel.mediaIsUrl)
                panel.mediaSource = ScrubUrl(panel.mediaSource);
        }
    }

    /// <summary>The address without a user name or password, or query parameters that look like keys.</summary>
    public static string ScrubUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return url;
        bool userInfo = !string.IsNullOrEmpty(uri.UserInfo);
        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = parameters.Where(p =>
        {
            string key = Uri.UnescapeDataString(p.Split('=')[0]).ToLowerInvariant();
            return !k_SecretParameters.Contains(key);
        }).ToArray();
        if (!userInfo && kept.Length == parameters.Length)
            return url;
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = string.Join("&", kept) };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>A short description for the list: "2 segments · 14 things · paint".</summary>
    public string Summary()
    {
        int count = thingCount;
        bool paint = segments.Any(s => s.paint != null && s.paint.ops.Count > 0);
        return $"{segments.Count} segment{(segments.Count == 1 ? "" : "s")} · {count} thing{(count == 1 ? "" : "s")}{(paint ? " · paint" : "")}";
    }
}

/// <summary>Where something is: which segment (0 is the tunnel's first), and its pose and scale in that segment's frame.</summary>
[Serializable]
public class Placement
{
    public int segment;
    public Vector3 position;
    public Quaternion rotation = Quaternion.identity;
    public Vector3 scale = Vector3.one;
}

/// <summary>A segment's look: its colour (hue, saturation, value, opacity), wall texture and the paint on its walls.</summary>
[Serializable]
public class SegmentState
{
    public Vector4 color;
    public string texture;
    public PaintMarks paint = new();
}

/// <summary>The marks painted on a wall or a painting copy, in the order they were made (see <see cref="TunnelSegmentPaint"/>).</summary>
[Serializable]
public class PaintMarks
{
    [Serializable]
    public class Op
    {
        [Tooltip("A stroke ended here (no stamps).")]
        public bool endStroke;
        public int mode; // PaintMode
        public Color32 color;
        public float diameter;
        [Tooltip("The stamps' canvas positions, packed as the network sends them, in base 64.")]
        public string stamps;
    }

    public List<Op> ops = new();
}

/// <summary>A molecule not inside a container: where it is, and what it is (its velocity and spin in the segment's frame).</summary>
[Serializable]
public class PlacedMolecule
{
    public Placement at = new();
    public MoleculeState molecule = new();
}

/// <summary>
/// A box, sphere or splat-box: where it is and its size (its scale), the movie it plays (as made, so a movie someone
/// made comes back even if it was deleted since), whether its movie controls show, and its medium and contents as a
/// movie's saved starting state.
/// </summary>
[Serializable]
public class PlacedContainer
{
    public Placement at = new();
    public string splatAsset;
    public float splatHeight;
    public bool controlsShown;
    [Tooltip("The movie chosen on it (MovieDefinition as JSON); empty for a splat-box.")]
    public string movie;
    public MovieSnapshot state = new();
}

/// <summary>A Viewer or a browser: where it is, its size, whether it stands free or hangs on a wall, and what it shows.</summary>
[Serializable]
public class PlacedPanel
{
    public Placement at = new();
    public bool viewer;
    public float width;
    public float aspect;
    public bool free;
    public bool contentOnly;
    [Tooltip("A browser's page.")]
    public string url;
    [Tooltip("A Viewer's media: its kind (ViewerMediaKind), an entry's name or an address, and its title.")]
    public int mediaKind;
    public string mediaSource;
    public bool mediaIsUrl;
    public string mediaTitle;
    public bool playing;
    public bool loop;
    public double time;
}

/// <summary>A sign: where it is, and its text, style and size (as the Signage list stores signs).</summary>
[Serializable]
public class PlacedSign
{
    public Placement at = new();
    public SignRecord record = new();
}

/// <summary>A painting copy on a wall: where it is, its canvas size, and its marks.</summary>
[Serializable]
public class PlacedCopy
{
    public Placement at = new();
    public Vector2 meters;
    public bool contentOnly;
    public PaintMarks paint = new();
}

/// <summary>The spawn nozzle: where it is, its Copies setting, and which of the saved molecules was attached to it.</summary>
[Serializable]
public class PlacedNozzle
{
    public Placement at = new();
    public int count;
    [Tooltip("Index into the state's molecules of the one attached to its cup, or -1.")]
    public int loaded = -1;
}

/// <summary>
/// The saved states made on this headset (on the host in a session) that aren't shared yet: one file per state in the
/// app's own storage (Saved States/&lt;id&gt;.json under Application.persistentDataPath, beside a small &lt;id&gt;.txt with
/// its name, date and summary for the list), so they are still there after the app restarts. Once
/// <see cref="TunnelSharedStates"/> has put one in the asset repository for every headset, it moves out of here into
/// the copy of the shared saves kept on the headset. A state's id is when it was saved (UTC) and three random letters,
/// such as 20261122-140531-k7q, so two headsets never make the same file; its name, State 1, State 2..., is what the
/// list shows.
/// </summary>
public static class TunnelStateStore
{
    const string k_Prefix = "State ";
    static readonly Regex k_Id = new(@"^\d{8}-\d{6}-[a-z0-9]{3}$");
    static readonly System.Random s_Random = new();
    static string s_Migrated;

    /// <summary>Fires on this headset after a state is saved or deleted here.</summary>
    public static event Action changed;

    /// <summary>The folder (changed only by the Editor tests, which work in a folder of their own).</summary>
    public static string folder { get; set; } = null;

    public static string Folder => folder ?? Path.Combine(Application.persistentDataPath, "Saved States");

    public static string PathOf(string id) => Path.Combine(Folder, id + ".json");

    public static string InfoPathOf(string id) => Path.Combine(Folder, id + ".txt");

    /// <summary>True for a state's id (20261122-140531-k7q).</summary>
    public static bool IsId(string id) => !string.IsNullOrEmpty(id) && k_Id.IsMatch(id);

    /// <summary>A new id for a state saved at this time (UTC).</summary>
    public static string NewId(DateTime utc)
    {
        const string letters = "abcdefghijkmnpqrstuvwxyz23456789";
        var tail = new char[3];
        lock (s_Random)
            for (int i = 0; i < tail.Length; i++)
                tail[i] = letters[s_Random.Next(letters.Length)];
        return utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + new string(tail);
    }

    /// <summary>The ids of the states saved here and not shared yet, oldest first.</summary>
    public static List<string> Ids()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return new List<string>();
            Migrate();
            return Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).Where(IsId)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] can't list the saved states in {Folder}: {e.Message}");
            return new List<string>();
        }
    }

    // States saved by 22.11.2026.1 were named files (State 1.json): they get an id from when they were written, and
    // keep their name.
    static void Migrate()
    {
        if (s_Migrated == Folder)
            return;
        s_Migrated = Folder;
        foreach (var path in Directory.GetFiles(Folder, "*.json"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (IsId(stem))
                continue;
            try
            {
                var state = JsonUtility.FromJson<TunnelSavedState>(File.ReadAllText(path));
                if (state == null)
                    continue;
                state.id = NewId(File.GetLastWriteTimeUtc(path));
                if (string.IsNullOrEmpty(state.name))
                    state.name = stem;
                Write(state);
                File.Delete(path);
                string oldInfo = Path.Combine(Folder, stem + ".txt");
                if (File.Exists(oldInfo))
                    File.Delete(oldInfo);
                Debug.Log($"[SaveState] \"{state.name}\" from an earlier version is now {state.id}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SaveState] couldn't bring \"{stem}\" from an earlier version: {e.Message}");
            }
        }
    }

    /// <summary>The two lines of a state's .txt: its name, then its date and summary ("21 Nov 14:05 · 2 segments · 14 things").</summary>
    public static string InfoText(TunnelSavedState state)
    {
        var when = DateTime.TryParse(state.saved, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.Now;
        return $"{state.name}\n{when.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} · {state.Summary()}";
    }

    /// <summary>A .txt's name and info (its first line, if it only has one, is the info).</summary>
    public static (string name, string info) ParseInfo(string text, string id)
    {
        var lines = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return lines.Length >= 2 ? (lines[0], lines[1]) : (id, lines.Length == 1 ? lines[0] : "");
    }

    static (string name, string info) Read(string id)
    {
        try
        {
            string path = InfoPathOf(id);
            return ParseInfo(File.Exists(path) ? File.ReadAllText(path) : "", id);
        }
        catch (Exception)
        {
            return (id, "");
        }
    }

    /// <summary>A state's name as the list shows it (State 3), or its id if it has none.</summary>
    public static string Name(string id) => Read(id).name;

    /// <summary>A state's date and summary for the list ("21 Nov 14:05 · 2 segments · 14 things"), or "".</summary>
    public static string Info(string id) => Read(id).info;

    public static bool Exists(string id) => IsId(id) && File.Exists(PathOf(id));

    /// <summary>The next number not yet used among these names: State 1, State 2, ... (one more than the highest).</summary>
    public static string NextName(IEnumerable<string> namesInUse)
    {
        int highest = 0;
        foreach (var name in namesInUse)
            if (name != null && name.StartsWith(k_Prefix, StringComparison.Ordinal) && int.TryParse(name.Substring(k_Prefix.Length), out int n))
                highest = Mathf.Max(highest, n);
        return k_Prefix + (highest + 1);
    }

    /// <summary>A name for the list: one line, at most 40 characters ("State" if nothing is left).</summary>
    public static string Clean(string name)
    {
        name = (name ?? "").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Trim();
        while (name.Contains("  "))
            name = name.Replace("  ", " ");
        if (name.Length > 40)
            name = name.Substring(0, 40).Trim();
        return name.Length == 0 ? "State" : name;
    }

    /// <summary>
    /// Writes a new state here, with its name, date and summary, under a new id (kept in it). Private bits of web
    /// addresses are taken out first, as a shared state is public (<see cref="TunnelSavedState.Scrub"/>). True if written.
    /// </summary>
    public static bool Save(TunnelSavedState state)
    {
        if (state == null)
            return false;
        state.name = Clean(state.name);
        if (!IsId(state.id))
            state.id = NewId(DateTime.UtcNow);
        state.Scrub();
        try
        {
            Write(state);
            Debug.Log($"[SaveState] saved \"{state.name}\" ({state.Summary()}) as {PathOf(state.id)}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't save \"{state.name}\": {e.Message}");
            return false;
        }
        changed?.Invoke();
        return true;
    }

    static void Write(TunnelSavedState state)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathOf(state.id), JsonUtility.ToJson(state));
        File.WriteAllText(InfoPathOf(state.id), InfoText(state));
    }

    /// <summary>A state saved here, or null if there's none with that id (or it can't be read).</summary>
    public static TunnelSavedState Load(string id) => Exists(id) ? Parse(File.ReadAllText(PathOf(id)), id) : null;

    /// <summary>A state from its file's text, or null if it can't be read.</summary>
    public static TunnelSavedState Parse(string json, string id)
    {
        try
        {
            var state = JsonUtility.FromJson<TunnelSavedState>(json);
            if (state != null)
            {
                state.id = id;
                state.name = Clean(string.IsNullOrEmpty(state.name) ? id : state.name);
            }
            return state;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't read the saved state {id}: {e.Message}");
            return null;
        }
    }

    /// <summary>Removes a state saved here. True if there was one.</summary>
    public static bool Delete(string id)
    {
        if (!Exists(id))
            return false;
        try
        {
            File.Delete(PathOf(id));
            if (File.Exists(InfoPathOf(id)))
                File.Delete(InfoPathOf(id));
            Debug.Log($"[SaveState] deleted the saved state {id}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't delete the saved state {id}: {e.Message}");
            return false;
        }
        changed?.Invoke();
        return true;
    }

    /// <summary>Tells the lists that states moved out of here (shared now).</summary>
    public static void NotifyChanged() => changed?.Invoke();
}
