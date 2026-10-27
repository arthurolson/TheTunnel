using TMPro;
using Unity.Netcode;
using UnityEngine.UI;

namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    /// <summary>
    /// Colour of the Tunnel Segment (TG) and its interior colour chooser. The wall button shows or hides the
    /// chooser; its sliders set hue, saturation, value and opacity. Both the colour and whether the chooser is
    /// open are replicated, so every player sees the same tunnel. Works locally when not connected.
    /// </summary>
    public class TunnelSegmentAppearance : NetworkBehaviour
    {
        const float k_OpaqueThreshold = 0.999f;
        const float k_RemoteUpdateHoldOff = 0.3f;

        [SerializeField]
        Renderer m_ShellRenderer;

        [SerializeField]
        [Tooltip("Used while opacity is 100%.")]
        Material m_OpaqueMaterial;

        [SerializeField]
        [Tooltip("Used while opacity is below 100%.")]
        Material m_TransparentMaterial;

        [SerializeField]
        [Tooltip("Optional floor; always transparent, in the shell's colour.")]
        Renderer m_FloorRenderer;

        [SerializeField]
        [Tooltip("Transparent material for the floor.")]
        Material m_FloorMaterial;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip("The floor's opacity never goes above this, so the real floor always shows through.")]
        float m_MaxFloorOpacity = 0.3f;

        [Header("Chooser UI")]
        [SerializeField]
        Button m_ToggleButton;

        [SerializeField]
        TMP_Text m_ToggleLabel;

        [SerializeField]
        GameObject m_ChooserPanel;

        [SerializeField]
        Slider m_HueSlider;

        [SerializeField]
        Slider m_SaturationSlider;

        [SerializeField]
        Slider m_ValueSlider;

        [SerializeField]
        Slider m_OpacitySlider;

        [SerializeField]
        TMP_Text m_HueText;

        [SerializeField]
        TMP_Text m_SaturationText;

        [SerializeField]
        TMP_Text m_ValueText;

        [SerializeField]
        TMP_Text m_OpacityText;

        [SerializeField]
        Image m_Swatch;

        [SerializeField]
        [Tooltip("Starting hue, saturation, value and opacity (0-1 each).")]
        Vector4 m_DefaultHsva = new(0.58f, 0.25f, 0.85f, 0.6f);

        // w < 0 means "not set yet"; the server fills in the default when the segment spawns.
        readonly NetworkVariable<Vector4> m_Hsva = new(new Vector4(0f, 0f, 0f, -1f), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        readonly NetworkVariable<bool> m_ChooserOpen = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        Vector4 m_LocalHsva;
        bool m_LocalChooserOpen;
        float m_LastLocalEditTime = float.NegativeInfinity;

        Material m_OpaqueInstance;
        Material m_TransparentInstance;
        Material m_FloorInstance;

        Vector4 currentHsva => IsSpawned && m_Hsva.Value.w >= 0f ? m_Hsva.Value : m_LocalHsva;
        bool chooserOpen => IsSpawned ? m_ChooserOpen.Value : m_LocalChooserOpen;

        void Awake()
        {
            m_LocalHsva = m_DefaultHsva;
            if (m_OpaqueMaterial != null)
                m_OpaqueInstance = new Material(m_OpaqueMaterial);
            if (m_TransparentMaterial != null)
                m_TransparentInstance = new Material(m_TransparentMaterial);
            if (m_FloorMaterial != null)
                m_FloorInstance = new Material(m_FloorMaterial);

            foreach (var slider in new[] { m_HueSlider, m_SaturationSlider, m_ValueSlider, m_OpacitySlider })
            {
                if (slider == null)
                    continue;
                slider.minValue = 0f;
                slider.maxValue = 1f;
                slider.wholeNumbers = false;
                slider.onValueChanged.AddListener(OnSliderChanged);
            }

            if (m_ToggleButton != null)
                m_ToggleButton.onClick.AddListener(OnTogglePressed);

            ApplyColor(m_LocalHsva, true);
            ShowChooser(false);
        }

        ///<inheritdoc/>
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            m_Hsva.OnValueChanged += OnHsvaChanged;
            m_ChooserOpen.OnValueChanged += OnChooserOpenChanged;

            if (IsServer && m_Hsva.Value.w < 0f)
                m_Hsva.Value = m_LocalHsva;

            ApplyColor(currentHsva, true);
            ShowChooser(chooserOpen);
        }

        ///<inheritdoc/>
        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            m_Hsva.OnValueChanged -= OnHsvaChanged;
            m_ChooserOpen.OnValueChanged -= OnChooserOpenChanged;
        }

        ///<inheritdoc/>
        public override void OnDestroy()
        {
            base.OnDestroy();
            if (m_ToggleButton != null)
                m_ToggleButton.onClick.RemoveListener(OnTogglePressed);
            foreach (var slider in new[] { m_HueSlider, m_SaturationSlider, m_ValueSlider, m_OpacitySlider })
            {
                if (slider != null)
                    slider.onValueChanged.RemoveListener(OnSliderChanged);
            }

            if (m_OpaqueInstance != null)
                Destroy(m_OpaqueInstance);
            if (m_TransparentInstance != null)
                Destroy(m_TransparentInstance);
            if (m_FloorInstance != null)
                Destroy(m_FloorInstance);
        }

        /// <summary>
        /// Puts a texture on the walls and ceiling (null removes it). One image height wraps the whole profile and
        /// the image keeps its proportions along the tunnel; the chosen colour still tints it.
        /// </summary>
        public void SetWallTexture(Texture texture)
        {
            float aspect = texture != null && texture.height > 0 ? texture.width / (float)texture.height : 1f;
            // Mesh u is tunnel distance in profile lengths, so tiling 1/aspect keeps the image's proportions.
            // Offsetting by the segment's position along its axis lines the image up across joined segments.
            float tiling = 1f / aspect;
            float offset = 0f;
            var geometry = m_ShellRenderer != null ? m_ShellRenderer.GetComponent<TunnelSegmentGeometry>() : null;
            if (geometry != null && geometry.perimeter > 0f)
            {
                var frame = geometry.transform;
                float nearEnd = Vector3.Dot(frame.position, frame.forward) - geometry.length * 0.5f;
                offset = Mathf.Repeat(nearEnd / geometry.perimeter * tiling, 1f);
            }

            var scaleOffset = new Vector4(tiling, 1f, offset, 0f);
            foreach (var material in new[] { m_OpaqueInstance, m_TransparentInstance })
            {
                if (material == null)
                    continue;
                material.SetTexture("_BaseMap", texture);
                material.SetVector("_BaseMap_ST", scaleOffset);
            }
        }

        /// <summary>Hue, saturation, value and opacity (0-1 each) of this segment.</summary>
        public Vector4 colorHsva => currentHsva;

        /// <summary>
        /// Gives a segment that hasn't been spawned yet its starting colour (e.g. the colour of the segment that
        /// added it); the server shares it with everyone when the segment spawns.
        /// </summary>
        public void SetStartColor(Vector4 hsva)
        {
            m_LocalHsva = hsva;
            ApplyColor(hsva, true);
        }

        /// <summary>Opens or closes the colour chooser (for everyone, when connected).</summary>
        public void SetChooserOpen(bool open)
        {
            if (chooserOpen != open)
                OnTogglePressed();
        }

        void OnTogglePressed()
        {
            if (IsSpawned)
            {
                ToggleChooserRpc();
            }
            else
            {
                m_LocalChooserOpen = !m_LocalChooserOpen;
                ShowChooser(m_LocalChooserOpen);
            }
        }

        void OnSliderChanged(float _)
        {
            var hsva = new Vector4(m_HueSlider.value, m_SaturationSlider.value, m_ValueSlider.value, m_OpacitySlider.value);
            m_LocalHsva = hsva;
            m_LastLocalEditTime = Time.unscaledTime;
            ApplyColor(hsva, false);

            if (IsSpawned)
                SetColorRpc(hsva);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void ToggleChooserRpc()
        {
            m_ChooserOpen.Value = !m_ChooserOpen.Value;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SetColorRpc(Vector4 hsva)
        {
            m_Hsva.Value = hsva;
        }

        void OnHsvaChanged(Vector4 previous, Vector4 current)
        {
            // Our own slider drag echoes back from the server slightly late; don't let it pull the handles back.
            if (Time.unscaledTime - m_LastLocalEditTime < k_RemoteUpdateHoldOff)
                return;

            m_LocalHsva = current;
            ApplyColor(current, true);
        }

        void OnChooserOpenChanged(bool previous, bool current) => ShowChooser(current);

        void ShowChooser(bool open)
        {
            if (m_ChooserPanel != null)
                m_ChooserPanel.SetActive(open);
            if (m_ToggleLabel != null)
                m_ToggleLabel.text = open ? "Hide Colors" : "Colors";
        }

        void ApplyColor(Vector4 hsva, bool updateSliders)
        {
            var color = Color.HSVToRGB(hsva.x, hsva.y, hsva.z);
            color.a = hsva.w;

            var material = hsva.w >= k_OpaqueThreshold ? m_OpaqueInstance : m_TransparentInstance;
            if (material != null && m_ShellRenderer != null)
            {
                material.SetColor("_BaseColor", color);
                m_ShellRenderer.sharedMaterial = material;
            }

            if (m_FloorInstance != null && m_FloorRenderer != null)
            {
                var floorColor = color;
                floorColor.a = Mathf.Min(hsva.w, m_MaxFloorOpacity);
                m_FloorInstance.SetColor("_BaseColor", floorColor);
                m_FloorRenderer.sharedMaterial = m_FloorInstance;
            }

            if (m_Swatch != null)
                m_Swatch.color = color;

            if (updateSliders)
            {
                if (m_HueSlider != null)
                    m_HueSlider.SetValueWithoutNotify(hsva.x);
                if (m_SaturationSlider != null)
                    m_SaturationSlider.SetValueWithoutNotify(hsva.y);
                if (m_ValueSlider != null)
                    m_ValueSlider.SetValueWithoutNotify(hsva.z);
                if (m_OpacitySlider != null)
                    m_OpacitySlider.SetValueWithoutNotify(hsva.w);
            }

            if (m_HueText != null)
                m_HueText.text = $"{hsva.x * 360f:0}°";
            if (m_SaturationText != null)
                m_SaturationText.text = $"{hsva.y * 100f:0}%";
            if (m_ValueText != null)
                m_ValueText.text = $"{hsva.z * 100f:0}%";
            if (m_OpacityText != null)
                m_OpacityText.text = $"{hsva.w * 100f:0}%";
        }
    }
}
