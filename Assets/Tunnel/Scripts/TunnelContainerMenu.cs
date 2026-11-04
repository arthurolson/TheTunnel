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
/// (see <see cref="TunnelContainer.viscosity"/>). Works locally when not connected.
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
    TMP_Text m_StatusText;

    [SerializeField]
    Slider m_ViscositySlider;

    [SerializeField]
    TMP_Text m_ViscosityText;

    [SerializeField]
    float m_GapFromList = 0.05f;

    [SerializeField]
    float m_GapFromWall = 0.1f;

    TunnelContainer.Shape m_Chosen = TunnelContainer.Shape.Box;

    // The container and value the slider shows, so it follows a change made by another player but not its own.
    TunnelContainer m_ViscosityFor;
    float m_ViscosityShown = -1f;

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
        if (m_ViscositySlider != null)
        {
            // Steps of 0.1: the slider counts tenths.
            m_ViscositySlider.minValue = 0f;
            m_ViscositySlider.maxValue = 10f;
            m_ViscositySlider.wholeNumbers = true;
            m_ViscositySlider.SetValueWithoutNotify(10f);
            m_ViscositySlider.onValueChanged.AddListener(OnViscosityMoved);
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
        ShowViscosity(container);
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
