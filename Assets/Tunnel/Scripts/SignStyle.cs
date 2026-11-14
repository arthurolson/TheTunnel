using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// How a sign looks (see <see cref="TunnelSign"/>): its font, regular / bold / italic, drop shadow, line alignment,
/// the colours of its letters, border and background, the border's width and the space between the letters and the
/// border. Shared by everyone in a session as one value.
/// </summary>
[Serializable]
public struct SignStyle : INetworkSerializeByMemcpy, IEquatable<SignStyle>
{
    public const byte k_Bold = 1, k_Italic = 2, k_Shadow = 4, k_Border = 8;

    public enum Align : byte
    {
        Left,
        Center,
        Right,
    }

    public enum Part : byte
    {
        Text,
        Border,
        Background,
    }

    public const float k_MinBorderWidth = 0.002f, k_MaxBorderWidth = 0.03f;
    public const float k_MaxPadding = 0.1f;

    /// <summary>The font's place in the sign font library.</summary>
    public byte font;

    public byte flags;
    public Align align;
    public Color32 textColor;
    public Color32 borderColor;
    public Color32 backgroundColor;

    /// <summary>Meters, before the sign is scaled.</summary>
    public float borderWidth;

    /// <summary>Space between the letters and the border, meters, before the sign is scaled.</summary>
    public float padding;

    /// <summary>White letters on a dark slate panel with a light frame, centred, in the library's first font.</summary>
    public static SignStyle Default => new()
    {
        font = 0,
        flags = k_Border,
        align = Align.Center,
        textColor = new Color32(255, 255, 255, 255),
        borderColor = new Color32(200, 214, 230, 255),
        backgroundColor = new Color32(28, 34, 46, 220),
        borderWidth = 0.006f,
        padding = 0.03f,
    };

    public bool bold { get => (flags & k_Bold) != 0; set => Set(k_Bold, value); }
    public bool italic { get => (flags & k_Italic) != 0; set => Set(k_Italic, value); }
    public bool shadow { get => (flags & k_Shadow) != 0; set => Set(k_Shadow, value); }
    public bool border { get => (flags & k_Border) != 0; set => Set(k_Border, value); }

    void Set(byte bit, bool on) => flags = (byte)(on ? flags | bit : flags & ~bit);

    public Color32 ColorOf(Part part) => part switch
    {
        Part.Border => borderColor,
        Part.Background => backgroundColor,
        _ => textColor,
    };

    public void SetColor(Part part, Color32 color)
    {
        switch (part)
        {
            case Part.Border: borderColor = color; break;
            case Part.Background: backgroundColor = color; break;
            default: textColor = color; break;
        }
    }

    /// <summary>The same style with every number kept in its range.</summary>
    public SignStyle Clamped()
    {
        var s = this;
        s.borderWidth = Mathf.Clamp(s.borderWidth, k_MinBorderWidth, k_MaxBorderWidth);
        s.padding = Mathf.Clamp(s.padding, 0f, k_MaxPadding);
        if ((byte)s.align > (byte)Align.Right)
            s.align = Align.Center;
        return s;
    }

    static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    public bool Equals(SignStyle other) =>
        font == other.font && flags == other.flags && align == other.align && Same(textColor, other.textColor)
        && Same(borderColor, other.borderColor) && Same(backgroundColor, other.backgroundColor)
        && Mathf.Approximately(borderWidth, other.borderWidth) && Mathf.Approximately(padding, other.padding);

    public override bool Equals(object obj) => obj is SignStyle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(font, flags, align, borderWidth, padding);
}

/// <summary>
/// A sign as it is stored on the host (see <see cref="SignStore"/>): its text, its style (with the font by name, so
/// a stored sign still finds its font if the list changes) and its size.
/// </summary>
[Serializable]
public class SignRecord
{
    public string name;
    public string text;
    public string font;
    public bool bold;
    public bool italic;
    public bool shadow;
    public bool border;
    public string align;
    public string textColor;
    public string borderColor;
    public string backgroundColor;
    public float borderWidth;
    public float padding;
    public float scale = 1f;
    public string saved;

    public static SignRecord From(string name, string text, SignStyle style, SignFontLibrary fonts, float scale) => new()
    {
        name = name,
        text = text,
        font = fonts != null && fonts.Get(style.font) != null ? fonts.Get(style.font).name : "",
        bold = style.bold,
        italic = style.italic,
        shadow = style.shadow,
        border = style.border,
        align = style.align.ToString(),
        textColor = "#" + ColorUtility.ToHtmlStringRGBA(style.textColor),
        borderColor = "#" + ColorUtility.ToHtmlStringRGBA(style.borderColor),
        backgroundColor = "#" + ColorUtility.ToHtmlStringRGBA(style.backgroundColor),
        borderWidth = style.borderWidth,
        padding = style.padding,
        scale = scale,
        saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
    };

    /// <summary>The style this record describes (anything missing or unreadable keeps the default).</summary>
    public SignStyle ToStyle(SignFontLibrary fonts)
    {
        var style = SignStyle.Default;
        int index = fonts != null && !string.IsNullOrEmpty(font) ? fonts.IndexOf(font) : -1;
        style.font = (byte)Mathf.Max(0, index);
        style.bold = bold;
        style.italic = italic;
        style.shadow = shadow;
        style.border = border;
        if (Enum.TryParse(align, true, out SignStyle.Align a))
            style.align = a;
        if (ColorUtility.TryParseHtmlString(textColor, out var c))
            style.textColor = c;
        if (ColorUtility.TryParseHtmlString(borderColor, out c))
            style.borderColor = c;
        if (ColorUtility.TryParseHtmlString(backgroundColor, out c))
            style.backgroundColor = c;
        style.borderWidth = borderWidth;
        style.padding = padding;
        return style.Clamped();
    }
}
