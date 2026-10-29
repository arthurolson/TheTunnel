using UnityEngine;

/// <summary>
/// Copy and paste between the app's panels (the browser's Copy button, the Viewer's Paste button). Also goes to the
/// headset's own clipboard, so text copied in another app can be pasted here and the other way round.
/// </summary>
public static class TunnelClipboard
{
    static string s_Text = string.Empty;

    public static void Copy(string text)
    {
        s_Text = text ?? string.Empty;
        try
        {
            GUIUtility.systemCopyBuffer = s_Text;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Clipboard] system clipboard unavailable: {e.Message}");
        }
    }

    /// <summary>The headset's clipboard if it has text, otherwise what was last copied in the app.</summary>
    public static string Paste()
    {
        try
        {
            string system = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrWhiteSpace(system))
                return system.Trim();
        }
        catch (System.Exception)
        {
            // fall back to the app's own copy
        }

        return s_Text.Trim();
    }
}
