using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Copies the media marked Save in a Viewer list into the app: from every connected headset (over adb) and from this
/// Mac's own Viewer folder, into Assets/StreamingAssets/Viewer/&lt;kind&gt;, so the next build has them on every headset.
/// Each headset keeps its marks in Android/data/&lt;package&gt;/files/Viewer/save-to-app.txt (see
/// <see cref="ViewerLibrary.SaveListFile"/>). Files already in the app are skipped. Runs before every build (from
/// <see cref="ViewerMediaIndex"/>) and from Tunnel > Viewer > Copy Saved Media Into App.
/// </summary>
static class ViewerSavedMedia
{
    const int k_ListTimeoutMs = 15000;
    const int k_PullTimeoutMs = 10 * 60 * 1000;

    [MenuItem("Tunnel/Viewer/Copy Saved Media Into App")]
    static void CopyFromMenu()
    {
        int copied = CopyIntoApp();
        AssetDatabase.Refresh();
        Debug.Log($"[Viewer] {copied} saved media file(s) copied into the app");
    }

    static string builtInRoot => Path.Combine(Application.streamingAssetsPath, ViewerLibrary.FolderName);

    /// <summary>Returns how many files were copied.</summary>
    public static int CopyIntoApp()
    {
        int copied = 0;

        // This Mac (Play mode in the Editor marks files in its own Viewer folder).
        foreach (var (kind, path) in ViewerLibrary.ReadSaveList())
        {
            string destination = Destination(kind, path);
            if (File.Exists(destination) || !File.Exists(path))
                continue;
            File.Copy(path, destination);
            copied++;
            Debug.Log($"[Viewer] saved into the app: {destination}");
        }

        string adb = FindAdb();
        if (adb == null)
            return copied;

        string list = $"/sdcard/Android/data/{PlayerSettings.applicationIdentifier}/files/{ViewerLibrary.FolderName}/{ViewerLibrary.SaveListFile}";
        foreach (string serial in Devices(adb))
        {
            if (!Run(adb, $"-s {serial} shell cat \"{list}\"", k_ListTimeoutMs, out string text) || text.Contains("No such file"))
                continue;
            foreach (string line in text.Split('\n').Select(l => l.Trim('\r')).Where(l => l.Length > 0))
            {
                var parts = line.Split('\t');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int kind) || kind < 0 || kind >= ViewerLibrary.KindFolders.Length)
                    continue;
                string destination = Destination(kind, parts[1]);
                if (File.Exists(destination))
                    continue;
                EditorUtility.DisplayProgressBar("Viewer", $"Copying {Path.GetFileName(destination)} from headset {serial}", 0.5f);
                try
                {
                    if (Run(adb, $"-s {serial} pull \"{parts[1]}\" \"{destination}\"", k_PullTimeoutMs, out string output) && File.Exists(destination))
                    {
                        copied++;
                        Debug.Log($"[Viewer] saved into the app from {serial}: {destination}");
                    }
                    else
                    {
                        Debug.LogWarning($"[Viewer] couldn't copy {parts[1]} from {serial}: {output.Trim()}");
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }

        return copied;
    }

    static string Destination(int kind, string path)
    {
        string folder = Path.Combine(builtInRoot, ViewerLibrary.KindFolders[kind]);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Path.GetFileName(path.Replace('\\', '/')));
    }

    static string FindAdb()
    {
        var candidates = new List<string>();
        try
        {
            string sdk = AndroidExternalToolsSettings.sdkRootPath;
            if (!string.IsNullOrEmpty(sdk))
                candidates.Add(Path.Combine(sdk, "platform-tools", "adb"));
        }
        catch (Exception)
        {
            // Android support not installed.
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, "Library/Android/sdk/platform-tools/adb"));
        candidates.Add("/opt/homebrew/bin/adb");
        candidates.Add("/usr/local/bin/adb");
        return candidates.FirstOrDefault(File.Exists);
    }

    static IEnumerable<string> Devices(string adb)
    {
        if (!Run(adb, "devices", k_ListTimeoutMs, out string text))
            return Array.Empty<string>();
        return text.Split('\n').Skip(1).Select(l => l.Trim().Split('\t')).Where(p => p.Length == 2 && p[1] == "device").Select(p => p[0]).ToList();
    }

    static bool Run(string exe, string arguments, int timeoutMs, out string output)
    {
        output = string.Empty;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill();
                output = "timed out";
                return false;
            }

            output = stdout.Result + stderr.Result;
            return process.ExitCode == 0;
        }
        catch (Exception e)
        {
            output = e.Message;
            return false;
        }
    }
}
