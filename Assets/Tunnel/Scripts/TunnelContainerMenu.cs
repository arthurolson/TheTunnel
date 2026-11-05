using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The TS Menu's "Containers" list: check a shape (Box or Sphere), then Place Container puts one for everyone inside
/// the tunnel, beside the list like a new molecule (see <see cref="TunnelContainer"/>). The Collider check box shows
/// and switches whether the container placed or held last is a collider (red) or not (green); the Viscosity slider
/// (0 to 1 in steps of 0.1) shows and sets how thick the medium inside that container is
/// (see <see cref="TunnelContainer.viscosity"/>); the Mixed reality box check box makes that container, if it is a
/// box, a mixed reality box that sets itself into a tunnel wall (see <see cref="TunnelContainer.isMixedReality"/>),
/// and Unsnap from wall pulls it back out into the tunnel (see <see cref="TunnelContainer.Unsnap"/>);
/// Random motion / Ballistic motion and the thermometer (0 to 10,
/// continuous, 10 being room temperature) show and set the heat inside it (see <see cref="TunnelContainer.temperature"/>).
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
    Button m_PlaceButton;

    [SerializeField]
    Button m_ColliderRow;

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
    float m_GapFromList = 0.05f;

    [SerializeField]
    float m_GapFromWall = 0.1f;

    TunnelContainer.Shape m_Chosen = TunnelContainer.Shape.Box;

    // The container and value the slider shows, so it follows a change made by another player but not its own.
    TunnelContainer m_ViscosityFor;
    float m_ViscosityShown = -1f;
    TunnelContainer m_TemperatureFor;
    float m_TemperatureShown = -1f;

    void Awake()
    {
        if (m_ListPanel != null)
            m_ListPanel.SetActive(false);
        if (m_BoxRow != null)
            m_BoxRow.onClick.AddListener(() => Check(TunnelContainer.Shape.Box));
        if (m_SphereRow != null)
            m_SphereRow.onClick.AddListener(() => Check(TunnelContainer.Shape.Sphere));
        if (m_PlaceButton != null)
            m_PlaceButton.onClick.AddListener(Place);
        if (m_ColliderRow != null)
            m_ColliderRow.onClick.AddListener(ToggleCollider);
        if (m_MixedRealityRow != null)
            m_MixedRealityRow.onClick.AddListener(ToggleMixedReality);
        if (m_UnsnapRow != null)
            m_UnsnapRow.onClick.AddListener(Unsnap);
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

    public void ShowList(bool show)
    {
        if (m_ListPanel == null)
            return;
        m_ListPanel.SetActive(show);
        if (show)
            SetStatus("Check a shape, then Place Container.\nCollider switches the container placed or held last: red holds things in, green lets them through.");
    }

    void Update()
    {
        if (m_ColliderRow == null || m_ListPanel == null || !m_ListPanel.activeInHierarchy)
            return;
        var container = TunnelContainer.current;
        string text = container == null
            ? "□  Collider  <color=#AAA>(place a container first)</color>"
            : (container.isCollider ? "■  Collider  <color=#F66>(red)</color>" : "□  Collider  <color=#6F6>(green)</color>")
                + $"  <color=#AAA>{container.shape.ToString().ToLowerInvariant()} held or placed last</color>";
        var label = m_ColliderRow.GetComponentInChildren<TMP_Text>(true);
        if (label.text != text)
            label.text = text;
        ShowMixedReality(container);
        ShowViscosity(container);
        ShowHeat(container);
    }

    void ShowMixedReality(TunnelContainer container)
    {
        if (container == null)
            SetRow(m_MixedRealityRow, "□  Mixed reality box  <color=#AAA>(place a box first)</color>", false);
        else if (container.shape != TunnelContainer.Shape.Box)
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
            SetStatus("Unsnap frees a mixed reality box set into a wall.");
        else
            container.Unsnap();
    }

    void ToggleMixedReality()
    {
        var container = TunnelContainer.current;
        if (container == null || container.shape != TunnelContainer.Shape.Box)
            SetStatus("Place a box first: only boxes can be mixed reality boxes.");
        else
            container.SetMixedReality(!container.isMixedReality);
    }

    void ShowHeat(TunnelContainer container)
    {
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
        string text = $"<b>{shown:0.0}</b>\n<size=75%>{kelvin:0} K\n{kelvin - 273.15f:0} °C</size>";
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
        if (container == null)
            return;
        m_TemperatureFor = container;
        m_TemperatureShown = Mathf.Round(value * 20f) / 20f;
        container.SetTemperature(value);
    }

    void ShowViscosity(TunnelContainer container)
    {
        if (m_ViscositySlider == null)
            return;
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
        string text = container == null
            ? "Viscosity  <color=#AAA>(place a container first)</color>"
            : $"Viscosity: {m_ViscositySlider.value / 10f:0.0}  <color=#AAA>{container.shape.ToString().ToLowerInvariant()} held or placed last</color>";
        if (m_ViscosityText.text != text)
            m_ViscosityText.text = text;
    }

    void OnViscosityMoved(float tenths)
    {
        var container = TunnelContainer.current;
        if (container == null)
            return;
        float value = tenths / 10f;
        m_ViscosityFor = container;
        m_ViscosityShown = value;
        container.SetViscosity(value);
    }

    void ToggleCollider()
    {
        var container = TunnelContainer.current;
        if (container == null)
            SetStatus("Place a container first.");
        else
            container.ToggleCollider();
    }

    void Check(TunnelContainer.Shape shape)
    {
        m_Chosen = shape;
        ShowChecks();
    }

    // ■ for the checked shape, □ for the other.
    void ShowChecks()
    {
        if (m_BoxRow != null)
            m_BoxRow.GetComponentInChildren<TMP_Text>(true).text = (m_Chosen == TunnelContainer.Shape.Box ? "■" : "□") + "  Box";
        if (m_SphereRow != null)
            m_SphereRow.GetComponentInChildren<TMP_Text>(true).text = (m_Chosen == TunnelContainer.Shape.Sphere ? "■" : "□") + "  Sphere";
    }

    void Place()
    {
        SetStatus($"Placing a {m_Chosen.ToString().ToLowerInvariant()}...");
        if (IsSpawned)
            SpawnContainerRpc(m_Chosen);
        else
            SpawnContainer(m_Chosen);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void SpawnContainerRpc(TunnelContainer.Shape shape) => SpawnContainer(shape);

    void SpawnContainer(TunnelContainer.Shape shape)
    {
        var prefab = m_Prefabs == null ? null : shape == TunnelContainer.Shape.Box ? m_Prefabs.containerBox : m_Prefabs.containerSphere;
        if (prefab == null || m_Geometry == null)
            return;

        // Placed before it wakes up, as the molecules are (its network transform takes its pose when it's created).
        var holder = new GameObject("Container Holder");
        holder.SetActive(false);
        var container = Instantiate(prefab, holder.transform);
        container.transform.rotation = m_Geometry.transform.rotation;
        var size = prefab.transform.localScale;
        float radius = shape == TunnelContainer.Shape.Box ? size.magnitude * 0.5f : Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.5f;
        container.transform.position = TunnelMoleculeMenu.PlaceBesideList(m_Geometry, (RectTransform)m_ListPanel.transform, container, radius, m_GapFromList, m_GapFromWall);
        container.transform.SetParent(null, true);
        Destroy(holder);
        Debug.Log($"[Containers] placed a {shape} at {container.transform.position:F2}");

        if (IsSpawned && container.TryGetComponent(out NetworkObject networkObject))
            networkObject.Spawn(true);
    }

    void SetStatus(string text)
    {
        if (m_StatusText != null)
            m_StatusText.text = text;
    }
}
