using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// A little person you drive with the left controller's thumbstick: a 3D figure made from a real person (see
/// FigureBuilder), called up from the TS Menu's New figure button in front of whoever pressed it. Push the stick and
/// the figure walks the way you push, as seen from where you stand (all the way over and it runs); press X (or
/// Space in the Editor) and it jumps. It walks on the room's floor and tables (the headset's Space Setup surfaces) and
/// on anything solid in the tunnel, steps up small ledges, and falls off edges onto whatever is below.
/// Pick it up with either hand to move it somewhere else; two hands make it bigger or smaller, and it walks and jumps
/// in proportion. Whoever called it up drives it; whoever picks it up after that drives it from then on.
/// Everyone sees it walk: the driver's headset moves it and the network carries the motion; every headset plays the
/// walk, run and jump from how it is moving, so no animation needs sending. Works the same when not connected.
/// </summary>
public class TunnelFigure : NetworkBehaviour
{
    [SerializeField]
    [Tooltip("Walking speed, in the figure's own heights per second.")]
    float m_WalkSpeed = 0.8f;

    [SerializeField]
    [Tooltip("Running speed (stick pushed all the way), in heights per second.")]
    float m_RunSpeed = 2.2f;

    [SerializeField]
    [Tooltip("How high it jumps, in its own heights.")]
    float m_JumpHeight = 0.3f;

    [SerializeField]
    [Tooltip("Degrees per second it turns to face the way it's going.")]
    float m_TurnSpeed = 540f;

    [SerializeField]
    [Tooltip("The tallest ledge it steps up onto, in its own heights.")]
    float m_StepHeight = 0.25f;

    [SerializeField]
    [Tooltip("Its height at scale 1 (meters), measured by the builder.")]
    float m_BaseHeight = 1.75f;

    [SerializeField]
    [Tooltip("Stick push below this is ignored.")]
    float m_DeadZone = 0.15f;

    // What drives it: the left stick (and a gamepad or WASD in the Editor), X (or Space) to jump.
    InputAction m_Move;
    InputAction m_Jump;

    Animator m_Animator;
    XRGrabInteractable m_Grab;
    Collider[] m_OwnColliders;

    float m_VerticalSpeed;
    bool m_Grounded;
    float m_PutDownHeight; // where it stands when nothing known is under it
    bool m_JumpQueued;

    // What every headset reads to animate it (from how it moves, so remote copies animate the same way).
    Vector3 m_LastPosition;
    float m_SmoothedSpeed;
    float m_SmoothedVertical;

    static readonly int k_Speed = Animator.StringToHash("Speed");
    static readonly int k_Grounded = Animator.StringToHash("Grounded");
    static readonly int k_VerticalSpeed = Animator.StringToHash("VerticalSpeed");

    static readonly List<TunnelFigure> s_All = new();
    static ARPlaneManager s_Planes;

    /// <summary>The figure this headset's stick drives now (the last one called up for or picked up by this player).</summary>
    public static TunnelFigure driven { get; private set; }

    /// <summary>Every figure in the tunnel.</summary>
    public static IReadOnlyList<TunnelFigure> all => s_All;

    /// <summary>Whether this headset moves it (it isn't networked, or this player owns it).</summary>
    public bool drivenHere => !IsSpawned || IsOwner;

    /// <summary>Its height now (meters).</summary>
    public float height => m_BaseHeight * transform.lossyScale.y;

    /// <summary>Set by FigureBuilder: its height at scale 1.</summary>
    public void Configure(float baseHeight) => m_BaseHeight = baseHeight;

    void Awake()
    {
        m_Animator = GetComponentInChildren<Animator>(true); // on the body, the model's own root
        if (m_Animator != null)
            m_Animator.applyRootMotion = false;
        m_Grab = GetComponent<XRGrabInteractable>();
        m_OwnColliders = GetComponentsInChildren<Collider>(true);

        m_Move = new InputAction("Figure Move", InputActionType.Value, expectedControlType: "Vector2");
        m_Move.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
        m_Move.AddBinding("<Gamepad>/leftStick");
        m_Move.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
        m_Jump = new InputAction("Figure Jump", InputActionType.Button);
        m_Jump.AddBinding("<XRController>{LeftHand}/{PrimaryButton}");
        m_Jump.AddBinding("<Gamepad>/buttonSouth");
        m_Jump.AddBinding("<Keyboard>/space");
    }

    void OnEnable()
    {
        s_All.Add(this);
        m_Move.Enable();
        m_Jump.Enable();
        m_LastPosition = transform.position;
        m_PutDownHeight = transform.position.y;
        if (m_Grab != null)
        {
            m_Grab.selectEntered.AddListener(OnGrabbed);
            m_Grab.selectExited.AddListener(OnReleased);
        }
    }

    void OnDisable()
    {
        s_All.Remove(this);
        m_Move.Disable();
        m_Jump.Disable();
        if (driven == this)
            driven = null;
        if (m_Grab != null)
        {
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
            m_Grab.selectExited.RemoveListener(OnReleased);
        }
    }

    void OnDestroy()
    {
        m_Move?.Dispose();
        m_Jump?.Dispose();
    }

    void Start()
    {
        if (drivenHere)
            driven = this;
        Debug.Log($"[Figure] {name} appeared at {transform.position:F2}, {height:F2} m tall " +
            $"({(IsSpawned ? (IsOwner ? "for everyone, driven here" : "for everyone, driven by another player") : "not connected, here only")})");
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        driven = this;
        Debug.Log($"[Figure] {name}: this player drives it now");
    }

    void OnGrabbed(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs args)
    {
        // Picking it up makes it the one this player drives (ownership follows the grab, see NetworkBaseInteractable).
        driven = this;
        m_VerticalSpeed = 0f;
    }

    void OnReleased(UnityEngine.XR.Interaction.Toolkit.SelectExitEventArgs args)
    {
        m_VerticalSpeed = 0f;
        m_Grounded = false; // it settles onto whatever is under it
        m_PutDownHeight = transform.position.y;
        Debug.Log($"[Figure] {name} put down at {transform.position:F2}, {height:F2} m tall");
    }

    bool held => m_Grab != null && m_Grab.isSelected;

    void Update()
    {
        if (drivenHere && !held)
            Drive(Time.deltaTime);
        Animate(Time.deltaTime);
    }

    void Drive(float dt)
    {
        float h = Mathf.Max(0.01f, height);
        float scale = h / Mathf.Max(0.01f, m_BaseHeight);

        // The stick, turned to the room as the driver sees it.
        Vector2 stick = driven == this ? m_Move.ReadValue<Vector2>() : Vector2.zero;
        if (driven == this && m_Jump.WasPressedThisFrame())
            m_JumpQueued = true;
        float push = Mathf.Clamp01((stick.magnitude - m_DeadZone) / (1f - m_DeadZone));
        Vector3 move = Vector3.zero;
        if (push > 0f)
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            Vector3 forward = head != null ? Vector3.ProjectOnPlane(head.forward, Vector3.up) : Vector3.forward;
            if (forward.sqrMagnitude < 1e-4f)
                forward = head != null ? Vector3.ProjectOnPlane(head.up, Vector3.up) : Vector3.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 direction = (forward * stick.y + right * stick.x).normalized;
            // Gentle push walks; the last third of the stick blends into a run.
            float speed = Mathf.Lerp(m_WalkSpeed, m_RunSpeed, Mathf.InverseLerp(0.7f, 1f, push)) * h * Mathf.Min(push / 0.7f, 1f);
            move = direction * speed * dt;
            var face = Quaternion.LookRotation(direction, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, face, m_TurnSpeed * dt);
        }

        // Gravity scaled to its size, so a small figure falls and jumps like a person would, just smaller.
        float gravity = 9.81f * scale;
        Vector3 position = transform.position + move;
        if (m_Grounded && m_JumpQueued)
        {
            m_VerticalSpeed = Mathf.Sqrt(2f * gravity * m_JumpHeight * h);
            m_Grounded = false;
            Debug.Log($"[Figure] {name} jumps");
        }
        m_JumpQueued = false;
        m_VerticalSpeed -= gravity * dt;
        position.y += m_VerticalSpeed * dt;

        // The ground: the highest surface under its feet that it can step onto; over nothing known (no room surfaces
        // yet, or in the Editor), the height it was put down at.
        float step = m_StepHeight * h;
        float ground = TryGround(position, transform.position.y + step, out float found) ? found : m_PutDownHeight;
        if (position.y <= ground + 0.001f * h)
        {
            position.y = ground;
            m_VerticalSpeed = 0f;
            m_Grounded = true;
        }
        else if (m_Grounded && m_VerticalSpeed <= 0f && position.y - ground < step)
        {
            // Walking down a small step: keep to the ground instead of hopping.
            position.y = ground;
            m_VerticalSpeed = 0f;
        }
        else
            m_Grounded = false;

        transform.position = position;
    }

    /// <summary>
    /// The highest walkable height under a point and no higher than <paramref name="top"/>: solid colliders below
    /// it (not its own, not triggers), and the room's horizontal Space Setup surfaces (floor, tables) it stands over.
    /// </summary>
    bool TryGround(Vector3 at, float top, out float ground)
    {
        ground = float.NegativeInfinity;
        var origin = new Vector3(at.x, top, at.z);
        var hits = Physics.RaycastAll(origin, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
        {
            if (IsOwn(hit.collider) || hit.normal.y < 0.7f)
                continue;
            if (hit.collider.attachedRigidbody != null && !hit.collider.attachedRigidbody.isKinematic)
                continue; // loose molecules: it doesn't stand on things that roll
            ground = Mathf.Max(ground, hit.point.y);
        }

        if (s_Planes == null)
            s_Planes = FindAnyObjectByType<ARPlaneManager>();
        if (s_Planes != null)
            foreach (var plane in s_Planes.trackables)
            {
                if (plane.alignment != PlaneAlignment.HorizontalUp)
                    continue;
                float y = plane.transform.position.y;
                if (y > top || y <= ground)
                    continue;
                if (Inside(plane, at))
                    ground = y;
            }
        return !float.IsNegativeInfinity(ground);
    }

    bool IsOwn(Collider c)
    {
        foreach (var own in m_OwnColliders)
            if (own == c)
                return true;
        return false;
    }

    // Whether a point lies over a plane's outline (the outline is in the plane's own x/z).
    static bool Inside(ARPlane plane, Vector3 world)
    {
        var boundary = plane.boundary;
        if (boundary.Length < 3)
            return false;
        var p = plane.transform.InverseTransformPoint(world);
        bool inside = false;
        for (int i = 0, j = boundary.Length - 1; i < boundary.Length; j = i++)
        {
            Vector2 a = boundary[i], b = boundary[j];
            if ((a.y > p.z) != (b.y > p.z) && p.x < (b.x - a.x) * (p.z - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    // Animator parameters from how it actually moved this frame, the same on every headset.
    void Animate(float dt)
    {
        if (dt <= 0f)
            return;
        var position = transform.position;
        var delta = position - m_LastPosition;
        m_LastPosition = position;
        float h = Mathf.Max(0.01f, height);
        bool grabbed = held;
        // In heights per second, so the walk matches the figure's size.
        float speed = grabbed ? 0f : new Vector2(delta.x, delta.z).magnitude / dt / h;
        float vertical = grabbed ? 0f : delta.y / dt / h;
        if (speed > 10f || Mathf.Abs(vertical) > 10f)
            speed = vertical = 0f; // a jump in place (put down, or the network catching up), not motion
        float k = 1f - Mathf.Exp(-dt * 12f);
        m_SmoothedSpeed = Mathf.Lerp(m_SmoothedSpeed, speed, k);
        m_SmoothedVertical = Mathf.Lerp(m_SmoothedVertical, vertical, k);
        bool grounded = grabbed || (drivenHere ? m_Grounded : Mathf.Abs(m_SmoothedVertical) < 0.15f);
        if (m_Animator == null || m_Animator.runtimeAnimatorController == null)
            return;
        m_Animator.SetFloat(k_Speed, m_SmoothedSpeed);
        m_Animator.SetFloat(k_VerticalSpeed, m_SmoothedVertical);
        m_Animator.SetBool(k_Grounded, grounded);
    }
}
