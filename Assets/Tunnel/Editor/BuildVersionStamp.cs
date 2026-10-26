using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Gives every build a new version before it starts: the version name is the build date plus that day's build
/// number (DD.MM.YYYY.N, e.g. 26.10.2026.3), and the Android version code is YYMMDDNN (26102603), which keeps
/// rising from day to day as the Meta store requires. A build that fails still uses up its number.
/// </summary>
class BuildVersionStamp : IPreprocessBuildWithReport
{
    const string k_DateFormat = "dd.MM.yyyy";
    const int k_MaxBuildsPerDay = 99; // the version code has two digits for the day's build number

    public int callbackOrder => -1000;

    public void OnPreprocessBuild(BuildReport report)
    {
        string previous = PlayerSettings.bundleVersion;
        int previousCode = PlayerSettings.Android.bundleVersionCode;
        string version = NextVersion(DateTime.Now, previous, previousCode, out int code);

        PlayerSettings.bundleVersion = version;
        PlayerSettings.Android.bundleVersionCode = code;
        AssetDatabase.SaveAssets();
        Debug.Log($"[BuildVersion] {previous} ({previousCode}) -> {version} ({code})");
    }

    internal static string NextVersion(DateTime now, string previous, int previousCode, out int code)
    {
        string today = now.ToString(k_DateFormat, CultureInfo.InvariantCulture);
        int build = 1;
        if (previous != null && previous.StartsWith(today + ".", StringComparison.Ordinal) &&
            int.TryParse(previous.Substring(today.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int last))
            build = last + 1;

        if (build > k_MaxBuildsPerDay)
            throw new BuildFailedException($"Already {k_MaxBuildsPerDay} builds today; the version code has no room for more until tomorrow.");

        code = (now.Year % 100) * 1000000 + now.Month * 10000 + now.Day * 100 + build;
        // Never go backwards (e.g. a wrong clock), or the store would refuse the upload.
        code = Math.Max(code, previousCode + 1);
        return $"{today}.{build}";
    }
}
