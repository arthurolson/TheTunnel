using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Makes the Signage (see <see cref="TunnelSign"/>): a TextMesh Pro SDF font for every font file in
/// Assets/Tunnel/Signage/Fonts (static 512 x 512 atlases holding the letters, digits, punctuation, common accents and a
/// few Greek letters and symbols a sign needs; others come from the TMP fallback font), each with a drop-shadow
/// material; the font library the sign's menu lists (see <see cref="SignFontLibrary"/>); the sign's menu (see
/// <see cref="TunnelSignPanel"/>); the sign prefab (a copy of the 2PLV molecule's toy without its molecule, so it is
/// grabbed, scaled and shared the same way) and its entry in the network prefab list; and the TS Menu's Signage button
/// and list in the tunnel segment prefab (see <see cref="TunnelSignageMenu"/>). Fonts already made are kept (delete an
/// SDF asset to make it again). Needs the Containers list (Tunnel > Containers > Build Containers), whose look the menus
/// copy. Runs from Tunnel > Signage > Build Signage.
/// </summary>
static class SignageBuilder
{
    const string k_TemplatePath = "Assets/Molecules/2PLV/Toy_2PLV_Networked.prefab";
    public const string k_Folder = "Assets/Tunnel/Signage";
    const string k_FontFolder = k_Folder + "/Fonts";
    const string k_PresetFolder = k_FontFolder + "/Presets";
    public const string k_PrefabPath = k_Folder + "/Sign_Networked.prefab";
    const string k_MenuPath = k_Folder + "/Sign_Menu.prefab";
    public const string k_LibraryPath = k_Folder + "/SignFontLibrary.asset";
    const string k_PanelMaterialPath = k_Folder + "/SignPanel.mat";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_NetworkListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";
    const string k_KeyboardPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Spatial Keyboard/Prefabs/XRI Keyboard.prefab";

    // The atlases: 512 x 512, the largest sampling size (from 56 points down) at which every character fits.
    const int k_AtlasSize = 512;
    const int k_Padding = 6;
    public const string k_Characters =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
        "°±×÷µ·•–—‘’“”…€£©®™²³½¼¾¡¿ÀÁÂÄÅÇÈÉÊËÍÎÏÑÓÔÖØÚÛÜßàáâäåçèéêëíîïñóôöøúûüæœ" +
        "αβγδεθλμπσΔΩ→←↑↓≤≥≈";

    // The menus: as the movie panel (0.4 mm per canvas unit); the sign's menu is 40 x 33 cm.
    const float k_CanvasScale = 0.0004f;
    static readonly Vector2 k_MenuSize = new(1000f, 820f);

    /// <summary>
    /// The fonts, in the order the sign's menu lists them: a friendly name, the folder under Signage/Fonts, and the
    /// files (without .ttf) of its regular, bold, italic and bold italic faces ("" when the family hasn't that face).
    /// A font already in the app is given by its SDF asset's path instead of a folder. Font folders not named here are
    /// added at the end, one entry per file, named after the file.
    /// </summary>
    static readonly (string name, string folder, string regular, string bold, string italic, string boldItalic)[] k_Fonts =
    {
        ("Roboto (sans)", "Roboto", "Roboto-Regular", "Roboto-Bold", "Roboto-Italic", "Roboto-BoldItalic"),
        ("Inter (sans)", "", "Assets/MRTabletopAssets/Fonts/Inter-Regular SDF.asset", "", "", ""),
        ("Lato (sans)", "Lato", "Lato-Regular", "", "", ""),
        ("Liberation Sans (sans)", "", "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset", "", "", ""),
        ("Oswald (condensed)", "Oswald", "Oswald-Variable", "", "", ""),
        ("Bebas Neue (headline)", "BebasNeue", "BebasNeue-Regular", "", "", ""),
        ("Teko (headline)", "Teko", "Teko-Bold", "", "", ""),
        ("Roboto Mono (monospace)", "RobotoMono", "RobotoMono-Regular", "", "", ""),
        ("Merriweather (serif)", "Merriweather", "Merriweather-Variable", "", "", ""),
        ("Playfair Display (serif)", "PlayfairDisplay", "PlayfairDisplay-Variable", "", "", ""),
        ("Cinzel (classical capitals)", "Cinzel", "Cinzel-Variable", "", "", ""),
        ("Abril Fatface (display)", "AbrilFatface", "AbrilFatface-Regular", "", "", ""),
        ("Lobster (script)", "Lobster", "Lobster-Regular", "", "", ""),
        ("Pacifico (script)", "Pacifico", "Pacifico-Regular", "", "", ""),
        ("Dancing Script (script)", "DancingScript", "DancingScript-Variable", "", "", ""),
        ("Stardos Stencil (stencil)", "StardosStencil", "StardosStencil-Regular", "StardosStencil-Bold", "", ""),
        ("Allerta Stencil (stencil)", "AllertaStencil", "AllertaStencil-Regular", "", "", ""),
        ("Saira Stencil One (stencil)", "SairaStencilOne", "SairaStencilOne-Regular", "", "", ""),
        ("Black Ops One (stencil)", "BlackOpsOne", "BlackOpsOne-Regular", "", "", ""),
    };

    [MenuItem("Tunnel/Signage/Build Signage")]
    public static string Build() => BuilderScratchScene.Run(BuildAll);

    static string BuildAll()
    {
        var report = new List<string>();
        var library = BuildLibrary(report);
        var panelMaterial = PanelMaterial();
        var menu = BuildMenu(library);
        var sign = BuildSign(library, panelMaterial, menu);

        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_NetworkListPath);
        foreach (var entry in list.PrefabList.Where(p => p.Prefab == null).ToList())
            list.Remove(entry);
        if (!list.PrefabList.Any(p => p.Prefab == sign.gameObject))
            list.Add(new NetworkPrefab { Prefab = sign.gameObject });
        EditorUtility.SetDirty(list);

        string tsMenu = BuildTsMenu(sign);
        AssetDatabase.SaveAssets();
        string log = $"[Sign] built {k_PrefabPath}, {k_MenuPath} and {k_LibraryPath} ({library.count} fonts); network prefab listed; {tsMenu}\n  " + string.Join("\n  ", report);
        Debug.Log(log);
        return log;
    }

    #region Fonts

    static SignFontLibrary BuildLibrary(List<string> report)
    {
        if (!AssetDatabase.IsValidFolder(k_PresetFolder))
            AssetDatabase.CreateFolder(k_FontFolder, "Presets");
        var entries = new List<SignFontLibrary.Entry>();
        var used = new HashSet<string>();

        SignFontLibrary.Face Face(string folder, string file)
        {
            if (string.IsNullOrEmpty(file))
                return new SignFontLibrary.Face();
            TMP_FontAsset font;
            if (file.EndsWith(".asset"))
                font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(file);
            else
            {
                string ttf = $"{k_FontFolder}/{folder}/{file}.ttf";
                if (!File.Exists(ttf))
                    ttf = Path.ChangeExtension(ttf, ".otf");
                used.Add(ttf);
                font = MakeSdf(ttf, report);
            }
            if (font == null)
            {
                report.Add($"FAILED: no font from {folder}/{file}");
                return new SignFontLibrary.Face();
            }
            return new SignFontLibrary.Face { font = font, shadow = ShadowPreset(font) };
        }

        foreach (var spec in k_Fonts)
        {
            var entry = new SignFontLibrary.Entry
            {
                name = spec.name,
                regular = Face(spec.folder, spec.regular),
                bold = Face(spec.folder, spec.bold),
                italic = Face(spec.folder, spec.italic),
                boldItalic = Face(spec.folder, spec.boldItalic),
            };
            if (entry.regular.font != null)
                entries.Add(entry);
        }

        // Font files dropped in later: one entry each, named after the file.
        foreach (var path in Directory.GetFiles(k_FontFolder, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".ttf") || p.EndsWith(".otf")).Select(p => p.Replace('\\', '/')).OrderBy(p => p))
        {
            if (used.Contains(path))
                continue;
            var font = MakeSdf(path, report);
            if (font == null)
                continue;
            entries.Add(new SignFontLibrary.Entry
            {
                name = Path.GetFileNameWithoutExtension(path).Replace("-Regular", "").Replace('-', ' '),
                regular = new SignFontLibrary.Face { font = font, shadow = ShadowPreset(font) },
            });
            report.Add($"added {path} (not in the builder's list, so named after its file)");
        }

        var library = AssetDatabase.LoadAssetAtPath<SignFontLibrary>(k_LibraryPath);
        if (library == null)
        {
            library = ScriptableObject.CreateInstance<SignFontLibrary>();
            AssetDatabase.CreateAsset(library, k_LibraryPath);
        }
        library.SetFonts(entries);
        EditorUtility.SetDirty(library);
        return library;
    }

    /// <summary>
    /// A static SDF font asset for a font file (kept if made before): the sign's characters in one 512 x 512 atlas, at
    /// the largest sampling size that holds every one of them the font has.
    /// </summary>
    static TMP_FontAsset MakeSdf(string fontPath, List<string> report)
    {
        string sdfPath = Path.ChangeExtension(fontPath, null) + " SDF.asset";
        var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(sdfPath);
        if (existing != null)
            return existing;
        var font = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
        if (font == null)
        {
            report.Add($"FAILED: {fontPath} isn't imported as a font");
            return null;
        }

        // The characters this font has (the others come from the fallback font).
        if (FontEngine.LoadFontFace(font, 48) != FontEngineError.Success)
        {
            report.Add($"FAILED: TextMesh Pro can't read {fontPath}");
            return null;
        }
        var wanted = k_Characters.Distinct().Where(c => FontEngine.TryGetGlyphIndex(c, out uint glyph) && glyph != 0).ToArray();
        string missingFromFont = new(k_Characters.Distinct().Except(wanted).ToArray());

        TMP_FontAsset asset = null;
        int size;
        for (size = 56; size >= 20; size -= 4)
        {
            asset = TMP_FontAsset.CreateFontAsset(font, size, k_Padding, GlyphRenderMode.SDFAA, k_AtlasSize, k_AtlasSize, AtlasPopulationMode.Dynamic, false);
            if (asset == null)
            {
                report.Add($"FAILED: TextMesh Pro made no font asset from {fontPath}");
                return null;
            }
            asset.TryAddCharacters(new string(wanted), out string missing);
            if (string.IsNullOrEmpty(missing) || size == 20)
            {
                if (!string.IsNullOrEmpty(missing))
                    report.Add($"{Path.GetFileName(fontPath)}: {missing.Length} characters didn't fit even at 20 pt: {missing}");
                break;
            }
            Object.DestroyImmediate(asset.atlasTexture);
            Object.DestroyImmediate(asset.material);
            Object.DestroyImmediate(asset);
        }

        asset.name = Path.GetFileNameWithoutExtension(sdfPath);
        asset.atlasPopulationMode = AtlasPopulationMode.Static;
        AssetDatabase.CreateAsset(asset, sdfPath);
        var atlas = asset.atlasTexture;
        atlas.name = asset.name + " Atlas";
        AssetDatabase.AddObjectToAsset(atlas, asset);
        var material = asset.material;
        material.name = asset.name + " Material";
        AssetDatabase.AddObjectToAsset(material, asset);
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();
        // The atlas is only drawn from, never read by the app: no copy of it kept in memory.
        var atlasSo = new SerializedObject(atlas);
        var readable = atlasSo.FindProperty("m_IsReadable");
        if (readable != null)
        {
            readable.boolValue = false;
            atlasSo.ApplyModifiedPropertiesWithoutUndo();
        }
        report.Add($"made {sdfPath}: {asset.characterTable.Count} characters at {size} pt" +
                   (missingFromFont.Length > 0 ? $"; not in the font (from the fallback): {missingFromFont}" : ""));
        return asset;
    }

    /// <summary>The font's own material with a soft dark shadow under the letters (TMP underlay), next to its SDF.</summary>
    static Material ShadowPreset(TMP_FontAsset font)
    {
        string fontPath = AssetDatabase.GetAssetPath(font);
        string path = fontPath.StartsWith(k_FontFolder) && !fontPath.StartsWith(k_PresetFolder)
            ? Path.ChangeExtension(fontPath, null) + " Shadow.mat"
            : $"{k_PresetFolder}/{font.name} Shadow.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(font.material);
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = font.material.shader;
        material.CopyPropertiesFromMaterial(font.material);
        material.name = Path.GetFileNameWithoutExtension(path);
        material.SetTexture(ShaderUtilities.ID_MainTex, font.atlasTexture);
        material.EnableKeyword("UNDERLAY_ON");
        material.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.7f));
        material.SetFloat("_UnderlayOffsetX", 0.6f);
        material.SetFloat("_UnderlayOffsetY", -0.6f);
        material.SetFloat("_UnderlayDilate", 0.15f);
        material.SetFloat("_UnderlaySoftness", 0.35f);
        ShaderUtilities.UpdateShaderRatios(material);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material PanelMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_PanelMaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Tunnel/Sign Panel")) { name = "SignPanel" };
            AssetDatabase.CreateAsset(material, k_PanelMaterialPath);
        }
        material.shader = Shader.Find("Tunnel/Sign Panel");
        material.renderQueue = 2990; // before the letters (3000)
        EditorUtility.SetDirty(material);
        return material;
    }

    #endregion

    #region UI parts (copied from the TS Menu's look)

    static RectTransform UiObject(string name, RectTransform parent, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return rect;
    }

    static RectTransform Canvas(Transform control, Transform parent, string name, Vector2 size, float scale)
    {
        var canvasObject = new GameObject(name, typeof(RectTransform));
        canvasObject.layer = control.gameObject.layer;
        var canvas = (RectTransform)canvasObject.transform;
        canvas.SetParent(parent, false);
        foreach (var type in new[] { typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster) })
        {
            var source = control.GetComponent(type);
            if (source != null && UnityEditorInternal.ComponentUtility.CopyComponent(source))
                UnityEditorInternal.ComponentUtility.PasteComponentAsNew(canvasObject);
        }
        canvas.sizeDelta = size;
        canvas.localScale = Vector3.one * scale;
        canvas.localPosition = Vector3.zero;
        canvas.localRotation = Quaternion.identity;
        return canvas;
    }

    static TMP_Text Text(GameObject template, RectTransform parent, string name, string text, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions alignment)
    {
        var go = Object.Instantiate(template, parent, false);
        go.name = name;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        var label = go.GetComponent<TMP_Text>();
        label.text = text;
        label.richText = true;
        label.enableAutoSizing = false;
        label.fontSize = fontSize;
        label.fontStyle = FontStyles.Normal;
        label.alignment = alignment;
        label.textWrappingMode = TextWrappingModes.Normal;
        return label;
    }

    static Button Row(GameObject template, RectTransform parent, string name, string text, Vector2 position, Vector2 size, bool centred = false)
    {
        var row = Object.Instantiate(template, parent, false);
        row.name = name;
        row.SetActive(true);
        var rect = (RectTransform)row.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        var button = row.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.interactable = true;
        var label = row.GetComponentInChildren<TMP_Text>(true);
        label.text = text;
        label.richText = true;
        if (centred)
            label.alignment = TextAlignmentOptions.Center;
        return button;
    }

    static Slider SliderGroup(GameObject template, RectTransform parent, string name, string label, Vector2 position, float width, out TMP_Text labelText)
    {
        var group = Object.Instantiate(template, parent, false);
        group.name = name;
        var rect = (RectTransform)group.transform;
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
        var slider = group.GetComponentInChildren<Slider>(true);
        slider.onValueChanged = new Slider.SliderEvent();
        slider.interactable = true;
        labelText = group.transform.Find("Label").GetComponent<TMP_Text>();
        labelText.text = label;
        labelText.fontSize = 20f;
        labelText.richText = true;
        return slider;
    }

    #endregion

    #region The sign's menu

    static TunnelSignPanel BuildMenu(SignFontLibrary library)
    {
        var segment = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var control = segment.transform.Find("Control Panel");
            var titleTemplate = control.Find("Container List/Title").gameObject;
            var rowTemplate = control.Find("Container List/Box Row").gameObject;
            var sliderTemplate = control.Find("Color Chooser/Hue Slider").gameObject;
            var moleculeList = control.Find("Molecule List");

            var root = new GameObject("Sign Menu");
            var canvas = Canvas(control, root.transform, "Sign Menu Canvas", k_MenuSize, k_CanvasScale);
            var menuPanel = control.Find("Menu Panel").GetComponent<Image>();
            var background = UiObject("Background", canvas, Vector2.zero, k_MenuSize).gameObject.AddComponent<Image>();
            background.sprite = menuPanel.sprite;
            background.type = menuPanel.type;
            background.color = menuPanel.color;

            const float left = -250f, right = 250f, column = 440f, third = 140f, step = 148f;
            var title = Text(titleTemplate, canvas, "Title", "<b>Sign</b>", new Vector2(left, 370f), new Vector2(column, 56f), 32f, TextAlignmentOptions.Left);
            var close = Row(rowTemplate, canvas, "Close Button", "Close menu", new Vector2(right + 140f, 370f), new Vector2(160f, 52f), true);

            // Left: the text, the font list, the style, alignment and shadow, Save.
            var edit = Row(rowTemplate, canvas, "Edit Text Button", "Edit text", new Vector2(left, 305f), new Vector2(column, 56f), true);
            edit.GetComponentInChildren<TMP_Text>(true).fontStyle = FontStyles.Bold;
            var fontLabel = Text(titleTemplate, canvas, "Font Label", "Font", new Vector2(left, 250f), new Vector2(column, 40f), 24f, TextAlignmentOptions.Left);

            var scroll = Object.Instantiate(moleculeList.Find("Scroll View").gameObject, canvas, false);
            scroll.name = "Font List";
            var scrollRect = (RectTransform)scroll.transform;
            scrollRect.anchorMin = scrollRect.anchorMax = scrollRect.pivot = new Vector2(0.5f, 0.5f);
            scrollRect.anchoredPosition = new Vector2(left, 55f);
            scrollRect.sizeDelta = new Vector2(column, 340f);
            var content = (RectTransform)scroll.transform.Find("Viewport/Content");
            for (int i = content.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(content.GetChild(i).gameObject);
            var fontRow = Object.Instantiate(moleculeList.Find("Row Template").gameObject, canvas, false);
            fontRow.name = "Font Row Template";
            ((RectTransform)fontRow.transform).sizeDelta = new Vector2(-8f, 46f);
            fontRow.GetComponentInChildren<TMP_Text>(true).fontSize = 24f;
            fontRow.SetActive(false);
            var fontRowButton = fontRow.GetComponent<Button>();
            fontRowButton.onClick = new Button.ButtonClickedEvent();

            var regular = Row(rowTemplate, canvas, "Regular Button", "Regular", new Vector2(left - step, -160f), new Vector2(third, 52f), true);
            var bold = Row(rowTemplate, canvas, "Bold Button", "Bold", new Vector2(left, -160f), new Vector2(third, 52f), true);
            var italic = Row(rowTemplate, canvas, "Italic Button", "Italic", new Vector2(left + step, -160f), new Vector2(third, 52f), true);
            var alignLeft = Row(rowTemplate, canvas, "Left Button", "Left", new Vector2(left - step, -222f), new Vector2(third, 52f), true);
            var alignCenter = Row(rowTemplate, canvas, "Centre Button", "Centre", new Vector2(left, -222f), new Vector2(third, 52f), true);
            var alignRight = Row(rowTemplate, canvas, "Right Button", "Right", new Vector2(left + step, -222f), new Vector2(third, 52f), true);
            var shadow = Row(rowTemplate, canvas, "Shadow Row", "Drop shadow", new Vector2(left, -284f), new Vector2(column, 52f));
            var save = Row(rowTemplate, canvas, "Save Button", "Save", new Vector2(left, -350f), new Vector2(column, 56f), true);
            save.GetComponentInChildren<TMP_Text>(true).fontStyle = FontStyles.Bold;

            // Right: the colour chooser (for the letters, border or background), then the border.
            var colourOf = Text(titleTemplate, canvas, "Colour Title", "Colour of", new Vector2(right - 60f, 305f), new Vector2(column - 120f, 40f), 24f, TextAlignmentOptions.Left);
            colourOf.fontStyle = FontStyles.Bold;
            var textTab = Row(rowTemplate, canvas, "Text Tab", "Text", new Vector2(right - step, 250f), new Vector2(third, 50f), true);
            var borderTab = Row(rowTemplate, canvas, "Border Tab", "Border", new Vector2(right, 250f), new Vector2(third, 50f), true);
            var backgroundTab = Row(rowTemplate, canvas, "Background Tab", "Background", new Vector2(right + step, 250f), new Vector2(third, 50f), true);
            foreach (var tab in new[] { textTab, borderTab, backgroundTab })
            {
                var tabLabel = tab.GetComponentInChildren<TMP_Text>(true);
                tabLabel.enableAutoSizing = true;
                tabLabel.fontSizeMin = 12f;
                tabLabel.fontSizeMax = 18f;
                tabLabel.textWrappingMode = TextWrappingModes.NoWrap;
            }
            var swatch = UiObject("Swatch", canvas, new Vector2(right + 160f, 305f), new Vector2(110f, 36f)).gameObject.AddComponent<Image>();
            swatch.sprite = menuPanel.sprite;
            swatch.type = menuPanel.type;
            swatch.color = Color.white;
            var hue = SliderGroup(sliderTemplate, canvas, "Hue Slider", "Hue", new Vector2(right, 190f), column, out _);
            var saturation = SliderGroup(sliderTemplate, canvas, "Saturation Slider", "Saturation", new Vector2(right, 128f), column, out _);
            var value = SliderGroup(sliderTemplate, canvas, "Value Slider", "Brightness", new Vector2(right, 66f), column, out _);
            var opacity = SliderGroup(sliderTemplate, canvas, "Opacity Slider", "Opacity", new Vector2(right, 4f), column, out _);
            // The hue slider's rainbow is the TS Menu's own; the others take theirs too.
            CopySliderLook(control.Find("Color Chooser/Saturation Slider"), saturation);
            CopySliderLook(control.Find("Color Chooser/Value Slider"), value);
            CopySliderLook(control.Find("Color Chooser/Opacity Slider"), opacity);

            var borderTitle = Text(titleTemplate, canvas, "Border Title", "Border", new Vector2(right, -62f), new Vector2(column, 40f), 24f, TextAlignmentOptions.Left);
            borderTitle.fontStyle = FontStyles.Bold;
            var borderRow = Row(rowTemplate, canvas, "Border Row", "Border", new Vector2(right, -115f), new Vector2(column, 52f));
            var borderWidth = SliderGroup(sliderTemplate, canvas, "Border Width Slider", "Width", new Vector2(right, -177f), column, out var borderWidthText);
            var padding = SliderGroup(sliderTemplate, canvas, "Padding Slider", "Space", new Vector2(right, -239f), column, out var paddingText);
            CopySliderLook(control.Find("Color Chooser/Value Slider"), borderWidth);
            CopySliderLook(control.Find("Color Chooser/Value Slider"), padding);
            var status = Text(titleTemplate, canvas, "Status", "", new Vector2(right, -340f), new Vector2(column, 120f), 19f, TextAlignmentOptions.TopLeft);

            // The keyboard: the browser's, under the menu, tilted toward whoever types.
            var keyboardRoot = new GameObject("Keyboard").transform;
            keyboardRoot.SetParent(root.transform, false);
            keyboardRoot.SetLocalPositionAndRotation(new Vector3(0f, -(k_MenuSize.y * k_CanvasScale * 0.5f + 0.2f), -0.12f), Quaternion.Euler(40f, 0f, 0f));
            var keyboardObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(k_KeyboardPath), keyboardRoot);
            keyboardObject.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 180f, 0f));
            keyboardObject.transform.localScale = Vector3.one * 0.07f;
            var keyboard = keyboardObject.GetComponent<XRKeyboard>();
            keyboard.submitOnEnter = false;
            keyboardObject.SetActive(false);

            var menu = root.AddComponent<TunnelSignPanel>();
            var so = new SerializedObject(menu);
            void Set(string field, Object v) => so.FindProperty(field).objectReferenceValue = v;
            Set("m_Canvas", canvas);
            Set("m_CloseButton", close);
            Set("m_EditTextButton", edit);
            Set("m_FontLabel", fontLabel);
            Set("m_FontContent", content);
            Set("m_FontRowTemplate", fontRowButton);
            Set("m_RegularButton", regular);
            Set("m_BoldButton", bold);
            Set("m_ItalicButton", italic);
            Set("m_LeftButton", alignLeft);
            Set("m_CenterButton", alignCenter);
            Set("m_RightButton", alignRight);
            Set("m_ShadowRow", shadow);
            Set("m_SaveButton", save);
            Set("m_TextTab", textTab);
            Set("m_BorderTab", borderTab);
            Set("m_BackgroundTab", backgroundTab);
            Set("m_Swatch", swatch);
            Set("m_Hue", hue);
            Set("m_Saturation", saturation);
            Set("m_Value", value);
            Set("m_Opacity", opacity);
            Set("m_BorderRow", borderRow);
            Set("m_BorderWidth", borderWidth);
            Set("m_BorderWidthText", borderWidthText);
            Set("m_Padding", padding);
            Set("m_PaddingText", paddingText);
            Set("m_Status", status);
            Set("m_Keyboard", keyboard);
            so.FindProperty("m_RowSpacing").floatValue = 50f;
            so.ApplyModifiedPropertiesWithoutUndo();
            title.name = "Title";

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, k_MenuPath);
            Object.DestroyImmediate(root);
            return prefab.GetComponent<TunnelSignPanel>();
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(segment);
        }
    }

    // A slider group's track and fill look, from the TS Menu's colour chooser.
    static void CopySliderLook(Transform sourceGroup, Slider target)
    {
        var source = sourceGroup != null ? sourceGroup.GetComponentInChildren<Slider>(true) : null;
        if (source == null || target == null)
            return;
        var from = source.GetComponentsInChildren<Image>(true);
        var to = target.GetComponentsInChildren<Image>(true);
        for (int i = 0; i < from.Length && i < to.Length; i++)
        {
            to[i].sprite = from[i].sprite;
            to[i].color = from[i].color;
            to[i].type = from[i].type;
        }
    }

    #endregion

    #region The sign

    static TunnelSign BuildSign(SignFontLibrary library, Material panelMaterial, TunnelSignPanel menu)
    {
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(k_TemplatePath);
        var segment = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        // Made in a preview scene, so the open scene is left as it was.
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try
        {
            var control = segment.transform.Find("Control Panel");
            var sign = (GameObject)PrefabUtility.InstantiatePrefab(template, scene);
            PrefabUtility.UnpackPrefabInstance(sign, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            sign.name = "Sign_Networked";

            // Only the toy's workings: no molecule, its colour feedback or its collider.
            for (int i = sign.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(sign.transform.GetChild(i).gameObject);
            Object.DestroyImmediate(sign.GetComponent<MoleculeGravity>());
            if (sign.TryGetComponent(out CapsidSites sites))
                Object.DestroyImmediate(sites);
            if (sign.TryGetComponent(out AntibodyBinder binder))
                Object.DestroyImmediate(binder);
            sign.transform.localScale = Vector3.one;

            // The letters.
            var textObject = new GameObject("Text");
            textObject.transform.SetParent(sign.transform, false);
            var text = textObject.AddComponent<TextMeshPro>();
            var first = library.Get(0);
            text.font = first.regular.font;
            text.fontSharedMaterial = first.regular.font.material;
            text.fontSize = 0.5f;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.richText = false;
            text.text = "New sign";
            text.rectTransform.sizeDelta = new Vector2(0.4f, 0.1f);
            var textRenderer = textObject.GetComponent<MeshRenderer>();
            textRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            textRenderer.receiveShadows = false;

            // The background and border.
            var panel = new GameObject("Panel", typeof(MeshFilter), typeof(MeshRenderer));
            panel.transform.SetParent(sign.transform, false);
            var panelRenderer = panel.GetComponent<MeshRenderer>();
            panelRenderer.sharedMaterial = panelMaterial;
            panelRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            panelRenderer.receiveShadows = false;
            panelRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            panelRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            // What hands grab.
            var grabBox = new GameObject("Grab Box").AddComponent<BoxCollider>();
            grabBox.transform.SetParent(sign.transform, false);
            grabBox.size = new Vector3(0.5f, 0.15f, 0.012f);

            // The Edit button, at the lower right corner.
            var editCanvas = Canvas(control, sign.transform, "Edit Button Canvas", new Vector2(100f, 100f), 0.0005f);
            var editRow = Row(control.Find("Container List/Box Row").gameObject, editCanvas, "Edit Button", "Edit", Vector2.zero, new Vector2(100f, 100f), true);
            var editLabel = editRow.GetComponentInChildren<TMP_Text>(true);
            editLabel.fontSize = 30f;
            editLabel.fontStyle = FontStyles.Bold;

            // It stays where it's left and isn't pushed; one hand moves and turns it, two scale it evenly.
            var body = sign.GetComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            var grab = sign.GetComponent<XRGrabInteractable>();
            grab.throwOnDetach = false;
            var grabSo = new SerializedObject(grab);
            grabSo.FindProperty("m_Colliders").ClearArray(); // its own grab box, collected when it wakes
            grabSo.ApplyModifiedPropertiesWithoutUndo();
            if (sign.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable networkPhysics))
                networkPhysics.spawnLocked = false;
            var general = sign.GetComponent<XRGeneralGrabTransformer>();
            var generalSo = new SerializedObject(general);
            generalSo.FindProperty("m_AllowTwoHandedScaling").boolValue = true;
            generalSo.FindProperty("m_MinimumScaleRatio").floatValue = 0.2f;
            generalSo.FindProperty("m_MaximumScaleRatio").floatValue = 10f;
            generalSo.ApplyModifiedPropertiesWithoutUndo();

            var component = sign.AddComponent<TunnelSign>();
            component.SetParts(library, text, panel.GetComponent<MeshFilter>(), grabBox, editRow, menu);

            var prefab = PrefabUtility.SaveAsPrefabAsset(sign, k_PrefabPath);
            Object.DestroyImmediate(sign);
            return prefab.GetComponent<TunnelSign>();
        }
        finally
        {
            UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            PrefabUtility.UnloadPrefabContents(segment);
        }
    }

    #endregion

    #region The TS Menu

    /// <summary>
    /// The Signage button under Containers (the panel's buttons re-spaced to fit), and the Signage list beside the
    /// menu: the Molecules list's panel, title, status and scroll view, with New sign, the saved signs, Place and
    /// Delete, and the Edit buttons on signs check box.
    /// </summary>
    static string BuildTsMenu(TunnelSign sign)
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var control = root.transform.Find("Control Panel");
            var menuPanel = control.Find("Menu Panel");
            var signageButton = menuPanel.Find("Signage Button");
            if (signageButton == null)
            {
                var containers = menuPanel.Find("Containers Button");
                signageButton = Object.Instantiate(containers.gameObject, menuPanel, false).transform;
                signageButton.name = "Signage Button";
            }
            var button = signageButton.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            var label = signageButton.Find("Button Front/Text (TMP)").GetComponent<TMP_Text>();
            label.text = "Signage";
            TunnelMenuLayout.Arrange(menuPanel);

            var old = control.Find("Signage List");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
            var moleculeList = control.Find("Molecule List");
            var list = Object.Instantiate(moleculeList.gameObject, control, false);
            list.name = "Signage List";
            foreach (var unused in new[] { "Magnification", NozzleBuilder.k_RowName })
            {
                var child = list.transform.Find(unused);
                if (child != null)
                    Object.DestroyImmediate(child.gameObject);
            }
            // The Molecules list's size and place (540 x 640, its top just under the TS Menu button).
            var listRect = (RectTransform)list.transform;
            var moleculeRect = (RectTransform)moleculeList;
            listRect.sizeDelta = new Vector2(540f, 640f);
            float top = moleculeRect.anchoredPosition.y + moleculeRect.sizeDelta.y * 0.5f;
            listRect.anchoredPosition = new Vector2(moleculeRect.anchoredPosition.x, top - 320f);

            var title = (RectTransform)list.transform.Find("Title");
            title.anchoredPosition = new Vector2(-40f, 275f);
            title.GetComponent<TMP_Text>().text = "Signage";
            var status = (RectTransform)list.transform.Find("Status");
            status.anchoredPosition = new Vector2(0f, 222f);
            status.sizeDelta = new Vector2(500f, 64f);
            var statusText = status.GetComponent<TMP_Text>();
            statusText.fontSize = 18f;
            statusText.textWrappingMode = TextWrappingModes.Normal;

            var rowTemplate = control.Find("Container List/Box Row").gameObject;
            var listPanel = (RectTransform)list.transform;
            var newSign = Row(rowTemplate, listPanel, "New Sign Button", "New sign", new Vector2(0f, 155f), new Vector2(460f, 56f), true);
            newSign.GetComponentInChildren<TMP_Text>(true).fontStyle = FontStyles.Bold;
            var savedTitle = Text(control.Find("Container List/Title").gameObject, listPanel, "Saved Title", "Saved signs", new Vector2(0f, 105f), new Vector2(460f, 36f), 22f, TextAlignmentOptions.Left);
            savedTitle.fontStyle = FontStyles.Bold;
            var scroll = (RectTransform)list.transform.Find("Scroll View");
            scroll.anchoredPosition = new Vector2(0f, -35f);
            scroll.sizeDelta = new Vector2(500f, 230f);
            var content = (RectTransform)scroll.Find("Viewport/Content");
            for (int i = content.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(content.GetChild(i).gameObject);
            var place = Row(rowTemplate, listPanel, "Place Button", "Place", new Vector2(-118f, -190f), new Vector2(224f, 56f), true);
            var delete = Row(rowTemplate, listPanel, "Delete Button", "Delete", new Vector2(118f, -190f), new Vector2(224f, 56f), true);
            var editButtons = Row(rowTemplate, listPanel, "Edit Buttons Row", "■  Edit buttons on signs", new Vector2(0f, -258f), new Vector2(460f, 56f));
            var rowTemplateInList = list.transform.Find("Row Template");
            list.SetActive(false);

            if (!root.TryGetComponent(out TunnelSignageMenu signage))
                signage = root.AddComponent<TunnelSignageMenu>();
            var moleculeMenu = root.GetComponent<TunnelMoleculeMenu>();
            var so = new SerializedObject(signage);
            so.FindProperty("m_Geometry").objectReferenceValue = new SerializedObject(moleculeMenu).FindProperty("m_Geometry").objectReferenceValue;
            so.FindProperty("m_ListPanel").objectReferenceValue = list;
            so.FindProperty("m_SignPrefab").objectReferenceValue = sign;
            so.FindProperty("m_NewButton").objectReferenceValue = newSign;
            so.FindProperty("m_ListContent").objectReferenceValue = content;
            so.FindProperty("m_RowTemplate").objectReferenceValue = rowTemplateInList.GetComponent<Button>();
            so.FindProperty("m_PlaceButton").objectReferenceValue = place;
            so.FindProperty("m_DeleteButton").objectReferenceValue = delete;
            so.FindProperty("m_EditButtonsRow").objectReferenceValue = editButtons;
            so.FindProperty("m_StatusText").objectReferenceValue = statusText;
            so.ApplyModifiedPropertiesWithoutUndo();

            var tunnelMenu = control.GetComponent<TunnelMenu>();
            var menuSo = new SerializedObject(tunnelMenu);
            menuSo.FindProperty("m_SignageButton").objectReferenceValue = button;
            menuSo.FindProperty("m_SignageLabel").objectReferenceValue = label;
            menuSo.FindProperty("m_Signage").objectReferenceValue = signage;
            menuSo.ApplyModifiedPropertiesWithoutUndo();

            bool saved = PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath) != null;
            return saved ? "TS Menu: Signage button and list added" : "TS Menu: FAILED to save";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    #endregion
}
