using System.Collections.Generic;

namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// Manages the visuals for hand rendering, applying passthrough and related effects to all shared materials.
    /// </summary>
    public class HandVisuals : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("List of hand mesh renderers to change the runtime shader properties on when allowing for passthrough to occur.")]
        List<Renderer> m_HandRenderers;

        /// <summary>
        /// List of hand mesh renderers to change the runtime shader properties on when allowing for passthrough to occur.
        /// </summary>
        public List<Renderer> handRenderers
        {
            get => m_HandRenderers;
            set => m_HandRenderers = value;
        }

        [SerializeField]
        [Tooltip("Shader reference to use for property lookup when changing the passthrough alpha properties at runtime.")]
        Shader m_Shader;

        /// <summary>
        /// Shader reference to use for property lookup when changing the passthrough alpha properties at runtime.
        /// </summary>
        public Shader shader
        {
            get => m_Shader;
            set => m_Shader = value;
        }

        [SerializeField]
        [Tooltip("Reference to the Appearance Manager instance in the scene that controls the state of the passthrough volume.")]
        AppearanceManger m_AppearanceManger;

        /// <summary>
        /// Reference to the Appearance Manager instance in the scene that controls the state of the passthrough volume.
        /// </summary>
        public AppearanceManger appearanceManger
        {
            get => m_AppearanceManger;
            set => m_AppearanceManger = value;
        }


        List<Material> m_HandMaterials = new List<Material>();

        float m_CurrentPassthroughOpacity;
        float m_TargetPassthroughOpacity;

        float m_CurrentNonPassthroughOpacity;
        float m_TargetNonPassthroughOpacity;

        float m_PassthroughHandSpeedScalar = 8f;

        readonly int m_NonPassthroughOpacityPropertyID = Shader.PropertyToID("_ARPassthroughAlpha");
        readonly int m_PassthroughOpacityPropertyID = Shader.PropertyToID("_PassthroughAlpha");
        readonly int m_ModePropertyID = Shader.PropertyToID("_Mode");

        bool m_Visible;

        public bool visible
        {
            set
            {
                m_Visible = value;
                m_TargetPassthroughOpacity = m_Visible ? 1f : 0f;
            }
        }

        bool m_DisplayARModePassthrougHands;

        public bool displayARModePassthrougHands
        {
            set
            {
                m_DisplayARModePassthrougHands = value;
                m_TargetNonPassthroughOpacity = m_DisplayARModePassthrougHands ? 1f : 0f;
            }
        }

        void Start()
        {
            SetupMaterials();
        }

        void SetupMaterials()
        {
            if (m_HandMaterials == null)
                m_HandMaterials = new List<Material>();

            // Instead of grabbing a single slot, we take all shared materials.
            foreach (var handRenderer in m_HandRenderers)
            {
                if (m_Shader != null)
                {
                    var materials = handRenderer.sharedMaterials;
                    for (int i = 0; i < materials.Length; ++i)
                    {
                        if (materials[i].shader == m_Shader)
                        {
                            m_HandMaterials.Add(materials[i]);
                        }
                    }
                }
                else
                {
                    m_HandMaterials.AddRange(handRenderer.sharedMaterials);
                }
            }
        }

        void Update()
        {
            if (m_AppearanceManger == null || m_HandMaterials == null || m_HandMaterials.Count == 0)
            {
                return;
            }

            // Opacity lerp
            m_CurrentPassthroughOpacity = Mathf.Lerp(m_CurrentPassthroughOpacity, m_TargetPassthroughOpacity, Time.deltaTime * m_PassthroughHandSpeedScalar);
            m_CurrentNonPassthroughOpacity = Mathf.Lerp(m_CurrentNonPassthroughOpacity, m_TargetNonPassthroughOpacity, Time.deltaTime * m_PassthroughHandSpeedScalar);

            var currentState = m_AppearanceManger.passThroughState.Value;

            // Map state to shader mode
            int mode;
            switch (currentState)
            {
                case AppearanceManger.PassthroughState.AR:
                    mode = 1; // AR
                    break;
                case AppearanceManger.PassthroughState.MR:
                    mode = 2; // MR
                    break;
                default:
                    mode = 0; // VR
                    break;
            }

            // Apply effects to all hand materials
            // In the future, we might want to check the game objects to see if they have been disabled to avoid the cost
            // of setting the properties on every instance that might be disabled, especially with 2 sets of hand meshes.
            for (int i = 0; i < m_HandMaterials.Count; i++)
            {
                m_HandMaterials[i].SetFloat(m_PassthroughOpacityPropertyID, m_CurrentPassthroughOpacity);
                m_HandMaterials[i].SetFloat(m_NonPassthroughOpacityPropertyID, m_CurrentNonPassthroughOpacity);
                m_HandMaterials[i].SetInt(m_ModePropertyID, mode);
            }
        }
    }
}
