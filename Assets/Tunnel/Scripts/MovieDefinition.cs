using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>How a receptor shows that binders are bound to it, in a 3D movie.</summary>
public enum MovieColourEffect : byte
{
    /// <summary>Its own colours shift toward the movie's colour in proportion to the share of its sites taken (the viruses going green).</summary>
    ShiftBySitesTaken,
    /// <summary>It is the movie's colour all the while anything is bound to it (EGFR going light blue).</summary>
    SolidWhileBound,
}

/// <summary>
/// One 3D movie a container can play (see <see cref="TunnelMovie"/>): which molecules bind (a receptor that carries
/// binding sites and the binder that binds to them), how strongly (the capture radius or binding sites' size, and the
/// binding time), how a receptor shows what is bound to it (its colour), and, for a movie someone made with the New
/// movie menu, how many of each fill the container and the container's medium and heat to start with.
/// The two built in (Antibody action and Ligand binding) are always there; the ones people make are stored on the
/// host (see <see cref="MovieLibrary"/>) and shared with everyone.
/// </summary>
[Serializable]
public class MovieDefinition
{
    public const string k_AntibodyActionId = "antibody-action";
    public const string k_LigandBindingId = "ligand-binding";

    [Tooltip("Stays the same for the movie's life: 'antibody-action', 'ligand-binding' or 'user-...'.")]
    public string id;
    public string name;
    public bool builtIn;

    [Tooltip("The receptor's name in the Molecules list; empty for any virus (Antibody action).")]
    public string receptor;
    [Tooltip("The binder's name in the Molecules list (IGG, EGFR ligand).")]
    public string binder;
    [Tooltip("Whether the receptor binds its binder in a pocket with a capture sphere (EGFR) rather than at sites on its surface.")]
    public bool pocket;

    [Tooltip("How many receptors and binders Create puts in the container.")]
    public int receptors = 1;
    public int binders = 15;

    [Tooltip("The capture sphere's radius (Å at true scale), the binding sites' diameter (fraction of a capsid's) and the binding time (s).")]
    public float captureRadius = TunnelMovie.k_DefaultCaptureRadius;
    public float siteDiameter = TunnelMovie.k_DefaultSiteDiameter;
    public float bindingTime = TunnelMovie.k_DefaultBindingTime;

    public MovieColourEffect colourEffect;
    public Color colour = new(0.18f, 0.85f, 0.25f, 1f);

    [Tooltip("Whether it sets the container's medium and heat when chosen (movies made with the New movie menu do).")]
    public bool hasStart;
    public float viscosity = 1f;
    [Tooltip("The container's temperature setting (10 is 20 °C, see TunnelContainer.temperature).")]
    public float temperature = TunnelContainer.k_RoomTemperature;
    public string motion = nameof(TunnelContainer.Motion.Random);

    public string created;
    public string createdWith; // the app's version

    /// <summary>Antibodies (IGG) binding the viruses' sites; each virus shifts toward green as its sites are taken.</summary>
    public static MovieDefinition AntibodyAction => new()
    {
        id = k_AntibodyActionId,
        name = TunnelMovie.k_Title,
        builtIn = true,
        receptor = "",
        binder = "IGG",
        pocket = false,
        colourEffect = MovieColourEffect.ShiftBySitesTaken,
        colour = new Color(0.18f, 0.85f, 0.25f, 1f),
    };

    /// <summary>EGFR ligands binding EGFR's pocket; EGFR is light blue while its ligand is bound.</summary>
    public static MovieDefinition LigandBinding => new()
    {
        id = k_LigandBindingId,
        name = TunnelMovie.k_LigandTitle,
        builtIn = true,
        receptor = "EGFR",
        binder = "EGFR ligand",
        pocket = true,
        colourEffect = MovieColourEffect.SolidWhileBound,
        colour = new Color(0.55f, 0.82f, 1f, 1f),
    };

    /// <summary>The two built-in movies, in the order the menu lists them.</summary>
    public static List<MovieDefinition> BuiltIn => new() { AntibodyAction, LigandBinding };

    /// <summary>Whether a molecule with binding sites takes part in this movie as its receptor.</summary>
    public bool Includes(CapsidSites capsid)
    {
        if (capsid == null || capsid.pocket != pocket || !Same(capsid.binds, binder))
            return false;
        return string.IsNullOrEmpty(receptor) || Same(CapsidSites.MoleculeName(capsid.gameObject), receptor);
    }

    /// <summary>Whether a molecule is this movie's binder (by its name in the Molecules list).</summary>
    public bool IsBinder(GameObject molecule) => molecule != null && Same(CapsidSites.MoleculeName(molecule), binder);

    static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>"virus"/"viruses", or the receptor's name, for the movie's messages.</summary>
    public string ReceptorWord(int count) => string.IsNullOrEmpty(receptor)
        ? (count == 1 ? "virus" : "viruses")
        : receptor;

    /// <summary>"antibody"/"antibodies", "ligand"/"ligands", or the binder's name, for the movie's messages.</summary>
    public string BinderWord(int count)
    {
        if (Same(binder, "IGG"))
            return count == 1 ? "antibody" : "antibodies";
        if ((binder ?? "").EndsWith("ligand", StringComparison.OrdinalIgnoreCase))
            return count == 1 ? "ligand" : "ligands";
        return count == 1 ? binder : binder + "s";
    }

    public MovieDefinition Clone() => (MovieDefinition)MemberwiseClone();

    public MovieEntry ToEntry() => new()
    {
        id = Fixed64(id),
        name = Fixed64(name),
        receptor = Fixed32(receptor),
        binder = Fixed32(binder),
        builtIn = builtIn,
        pocket = pocket,
        receptors = (byte)Mathf.Clamp(receptors, 0, 255),
        binders = (byte)Mathf.Clamp(binders, 0, 255),
        captureRadius = captureRadius,
        siteDiameter = siteDiameter,
        bindingTime = bindingTime,
        colourEffect = (byte)colourEffect,
        colour = colour,
        hasStart = hasStart,
        viscosity = viscosity,
        temperature = temperature,
        motion = (byte)(Enum.TryParse(motion, out TunnelContainer.Motion m) ? m : TunnelContainer.Motion.Random),
    };

    static FixedString64Bytes Fixed64(string text)
    {
        text ??= "";
        // At most 61 bytes of UTF-8 fit: cut by characters until it does.
        while (System.Text.Encoding.UTF8.GetByteCount(text) > 61)
            text = text.Substring(0, text.Length - 1);
        return new FixedString64Bytes(text);
    }

    static FixedString32Bytes Fixed32(string text)
    {
        text ??= "";
        while (System.Text.Encoding.UTF8.GetByteCount(text) > 29)
            text = text.Substring(0, text.Length - 1);
        return new FixedString32Bytes(text);
    }
}

/// <summary>A <see cref="MovieDefinition"/> as it is shared over the network.</summary>
public struct MovieEntry : INetworkSerializable, IEquatable<MovieEntry>
{
    public FixedString64Bytes id;
    public FixedString64Bytes name;
    public FixedString32Bytes receptor;
    public FixedString32Bytes binder;
    public bool builtIn;
    public bool pocket;
    public byte receptors;
    public byte binders;
    public float captureRadius;
    public float siteDiameter;
    public float bindingTime;
    public byte colourEffect;
    public Color colour;
    public bool hasStart;
    public float viscosity;
    public float temperature;
    public byte motion;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref id);
        serializer.SerializeValue(ref name);
        serializer.SerializeValue(ref receptor);
        serializer.SerializeValue(ref binder);
        serializer.SerializeValue(ref builtIn);
        serializer.SerializeValue(ref pocket);
        serializer.SerializeValue(ref receptors);
        serializer.SerializeValue(ref binders);
        serializer.SerializeValue(ref captureRadius);
        serializer.SerializeValue(ref siteDiameter);
        serializer.SerializeValue(ref bindingTime);
        serializer.SerializeValue(ref colourEffect);
        serializer.SerializeValue(ref colour);
        serializer.SerializeValue(ref hasStart);
        serializer.SerializeValue(ref viscosity);
        serializer.SerializeValue(ref temperature);
        serializer.SerializeValue(ref motion);
    }

    public bool Equals(MovieEntry other) =>
        id.Equals(other.id) && name.Equals(other.name) && receptor.Equals(other.receptor) && binder.Equals(other.binder)
        && builtIn == other.builtIn && pocket == other.pocket && receptors == other.receptors && binders == other.binders
        && captureRadius == other.captureRadius && siteDiameter == other.siteDiameter && bindingTime == other.bindingTime
        && colourEffect == other.colourEffect && colour == other.colour && hasStart == other.hasStart
        && viscosity == other.viscosity && temperature == other.temperature && motion == other.motion;

    public override bool Equals(object obj) => obj is MovieEntry other && Equals(other);

    public override int GetHashCode() => id.GetHashCode();

    public MovieDefinition ToDefinition() => new()
    {
        id = id.ToString(),
        name = name.ToString(),
        receptor = receptor.ToString(),
        binder = binder.ToString(),
        builtIn = builtIn,
        pocket = pocket,
        receptors = receptors,
        binders = binders,
        captureRadius = captureRadius,
        siteDiameter = siteDiameter,
        bindingTime = bindingTime,
        colourEffect = (MovieColourEffect)colourEffect,
        colour = colour,
        hasStart = hasStart,
        viscosity = viscosity,
        temperature = temperature,
        motion = ((TunnelContainer.Motion)motion).ToString(),
    };
}

/// <summary>
/// The 3D movies people have made with the New movie menu, kept on this headset (on the host in a session): one small
/// file per movie in the app's own storage (3D Movies/Movie definitions/&lt;id&gt;.json under
/// Application.persistentDataPath), so they are there again after the app restarts. Only the host reads and writes
/// them; the other players see the host's list through each container's movie (see <see cref="TunnelMovie.movies"/>).
/// The two built-in movies are not stored here and can't be deleted.
/// </summary>
public static class MovieLibrary
{
    /// <summary>Longest name a movie can have (it is shared over the network and shown on one menu row).</summary>
    public const int k_MaxNameLength = 40;

    /// <summary>Fires on this headset after a movie is added or deleted here.</summary>
    public static event Action changed;

    /// <summary>The folder (changed only by the Editor test, which works in a folder of its own).</summary>
    public static string folder { get; set; } = null;

    static string Folder => folder ?? Path.Combine(TunnelMovie.folder, "Movie definitions");

    static string PathOf(string id) => Path.Combine(Folder, id + ".json");

    static List<MovieDefinition> s_Cache;

    /// <summary>The movies made here, A to Z by name.</summary>
    public static List<MovieDefinition> All()
    {
        if (s_Cache != null)
            return s_Cache.Select(m => m.Clone()).ToList();
        var result = new List<MovieDefinition>();
        try
        {
            if (Directory.Exists(Folder))
                foreach (string file in Directory.GetFiles(Folder, "*.json"))
                {
                    try
                    {
                        var movie = JsonUtility.FromJson<MovieDefinition>(File.ReadAllText(file));
                        if (movie != null && !string.IsNullOrEmpty(movie.id) && !movie.builtIn)
                            result.Add(movie);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Movie] couldn't read the movie {file}: {e.Message}");
                    }
                }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Movie] can't list the movies in {Folder}: {e.Message}");
        }
        s_Cache = result.OrderBy(m => m.name, StringComparer.OrdinalIgnoreCase).ToList();
        return s_Cache.Select(m => m.Clone()).ToList();
    }

    /// <summary>A movie made here, or null.</summary>
    public static MovieDefinition Find(string id) => All().FirstOrDefault(m => m.id == id);

    /// <summary>A movie's name cleaned up (no line breaks or characters a file can't hold, at most 40 characters).</summary>
    public static string CleanName(string name)
    {
        name = TunnelMovie.CleanName(name);
        return name.Length > k_MaxNameLength ? name.Substring(0, k_MaxNameLength).Trim() : name;
    }

    /// <summary>A name no other movie (built in or made here) has: the name itself, or with " 2", " 3"... added.</summary>
    public static string UniqueName(string name)
    {
        name = CleanName(name);
        if (name.Length == 0)
            name = "New movie";
        var taken = new HashSet<string>(MovieDefinition.BuiltIn.Concat(All()).Select(m => m.name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
            return name;
        for (int i = 2; ; i++)
        {
            string candidate = $"{name} {i}";
            if (candidate.Length > k_MaxNameLength)
                candidate = $"{name.Substring(0, k_MaxNameLength - 1 - i.ToString().Length).Trim()} {i}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Stores a new movie: gives it an id of its own and a name no other movie has, and writes it. Returns the stored
    /// movie, or null if it couldn't be written.
    /// </summary>
    public static MovieDefinition Add(MovieDefinition movie)
    {
        if (movie == null)
            return null;
        movie = movie.Clone();
        movie.builtIn = false;
        movie.id = "user-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        movie.name = UniqueName(movie.name);
        movie.hasStart = true;
        movie.created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        movie.createdWith = Application.version;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathOf(movie.id), JsonUtility.ToJson(movie, true));
            Debug.Log($"[Movie] stored the movie \"{movie.name}\" ({movie.receptors} {movie.receptor}, {movie.binders} {movie.binder}) in {Folder}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Movie] couldn't store the movie \"{movie.name}\": {e.Message}");
            return null;
        }
        s_Cache = null;
        changed?.Invoke();
        return movie;
    }

    /// <summary>Removes a movie made here (never a built-in one). True if there was one.</summary>
    public static bool Delete(string id)
    {
        if (string.IsNullOrEmpty(id) || id == MovieDefinition.k_AntibodyActionId || id == MovieDefinition.k_LigandBindingId)
            return false;
        var movie = Find(id);
        string path = PathOf(id);
        if (!File.Exists(path))
            return false;
        try
        {
            File.Delete(path);
            Debug.Log($"[Movie] deleted the movie \"{movie?.name ?? id}\"");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Movie] couldn't delete the movie \"{movie?.name ?? id}\": {e.Message}");
            return false;
        }
        s_Cache = null;
        changed?.Invoke();
        return true;
    }

    /// <summary>Forgets what it read, so the next look reads the folder again (after the folder is changed).</summary>
    public static void Reload() => s_Cache = null;
}
