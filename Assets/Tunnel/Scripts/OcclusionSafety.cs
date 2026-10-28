using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Only lets occlusion shaders hide virtual content while the headset is actually delivering depth frames.
/// AR Shader Occlusion switches occlusion on as soon as it is enabled; without depth data (no scene permission,
/// depth not started yet) the occlusion shaders would have nothing valid to test against. This keeps the global
/// switch off until frames arrive, and turns it off again if they stop. Status changes are logged
/// ("[Occlusion] ...") so the headset log shows whether depth is reaching the shaders.
/// It also gives the shaders the world-to-tracking-space transform: the depth matrices are in the headset's tracking
/// space, which stops matching the world as soon as the XR Origin is moved (QR co-location, table seats, teleport).
/// </summary>
[RequireComponent(typeof(AROcclusionManager))]
public class OcclusionSafety : MonoBehaviour
{
    static readonly int k_IsOcclusionOn = Shader.PropertyToID("_IsOcclusionOn");
    static readonly int k_WorldToTrackingSpace = Shader.PropertyToID("_OcclusionWorldToTrackingSpace");

    [SerializeField]
    [Tooltip("Occlusion is switched off if no depth frame has arrived for this long (seconds).")]
    float m_Timeout = 1f;

    [SerializeField]
    [Tooltip("How often a status line is written to the log (seconds); 0 turns it off.")]
    float m_StatusInterval = 10f;

    AROcclusionManager m_Manager;
    Transform m_TrackingSpace;
    float m_LastFrameTime = float.NegativeInfinity;
    bool m_On;
    int m_Frames;
    float m_NextStatus;

    void Awake()
    {
        m_Manager = GetComponent<AROcclusionManager>();
        Shader.SetGlobalMatrix(k_WorldToTrackingSpace, Matrix4x4.identity);
    }

    void OnEnable()
    {
        m_Manager.frameReceived += OnFrameReceived;
        Application.onBeforeRender += UpdateTrackingSpace;
        Debug.Log($"[Occlusion] enabled; requested depth mode {m_Manager.requestedEnvironmentDepthMode}, " +
            $"subsystem {(m_Manager.subsystem != null ? (m_Manager.subsystem.running ? "running" : "not running") : "missing")}");
    }

    void OnDisable()
    {
        m_Manager.frameReceived -= OnFrameReceived;
        Application.onBeforeRender -= UpdateTrackingSpace;
        Shader.SetGlobalInteger(k_IsOcclusionOn, 0);
    }

    /// <summary>The camera's parent (the origin's camera floor offset) is the tracking space the depth poses are in.</summary>
    void UpdateTrackingSpace()
    {
        if (m_TrackingSpace == null)
        {
            var origin = GetComponentInParent<XROrigin>();
            m_TrackingSpace = origin != null && origin.CameraFloorOffsetObject != null
                ? origin.CameraFloorOffsetObject.transform
                : transform.parent;
        }

        Shader.SetGlobalMatrix(k_WorldToTrackingSpace, m_TrackingSpace != null ? m_TrackingSpace.worldToLocalMatrix : Matrix4x4.identity);
    }

    void OnFrameReceived(AROcclusionFrameEventArgs args)
    {
        if (m_Frames == 0)
        {
            var textures = new System.Text.StringBuilder();
            if (args.externalTextures != null)
            {
                foreach (var t in args.externalTextures)
                    textures.Append(t.texture != null ? $"{t.texture.name} {t.texture.width}x{t.texture.height}; " : "null; ");
            }

            var keywords = new System.Text.StringBuilder();
            if (args.shaderKeywords.enabledKeywords != null)
            {
                foreach (var k in args.shaderKeywords.enabledKeywords)
                    keywords.Append(k).Append(' ');
            }

            Debug.Log($"[Occlusion] first depth frame: textures [{textures}], keywords [{keywords}]");
        }

        m_Frames++;
        m_LastFrameTime = Time.unscaledTime;
    }

    void LateUpdate()
    {
        UpdateTrackingSpace();
        bool depthFlowing = Time.unscaledTime - m_LastFrameTime < m_Timeout;
        Shader.SetGlobalInteger(k_IsOcclusionOn, depthFlowing ? 1 : 0);

        if (depthFlowing != m_On)
        {
            m_On = depthFlowing;
            Debug.Log(m_On ? "[Occlusion] depth frames arriving: occlusion ON" : "[Occlusion] no depth frames: occlusion OFF");
        }

        if (m_StatusInterval > 0f && Time.unscaledTime >= m_NextStatus)
        {
            m_NextStatus = Time.unscaledTime + m_StatusInterval;
            Debug.Log($"[Occlusion] status: {(m_On ? "ON" : "OFF")}, depth frames so far {m_Frames}, " +
                $"current depth mode {m_Manager.currentEnvironmentDepthMode}, " +
                $"subsystem {(m_Manager.subsystem != null ? (m_Manager.subsystem.running ? "running" : "not running") : "missing")}");
        }
    }
}
