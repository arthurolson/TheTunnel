using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;

/// <summary>
/// A sign's own menu (see <see cref="TunnelSign"/>), beside it on its right: Edit text (opens the keyboard under the
/// menu; Enter starts a new line, and every key shows on the sign for everyone), the list of fonts (each name written
/// in its own font), Regular / Bold / Italic, Left / Centre / Right, Drop shadow, a colour chooser (hue, saturation,
/// brightness, opacity) for the letters, the border or the background, and the Border section: Border on or off, its
/// width and the space round the letters. Save keeps the sign in the TS Menu's Signage list. Each headset has its own
/// copy, opened with the sign's Edit button. Made by Tunnel > Signage > Build Signage.
/// </summary>
public class TunnelSignPanel : MonoBehaviour
{
    [SerializeField] RectTransform m_Canvas;
    [SerializeField] Button m_CloseButton;
    [SerializeField] Button m_EditTextButton;
    [SerializeField] TMP_Text m_FontLabel;
    [SerializeField] RectTransform m_FontContent;
    [SerializeField] Button m_FontRowTemplate;
    [SerializeField] Button m_RegularButton;
    [SerializeField] Button m_BoldButton;
    [SerializeField] Button m_ItalicButton;
    [SerializeField] Button m_LeftButton;
    [SerializeField] Button m_CenterButton;
    [SerializeField] Button m_RightButton;
    [SerializeField] Button m_ShadowRow;
    [SerializeField] Button m_SaveButton;
    [SerializeField] Button m_TextTab;
    [SerializeField] Button m_BorderTab;
    [SerializeField] Button m_BackgroundTab;
    [SerializeField] Image m_Swatch;
    [SerializeField] Slider m_Hue;
    [SerializeField] Slider m_Saturation;
    [SerializeField] Slider m_Value;
    [SerializeField] Slider m_Opacity;
    [SerializeField] Button m_BorderRow;
    [SerializeField] Slider m_BorderWidth;
    [SerializeField] TMP_Text m_BorderWidthText;
    [SerializeField] Slider m_Padding;
    [SerializeField] TMP_Text m_PaddingText;
    [SerializeField] TMP_Text m_Status;
    [SerializeField] XRKeyboard m_Keyboard;
    [SerializeField] float m_RowSpacing = 50f;

    static readonly Color k_RowColor = new(1f, 1f, 1f, 0.08f);
    static readonly Color k_ChosenRowColor = new(0.3f, 0.6f, 1f, 0.55f);

    TunnelSign m_Sign;
    readonly List<Button> m_FontRows = new();
    SignStyle.Part m_Part = SignStyle.Part.Text;
    bool m_Editing;
    float m_NextRefresh;

    // What the sliders show, so they follow another player's change but don't jump while being dragged here.
    SignStyle.Part m_SlidersFor = (SignStyle.Part)255;
    Color32 m_SlidersShow;
    float m_BorderWidthShown = -1f, m_PaddingShown = -1f;

    /// <summary>Its width (meters).</summary>
    public float width => m_Canvas != null ? m_Canvas.sizeDelta.x * m_Canvas.lossyScale.x : 0.4f;

    public bool keyboardOpen => m_Keyboard != null && m_Keyboard.isOpen;

    public XRKeyboard keyboard => m_Keyboard;

    /// <summary>Which part the colour chooser colours (the letters, the border or the background).</summary>
    public SignStyle.Part colorPart => m_Part;

    public IReadOnlyList<Button> fontRows => m_FontRows;

    public void Bind(TunnelSign sign)
    {
        if (m_Sign != null)
            m_Sign.changed -= OnSignChanged;
        m_Sign = sign;
        if (m_Sign != null)
            m_Sign.changed += OnSignChanged;
        BuildFontList();
        Refresh(true);
    }

    void Awake()
    {
        if (m_FontRowTemplate != null)
            m_FontRowTemplate.gameObject.SetActive(false);
        Listen(m_CloseButton, () => m_Sign?.CloseMenu());
        Listen(m_EditTextButton, ToggleKeyboard);
        Listen(m_RegularButton, () => m_Sign?.SetRegular());
        Listen(m_BoldButton, () => m_Sign?.SetBold(!m_Sign.style.bold));
        Listen(m_ItalicButton, () => m_Sign?.SetItalic(!m_Sign.style.italic));
        Listen(m_LeftButton, () => m_Sign?.SetAlign(SignStyle.Align.Left));
        Listen(m_CenterButton, () => m_Sign?.SetAlign(SignStyle.Align.Center));
        Listen(m_RightButton, () => m_Sign?.SetAlign(SignStyle.Align.Right));
        Listen(m_ShadowRow, () => m_Sign?.SetShadow(!m_Sign.style.shadow));
        Listen(m_BorderRow, () => m_Sign?.SetBorder(!m_Sign.style.border));
        Listen(m_SaveButton, () => m_Sign?.Save());
        Listen(m_TextTab, () => ChoosePart(SignStyle.Part.Text));
        Listen(m_BorderTab, () => ChoosePart(SignStyle.Part.Border));
        Listen(m_BackgroundTab, () => ChoosePart(SignStyle.Part.Background));

        foreach (var slider in new[] { m_Hue, m_Saturation, m_Value, m_Opacity })
            if (slider != null)
            {
                slider.minValue = 0f;
                slider.maxValue = 1f;
                slider.wholeNumbers = false;
                slider.onValueChanged.AddListener(_ => OnColorMoved());
            }
        if (m_BorderWidth != null)
        {
            m_BorderWidth.minValue = SignStyle.k_MinBorderWidth * 1000f;
            m_BorderWidth.maxValue = SignStyle.k_MaxBorderWidth * 1000f;
            m_BorderWidth.wholeNumbers = true; // millimetres
            m_BorderWidth.onValueChanged.AddListener(OnBorderWidthMoved);
        }
        if (m_Padding != null)
        {
            m_Padding.minValue = 0f;
            m_Padding.maxValue = SignStyle.k_MaxPadding * 1000f;
            m_Padding.wholeNumbers = true; // millimetres
            m_Padding.onValueChanged.AddListener(OnPaddingMoved);
        }
    }

    static void Listen(Button button, UnityEngine.Events.UnityAction action)
    {
        if (button != null)
            button.onClick.AddListener(action);
    }

    void OnEnable()
    {
        if (m_Keyboard != null)
        {
            m_Keyboard.onTextUpdated.AddListener(OnKeyboardText);
            m_Keyboard.onClosed.AddListener(OnKeyboardClosed);
        }
    }

    void OnDisable()
    {
        if (m_Keyboard != null)
        {
            m_Keyboard.onTextUpdated.RemoveListener(OnKeyboardText);
            m_Keyboard.onClosed.RemoveListener(OnKeyboardClosed);
        }
        CloseKeyboard();
    }

    void OnDestroy()
    {
        if (m_Sign != null)
            m_Sign.changed -= OnSignChanged;
    }

    void OnSignChanged() => Refresh();

    void Update()
    {
        // A few times a second is plenty for a menu that follows the sign's state.
        if (Time.unscaledTime < m_NextRefresh)
            return;
        m_NextRefresh = Time.unscaledTime + 0.25f;
        Refresh();
    }

    #region Fonts

    void BuildFontList()
    {
        if (m_FontContent == null || m_FontRowTemplate == null)
            return;
        foreach (var row in m_FontRows)
            if (row != null)
                Destroy(row.gameObject);
        m_FontRows.Clear();
        var library = m_Sign != null ? m_Sign.fonts : null;
        int n = library != null ? library.count : 0;
        for (int i = 0; i < n; i++)
        {
            int index = i;
            var entry = library.Get(i);
            var row = Instantiate(m_FontRowTemplate, m_FontContent);
            row.name = $"Font {entry.name}";
            row.gameObject.SetActive(true);
            ((RectTransform)row.transform).anchoredPosition = new Vector2(0f, -i * m_RowSpacing);
            var label = row.GetComponentInChildren<TMP_Text>(true);
            label.text = entry.name;
            label.richText = false;
            if (entry.regular != null && entry.regular.font != null)
            {
                // Each name in its own font.
                label.font = entry.regular.font;
                label.fontSharedMaterial = entry.regular.font.material;
            }
            row.onClick.AddListener(() => m_Sign?.SetFont(index));
            m_FontRows.Add(row);
        }
        m_FontContent.sizeDelta = new Vector2(m_FontContent.sizeDelta.x, Mathf.Max(1, n) * m_RowSpacing);
    }

    #endregion

    #region Colour

    void ChoosePart(SignStyle.Part part)
    {
        m_Part = part;
        m_SlidersFor = (SignStyle.Part)255;
        Refresh();
    }

    /// <summary>The colour chooser's sliders for this part (letters, border or background).</summary>
    public void ShowColorOf(SignStyle.Part part) => ChoosePart(part);

    void OnColorMoved()
    {
        if (m_Sign == null || m_Hue == null)
            return;
        var color = Color.HSVToRGB(m_Hue.value, m_Saturation.value, m_Value.value);
        color.a = m_Opacity.value;
        m_SlidersShow = color;
        m_Sign.SetColor(m_Part, color);
        ShowSwatch(color);
    }

    void ShowSwatch(Color color)
    {
        if (m_Swatch != null)
            m_Swatch.color = color;
    }

    static bool Close(Color32 a, Color32 b) =>
        Mathf.Abs(a.r - b.r) <= 1 && Mathf.Abs(a.g - b.g) <= 1 && Mathf.Abs(a.b - b.b) <= 1 && Mathf.Abs(a.a - b.a) <= 1;

    // The sliders follow the part's colour when it changed elsewhere (a hue kept for greys, where it doesn't matter).
    void ShowColorSliders(SignStyle style)
    {
        if (m_Hue == null)
            return;
        Color32 shared = style.ColorOf(m_Part);
        if (m_SlidersFor == m_Part && Close(shared, m_SlidersShow))
            return;
        m_SlidersFor = m_Part;
        m_SlidersShow = shared;
        Color color = shared;
        Color.RGBToHSV(color, out float h, out float s, out float v);
        if (s > 0.001f && v > 0.001f)
            m_Hue.SetValueWithoutNotify(h);
        if (v > 0.001f)
            m_Saturation.SetValueWithoutNotify(s);
        m_Value.SetValueWithoutNotify(v);
        m_Opacity.SetValueWithoutNotify(color.a);
        ShowSwatch(color);
    }

    #endregion

    #region Border

    void OnBorderWidthMoved(float millimetres)
    {
        m_BorderWidthShown = millimetres / 1000f;
        m_Sign?.SetBorderWidth(m_BorderWidthShown);
        ShowSizes();
    }

    void OnPaddingMoved(float millimetres)
    {
        m_PaddingShown = millimetres / 1000f;
        m_Sign?.SetPadding(m_PaddingShown);
        ShowSizes();
    }

    void ShowSizes()
    {
        if (m_BorderWidth != null)
            SetText(m_BorderWidthText, $"Width {m_BorderWidth.value / 10f:0.0} cm");
        if (m_Padding != null)
            SetText(m_PaddingText, $"Space {m_Padding.value / 10f:0.0} cm  <size=70%><color=#AAA>round text</color></size>");
    }

    #endregion

    #region Keyboard

    void ToggleKeyboard()
    {
        if (keyboardOpen)
            CloseKeyboard();
        else
            OpenKeyboard();
    }

    /// <summary>Opens the keyboard under the menu with the sign's text in it; what's typed shows on the sign for everyone.</summary>
    public void OpenKeyboard()
    {
        if (m_Keyboard == null || m_Sign == null)
            return;
        m_Keyboard.submitOnEnter = false; // Enter starts a new line
        m_Editing = true;
        m_Keyboard.Open(m_Sign.text);
        Debug.Log("[Sign] keyboard opened to edit the text");
        Refresh();
    }

    public void CloseKeyboard()
    {
        m_Editing = false;
        if (m_Keyboard != null && m_Keyboard.isOpen)
            m_Keyboard.Close(false);
    }

    void OnKeyboardText(KeyboardTextEventArgs args)
    {
        if (!m_Editing || m_Sign == null)
            return;
        m_Sign.SetText(args.keyboardText ?? "");
    }

    void OnKeyboardClosed(KeyboardTextEventArgs args)
    {
        m_Editing = false;
        Refresh();
    }

    #endregion

    /// <summary>
    /// Shows the sign's state: the chosen font, the style and alignment check marks, the colour sliders, the border
    /// sliders and the latest message. With <paramref name="moveSliders"/> the size sliders move to the sign's values
    /// even if they were being dragged.
    /// </summary>
    public void Refresh(bool moveSliders = false)
    {
        if (m_Sign == null)
            return;
        var s = m_Sign.style;
        var library = m_Sign.fonts;
        var entry = library != null ? library.Get(s.font) : null;
        SetText(m_FontLabel, entry != null ? $"Font: {entry.name}" : "Font");
        for (int i = 0; i < m_FontRows.Count; i++)
        {
            var image = m_FontRows[i] != null ? m_FontRows[i].targetGraphic as Image : null;
            if (image != null)
            {
                var want = i == s.font ? k_ChosenRowColor : k_RowColor;
                if (image.color != want)
                    image.color = want;
            }
        }

        SetLabel(m_RegularButton, Check(!s.bold && !s.italic) + "Regular");
        SetLabel(m_BoldButton, Check(s.bold) + "<b>Bold</b>");
        SetLabel(m_ItalicButton, Check(s.italic) + "<i>Italic</i>");
        SetLabel(m_LeftButton, Check(s.align == SignStyle.Align.Left) + "Left");
        SetLabel(m_CenterButton, Check(s.align == SignStyle.Align.Center) + "Centre");
        SetLabel(m_RightButton, Check(s.align == SignStyle.Align.Right) + "Right");
        SetLabel(m_ShadowRow, Check(s.shadow) + "Drop shadow  <color=#AAA>under the letters</color>");
        SetLabel(m_BorderRow, Check(s.border) + "Border");
        SetLabel(m_TextTab, Check(m_Part == SignStyle.Part.Text) + "Text");
        SetLabel(m_BorderTab, Check(m_Part == SignStyle.Part.Border) + "Border");
        SetLabel(m_BackgroundTab, Check(m_Part == SignStyle.Part.Background) + "Background");
        SetLabel(m_EditTextButton, keyboardOpen ? "Done editing" : "Edit text");
        if (m_SaveButton != null)
            SetLabel(m_SaveButton, string.IsNullOrEmpty(m_Sign.saveName) ? "Save" : "Save  <color=#AAA>(replaces its stored copy)</color>");

        ShowColorSliders(s);
        if (m_BorderWidth != null)
        {
            m_BorderWidth.interactable = s.border;
            if (moveSliders || !Mathf.Approximately(s.borderWidth, m_BorderWidthShown))
            {
                m_BorderWidthShown = s.borderWidth;
                m_BorderWidth.SetValueWithoutNotify(Mathf.Round(s.borderWidth * 1000f));
            }
        }
        if (m_Padding != null && (moveSliders || !Mathf.Approximately(s.padding, m_PaddingShown)))
        {
            m_PaddingShown = s.padding;
            m_Padding.SetValueWithoutNotify(Mathf.Round(s.padding * 1000f));
        }
        ShowSizes();

        string where = m_Sign.IsOnWall(out _) ? "On the wall: take it in hand to pull it off." : "Let go of it against a tunnel wall to put it on the wall.";
        string message = m_Sign.message;
        SetText(m_Status, string.IsNullOrEmpty(message) ? $"<color=#AAA>{where}</color>" : $"{message}\n<color=#AAA>{where}</color>");
    }

    static string Check(bool on) => on ? "■  " : "□  ";

    static void SetLabel(Button button, string text)
    {
        if (button == null)
            return;
        var label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            SetText(label, text);
    }

    static void SetText(TMP_Text label, string text)
    {
        if (label != null && label.text != text)
            label.text = text;
    }
}
