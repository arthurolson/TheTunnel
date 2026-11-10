using System;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// A painter's palette, placed by the TS Menu's Paint button with its marker (<see cref="TunnelMarker"/>) resting in
/// the holder along its right side. Held and moved by its rim; let go near one of the tunnel's flat walls, it sets
/// itself flat on the wall, and taking it in hand again takes it off. Grabbing the holder twice quickly (hand or laser)
/// calls its marker back into the holder from wherever it is, even from another player's hand.
/// On it: a colour chooser (hue, saturation, value and opacity) and five colour disks. Selecting a disk (point or
/// poke) lets the chooser change that disk's colour; touching a disk with the marker's tip loads its colour into the
/// marker. Check boxes choose what the marker does: solid Paint, Pencil lines (straight from touch to touch, or with
/// Curve a Bezier curve whose control points are the touches; End line finishes one, as does touching the same spot
/// twice), Airbrush, or Eraser. Clear all marks (pressed twice) takes every mark off the tunnel.
/// Real marker paints with a real marker, tracked by this headset's camera, on a real panel with marked corners or on
/// a tunnel wall that lies on a real wall (<see cref="RealMarkerTracker"/>): it uses the marker's colour, and selecting
/// a disk then gives the marker its colour. Copy to wall puts a copy of the panel's painting on the tunnel wall in
/// view, for everyone (<see cref="PaintingCopy"/>).
/// Shared: everyone sees the same disks, choices and messages. Works locally when not connected.
/// </summary>
public class TunnelPalette : NetworkBehaviour
{
    [Header("Colour chooser")]
    [SerializeField] Slider m_Hue;
    [SerializeField] Slider m_Saturation;
    [SerializeField] Slider m_Value;
    [SerializeField] Slider m_Opacity;
    [SerializeField] Image m_Swatch;

    [Header("Disks")]
    [SerializeField] Button[] m_Disks = new Button[5];
    [SerializeField] Image[] m_DiskRings = new Image[5];

    [Header("Marks")]
    [SerializeField] Button m_PaintRow;
    [SerializeField] Button m_PencilRow;
    [SerializeField] Button m_AirbrushRow;
    [SerializeField] Button m_EraserRow;
    [SerializeField] Button m_CurveRow;
    [SerializeField] Button m_EndLineRow;
    [SerializeField] Button m_ClearRow;
    [SerializeField] Button m_RealRow;
    [SerializeField] Button m_CopyRow;
    [SerializeField] TMP_Text m_Status;

    [Header("Real marker")]
    [SerializeField]
    [Tooltip("Tunnel/Paint Overlay: draws the paint on the real whiteboard.")]
    Material m_BoardOverlay;

    [SerializeField]
    [Tooltip("Tunnel/Paint Brush.")]
    Shader m_BoardBrush;

    [Header("Holder and wall")]
    [SerializeField]
    [Tooltip("Where the marker rests on the palette.")]
    Transform m_Dock;

    [SerializeField]
    [Tooltip("The grab zone round the holder: grabbed twice quickly there, the marker comes back.")]
    Collider m_HolderGrab;

    [SerializeField]
    [Tooltip("The most time between the two grabs on the holder (seconds).")]
    float m_DoubleClickTime = 0.6f;

    [SerializeField]
    [Tooltip("Let go this close to a flat tunnel wall (meters) and it sets itself on the wall.")]
    float m_SnapDistance = 0.25f;

    [SerializeField]
    [Tooltip("How far its face stands off the wall once set on it (meters).")]
    float m_WallGap = 0.02f;

    /// <summary>Every palette.</summary>
    public static readonly System.Collections.Generic.List<TunnelPalette> all = new();

    public static readonly Color32[] k_DefaultDisks =
    {
        new(220, 30, 30, 255), new(250, 200, 20, 255), new(40, 170, 60, 255), new(30, 90, 220, 255), new(20, 20, 20, 255),
    };

    readonly NetworkVariable<Color32> m_Disk0 = new(k_DefaultDisks[0], NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Color32> m_Disk1 = new(k_DefaultDisks[1], NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Color32> m_Disk2 = new(k_DefaultDisks[2], NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Color32> m_Disk3 = new(k_DefaultDisks[3], NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<Color32> m_Disk4 = new(k_DefaultDisks[4], NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_SharedSelected = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> m_SharedMode = new((byte)PaintMode.Paint, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<bool> m_SharedCurve = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    NetworkVariable<Color32>[] m_SharedDisks;
    readonly Color32[] m_LocalDisks = (Color32[])k_DefaultDisks.Clone();
    int m_LocalSelected;
    PaintMode m_LocalMode = PaintMode.Paint;
    bool m_LocalCurve;

    XRGrabInteractable m_Grab;
    Rigidbody m_Body;
    Color32 m_SlidersShow;     // the colour the sliders are set to
    bool m_SlidersSet;
    float m_ClearArmedUntil;

    /// <summary>Asked (by anyone) to finish the pencil line being drawn with this palette's marker.</summary>
    public event Action endLineRequested;

    public PaintMode mode => IsSpawned ? (PaintMode)m_SharedMode.Value : m_LocalMode;
    public bool curve => IsSpawned ? m_SharedCurve.Value : m_LocalCurve;
    public int selectedDisk => IsSpawned ? m_SharedSelected.Value : m_LocalSelected;
    public int diskCount => m_Disks.Length;
    public Transform dock => m_Dock;

    public Color32 DiskColor(int index) => IsSpawned ? m_SharedDisks[index].Value : m_LocalDisks[index];

    void Awake()
    {
        m_SharedDisks = new[] { m_Disk0, m_Disk1, m_Disk2, m_Disk3, m_Disk4 };
        m_Grab = GetComponent<XRGrabInteractable>();
        m_Body = GetComponent<Rigidbody>();
        if (TryGetComponent(out NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = false; // moved only by hand; frozen, it would turn but not move
        if (m_Body != null)
            m_Body.constraints = RigidbodyConstraints.None;
        if (m_Grab != null)
        {
            m_Grab.selectEntered.AddListener(OnGrabbed);
            m_Grab.selectExited.AddListener(OnReleased);
        }

        foreach (var slider in new[] { m_Hue, m_Saturation, m_Value, m_Opacity })
        {
            if (slider == null)
                continue;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;
            slider.onValueChanged.AddListener(_ => OnSlidersMoved());
        }
        for (int i = 0; i < m_Disks.Length; i++)
        {
            int index = i;
            if (m_Disks[i] != null)
                m_Disks[i].onClick.AddListener(() => SelectDisk(index));
        }
        m_PaintRow?.onClick.AddListener(() => SetMode(PaintMode.Paint));
        m_PencilRow?.onClick.AddListener(() => SetMode(PaintMode.Pencil));
        m_AirbrushRow?.onClick.AddListener(() => SetMode(PaintMode.Airbrush));
        m_EraserRow?.onClick.AddListener(() => SetMode(PaintMode.Eraser));
        m_CurveRow?.onClick.AddListener(() => SetCurve(!curve));
        m_EndLineRow?.onClick.AddListener(RequestEndLine);
        m_ClearRow?.onClick.AddListener(PressClear);
        m_RealRow?.onClick.AddListener(() => RealMarkerTracker.Get().Toggle(this, m_BoardOverlay, m_BoardBrush));
        m_CopyRow?.onClick.AddListener(CopyToWall);
        SetStatus("Touch a disk with the marker's tip to take its colour. Select a disk here to change its colour.");
    }

    void OnEnable() => all.Add(this);

    void OnDisable() => all.Remove(this);

    public override void OnDestroy()
    {
        if (m_Grab != null)
        {
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
            m_Grab.selectExited.RemoveListener(OnReleased);
        }
        base.OnDestroy();
    }

    void Update()
    {
        ShowDisks();
        ShowRows();
        ShowSliders();
        ShowRealStatus();
    }

    bool realOn => RealMarkerTracker.instance != null && RealMarkerTracker.instance.palette == this &&
        RealMarkerTracker.instance.stage != RealMarkerTracker.Stage.Off;

    string m_RealStatusShown;

    // The real marker's progress, on this headset only.
    void ShowRealStatus()
    {
        var tracker = RealMarkerTracker.instance;
        if (tracker == null || tracker.palette != this || tracker.status == m_RealStatusShown)
            return;
        m_RealStatusShown = tracker.status;
        SetStatus(tracker.status);
    }

    #region Showing

    void ShowDisks()
    {
        int selected = selectedDisk;
        for (int i = 0; i < m_Disks.Length; i++)
        {
            if (m_Disks[i] != null && m_Disks[i].targetGraphic != null)
            {
                Color color = DiskColor(i);
                if (m_Disks[i].targetGraphic.color != color)
                    m_Disks[i].targetGraphic.color = color;
            }
            if (i < m_DiskRings.Length && m_DiskRings[i] != null && m_DiskRings[i].enabled != (i == selected))
                m_DiskRings[i].enabled = i == selected;
        }
    }

    void ShowRows()
    {
        var current = mode;
        SetRow(m_PaintRow, Check(current == PaintMode.Paint) + "  Paint  <color=#AAA>solid, freehand</color>", true);
        SetRow(m_PencilRow, Check(current == PaintMode.Pencil) + "  Pencil  <color=#AAA>touch point to point</color>", true);
        SetRow(m_AirbrushRow, Check(current == PaintMode.Airbrush) + "  Airbrush  <color=#AAA>spray</color>", true);
        SetRow(m_EraserRow, Check(current == PaintMode.Eraser) + "  Eraser", true);
        bool pencil = current == PaintMode.Pencil;
        SetRow(m_CurveRow, Check(curve) + "  Curve  <color=#AAA>" + (pencil ? "touches are Bezier controls" : "(pencil)") + "</color>", pencil);
        SetRow(m_EndLineRow, "End line  <color=#AAA>" + (pencil ? "or touch the last point again" : "(pencil)") + "</color>", pencil);
        SetRow(m_ClearRow, Time.time < m_ClearArmedUntil ? "<color=#F88>Press again to clear every mark</color>" : "Clear all marks", true);
        SetRow(m_RealRow, Check(realOn) + "  Real marker  <color=#AAA>" + (realOn ? "press to stop" : "tracked by camera") + "</color>", true);
        SetRow(m_CopyRow, "Copy to wall  <color=#AAA>the panel's painting</color>", PanelPaint() != null);
    }

    static string Check(bool on) => on ? "■" : "□";

    static void SetRow(Button row, string text, bool interactable)
    {
        if (row == null)
            return;
        row.interactable = interactable;
        var label = row.GetComponentInChildren<TMP_Text>(true);
        if (label != null && label.text != text)
            label.text = text;
    }

    // The sliders follow the selected disk's colour, unless they already show it (hue and saturation are kept where
    // a grey or black colour leaves them undefined).
    void ShowSliders()
    {
        if (m_Hue == null)
            return;
        var color = DiskColor(selectedDisk);
        if (m_SlidersSet && color.Equals(m_SlidersShow))
            return;
        Color.RGBToHSV(color, out float h, out float s, out float v);
        if (v > 0.001f)
        {
            if (s > 0.001f)
                m_Hue.SetValueWithoutNotify(h);
            m_Saturation.SetValueWithoutNotify(s);
        }
        m_Value.SetValueWithoutNotify(v);
        m_Opacity.SetValueWithoutNotify(color.a / 255f);
        m_SlidersShow = color;
        m_SlidersSet = true;
        if (m_Swatch != null)
            m_Swatch.color = (Color)color;
    }

    #endregion

    #region Choices

    void OnSlidersMoved()
    {
        var rgb = Color.HSVToRGB(m_Hue.value, m_Saturation.value, m_Value.value);
        rgb.a = m_Opacity.value;
        Color32 color = rgb;
        m_SlidersShow = color;
        m_SlidersSet = true;
        if (m_Swatch != null)
            m_Swatch.color = rgb;
        SetDiskColor(selectedDisk, color);
    }

    void SetDiskColor(int index, Color32 color)
    {
        if (IsSpawned)
            SetDiskColorRpc((byte)index, color);
        else
            m_LocalDisks[index] = color;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetDiskColorRpc(byte index, Color32 color)
    {
        if (index < m_SharedDisks.Length)
            m_SharedDisks[index].Value = color;
    }

    void SelectDisk(int index)
    {
        if (IsSpawned)
            SelectDiskRpc((byte)index);
        else
            m_LocalSelected = index;
        if (realOn)
        {
            // The real marker can't touch the disks: selecting one gives it the disk's colour.
            foreach (var marker in TunnelMarker.all)
                if (marker != null && marker.palette == this)
                    marker.TakeColor(DiskColor(index));
            PlayerHudNotification.Instance?.ShowText($"Real marker: colour of disk {index + 1}", 1.5f);
            return;
        }
        ShareStatus($"Disk {index + 1} selected: the colour chooser now changes its colour.");
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SelectDiskRpc(byte index) => m_SharedSelected.Value = index;

    void SetMode(PaintMode value)
    {
        if (IsSpawned)
            SetModeRpc((byte)value);
        else
            m_LocalMode = value;
        ShareStatus(value switch
        {
            PaintMode.Pencil => curve
                ? "Pencil, curve: touch the wall at each control point; End line (or the last point again) draws the curve."
                : "Pencil: touch the wall point after point for straight lines; End line (or the last point again) finishes.",
            PaintMode.Airbrush => "Airbrush: hold the marker on the wall to spray; the longer, the stronger.",
            PaintMode.Eraser => "Eraser: rub the marker on marks to take them off.",
            _ => "Paint: touch the wall with the marker and move it.",
        });
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetModeRpc(byte value) => m_SharedMode.Value = value;

    void SetCurve(bool on)
    {
        if (IsSpawned)
            SetCurveRpc(on);
        else
            m_LocalCurve = on;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SetCurveRpc(bool on) => m_SharedCurve.Value = on;

    void RequestEndLine()
    {
        if (IsSpawned)
            EndLineRpc();
        else
            endLineRequested?.Invoke();
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void EndLineRpc() => endLineRequested?.Invoke();

    void PressClear()
    {
        if (Time.time >= m_ClearArmedUntil)
        {
            m_ClearArmedUntil = Time.time + 3f;
            return;
        }
        m_ClearArmedUntil = 0f;
        TunnelSegmentPaint.ClearAll();
        Debug.Log("[Paint] every mark cleared");
        ShareStatus("Every mark cleared.");
    }

    // The painting on the real panel, on this headset, if there is one.
    static TunnelSegmentPaint PanelPaint()
    {
        var tracker = RealMarkerTracker.instance;
        return tracker != null && tracker.board != null && tracker.panelPaint != null ? tracker.panelPaint : null;
    }

    // A copy of the panel's painting on the tunnel wall in view (or in front of the head), for everyone.
    void CopyToWall()
    {
        var source = PanelPaint();
        if (source == null || !source.hasMarks)
        {
            PlayerHudNotification.Instance?.ShowText("Nothing painted on the panel yet", 2f);
            return;
        }

        var menu = FindAnyObjectByType<TunnelPaintMenu>();
        if (menu == null)
            return;
        if (!PaintingCopy.WallInView(out var pose))
        {
            var head = Camera.main.transform;
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            pose = new Pose(head.position + forward * 1.2f, Quaternion.LookRotation(forward, Vector3.up));
        }

        var size = RealMarkerTracker.instance.board.size;
        menu.CopyPainting(source, size, pose);
        PlayerHudNotification.Instance?.ShowText("Painting copied: grab a corner to size and turn it", 2.5f);
        Debug.Log($"[Paint] copied the panel's painting ({size.x * 100f:0} x {size.y * 100f:0} cm) to {pose.position:F2}");
    }

    /// <summary>
    /// The disk a point (the marker's tip) is touching: within its circle, and no further than
    /// <paramref name="reach"/> (meters) in front of or behind the palette. -1 if none.
    /// </summary>
    public int DiskAt(Vector3 world, float reach)
    {
        var normal = transform.forward;
        for (int i = 0; i < m_Disks.Length; i++)
        {
            if (m_Disks[i] == null)
                continue;
            var rect = (RectTransform)m_Disks[i].transform;
            var offset = world - rect.position;
            float off = Vector3.Dot(offset, normal);
            float across = (offset - normal * off).magnitude;
            float radius = rect.rect.width * 0.5f * rect.lossyScale.x;
            if (across <= radius * 1.15f && Mathf.Abs(off) <= reach)
                return i;
        }
        return -1;
    }

    #endregion

    #region Messages

    void ShareStatus(string text)
    {
        if (IsSpawned)
            StatusRpc(text);
        else
            SetStatus(text);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Everyone)]
    void StatusRpc(string text) => SetStatus(text);

    void SetStatus(string text)
    {
        if (m_Status != null)
            m_Status.text = text;
    }

    #endregion

    #region Holding and the wall

    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (TunnelMenu.deleteMode)
        {
            // Its marker goes with it.
            foreach (var marker in TunnelMarker.all.ToArray())
                if (marker != null && marker.palette == this)
                    TunnelMenu.Delete(marker.gameObject);
            TunnelMenu.DeleteGrabbed(this, m_Grab);
            return;
        }
        if (OnHolder(args.interactorObject))
        {
            if (Time.unscaledTime - m_LastHolderClick <= m_DoubleClickTime)
            {
                m_LastHolderClick = -10f;
                RecallMarkers();
            }
            else
            {
                m_LastHolderClick = Time.unscaledTime;
            }
        }
        if (IsSpawned)
            GrabbedRpc();
    }

    float m_LastHolderClick = -10f;

    // Whether a grab took the palette by its holder: where the laser hit it, or where the hand is.
    bool OnHolder(IXRSelectInteractor interactor)
    {
        if (m_HolderGrab == null || interactor == null)
            return false;
        Vector3 point;
        if (interactor is NearFarInteractor nearFar && nearFar.selectionRegion.Value == NearFarInteractor.Region.Far &&
            nearFar.TryGetCurveEndPoint(out var end) != EndPointType.None)
            point = end;
        else
            point = interactor.GetAttachTransform(m_Grab).position;
        return (m_HolderGrab.ClosestPoint(point) - point).sqrMagnitude <= 0.02f * 0.02f;
    }

    void RecallMarkers()
    {
        int recalled = 0;
        foreach (var marker in TunnelMarker.all)
        {
            if (marker == null || marker.palette != this)
                continue;
            marker.Recall();
            recalled++;
        }
        Debug.Log($"[Paint] holder double-clicked: {recalled} marker(s) called back");
        if (recalled > 0)
            PlayerHudNotification.Instance?.ShowText("Marker back in its holder", 1.5f);
    }

    // The marker resting on it goes to whoever holds it, so it moves with the palette on their headset.
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void GrabbedRpc(RpcParams rpcParams = default)
    {
        ulong holder = rpcParams.Receive.SenderClientId;
        foreach (var marker in TunnelMarker.all)
            if (marker != null && marker.palette == this && marker.docked && marker.IsSpawned && marker.OwnerClientId != holder)
                marker.NetworkObject.ChangeOwnership(holder);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        if (m_Grab != null && !m_Grab.isSelected && !TunnelMenu.deleteMode && isActiveAndEnabled)
            StartCoroutine(SnapAfterRelease());
    }

    System.Collections.IEnumerator SnapAfterRelease()
    {
        yield return null; // after the interaction toolkit and the network interactable have let go
        if (m_Grab.isSelected || (IsSpawned && !IsOwner))
            yield break;
        TrySnapToWall();
    }

    /// <summary>
    /// Let go near one of the flat walls: flat on it, upright, its face toward the tunnel, where it was let go
    /// (kept within the wall).
    /// </summary>
    bool TrySnapToWall()
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
        // Its forward (the canvas's) points into the wall, so its face looks into the tunnel.
        var rotation = Quaternion.LookRotation(frame.TransformDirection(Vector3.right * wallSide), frame.up);
        float halfLength = wall.length * 0.5f;
        float z = Mathf.Clamp(local.z, -halfLength + 0.25f, halfLength - 0.25f);
        float y = Mathf.Clamp(local.y, 0.3f, wall.wallHeight - 0.25f);
        var position = frame.TransformPoint(new Vector3(wallSide * (wall.width * 0.5f - m_WallGap), y, z));
        transform.SetPositionAndRotation(position, rotation);
        if (m_Body != null)
        {
            m_Body.position = position;
            m_Body.rotation = rotation;
        }
        Debug.Log($"[Paint] palette set on the {(wallSide < 0 ? "-X" : "+X")} wall of {wall.name}");
        PlayerHudNotification.Instance?.ShowText("Palette on the wall: take it in hand to take it off", 2f);
        return true;
    }

    #endregion
}
