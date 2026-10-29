using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Keeps the Viewer's built-in media folders (Assets/StreamingAssets/Viewer/Images, Videos, Spatial Images,
/// Spatial Videos) in place and writes index.json beside them listing what they hold, since the app can't list the
/// folders inside an APK. Runs when the Editor loads or reloads scripts, before every build, and from
/// Tunnel > Viewer > Update Media List.
/// </summary>
class ViewerMediaIndex : IPreprocessBuildWithReport
{
    const string k_Readme =
        "Media for the Viewer, built into the app. One folder per menu list:\n" +
        "  Images          PNG or JPEG\n" +
        "  Videos          MP4 (H.264/H.265), MOV, WebM\n" +
        "  Spatial Images  stereo PNG or JPEG, or MPO stereo pairs\n" +
        "  Spatial Videos  stereo MP4/MOV/WebM\n" +
        "A folder can also hold a link instead of the file: a .url, .webloc or .txt file containing the http(s) address\n" +
        "of the image or video (a direct link to the file, not a web page such as YouTube).\n" +
        "The list shows each file's name without its extension.\n\n" +
        "Spatial media is side by side (left eye on the left) unless its name says otherwise:\n" +
        "  _tb / _ou / top-bottom / over-under   top and bottom (left eye on top)\n" +
        "  _sbs / _lr / side-by-side             side by side\n" +
        "  _rl                                   side by side, right eye first\n" +
        "  _hsbs / _half                         each eye squeezed to half width (shown at full width)\n" +
        "Without a tag, a frame taller than wide is taken as top and bottom.\n" +
        "Files that say what they are need no tag: 3D videos with a stereo tag, MPO stereo photos, and frames at\n" +
        "least twice as wide as 16:9 (two views side by side). The Quest's own 3D captures are found this way too.\n" +
        "Apple spatial photos (HEIC) and spatial videos (MV-HEVC) must be converted to side by side first.\n\n" +
        "Media files here are not committed to git (see .gitignore); large videos make the APK large.\n" +
        "For media without a rebuild, use push-media.sh to copy files onto a headset.\n";

    public int callbackOrder => -100;

    public void OnPreprocessBuild(BuildReport report) => WriteIndex();

    [InitializeOnLoadMethod]
    static void WriteAfterReload() => EditorApplication.delayCall += () =>
    {
        if (!EditorApplication.isPlayingOrWillChangePlaymode && !BuildPipeline.isBuildingPlayer)
            WriteIndex();
    };

    [MenuItem("Tunnel/Viewer/Update Media List")]
    static void WriteFromMenu()
    {
        int count = WriteIndex();
        Debug.Log($"[Viewer] {count} built-in media item(s) listed");
    }

    static string root => Path.Combine(Application.streamingAssetsPath, ViewerLibrary.FolderName);

    /// <summary>Creates the folders if needed and rewrites index.json when it changed; returns the entry count.</summary>
    static int WriteIndex()
    {
        bool created = false;
        foreach (string folder in ViewerLibrary.KindFolders)
        {
            string path = Path.Combine(root, folder);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                created = true;
            }
        }

        string readme = Path.Combine(root, "README.txt");
        if (!File.Exists(readme) || File.ReadAllText(readme) != k_Readme)
        {
            File.WriteAllText(readme, k_Readme);
            created = true;
        }

        var index = new ViewerLibrary.Index { entries = ViewerLibrary.ScanFolder(root, false, false) };
        string json = JsonUtility.ToJson(index, true);
        string indexPath = Path.Combine(root, ViewerLibrary.IndexFile);
        if (!File.Exists(indexPath) || File.ReadAllText(indexPath) != json)
        {
            File.WriteAllText(indexPath, json);
            created = true;
        }

        if (created)
            AssetDatabase.Refresh();
        return index.entries.Count;
    }
}
