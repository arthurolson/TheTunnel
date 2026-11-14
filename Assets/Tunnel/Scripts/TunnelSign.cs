using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// A sign: a panel of text, made from the TS Menu's Signage list (see <see cref="TunnelSignageMenu"/>), that can be
/// picked up, scaled with two hands, and let go of against a tunnel wall to lie flat on it (taking it in hand pulls it
/// off again). Its text can run over several lines; its font, regular / bold / italic, drop shadow, colours, border
/// and background are set on its own menu (see <see cref="TunnelSignPanel"/>), opened by the small Edit button at its
/// lower right corner. Save keeps its text and style on the host (see <see cref="SignStore"/>) to call up again later.
/// In a session everyone sees the same sign: text, style, size and place. Works locally when not connected.
/// </summary>
public class TunnelSign : NetworkBehaviour
{
    public const int k_MaxCharacters = 1000;
    const int k_MaxBytes = 4000; // what one shared text holds
    const float k_FontSize = 0.5f; // letters about 5 cm high before the sign is scaled
    const float k_BackZ = 0.002f; // the panel sits just behind the letters
    const float k_Depth = 0.012f; // the grab box's thickness

    [SerializeField]
    SignFontLibrary m_Fonts;

    [SerializeField]
    TextMeshPro m_Text;

    [SerializeField]
    [Tooltip("The background and border, drawn as one mesh (made here as the text changes).")]
    MeshFilter m_Panel;

    [SerializeField]
    [Tooltip("What hands grab: fitted round the panel.")]
    BoxCollider m_Collider;

    [SerializeField]
    [Tooltip("The small Edit button at the lower right corner, which opens and closes the sign's menu.")]
    Button m_EditButton;

    [SerializeField]
    TunnelSignPanel m_MenuPrefab;

    [SerializeField]
    [Tooltip("Let go of with its middle this close to a flat tunnel wall (meters), it lies flat on the wall.")]
    float m_SnapDistance = 0.25f;

    [SerializeField]
    [Tooltip("Gap between the back of a sign on a wall and the wall (meters).")]
    float m_WallGap = 0.002f;

    [SerializeField]
    [Tooltip("The Edit button's size in the world, however large the sign (meters).")]
    float m_EditButtonSize = 0.05f;

    [SerializeField]
    [Tooltip("Gap between the sign and its menu (meters).")]
    float m_MenuGap = 0.04f;

    /// <summary>Every sign in the tunnel.</summary>
    public static readonly List<TunnelSign> all = new();

    readonly NetworkVariable<FixedString4096Bytes> m_SharedText = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<SignStyle> m_SharedStyle = new(SignStyle.Default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<FixedString128Bytes> m_SharedSaveName = new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<ulong> m_SharedMenuFor = new(ulong.MaxValue, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    string m_LocalText = "New sign";
    SignStyle m_LocalStyle = SignStyle.Default;
    string m_LocalSaveName = "";
    ulong m_LocalMenuFor = ulong.MaxValue;

    // This player's latest change, shown at once until the host's copy comes back with it.
    string m_PendingText;
    SignStyle m_PendingStyle;
    float m_PendingTextUntil, m_PendingStyleUntil;

    XRGrabInteractable m_Grab;
    Rigidbody m_Body;
    Mesh m_Mesh;
    TunnelSignPanel m_Menu;
    Vector2 m_Inner, m_Outer; // half sizes of the background and of the border's outside (meters, before scaling)
    bool m_EditButtonShown = true;

    /// <summary>Fires on this headset when the sign's text or style changes (here or from another player).</summary>
    public event Action changed;

    public SignFontLibrary fonts => m_Fonts;

    /// <summary>What it says (lines split by '\n').</summary>
    public string text
    {
        get
        {
            if (!IsSpawned)
                return m_LocalText;
            return Time.unscaledTime < m_PendingTextUntil ? m_PendingText : m_SharedText.Value.ToString();
        }
    }

    public SignStyle style
    {
        get
        {
            if (!IsSpawned)
                return m_LocalStyle;
            return Time.unscaledTime < m_PendingStyleUntil ? m_PendingStyle : m_SharedStyle.Value;
        }
    }

    /// <summary>The name it's stored under in the Signage list ("" until it's first saved).</summary>
    public string saveName => IsSpawned ? m_SharedSaveName.Value.ToString() : m_LocalSaveName;

    /// <summary>Its latest message, for its menu.</summary>
    public string message { get; private set; } = "";

    public bool menuOpen => m_Menu != null && m_Menu.gameObject.activeSelf;

    public TunnelSignPanel menu => m_Menu;

    /// <summary>Half its width and height in the world, border included (meters).</summary>
    public Vector2 worldHalfSize => m_Outer * transform.lossyScale.x;

    /// <summary>Half its width and height before scaling, border included (meters).</summary>
    public Vector2 halfSize => m_Outer;

    /// <summary>Half the background's width and height before scaling (meters).</summary>
    public Vector2 innerHalfSize => m_Inner;

    public TextMeshPro textMesh => m_Text;

    public Mesh panelMesh => m_Mesh;

    void Awake()
    {
        TryGetComponent(out m_Grab);
        TryGetComponent(out m_Body);
        if (m_Grab != null)
        {
            m_Grab.selectEntered.AddListener(OnGrabbed);
            m_Grab.selectExited.AddListener(OnReleased);
        }
        if (m_EditButton != null)
            m_EditButton.onClick.AddListener(ToggleMenu);
        m_Mesh = new Mesh { name = "Sign Panel" };
        m_Mesh.MarkDynamic();
        if (m_Panel != null)
            m_Panel.sharedMesh = m_Mesh;
    }

    void OnEnable()
    {
        if (!all.Contains(this))
            all.Add(this);
        Apply();
        if (m_Menu != null && m_MenuWasOpen)
            m_Menu.gameObject.SetActive(true);
    }

    bool m_MenuWasOpen;

    void OnDisable()
    {
        all.Remove(this);
        m_MenuWasOpen = menuOpen;
        if (m_Menu != null)
            m_Menu.gameObject.SetActive(false);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer)
        {
            // What was set up before it appeared (see Configure) becomes everyone's.
            m_SharedText.Value = ToShared(m_LocalText);
            m_SharedStyle.Value = m_LocalStyle;
            m_SharedSaveName.Value = new FixedString128Bytes(Fit(m_LocalSaveName, 120));
            m_SharedMenuFor.Value = m_LocalMenuFor;
        }
        m_SharedText.OnValueChanged += OnTextChanged;
        m_SharedStyle.OnValueChanged += OnStyleChanged;
        m_SharedSaveName.OnValueChanged += OnSaveNameChanged;
        m_SharedMenuFor.OnValueChanged += OnMenuForChanged;
        Apply();
        OpenMenuIfMine(m_SharedMenuFor.Value);
        Debug.Log($"[Sign] \"{FirstLine(text)}\" appeared for everyone at {transform.position:F2}");
    }

    public override void OnNetworkDespawn()
    {
        m_SharedText.OnValueChanged -= OnTextChanged;
        m_SharedStyle.OnValueChanged -= OnStyleChanged;
        m_SharedSaveName.OnValueChanged -= OnSaveNameChanged;
        m_SharedMenuFor.OnValueChanged -= OnMenuForChanged;
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        if (m_Grab != null)
        {
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
            m_Grab.selectExited.RemoveListener(OnReleased);
        }
        if (m_EditButton != null)
            m_EditButton.onClick.RemoveListener(ToggleMenu);
        if (m_Menu != null)
            Destroy(m_Menu.gameObject);
        if (m_Mesh != null)
            Destroy(m_Mesh);
        base.OnDestroy();
    }

    void OnTextChanged(FixedString4096Bytes previous, FixedString4096Bytes current)
    {
        if (m_PendingText == current.ToString())
            m_PendingTextUntil = 0f;
        Apply();
    }

    void OnStyleChanged(SignStyle previous, SignStyle current)
    {
        if (m_PendingStyle.Equals(current))
            m_PendingStyleUntil = 0f;
        Apply();
    }

    void OnSaveNameChanged(FixedString128Bytes previous, FixedString128Bytes current) => changed?.Invoke();

    void OnMenuForChanged(ulong previous, ulong current) => OpenMenuIfMine(current);

    void OpenMenuIfMine(ulong player)
    {
        if (IsSpawned && player != ulong.MaxValue && NetworkManager != null && player == NetworkManager.LocalClientId)
            OpenMenu();
    }

    #region Setting up and changing

    /// <summary>
    /// Sets a new sign's text, style, stored name and size before it appears (by the Signage list, on the host or
    /// when not connected); <paramref name="menuFor"/> is the player whose menu opens on it (ulong.MaxValue: nobody).
    /// </summary>
    public void Configure(string newText, SignStyle newStyle, string newSaveName, float scale, ulong menuFor)
    {
        m_LocalText = Limit(newText);
        m_LocalStyle = newStyle.Clamped();
        m_LocalSaveName = newSaveName ?? "";
        m_LocalMenuFor = menuFor;
        transform.localScale = Vector3.one * Mathf.Clamp(scale, 0.05f, 50f);
        if (isActiveAndEnabled)
            Apply();
    }

    /// <summary>Changes what it says, for everyone (at most 1000 characters).</summary>
    public void SetText(string value)
    {
        value = Limit(value);
        if (value == text)
            return;
        if (IsSpawned)
        {
            m_PendingText = value;
            m_PendingTextUntil = Time.unscaledTime + 1.5f;
            SetTextRpc(value);
            Apply();
        }
        else
        {
            m_LocalText = value;
            Apply();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetTextRpc(string value) => m_SharedText.Value = ToShared(Limit(value));

    /// <summary>Changes how it looks, for everyone.</summary>
    public void SetStyle(SignStyle value)
    {
        value = value.Clamped();
        if (value.Equals(style))
            return;
        if (IsSpawned)
        {
            m_PendingStyle = value;
            m_PendingStyleUntil = Time.unscaledTime + 1.5f;
            SetStyleRpc(value);
            Apply();
        }
        else
        {
            m_LocalStyle = value;
            Apply();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetStyleRpc(SignStyle value) => m_SharedStyle.Value = value.Clamped();

    public void SetFont(int index)
    {
        var s = style;
        s.font = (byte)Mathf.Clamp(index, 0, Mathf.Max(0, (m_Fonts != null ? m_Fonts.count : 1) - 1));
        SetStyle(s);
    }

    /// <summary>Regular: neither bold nor italic.</summary>
    public void SetRegular()
    {
        var s = style;
        s.bold = false;
        s.italic = false;
        SetStyle(s);
    }

    public void SetBold(bool on)
    {
        var s = style;
        s.bold = on;
        SetStyle(s);
    }

    public void SetItalic(bool on)
    {
        var s = style;
        s.italic = on;
        SetStyle(s);
    }

    public void SetShadow(bool on)
    {
        var s = style;
        s.shadow = on;
        SetStyle(s);
    }

    public void SetBorder(bool on)
    {
        var s = style;
        s.border = on;
        SetStyle(s);
    }

    public void SetAlign(SignStyle.Align align)
    {
        var s = style;
        s.align = align;
        SetStyle(s);
    }

    public void SetColor(SignStyle.Part part, Color color)
    {
        var s = style;
        s.SetColor(part, color);
        SetStyle(s);
    }

    public void SetBorderWidth(float meters)
    {
        var s = style;
        s.borderWidth = meters;
        SetStyle(s);
    }

    public void SetPadding(float meters)
    {
        var s = style;
        s.padding = meters;
        SetStyle(s);
    }

    /// <summary>At most 1000 characters, and no more than one shared text holds; Windows line ends made plain.</summary>
    public static string Limit(string value)
    {
        value = (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        if (value.Length > k_MaxCharacters)
            value = value.Substring(0, k_MaxCharacters);
        return Fit(value, k_MaxBytes);
    }

    // Cut to whole characters that fit in this many bytes.
    static string Fit(string value, int bytes)
    {
        value ??= "";
        if (Encoding.UTF8.GetByteCount(value) <= bytes)
            return value;
        var sb = new StringBuilder();
        int used = 0;
        foreach (char c in value)
        {
            int n = Encoding.UTF8.GetByteCount(c.ToString());
            if (used + n > bytes)
                break;
            sb.Append(c);
            used += n;
        }
        return sb.ToString();
    }

    static FixedString4096Bytes ToShared(string value) => new(Fit(value, k_MaxBytes));

    static string FirstLine(string value)
    {
        value ??= "";
        int end = value.IndexOf('\n');
        return end < 0 ? value : value.Substring(0, end) + "...";
    }

    #endregion

    #region Drawing

    /// <summary>
    /// Shows its text and style: the letters in their font, style, alignment and colour (with the drop-shadow material
    /// when it's on), then the background and border drawn round them, and the grab box fitted to them.
    /// </summary>
    public void Apply()
    {
        if (m_Text == null || !gameObject.activeInHierarchy)
            return;
        var s = style;
        string shown = text;
        bool fakeBold = false, fakeItalic = false;
        var face = m_Fonts != null ? m_Fonts.FaceFor(s.font, s.bold, s.italic, out fakeBold, out fakeItalic) : null;
        if (face != null && face.font != null)
        {
            if (m_Text.font != face.font)
                m_Text.font = face.font;
            var material = s.shadow && face.shadow != null ? face.shadow : face.font.material;
            if (m_Text.fontSharedMaterial != material)
                m_Text.fontSharedMaterial = material;
        }
        else
        {
            fakeBold = s.bold;
            fakeItalic = s.italic;
        }

        m_Text.richText = false;
        m_Text.enableAutoSizing = false;
        m_Text.textWrappingMode = TextWrappingModes.NoWrap;
        m_Text.overflowMode = TextOverflowModes.Overflow;
        m_Text.fontSize = k_FontSize;
        m_Text.fontStyle = (fakeBold ? FontStyles.Bold : FontStyles.Normal) | (fakeItalic ? FontStyles.Italic : FontStyles.Normal);
        m_Text.alignment = s.align == SignStyle.Align.Left ? TextAlignmentOptions.Left
            : s.align == SignStyle.Align.Right ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;
        m_Text.color = s.textColor;
        m_Text.text = shown;

        var size = shown.Trim().Length == 0 ? new Vector2(0.2f, 0.06f) : m_Text.GetPreferredValues(shown, 100000f, 100000f);
        size = new Vector2(Mathf.Max(size.x, 0.05f), Mathf.Max(size.y, 0.03f));
        m_Text.rectTransform.sizeDelta = size + new Vector2(0.002f, 0.002f);
        m_Text.rectTransform.localPosition = Vector3.zero;
        m_Text.ForceMeshUpdate();

        m_Inner = size * 0.5f + Vector2.one * s.padding;
        m_Outer = m_Inner + (s.border ? Vector2.one * s.borderWidth : Vector2.zero);
        BuildPanel(s);
        if (m_Collider != null)
        {
            m_Collider.size = new Vector3(m_Outer.x * 2f, m_Outer.y * 2f, k_Depth);
            m_Collider.center = new Vector3(0f, 0f, k_BackZ);
        }
        PlaceEditButton();
        changed?.Invoke();
    }

    void BuildPanel(SignStyle s)
    {
        if (m_Mesh == null)
            return;
        var vertices = new List<Vector3>();
        var colors = new List<Color32>();
        var triangles = new List<int>();
        void Quad(Vector2 min, Vector2 max, Color32 color)
        {
            int start = vertices.Count;
            vertices.Add(new Vector3(min.x, min.y, k_BackZ));
            vertices.Add(new Vector3(min.x, max.y, k_BackZ));
            vertices.Add(new Vector3(max.x, max.y, k_BackZ));
            vertices.Add(new Vector3(max.x, min.y, k_BackZ));
            for (int i = 0; i < 4; i++)
                colors.Add(color);
            triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }

        var inner = m_Inner;
        var outer = m_Outer;
        if (s.backgroundColor.a > 0)
            Quad(-inner, inner, s.backgroundColor);
        if (s.border)
        {
            Quad(new Vector2(-outer.x, inner.y), new Vector2(outer.x, outer.y), s.borderColor);   // top
            Quad(new Vector2(-outer.x, -outer.y), new Vector2(outer.x, -inner.y), s.borderColor); // bottom
            Quad(new Vector2(-outer.x, -inner.y), new Vector2(-inner.x, inner.y), s.borderColor); // left
            Quad(new Vector2(inner.x, -inner.y), new Vector2(outer.x, inner.y), s.borderColor);   // right
        }

        m_Mesh.Clear();
        m_Mesh.SetVertices(vertices);
        m_Mesh.SetColors(colors);
        m_Mesh.SetTriangles(triangles, 0);
        m_Mesh.RecalculateBounds();
    }

    // At the lower right corner, just outside the border, the same size in the world however large the sign is.
    void PlaceEditButton()
    {
        if (m_EditButton == null)
            return;
        var root = m_EditButton.transform.parent as RectTransform;
        if (root == null)
            return;
        float scale = Mathf.Max(transform.lossyScale.x, 1e-4f);
        float size = m_EditButtonSize / scale;
        float canvasWidth = Mathf.Max(root.sizeDelta.x, 1f);
        root.localScale = Vector3.one * (size / canvasWidth);
        root.localPosition = new Vector3(m_Outer.x - size * 0.5f, -m_Outer.y - size * 0.5f - 0.008f / scale, -0.001f);
        root.localRotation = Quaternion.identity;
    }

    void LateUpdate()
    {
        PlaceEditButton();
        bool show = TunnelSignageMenu.editButtonsShown || menuOpen;
        if (m_EditButton != null && show != m_EditButtonShown)
        {
            m_EditButtonShown = show;
            m_EditButton.transform.parent.gameObject.SetActive(show);
        }
        if (menuOpen)
            PlaceMenu();
    }

    #endregion

    #region Wall

    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (TunnelMenu.deleteMode && !(args.interactorObject is XRSocketInteractor))
            TunnelMenu.DeleteGrabbed(this, m_Grab);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        if (m_Grab != null && !m_Grab.isSelected && !TunnelMenu.deleteMode && isActiveAndEnabled)
            StartCoroutine(SnapAfterRelease());
    }

    IEnumerator SnapAfterRelease()
    {
        yield return null; // after the interaction toolkit and the network interactable have let go
        if (m_Grab.isSelected || (IsSpawned && !IsOwner))
            yield break;
        TrySnapToWall();
    }

    /// <summary>
    /// Let go of near one of the flat tunnel walls (within 25 cm of it, between the floor and the wall's top): flat on
    /// the wall, upright, its face toward the tunnel, where it was let go (kept on the wall). False if no wall is near.
    /// </summary>
    public bool TrySnapToWall()
    {
        TunnelSegmentGeometry wall = null;
        int wallSide = 0;
        float nearest = float.MaxValue;
        Vector3 local = default;
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null || !segment.isActiveAndEnabled)
                continue;
            var p = segment.transform.InverseTransformPoint(transform.position);
            if (Mathf.Abs(p.z) > segment.length * 0.5f || p.y < 0f || p.y > segment.wallHeight)
                continue;
            int side = p.x < 0f ? -1 : 1;
            float gap = segment.width * 0.5f - side * p.x;
            if (gap > m_SnapDistance || gap < -0.2f || gap >= nearest)
                continue;
            nearest = gap;
            wall = segment;
            wallSide = side;
            local = p;
        }
        if (wall == null)
            return false;

        var frame = wall.transform;
        var half = worldHalfSize;
        float scale = transform.lossyScale.x;
        float halfLength = wall.length * 0.5f;
        float z = half.x < halfLength ? Mathf.Clamp(local.z, -halfLength + half.x, halfLength - half.x) : 0f;
        float y = half.y * 2f + 0.02f < wall.wallHeight ? Mathf.Clamp(local.y, half.y + 0.02f, wall.wallHeight - half.y) : wall.wallHeight * 0.5f;
        // Its back (the grab box's far face) just off the wall; its forward into the wall, so its face looks into the tunnel.
        float back = (k_BackZ + k_Depth * 0.5f) * scale + m_WallGap;
        var position = frame.TransformPoint(new Vector3(wallSide * (wall.width * 0.5f - back), y, z));
        var rotation = Quaternion.LookRotation(frame.TransformDirection(Vector3.right * wallSide), frame.up);
        transform.SetPositionAndRotation(position, rotation);
        if (m_Body != null)
        {
            m_Body.position = position;
            m_Body.rotation = rotation;
        }
        Debug.Log($"[Sign] \"{FirstLine(text)}\" set on the {(wallSide < 0 ? "-X" : "+X")} wall of {wall.name} at {y:F2} m up");
        XRMultiplayer.PlayerHudNotification.Instance?.ShowText("Sign on the wall: take it in hand to pull it off", 2f);
        return true;
    }

    /// <summary>Whether it lies flat on a flat tunnel wall now (and which).</summary>
    public bool IsOnWall(out TunnelSegmentGeometry wall)
    {
        wall = null;
        float scale = transform.lossyScale.x;
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null || !segment.isActiveAndEnabled)
                continue;
            var frame = segment.transform;
            var p = frame.InverseTransformPoint(transform.position);
            if (Mathf.Abs(p.z) > segment.length * 0.5f + 0.01f || p.y < 0f || p.y > segment.wallHeight)
                continue;
            int side = p.x < 0f ? -1 : 1;
            float back = (k_BackZ + k_Depth * 0.5f) * scale + m_WallGap;
            float off = Mathf.Abs(segment.width * 0.5f - side * p.x - back);
            float angle = Vector3.Angle(transform.forward, frame.TransformDirection(Vector3.right * side));
            if (off < 0.01f && angle < 2f)
            {
                wall = segment;
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Menu

    public void ToggleMenu()
    {
        if (menuOpen)
            CloseMenu();
        else
            OpenMenu();
    }

    /// <summary>Opens its menu beside it, on this headset only.</summary>
    public void OpenMenu()
    {
        if (m_MenuPrefab == null)
            return;
        if (m_Menu == null)
        {
            m_Menu = Instantiate(m_MenuPrefab);
            m_Menu.name = "Sign Menu";
            m_Menu.Bind(this);
        }
        m_Menu.gameObject.SetActive(true);
        PlaceMenu();
        m_Menu.Refresh(true);
    }

    public void CloseMenu()
    {
        if (m_Menu != null)
        {
            m_Menu.CloseKeyboard();
            m_Menu.gameObject.SetActive(false);
        }
    }

    /// <summary>Its menu on its right, in its plane, just in front of it (so on a wall it lies on the wall too).</summary>
    void PlaceMenu()
    {
        var t = transform;
        float halfWidth = worldHalfSize.x;
        m_Menu.transform.SetPositionAndRotation(
            t.position + t.right * (halfWidth + m_MenuGap + m_Menu.width * 0.5f) - t.forward * 0.004f,
            t.rotation);
    }

    #endregion

    #region Saving

    /// <summary>Keeps its text, style and size on the host, under its name in the Signage list (made from its first line).</summary>
    public void Save()
    {
        Debug.Log($"[Sign] save pressed ({(IsSpawned ? (IsServer ? "storing here, the host" : "asking the host") : "not connected, storing here")})");
        if (IsSpawned)
            SaveRpc(ToShared(text), style);
        else
            SaveHere(text, style);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SaveRpc(FixedString4096Bytes savedText, SignStyle savedStyle) => SaveHere(savedText.ToString(), savedStyle);

    // On the host (or here when not connected). Saved again, it replaces its own stored copy.
    void SaveHere(string savedText, SignStyle savedStyle)
    {
        string name = saveName;
        if (string.IsNullOrEmpty(name) || !SignStore.Exists(name))
            name = string.IsNullOrEmpty(name) ? SignStore.NewName(savedText) : SignStore.Clean(name);
        var record = SignRecord.From(name, savedText, savedStyle, m_Fonts, transform.localScale.x);
        if (!SignStore.Save(record))
        {
            ShareMessage("Couldn't save the sign on the host.");
            return;
        }
        if (IsSpawned)
            m_SharedSaveName.Value = new FixedString128Bytes(Fit(record.name, 120));
        else
            m_LocalSaveName = record.name;
        ShareMessage($"Saved as \"{record.name}\": call it up again from the TS Menu's Signage list.");
        changed?.Invoke();
    }

    #endregion

    /// <summary>A message on everyone's copy of its menu.</summary>
    public void ShareMessage(string value)
    {
        if (IsSpawned)
            StatusRpc(value);
        else
            SetMessage(value);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void StatusRpc(string value) => SetMessage(value);

    void SetMessage(string value)
    {
        message = value;
        if (m_Menu != null)
            m_Menu.Refresh();
    }

#if UNITY_EDITOR
    /// <summary>Set by the builder (Tunnel > Signage > Build Signage).</summary>
    public void SetParts(SignFontLibrary library, TextMeshPro textMesh, MeshFilter panel, BoxCollider grabBox, Button editButton, TunnelSignPanel menuPrefab)
    {
        m_Fonts = library;
        m_Text = textMesh;
        m_Panel = panel;
        m_Collider = grabBox;
        m_EditButton = editButton;
        m_MenuPrefab = menuPrefab;
    }
#endif
}
