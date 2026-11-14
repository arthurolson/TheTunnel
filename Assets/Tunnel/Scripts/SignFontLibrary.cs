using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The fonts a sign can be written in (see <see cref="TunnelSign"/>), in the order its menu lists them, each with a
/// friendly name such as "Merriweather (serif)". A font has a regular face and, where the font family has them, real
/// bold, italic and bold italic faces; where it doesn't, the sign leans or thickens the regular face instead. Each face
/// also has a drop-shadow version of its material. Made by Tunnel > Signage > Build Signage from the font files in
/// Assets/Tunnel/Signage/Fonts (each folder with its licence), so a new open-licence font is added by dropping its
/// folder there and running the builder again.
/// </summary>
[CreateAssetMenu(menuName = "Tunnel/Sign Font Library")]
public class SignFontLibrary : ScriptableObject
{
    [Serializable]
    public class Face
    {
        public TMP_FontAsset font;

        [Tooltip("The same face's material with a soft drop shadow under the letters (TMP underlay).")]
        public Material shadow;
    }

    [Serializable]
    public class Entry
    {
        [Tooltip("As the sign's menu lists it, e.g. \"Pacifico (script)\".")]
        public string name;

        public Face regular = new();
        public Face bold = new();
        public Face italic = new();
        public Face boldItalic = new();
    }

    [SerializeField]
    List<Entry> m_Fonts = new();

    public IReadOnlyList<Entry> fonts => m_Fonts;

    public int count => m_Fonts.Count;

    /// <summary>The font at this place in the list (the first one if out of range), or null for an empty library.</summary>
    public Entry Get(int index) => m_Fonts.Count == 0 ? null : m_Fonts[index >= 0 && index < m_Fonts.Count ? index : 0];

    /// <summary>Where a font with this name is in the list, or -1.</summary>
    public int IndexOf(string fontName)
    {
        for (int i = 0; i < m_Fonts.Count; i++)
            if (m_Fonts[i] != null && string.Equals(m_Fonts[i].name, fontName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// The face to write in for this font and style: the family's own bold / italic face when it has one; otherwise
    /// the closest face it has, with <paramref name="fakeBold"/> / <paramref name="fakeItalic"/> saying what the sign
    /// must thicken or lean itself.
    /// </summary>
    public Face FaceFor(int index, bool bold, bool italic, out bool fakeBold, out bool fakeItalic)
    {
        fakeBold = fakeItalic = false;
        var entry = Get(index);
        if (entry == null)
            return null;
        static bool Has(Face face) => face != null && face.font != null;
        if (bold && italic && Has(entry.boldItalic))
            return entry.boldItalic;
        if (bold && italic && Has(entry.bold))
        {
            fakeItalic = true;
            return entry.bold;
        }
        if (bold && italic && Has(entry.italic))
        {
            fakeBold = true;
            return entry.italic;
        }
        if (bold && !italic && Has(entry.bold))
            return entry.bold;
        if (italic && !bold && Has(entry.italic))
            return entry.italic;
        fakeBold = bold;
        fakeItalic = italic;
        return entry.regular;
    }

#if UNITY_EDITOR
    /// <summary>Set by the builder (Tunnel > Signage > Build Signage).</summary>
    public void SetFonts(List<Entry> fonts) => m_Fonts = fonts;
#endif
}
