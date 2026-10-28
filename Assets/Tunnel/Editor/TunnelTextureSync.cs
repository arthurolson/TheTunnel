using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds the images in the project's tunnel-textures folder (next to Assets) into the app: every PNG or JPEG there
/// that isn't in Assets/Tunnel/Resources/TunnelTextures yet is copied in under a readable name
/// ("stacked_brick_wall_ao_2k.jpg" becomes "Stacked Brick Wall AO.jpg"), where it shows in the TS Menu's Textures list.
/// Runs when the Editor loads or reloads scripts, before every build, and from Tunnel > Textures > Add New Textures.
/// Zip downloads and sub-folders are left alone (push-textures.sh handles those for a headset's own folder).
/// </summary>
class TunnelTextureSync : IPreprocessBuildWithReport
{
    const string k_Destination = "Assets/Tunnel/Resources/TunnelTextures";
    static readonly string[] k_Extensions = { ".png", ".jpg", ".jpeg" };

    public int callbackOrder => -100;

    public void OnPreprocessBuild(BuildReport report) => AddNewTextures();

    [InitializeOnLoadMethod]
    static void AddAfterReload() => EditorApplication.delayCall += () =>
    {
        if (!EditorApplication.isPlayingOrWillChangePlaymode && !BuildPipeline.isBuildingPlayer)
            AddNewTextures();
    };

    [MenuItem("Tunnel/Textures/Add New Textures")]
    static void AddFromMenu()
    {
        int added = AddNewTextures();
        Debug.Log(added == 0 ? "[Textures] no new images in tunnel-textures" : $"[Textures] added {added} image(s)");
    }

    /// <summary>Copies images not built in yet; returns how many.</summary>
    static int AddNewTextures()
    {
        string source = Path.Combine(Path.GetDirectoryName(Application.dataPath), "tunnel-textures");
        if (!Directory.Exists(source))
            return 0;

        Directory.CreateDirectory(k_Destination);
        var existing = Directory.GetFiles(k_Destination)
            .Where(f => !f.EndsWith(".meta"))
            .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())
            .ToHashSet();

        int added = 0;
        foreach (string file in Directory.GetFiles(source))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (!k_Extensions.Contains(extension))
                continue;
            string name = ReadableName(Path.GetFileNameWithoutExtension(file));
            if (name.Length == 0 || !existing.Add(name.ToLowerInvariant()))
                continue;

            string destination = $"{k_Destination}/{name}{extension}";
            File.Copy(file, destination);
            AssetDatabase.ImportAsset(destination);
            Debug.Log($"[Textures] built in {Path.GetFileName(file)} as \"{name}\"");
            added++;
        }

        return added;
    }

    /// <summary>"stacked_brick_wall_ao_2k" → "Stacked Brick Wall AO": words, no resolution tag, short tags in capitals.</summary>
    static string ReadableName(string fileName)
    {
        var words = Regex.Split(fileName, @"[\s_\-.]+")
            .Where(w => w.Length > 0 && !Regex.IsMatch(w, @"^\d+k$", RegexOptions.IgnoreCase))
            .Select(w => w.Length <= 2 ? w.ToUpperInvariant() : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.ToLowerInvariant()));
        return string.Join(" ", words);
    }
}
