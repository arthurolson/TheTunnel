using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// Checks the signage (see <see cref="TunnelSign"/>) without a headset or a session, through its own buttons where it
/// has them: the font library; New sign on the TS Menu's Signage list (the sign lies flat on the wall beside the list
/// with its menu open); editing the text on the keyboard (two lines); every font in the list; Regular, Bold and Italic
/// (the family's own faces where it has them, else leaned or thickened); drop shadow; the colours of the letters and
/// background; alignment; the border on and off, its width and the space round the text; Save (and saving again
/// replacing its stored copy), Place from the saved list, Delete; letting go of a sign near a wall and far from one,
/// pulling it off, a large sign kept on the wall; the 1000-character limit; and the Edit buttons check box. Stored
/// signs are written to a folder of the test's own, removed afterwards. Runs from Tunnel > Signage > Test Signage (in
/// Play mode) and logs PASS or FAIL with the tag [Sign].
/// </summary>
static class SignageEditorTests
{
    const BindingFlags k_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, k_Flags).GetValue(target);

    static void Press(object owner, string buttonField) => Field<Button>(owner, buttonField).onClick.Invoke();

    static void Call(object target, string method) => target.GetType().GetMethod(method, k_Flags).Invoke(target, null);

    [MenuItem("Tunnel/Signage/Test Signage (in Play mode)")]
    public static string Test() => Run(false);

    /// <summary>Runs the checks; with <paramref name="keep"/> the test's segment and signs are left in place to look at.</summary>
    public static string Run(bool keep)
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Sign] enter Play mode (any scene) first, then run Tunnel > Signage > Test Signage");
            return "needs Play mode";
        }

        var report = new StringBuilder("[Sign] signage test (not connected)\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }

        string previousFolder = SignStore.folder;
        string folder = Path.Combine(Application.temporaryCachePath, "SignageTest");
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        SignStore.folder = folder;
        GameObject segment = null;
        var made = new List<TunnelSign>();
        try
        {
            // The fonts.
            var library = AssetDatabase.LoadAssetAtPath<SignFontLibrary>(SignageBuilder.k_LibraryPath);
            Check(library != null && library.count >= 19, $"the font library lists {library?.count} fonts");
            Check(library.fonts.All(e => e.regular.font != null && e.regular.shadow != null && e.regular.font.HasCharacter('A') && e.regular.font.HasCharacter('g')),
                "every font has its letters and a drop-shadow material");
            Check(library.fonts.All(e => e.regular.font.atlasTexture != null && e.regular.font.atlasTexture.width <= 1024),
                "every atlas is at most 1024 wide (" + string.Join(", ", library.fonts.Select(e => e.regular.font.atlasTexture.width).Distinct()) + ")");

            // A tunnel segment of its own, away from everything, its Signage list open.
            foreach (var old in TunnelSign.all.ToList())
                Object.Destroy(old.gameObject);
            segment = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(k_SegmentPath), new Vector3(40f, 0f, 40f), Quaternion.identity);
            segment.name = "Signage Test Segment";
            var signage = segment.GetComponent<TunnelSignageMenu>();
            Check(signage != null, "the tunnel segment has the Signage list");
            var geometry = Field<TunnelSegmentGeometry>(signage, "m_Geometry");
            signage.ShowList(true);

            // New sign.
            Press(signage, "m_NewButton");
            var sign = TunnelSign.all.LastOrDefault();
            if (sign != null)
                made.Add(sign);
            Check(sign != null && sign.text == "New sign", "New sign makes a sign that says \"New sign\"");
            Check(sign.IsOnWall(out var onWall) && onWall == geometry, "it lies flat on the list's wall");
            var list = (RectTransform)Field<GameObject>(signage, "m_ListPanel").transform;
            var corners = new Vector3[4];
            list.GetWorldCorners(corners);
            float listFar = corners.Max(c => geometry.transform.InverseTransformPoint(c).z);
            float signNear = geometry.transform.InverseTransformPoint(sign.transform.position).z - sign.worldHalfSize.x;
            Check(signNear > listFar, $"beside the list, not over it ({(signNear - listFar) * 100f:0} cm past it)");
            Check(sign.menuOpen, "its menu is open for whoever made it");
            var menu = sign.menu;
            Vector3 toMenu = menu.transform.position - sign.transform.position;
            Check(Vector3.Dot(toMenu, sign.transform.right) > sign.worldHalfSize.x && Quaternion.Angle(menu.transform.rotation, sign.transform.rotation) < 0.1f,
                "the menu is beside it, on its right, in its plane");

            // Text, typed on the keyboard.
            float oneLine = sign.innerHalfSize.y;
            Press(menu, "m_EditTextButton");
            Check(menu.keyboardOpen && menu.keyboard.text == "New sign", "Edit text opens the keyboard with the sign's text");
            menu.keyboard.Clear();
            menu.keyboard.UpdateText("Welcome");
            menu.keyboard.ProcessKeyCode(KeyCode.Return); // Enter: a new line
            menu.keyboard.UpdateText("to the Tunnel");
            Check(sign.text == "Welcome\nto the Tunnel", $"typing shows on the sign, Enter starting a new line (\"{sign.text.Replace("\n", "\\n")}\")");
            sign.textMesh.ForceMeshUpdate();
            Check(sign.textMesh.textInfo.lineCount == 2 && sign.innerHalfSize.y > oneLine * 1.5f, $"two lines: the panel grew from {oneLine * 200f:0.0} to {sign.innerHalfSize.y * 200f:0.0} cm high");
            Press(menu, "m_EditTextButton");
            Check(!menu.keyboardOpen, "Done editing closes the keyboard");

            // Every font in the list.
            int fontsOk = 0;
            for (int i = 0; i < menu.fontRows.Count; i++)
            {
                menu.fontRows[i].onClick.Invoke();
                if (sign.style.font == i && sign.textMesh.font == library.Get(i).regular.font && sign.halfSize.x > 0.05f)
                    fontsOk++;
            }
            Check(menu.fontRows.Count == library.count && fontsOk == library.count, $"each of the {menu.fontRows.Count} font rows writes the sign in its font ({fontsOk} did)");

            // Regular, bold, italic: Roboto's own faces; Lato has only a regular face.
            var roboto = library.Get(0);
            menu.fontRows[0].onClick.Invoke();
            Press(menu, "m_BoldButton");
            Check(sign.style.bold && sign.textMesh.font == roboto.bold.font && sign.textMesh.fontStyle == FontStyles.Normal, "Bold in Roboto uses Roboto Bold");
            Press(menu, "m_ItalicButton");
            Check(sign.textMesh.font == roboto.boldItalic.font, "Bold and Italic in Roboto uses Roboto Bold Italic");
            Press(menu, "m_RegularButton");
            Check(!sign.style.bold && !sign.style.italic && sign.textMesh.font == roboto.regular.font && sign.textMesh.fontStyle == FontStyles.Normal, "Regular goes back to Roboto Regular");
            int lato = library.IndexOf("Lato (sans)");
            menu.fontRows[lato].onClick.Invoke();
            Press(menu, "m_BoldButton");
            Press(menu, "m_ItalicButton");
            Check(sign.textMesh.font == library.Get(lato).regular.font && sign.textMesh.fontStyle == (FontStyles.Bold | FontStyles.Italic), "Bold and Italic in Lato thicken and lean its regular face");
            Press(menu, "m_RegularButton");

            // Drop shadow.
            Press(menu, "m_ShadowRow");
            Check(sign.style.shadow && sign.textMesh.fontSharedMaterial == library.Get(lato).regular.shadow && sign.textMesh.fontSharedMaterial.IsKeywordEnabled("UNDERLAY_ON"), "Drop shadow draws a shadow under the letters");
            Press(menu, "m_ShadowRow");
            Check(!sign.style.shadow && sign.textMesh.fontSharedMaterial == library.Get(lato).regular.font.material, "and off again");
            menu.fontRows[0].onClick.Invoke();

            // Colours.
            var hue = Field<Slider>(menu, "m_Hue");
            var saturation = Field<Slider>(menu, "m_Saturation");
            var value = Field<Slider>(menu, "m_Value");
            var opacity = Field<Slider>(menu, "m_Opacity");
            Press(menu, "m_TextTab");
            hue.value = 0.1f;
            saturation.value = 1f;
            value.value = 1f;
            hue.value = 0f;
            opacity.value = 1f;
            Color32 text = sign.style.textColor;
            Check(text.r == 255 && text.g == 0 && text.b == 0 && text.a == 255 && sign.textMesh.color == Color.red, $"the colour chooser makes the letters red ({text})");
            Press(menu, "m_BackgroundTab");
            Check(menu.colorPart == SignStyle.Part.Background && Mathf.Abs(opacity.value - sign.style.backgroundColor.a / 255f) < 0.01f, "Background shows the background's colour on the sliders");
            opacity.value = 0.5f;
            var colors = new List<Color32>();
            sign.panelMesh.GetColors(colors);
            Check(Mathf.Abs(sign.style.backgroundColor.a - 128) <= 1 && colors.Count > 0 && Mathf.Abs(colors[0].a - 128) <= 1, $"background opacity 0.5 ({sign.style.backgroundColor.a}/255)");
            Press(menu, "m_TextTab");
            Check(Mathf.Approximately(hue.value, 0f) && Mathf.Approximately(saturation.value, 1f), "Text shows the letters' red again");

            // Alignment.
            Press(menu, "m_LeftButton");
            Check(sign.style.align == SignStyle.Align.Left && sign.textMesh.alignment == TextAlignmentOptions.Left, "Left lines the lines up on the left");
            Press(menu, "m_CenterButton");
            Check(sign.textMesh.alignment == TextAlignmentOptions.Center, "Centre centres them");

            // The border.
            Check(sign.style.border && sign.panelMesh.vertexCount == 20, $"a new sign has a border ({sign.panelMesh.vertexCount} corners: background and frame)");
            Press(menu, "m_BorderRow");
            Check(!sign.style.border && sign.panelMesh.vertexCount == 4 && Mathf.Approximately(sign.halfSize.x, sign.innerHalfSize.x), "Border off leaves the background only");
            Press(menu, "m_BorderRow");
            Field<Slider>(menu, "m_BorderWidth").value = 20f;
            Check(Mathf.Abs(sign.style.borderWidth - 0.02f) < 1e-4f && Mathf.Abs(sign.halfSize.x - sign.innerHalfSize.x - 0.02f) < 1e-4f, "Border width 2 cm makes a 2 cm frame");
            float before = sign.innerHalfSize.x;
            Field<Slider>(menu, "m_Padding").value = 60f;
            Check(Mathf.Abs(sign.style.padding - 0.06f) < 1e-4f && Mathf.Abs(sign.innerHalfSize.x - before - (0.06f - SignStyle.Default.padding)) < 1e-4f, "Space round text 6 cm widens the panel by the difference");
            Field<Slider>(menu, "m_BorderWidth").value = 6f;

            // Save, save again, Place, Delete.
            Press(menu, "m_SaveButton");
            Check(SignStore.Names().SequenceEqual(new[] { "Welcome" }) && sign.saveName == "Welcome", $"Save stores it as \"Welcome\" ({string.Join(", ", SignStore.Names())}): {sign.message}");
            var record = SignStore.Load("Welcome");
            Check(record != null && record.text == "Welcome\nto the Tunnel" && record.font == "Roboto (sans)" && record.textColor == "#FF0000FF" && record.ToStyle(library).Equals(sign.style),
                "the stored copy has its text, font and colours");
            sign.SetText("Welcome\nto the Tunnel!");
            Press(menu, "m_SaveButton");
            Check(SignStore.Names().Count == 1 && SignStore.Load("Welcome").text.EndsWith("!"), "saving again replaces its stored copy");
            signage.ShowList(true);
            Check(signage.storedNames.SequenceEqual(new[] { "Welcome" }), "the Signage list shows it");
            sign.transform.localScale = Vector3.one * 1.5f;
            Press(menu, "m_SaveButton");
            signage.CheckStored("Welcome");
            Press(signage, "m_PlaceButton");
            var copy = TunnelSign.all.LastOrDefault();
            if (copy != null && copy != sign)
                made.Add(copy);
            Check(copy != null && copy != sign && copy.text == sign.text && copy.style.Equals(sign.style) && Mathf.Approximately(copy.transform.localScale.x, 1.5f) && copy.saveName == "Welcome",
                "Place puts up a copy of it: same text, style and size");
            Check(copy.IsOnWall(out _) && Mathf.Abs(geometry.transform.InverseTransformPoint(copy.transform.position).z - geometry.transform.InverseTransformPoint(sign.transform.position).z) >= copy.worldHalfSize.x + sign.worldHalfSize.x - 0.01f,
                "on the wall, beside the first one rather than over it");
            signage.CheckStored("Welcome");
            Press(signage, "m_DeleteButton");
            Check(SignStore.Names().Count == 0 && TunnelSign.all.Contains(copy), "Delete removes it from the saved signs; the signs in the tunnel stay");

            // Let go near a wall, far from one, pulled off; a large sign stays on the wall.
            var frame = geometry.transform;
            sign.transform.localScale = Vector3.one;
            float halfWidth = geometry.width * 0.5f;
            sign.transform.SetPositionAndRotation(frame.TransformPoint(new Vector3(-halfWidth + 0.15f, 1.4f, 0.3f)), frame.rotation * Quaternion.Euler(10f, 70f, 5f));
            Check(sign.TrySnapToWall(), "let go of 15 cm from the -X wall, it goes to the wall");
            var local = frame.InverseTransformPoint(sign.transform.position);
            Check(sign.IsOnWall(out _) && Vector3.Angle(sign.transform.forward, -frame.right) < 0.01f && Vector3.Angle(sign.transform.up, frame.up) < 0.01f
                  && Mathf.Abs(local.y - 1.4f) < 1e-3f && Mathf.Abs(local.z - 0.3f) < 1e-3f,
                $"flat and upright on it, where it was let go ({local.x:F3}, {local.y:F2}, {local.z:F2})");
            Physics.SyncTransforms();
            var box = sign.GetComponentInChildren<BoxCollider>();
            float backGap = box.bounds.min.x - frame.TransformPoint(new Vector3(-halfWidth, 0f, 0f)).x;
            Check(Mathf.Abs(frame.right.x) > 0.99f ? backGap > 0f && backGap < 0.005f : true, $"its back just off the wall ({backGap * 1000f:0.0} mm)");
            sign.transform.position += frame.right * 0.3f;
            Check(!sign.IsOnWall(out _), "taken off and moved 30 cm, it's off the wall");
            sign.transform.SetPositionAndRotation(frame.TransformPoint(new Vector3(0f, 1.4f, 0f)), frame.rotation);
            Check(!sign.TrySnapToWall(), "let go of in the middle of the tunnel, it stays where it is");
            sign.transform.localScale = Vector3.one * 3f;
            sign.transform.SetPositionAndRotation(frame.TransformPoint(new Vector3(halfWidth - 0.1f, geometry.wallHeight - 0.05f, geometry.length * 0.5f - 0.05f)), frame.rotation);
            Check(sign.TrySnapToWall(), "a large sign let go of high in the +X wall's corner goes to the wall");
            local = frame.InverseTransformPoint(sign.transform.position);
            var half = sign.worldHalfSize;
            Check(local.y + half.y <= geometry.wallHeight + 1e-3f && local.z + half.x <= geometry.length * 0.5f + 1e-3f && Vector3.Angle(sign.transform.forward, frame.right) < 0.01f,
                $"kept on the wall: its top {(geometry.wallHeight - local.y - half.y) * 100f:0.0} cm under the wall's top, its end {(geometry.length * 0.5f - local.z - half.x) * 100f:0.0} cm in from the segment's end");
            sign.transform.localScale = Vector3.one;

            // The text limit.
            sign.SetText(new string('x', 2500));
            Check(sign.text.Length == TunnelSign.k_MaxCharacters, $"text is cut at {TunnelSign.k_MaxCharacters} characters ({sign.text.Length})");
            sign.SetText("Welcome\nto the Tunnel");

            // The Edit buttons check box.
            Press(signage, "m_EditButtonsRow");
            sign.CloseMenu();
            Call(sign, "LateUpdate");
            var editCanvas = Field<Button>(sign, "m_EditButton").transform.parent.gameObject;
            Check(!TunnelSignageMenu.editButtonsShown && !editCanvas.activeSelf, "Edit buttons on signs unticked hides the sign's Edit button");
            Press(signage, "m_EditButtonsRow");
            Call(sign, "LateUpdate");
            Check(TunnelSignageMenu.editButtonsShown && editCanvas.activeSelf, "and ticked shows it again");
            Field<Button>(sign, "m_EditButton").onClick.Invoke();
            Check(sign.menuOpen, "the Edit button opens the menu");
            Press(menu, "m_CloseButton");
            Check(!sign.menuOpen, "Close menu closes it");
            if (keep)
                sign.OpenMenu();
        }
        catch (System.Exception e)
        {
            Check(false, $"stopped by {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
        finally
        {
            if (!keep)
            {
                foreach (var s in made)
                    if (s != null)
                        Object.Destroy(s.gameObject);
                if (segment != null)
                    Object.Destroy(segment);
            }
            SignStore.folder = previousFolder;
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }

        report.AppendLine(failures == 0 ? "  PASS" : $"  FAIL ({failures})");
        string result = report.ToString();
        if (failures == 0)
            Debug.Log(result);
        else
            Debug.LogWarning(result);
        return result;
    }
}
