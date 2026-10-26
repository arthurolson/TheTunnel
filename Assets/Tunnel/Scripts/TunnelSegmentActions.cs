using System.Collections;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The Tunnel Segment's action buttons. Any player can press them; the server does the spawning and removal so
/// everyone sees the same tunnel. Works locally when not connected.
///  - Add Segment: a new segment joined to this one's far (+Z) end, same height and direction.
///  - Remove Segment: deletes this segment (press twice within a few seconds to confirm).
///  - New Browser: a browser panel on this segment's wall the viewer is facing, level with their position.
/// </summary>
public class TunnelSegmentActions : NetworkBehaviour
{
    [SerializeField]
    TunnelPrefabRegistry m_Prefabs;

    [SerializeField]
    TunnelSegmentGeometry m_Geometry;

    [SerializeField]
    Button m_AddSegmentButton;

    [SerializeField]
    Button m_RemoveSegmentButton;

    [SerializeField]
    TMP_Text m_RemoveSegmentLabel;

    [SerializeField]
    Button m_NewBrowserButton;

    [SerializeField]
    [Tooltip("Seconds the Remove button waits for its confirming second press.")]
    float m_RemoveConfirmTime = 3f;

    [SerializeField]
    [Tooltip("Height of a new browser panel's centre above the floor (meters).")]
    float m_BrowserHeight = 1.5f;

    [SerializeField]
    [Tooltip("Gap between a new browser panel and the wall (meters).")]
    float m_BrowserWallOffset = 0.02f;

    const string k_RemoveText = "Remove Segment";
    const string k_ConfirmText = "Tap again to remove";

    Coroutine m_ConfirmRoutine;

    void Awake()
    {
        if (m_AddSegmentButton != null)
            m_AddSegmentButton.onClick.AddListener(OnAddSegmentPressed);
        if (m_RemoveSegmentButton != null)
            m_RemoveSegmentButton.onClick.AddListener(OnRemoveSegmentPressed);
        if (m_NewBrowserButton != null)
            m_NewBrowserButton.onClick.AddListener(OnNewBrowserPressed);
        SetRemoveLabel(k_RemoveText);
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (m_AddSegmentButton != null)
            m_AddSegmentButton.onClick.RemoveListener(OnAddSegmentPressed);
        if (m_RemoveSegmentButton != null)
            m_RemoveSegmentButton.onClick.RemoveListener(OnRemoveSegmentPressed);
        if (m_NewBrowserButton != null)
            m_NewBrowserButton.onClick.RemoveListener(OnNewBrowserPressed);
    }

    #region Add segment

    void OnAddSegmentPressed()
    {
        if (IsSpawned)
            AddSegmentRpc();
        else
            SpawnSegment();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void AddSegmentRpc() => SpawnSegment();

    void SpawnSegment()
    {
        if (m_Prefabs == null || m_Prefabs.tunnelSegment == null || m_Geometry == null)
            return;

        // The new segment's near end meets this one's far end.
        Vector3 position = transform.TransformPoint(new Vector3(0f, 0f, m_Geometry.length));
        var segment = Instantiate(m_Prefabs.tunnelSegment, position, transform.rotation);
        SpawnOnNetwork(segment);
    }

    #endregion

    #region Remove segment

    void OnRemoveSegmentPressed()
    {
        if (m_ConfirmRoutine == null)
        {
            m_ConfirmRoutine = StartCoroutine(WaitForConfirm());
            return;
        }

        StopCoroutine(m_ConfirmRoutine);
        m_ConfirmRoutine = null;
        SetRemoveLabel(k_RemoveText);

        if (IsSpawned)
            RemoveSegmentRpc();
        else
            Destroy(gameObject);
    }

    IEnumerator WaitForConfirm()
    {
        SetRemoveLabel(k_ConfirmText);
        yield return new WaitForSeconds(m_RemoveConfirmTime);
        SetRemoveLabel(k_RemoveText);
        m_ConfirmRoutine = null;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RemoveSegmentRpc()
    {
        if (NetworkObject != null && NetworkObject.IsSpawned)
            NetworkObject.Despawn(true);
        else
            Destroy(gameObject);
    }

    void SetRemoveLabel(string text)
    {
        if (m_RemoveSegmentLabel != null)
            m_RemoveSegmentLabel.text = text;
    }

    #endregion

    #region New browser

    void OnNewBrowserPressed()
    {
        var head = Camera.main != null ? Camera.main.transform : null;
        Vector3 headPosition = head != null ? head.position : transform.position;
        Vector3 headForward = head != null ? head.forward : transform.forward;

        if (IsSpawned)
            NewBrowserRpc(headPosition, headForward);
        else
            SpawnBrowser(headPosition, headForward);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NewBrowserRpc(Vector3 headPosition, Vector3 headForward) => SpawnBrowser(headPosition, headForward);

    void SpawnBrowser(Vector3 headPosition, Vector3 headForward)
    {
        if (m_Prefabs == null || m_Prefabs.browserPanel == null || m_Geometry == null)
            return;

        // On the wall the viewer is facing, level with where they stand along the tunnel, facing the interior.
        Transform frame = m_Geometry.transform;
        Vector3 localHead = frame.InverseTransformPoint(headPosition);
        Vector3 localForward = frame.InverseTransformDirection(headForward);
        float side = localForward.x >= 0f ? 1f : -1f;
        float halfLength = m_Geometry.length * 0.5f;
        float z = Mathf.Clamp(localHead.z, -halfLength + 0.7f, halfLength - 0.7f);
        Vector3 localPosition = new Vector3(side * (m_Geometry.width * 0.5f - m_BrowserWallOffset), m_BrowserHeight, z);

        Vector3 position = frame.TransformPoint(localPosition);
        Quaternion rotation = Quaternion.Euler(0f, frame.eulerAngles.y + (side > 0f ? 90f : -90f), 0f);
        var panel = Instantiate(m_Prefabs.browserPanel, position, rotation);
        SpawnOnNetwork(panel);
    }

    #endregion

    void SpawnOnNetwork(GameObject instance)
    {
        if (!IsSpawned)
            return; // offline: the local instance is all there is
        var networkObject = instance.GetComponent<NetworkObject>();
        if (networkObject != null)
            networkObject.Spawn(true);
    }
}
