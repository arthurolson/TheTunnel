using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// The signs kept on this headset (on the host in a session): one small file per sign in the app's own storage
/// (Signs/&lt;name&gt;.json under Application.persistentDataPath), so a sign's text and style can be called up again
/// from the TS Menu's Signage list, also after the app restarts. Only the host reads and writes them; the other players
/// see the host's list.
/// </summary>
public static class SignStore
{
    const int k_MaxNameLength = 40;

    /// <summary>Fires on this headset after a sign is saved or deleted here.</summary>
    public static event Action changed;

    /// <summary>The folder (changed only by the Editor test, which works in a folder of its own).</summary>
    public static string folder { get; set; } = null;

    static string Folder => folder ?? Path.Combine(Application.persistentDataPath, "Signs");

    static string PathOf(string name) => Path.Combine(Folder, name + ".json");

    /// <summary>The stored signs' names, A to Z.</summary>
    public static List<string> Names()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return new List<string>();
            return Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Sign] can't list the stored signs in {Folder}: {e.Message}");
            return new List<string>();
        }
    }

    public static bool Exists(string name) => !string.IsNullOrEmpty(name) && File.Exists(PathOf(Clean(name)));

    /// <summary>
    /// A file name made from a sign's text: its first line, without characters a file name can't hold, at most 40
    /// characters ("Sign" if nothing is left).
    /// </summary>
    public static string Clean(string text)
    {
        string line = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var sb = new StringBuilder();
        foreach (char c in line)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? ' ' : c);
        string name = sb.ToString().Trim().TrimEnd('.');
        while (name.Contains("  "))
            name = name.Replace("  ", " ");
        if (name.Length > k_MaxNameLength)
            name = name.Substring(0, k_MaxNameLength).Trim();
        return name.Length == 0 ? "Sign" : name;
    }

    /// <summary>A name for a new stored sign from its text, with " 2", " 3"... added if that name is taken.</summary>
    public static string NewName(string text)
    {
        string name = Clean(text);
        if (!Exists(name))
            return name;
        for (int i = 2; ; i++)
            if (!Exists($"{name} {i}"))
                return $"{name} {i}";
    }

    /// <summary>Writes a sign under its record's name (replacing one of that name). True if it was written.</summary>
    public static bool Save(SignRecord record)
    {
        if (record == null)
            return false;
        record.name = Clean(record.name);
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathOf(record.name), JsonUtility.ToJson(record, true));
            Debug.Log($"[Sign] stored \"{record.name}\" in {Folder}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Sign] couldn't store \"{record.name}\": {e.Message}");
            return false;
        }
        changed?.Invoke();
        return true;
    }

    /// <summary>A stored sign, or null if there's none of that name (or it can't be read).</summary>
    public static SignRecord Load(string name)
    {
        if (!Exists(name))
            return null;
        try
        {
            var record = JsonUtility.FromJson<SignRecord>(File.ReadAllText(PathOf(Clean(name))));
            if (record != null)
                record.name = Clean(name);
            return record;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Sign] couldn't read the stored sign \"{name}\": {e.Message}");
            return null;
        }
    }

    /// <summary>Removes a stored sign. True if there was one.</summary>
    public static bool Delete(string name)
    {
        if (!Exists(name))
            return false;
        try
        {
            File.Delete(PathOf(Clean(name)));
            Debug.Log($"[Sign] deleted the stored sign \"{name}\"");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Sign] couldn't delete the stored sign \"{name}\": {e.Message}");
            return false;
        }
        changed?.Invoke();
        return true;
    }
}
