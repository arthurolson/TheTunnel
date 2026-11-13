using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The TS Menu's "Containers" list: check a shape (Box, Splat-box or Sphere), then Place Container puts one for everyone inside
/// the tunnel, beside the list like a new molecule (see <see cref="TunnelContainer"/>); its walls always hold molecules
/// in. For the container placed or held last: the Viscosity slider
/// (0 to 1 in steps of 0.1) shows and sets how thick the medium inside it is
/// (see <see cref="TunnelContainer.viscosity"/>); the Mixed reality box check box makes it, if a box, a mixed reality box that
/// sets itself into a tunnel wall (see <see cref="TunnelContainer.isMixedReality"/>),
/// and Unsnap from wall pulls it back out into the tunnel (see <see cref="TunnelContainer.Unsnap"/>);
/// Random motion / Ballistic motion and the thermometer (absolute zero to 100 °C, continuous, read in °C) show and set
/// the heat inside it (see <see cref="TunnelContainer.temperature"/>); 3D movie controls shows or hides its movie's
/// controls on the container (see <see cref="TunnelMovie"/>).
/// A splat-box (see <see cref="SplatBox"/>) is only a frame for a 3D portrait, chosen on its own menu beside it: it can
/// be a mixed reality box, but the medium, heat and movie rows don't apply to it.
/// In a session everyone sees the same menu: the shape checked, the container it shows (the one placed or held last by
/// anyone) and its messages.
/// Works locally when not connected.
/// </summary>
public class TunnelContainerMenu : NetworkBehaviour
{
    [SerializeField]
    TunnelPrefabRegistry m_Prefabs;

    [SerializeField]
    TunnelSegmentGeometry m_Geometry;

    [SerializeField]
    GameObject m_ListPanel;

    [SerializeField]
    Button m_BoxRow;

    [SerializeField]
    Button m_SphereRow;

    [SerializeField]
    Button m_SplatBoxRow;

    [SerializeField]
    [Tooltip("The splat-box (made by Tunnel > Containers > Build Containers).")]
    GameObject m_SplatBoxPrefab;

    [SerializeField]
    Button m_PlaceButton;

    [SerializeField]
    Button m_MixedRealityRow;

    [SerializeField]
    Button m_UnsnapRow;

    [SerializeField]
    TMP_Text m_StatusText;

    [SerializeField]
    Slider m_ViscositySlider;

    [SerializeField]
    TMP_Text m_ViscosityText;

    [SerializeField]
    Button m_RandomRow;

    [SerializeField]
    Button m_BallisticRow;

    [SerializeField]
    Slider m_TemperatureSlider;

    [SerializeField]
    TMP_Text m_TemperatureText;

    [SerializeField]
    Button m_MovieRow;

    [SerializeField]
    float m_GapFromList = 0.05f;

    [SerializeField]
    float m_GapFromWall = 0.1f;

    // The container and value the slider shows, so it follows a change made by another player but not its own.
    TunnelContainer m_ViscosityFor;
    float m_ViscosityShown = -1f;
    TunnelContainer m_TemperatureFor;
    float m_TemperatureShown = -1f;

    readonly NetworkVariable<TunnelContainer.Shape> m_SharedChosen = new(TunnelContainer.Shape.Box, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    TunnelContainer.Shape m_LocalChosen = TunnelContainer.Shape.Box;

    TunnelContainer.Shape m_Chosen => IsSpawned ? m_SharedChosen.Value : m_LocalChosen;

    void Awake()
    {
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);
        if (m_BoxRow != null)
            m_BoxRow.onClick.AddListener(() => Check(TunnelContainer.Shape.Box));
        if (m_SphereRow != null)
            m_SphereRow.onClick.AddListener(() => Check(TunnelContainer.Shape.Sphere));
        if (m_SplatBoxRow != null)
            m_SplatBoxRow.onClick.AddListener(() => Check(TunnelContainer.Shape.SplatBox));
        if (m_PlaceButton != null)
            m_PlaceButton.onClick.AddListener(Place);
        if (m_MixedRealityRow != null)
            m_MixedRealityRow.onClick.AddListener(ToggleMixedReality);
        if (m_UnsnapRow != null)
            m_UnsnapRow.onClick.AddListener(Unsnap);
        if (m_MovieRow != null)
            m_MovieRow.onClick.AddListener(ToggleMovie);
        if (m_ViscositySlider != null)
        {
            // Steps of 0.1: the slider counts tenths.
            m_ViscositySlider.minValue = 0f;
            m_ViscositySlider.maxValue = 10f;
            m_ViscositySlider.wholeNumbers = true;
            m_ViscositySlider.SetValueWithoutNotify(10f);
            m_ViscositySlider.onValueChanged.AddListener(OnViscosityMoved);
        }
        if (m_RandomRow != null)
            m_RandomRow.onClick.AddListener(() => TunnelContainer.current?.SetMotion(TunnelContainer.Motion.Random));
        if (m_BallisticRow != null)
            m_BallisticRow.onClick.AddListener(() => TunnelContainer.current?.SetMotion(TunnelContainer.Motion.Ballistic));
        if (m_TemperatureSlider != null)
        {
            m_TemperatureSlider.minValue = 0f;
            m_TemperatureSlider.maxValue = TunnelContainer.k_MaxTemperature;
            m_TemperatureSlider.wholeNumbers = false;
            m_TemperatureSlider.SetValueWithoutNotify(0f);
            m_TemperatureSlider.onValueChanged.AddListener(OnTemperatureMoved);
        }
        ShowChecks();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_SharedChosen.OnValueChanged += OnChosenChanged;
        ShowChecks();
    }

    public override void OnNetworkDespawn()
    {
        m_SharedChosen.OnValueChanged -= OnChosenChanged;
        base.OnNetworkDespawn();
    }

    void OnChosenChanged(TunnelContainer.Shape previous, TunnelContainer.Shape current) => ShowChecks();

    public void ShowList(bool show)
    {
        if (m_ListPanel == null)
            return;
        m_ListPanel.SetActive(show);
        if (show)
            SetStatus("Check a shape, then Place Container. Put molecules in by hand: its walls hold them in. A splat-box shows a 3D portrait, picked on its own menu.\nThe settings below are for the container placed or held last.");
    }

    void Update()
    {
        if (m_ListPanel == null || !m_ListPanel.activeInHierarchy)
            return;
        var container = TunnelContainer.current;
        ShowMixedReality(container);
        ShowViscosity(container);
        ShowHeat(container);
        ShowMovie(container);
    }

    void ShowMovie(TunnelContainer container)
    {
        var movie = container != null ? container.movie : null;
        if (container != null && !container.hasMedium)
            SetRow(m_MovieRow, "□  3D movie controls  <color=#AAA>(not for a splat-box)</color>", false);
        else if (movie == null)
            SetRow(m_MovieRow, "□  3D movie controls  <color=#AAA>(place a container first)</color>", false);
        else
            SetRow(m_MovieRow, (movie.controlsShown ? "■" : "□") + "  3D movie controls  <color=#AAA>on the container</color>", true);
    }

    void ToggleMovie()
    {
        var movie = TunnelContainer.current != null ? TunnelContainer.current.movie : null;
        if (movie != null)
            movie.SetControls(!movie.controlsShown);
    }

    void ShowMixedReality(TunnelContainer container)
    {
        if (container == null)
            SetRow(m_MixedRealityRow, "□  Mixed reality box  <color=#AAA>(place a box first)</color>", false);
        else if (!container.isBox)
            SetRow(m_MixedRealityRow, "□  Mixed reality box  <color=#AAA>(boxes only)</color>", false);
        else
            SetRow(m_MixedRealityRow, container.isMixedReality
                ? "■  Mixed reality box  <color=#AAA>let go at a wall</color>"
                : "□  Mixed reality box  <color=#AAA>free-standing</color>", true);

        bool inWall = container != null && container.isSetInWall;
        SetRow(m_UnsnapRow, inWall
            ? "Unsnap from wall  <color=#AAA>pull it into the tunnel</color>"
            : "Unsnap from wall  <color=#AAA>(not set into a wall)</color>", inWall);
    }

    void Unsnap()
    {
        var container = TunnelContainer.current;
        if (container == null || !container.isSetInWall)
            ShareStatus("Unsnap frees a mixed reality box set into a wall.");
        else
            container.Unsnap();
    }

    void ToggleMixedReality()
    {
        var container = TunnelContainer.current;
        if (container == null || !container.isBox)
            ShareStatus("Place a box first: only boxes (and splat-boxes) can be mixed reality boxes.");
        else
            container.SetMixedReality(!container.isMixedReality);
    }

    void ShowHeat(TunnelContainer container)
    {
        // A splat-box has no medium: its heat rows are off.
        if (container != null && !container.hasMedium)
            container = null;
        var motion = container != null ? container.motion : TunnelContainer.Motion.Random;
        SetRow(m_RandomRow, (motion == TunnelContainer.Motion.Random ? "■" : "□") + "  Random motion  <color=#AAA>Brownian</color>", container != null);
        SetRow(m_BallisticRow, (motion == TunnelContainer.Motion.Ballistic ? "■" : "□") + "  Ballistic motion  <color=#AAA>like a gas</color>", container != null);

        if (m_TemperatureSlider == null)
            return;
        m_TemperatureSlider.interactable = container != null;
        float value = container != null ? container.temperature : 0f;
        if (container != m_TemperatureFor || !Mathf.Approximately(value, m_TemperatureShown))
        {
            m_TemperatureFor = container;
            m_TemperatureShown = value;
            m_TemperatureSlider.SetValueWithoutNotify(value);
        }

        if (m_TemperatureText == null)
            return;
        float shown = m_TemperatureSlider.value;
        float kelvin = TunnelContainer.Kelvin(shown);
        string text = $"<b>{kelvin - 273.15f:0} °C</b>\n<size=75%>{kelvin:0} K</size>";
        if (m_TemperatureText.text != text)
            m_TemperatureText.text = text;
    }

    static void SetRow(Button row, string text, bool interactable)
    {
        if (row == null)
            return;
        row.interactable = interactable;
        var label = row.GetComponentInChildren<TMP_Text>(true);
        if (label.text != text)
            label.text = text;
    }

    void OnTemperatureMoved(float value)
    {
        var container = TunnelContainer.current;
        if (container == null || !container.hasMedium)
            return;
        m_TemperatureFor = container;
        m_TemperatureShown = Mathf.Round(value * 20f) / 20f;
        container.SetTemperature(value);
    }

    void ShowViscosity(TunnelContainer container)
    {
        if (m_ViscositySlider == null)
            return;
        bool splatBox = container != null && !container.hasMedium;
        if (splatBox)
            container = null;
        m_ViscositySlider.interactable = container != null;
        float value = container == null ? 1f : container.viscosity;
        if (container != m_ViscosityFor || !Mathf.Approximately(value, m_ViscosityShown))
        {
            m_ViscosityFor = container;
            m_ViscosityShown = value;
            m_ViscositySlider.SetValueWithoutNotify(Mathf.Round(value * 10f));
        }

        if (m_ViscosityText == null)
            return;
        string text = splatBox
            ? "Viscosity  <color=#AAA>(a splat-box has no medium)</color>"
            : container == null
            ? "Viscosity  <color=#AAA>(place a container first)</color>"
            : $"Viscosity: {m_ViscositySlider.value / 10f:0.0}  <color=#AAA>{container.shape.ToString().ToLowerInvariant()} held or placed last</color>";
        if (m_ViscosityText.text != text)
            m_ViscosityText.text = text;
    }

    void OnViscosityMoved(float tenths)
    {
        var container = TunnelContainer.current;
        if (container == null || !container.hasMedium)
            return;
        float value = tenths / 10f;
        m_ViscosityFor = container;
        m_ViscosityShown = value;
        container.SetViscosity(value);
    }

    void Check(TunnelContainer.Shape shape)
    {
        if (IsSpawned)
            CheckRpc(shape);
        else
        {
            m_LocalChosen = shape;
            ShowChecks();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void CheckRpc(TunnelContainer.Shape shape) => m_SharedChosen.Value = shape;

    // ■ for the checked shape, □ for the other.
    void ShowChecks()
    {
        if (m_BoxRow != null)
            m_BoxRow.GetComponentInChildren<TMP_Text>(true).text = (m_Chosen == TunnelContainer.Shape.Box ? "■" : "□") + "  Box";
        if (m_SphereRow != null)
            m_SphereRow.GetComponentInChildren<TMP_Text>(true).text = (m_Chosen == TunnelContainer.Shape.Sphere ? "■" : "□") + "  Sphere";
        if (m_SplatBoxRow != null)
            m_SplatBoxRow.GetComponentInChildren<TMP_Text>(true).text = (m_Chosen == TunnelContainer.Shape.SplatBox ? "■" : "□") + "  Splat-box";
    }

    void Place()
    {
        ShareStatus($"Placing a {ShapeName(m_Chosen)}...");
        if (IsSpawned)
            SpawnContainerRpc(m_Chosen);
        else
            SpawnContainer(m_Chosen);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SpawnContainerRpc(TunnelContainer.Shape shape) => SpawnContainer(shape);

    void SpawnContainer(TunnelContainer.Shape shape)
    {
        var prefab = shape == TunnelContainer.Shape.SplatBox ? m_SplatBoxPrefab
            : m_Prefabs == null ? null : shape == TunnelContainer.Shape.Box ? m_Prefabs.containerBox : m_Prefabs.containerSphere;
        if (prefab == null || m_Geometry == null)
        {
            Debug.LogWarning($"[Containers] no {ShapeName(shape)} to place (run Tunnel > Containers > Build Containers)");
            return;
        }

        // Placed before it wakes up, as the molecules are (its network transform takes its pose when it's created).
        var holder = new GameObject("Container Holder");
        holder.SetActive(false);
        var container = Instantiate(prefab, holder.transform);
        container.transform.rotation = m_Geometry.transform.rotation;
        var size = prefab.transform.localScale;
        float radius = shape != TunnelContainer.Shape.Sphere ? size.magnitude * 0.5f : Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.5f;
        container.transform.position = TunnelMoleculeMenu.PlaceBesideList(m_Geometry, (RectTransform)m_ListPanel.transform, container, radius, m_GapFromList, m_GapFromWall);
        if (shape == TunnelContainer.Shape.SplatBox)
        {
            // Its splat's side (+Z) toward the middle of the tunnel, upright.
            var geometry = m_Geometry.transform;
            var local = geometry.InverseTransformPoint(container.transform.position);
            var toMiddle = geometry.TransformDirection(new Vector3(-local.x, 0f, 0f));
            if (toMiddle.sqrMagnitude > 1e-4f)
                container.transform.rotation = Quaternion.LookRotation(toMiddle.normalized, geometry.up);
        }
        container.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Containers] placed a {shape} at {container.transform.position:F2}");

        if (IsSpawned && container.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
    }

    static string ShapeName(TunnelContainer.Shape shape) => shape == TunnelContainer.Shape.SplatBox ? "splat-box" : shape.ToString().ToLowerInvariant();

    /// <summary>A message on everyone's copy of the menu.</summary>
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
        if (m_StatusText != null)
            m_StatusText.text = text;
    }
}
