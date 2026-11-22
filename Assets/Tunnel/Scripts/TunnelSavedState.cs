using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// The saved states kept on this headset (on the host in a session): one file per state in the app's own storage
/// (Saved States/&lt;name&gt;.json under Application.persistentDataPath, beside a small &lt;name&gt;.txt with its date
/// and summary for the list), so they are still there after the app restarts. Only the host reads and writes them;
/// the other players see the host's list.
/// </summary>
public static class TunnelStateStore
{
    const string k_Prefix = "State ";

    /// <summary>Fires on this headset after a state is saved or deleted here.</summary>
    public static event Action changed;

    /// <summary>The folder (changed only by the Editor test, which works in a folder of its own).</summary>
    public static string folder { get; set; } = null;

    static string Folder => folder ?? Path.Combine(Application.persistentDataPath, "Saved States");

    static string PathOf(string name) => Path.Combine(Folder, name + ".json");

    static string InfoPathOf(string name) => Path.Combine(Folder, name + ".txt");

    /// <summary>The saved states' names, oldest first.</summary>
    public static List<string> Names()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return new List<string>();
            return Directory.GetFiles(Folder, "*.json")
                .OrderBy(File.GetLastWriteTimeUtc)
                .Select(Path.GetFileNameWithoutExtension).ToList();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] can't list the saved states in {Folder}: {e.Message}");
            return new List<string>();
        }
    }

    /// <summary>A saved state's date and summary for the list ("21 Nov 14:05 · 2 segments · 14 things"), or "".</summary>
    public static string Info(string name)
    {
        try
        {
            string path = InfoPathOf(Clean(name));
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static bool Exists(string name) => !string.IsNullOrEmpty(name) && File.Exists(PathOf(Clean(name)));

    /// <summary>A name usable as a file name (at most 40 characters; "State" if nothing is left).</summary>
    public static string Clean(string name)
    {
        name = (name ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        foreach (char c in "/\\:*?\"<>|")
            name = name.Replace(c, ' ');
        while (name.Contains("  "))
            name = name.Replace("  ", " ");
        name = name.Trim().TrimEnd('.');
        if (name.Length > 40)
            name = name.Substring(0, 40).Trim();
        return name.Length == 0 ? "State" : name;
    }

    /// <summary>The next number not yet used: State 1, State 2, ... (one more than the highest kept).</summary>
    public static string NextName()
    {
        int highest = 0;
        foreach (var name in Names())
            if (name.StartsWith(k_Prefix, StringComparison.Ordinal) && int.TryParse(name.Substring(k_Prefix.Length), out int n))
                highest = Mathf.Max(highest, n);
        return k_Prefix + (highest + 1);
    }

    /// <summary>Writes a state under its name (replacing one of that name), with its date and summary. True if written.</summary>
    public static bool Save(TunnelSavedState state)
    {
        if (state == null)
            return false;
        state.name = Clean(state.name);
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathOf(state.name), JsonUtility.ToJson(state));
            var when = DateTime.TryParse(state.saved, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.Now;
            File.WriteAllText(InfoPathOf(state.name), $"{when.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} · {state.Summary()}");
            Debug.Log($"[SaveState] saved \"{state.name}\" ({state.Summary()}) to {PathOf(state.name)}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't save \"{state.name}\": {e.Message}");
            return false;
        }
        changed?.Invoke();
        return true;
    }

    /// <summary>A saved state, or null if there's none of that name (or it can't be read).</summary>
    public static TunnelSavedState Load(string name)
    {
        if (!Exists(name))
            return null;
        try
        {
            var state = JsonUtility.FromJson<TunnelSavedState>(File.ReadAllText(PathOf(Clean(name))));
            if (state != null)
                state.name = Clean(name);
            return state;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't read the saved state \"{name}\": {e.Message}");
            return null;
        }
    }

    /// <summary>Removes a saved state. True if there was one.</summary>
    public static bool Delete(string name)
    {
        if (!Exists(name))
            return false;
        try
        {
            File.Delete(PathOf(Clean(name)));
            if (File.Exists(InfoPathOf(Clean(name))))
                File.Delete(InfoPathOf(Clean(name)));
            Debug.Log($"[SaveState] deleted the saved state \"{name}\"");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveState] couldn't delete the saved state \"{name}\": {e.Message}");
            return false;
        }
        changed?.Invoke();
        return true;
    }
}
