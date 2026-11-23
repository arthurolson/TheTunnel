using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// A little person you drive with the left controller's thumbstick: a 3D figure made from a real person (see
/// FigureBuilder), called up from the TS Menu's New figure button in front of whoever pressed it. Push the stick and
/// the figure walks the way you push, as seen from where you stand (all the way over and it runs); press X (or
/// Space in the Editor) and it jumps. It walks on the room's floor and tables (the headset's Space Setup surfaces) and
/// on anything solid in the tunnel, steps up small ledges, and falls off edges onto whatever is below.
/// The tunnel's walls and the Containers' walls stop it: it walks up to a wall and slides along it, bumps its head
/// on a box's lid when it jumps, and a box it is put into keeps it in (a mixed reality box's glass front too). Inside a
/// mixed reality box set into a tunnel wall, the tunnel's own walls don't touch it, as for the molecules in there.
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
    [Tooltip("How far its body reaches out from its middle, in its own heights: how close it gets to a wall.")]
    float m_BodyRadius = 0.12f;

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

    // What stops it walking: the tunnel's walls, floor and arch, and the Containers' walls (looked up once each).
    enum Solid : byte { No, Tunnel, Container }
    static readonly Dictionary<Collider, Solid> s_Solids = new();
    static readonly RaycastHit[] s_Hits = new RaycastHit[32];
    static readonly Collider[] s_Overlaps = new Collider[32];
    Collider m_LastBumped;
    float m_LastBumpLog;

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

        // Walls: first out of any it has ended up in (a box moved onto it, or put down across a wall), then along
        // the way it's going, sliding along whatever is in the way.
        bool passTunnel = InMixedRealityBox(transform.position, h);
        Vector3 position = Unstick(transform.position, h, passTunnel);
        position = Slide(position, move, h, passTunnel);

        // Gravity scaled to its size, so a small figure falls and jumps like a person would, just smaller.
        float gravity = 9.81f * scale;
        if (m_Grounded && m_JumpQueued)
        {
            m_VerticalSpeed = Mathf.Sqrt(2f * gravity * m_JumpHeight * h);
            m_Grounded = false;
            Debug.Log($"[Figure] {name} jumps");
        }
        m_JumpQueued = false;
        m_VerticalSpeed -= gravity * dt;
        float rise = m_VerticalSpeed * dt;
        if (rise > 0f && HeadRoom(position, rise, h, passTunnel, out float room))
        {
            rise = room; // its head hits a lid or the tunnel's arch: it stops going up and falls back
            m_VerticalSpeed = 0f;
        }
        position.y += rise;

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
            if (IsOwn(hit.collider) || Touchless(hit.collider) || hit.normal.y < 0.7f)
                continue; // (grab zones, like a box's edges and corner balls, aren't something to stand on)
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

    // The body as a capsule, from just above the tallest step it walks up (so steps don't stop it) to the top of its head.
    void Body(Vector3 feet, float h, out Vector3 low, out Vector3 high, out float radius)
    {
        radius = m_BodyRadius * h;
        low = feet + Vector3.up * (m_StepHeight * h + radius);
        high = feet + Vector3.up * Mathf.Max(h - radius, m_StepHeight * h + radius);
    }

    /// <summary>
    /// Moves the feet along <paramref name="move"/> until a wall is in the way, then slides along the wall with
    /// what's left of the move (a few times, so it can follow into a corner).
    /// </summary>
    Vector3 Slide(Vector3 feet, Vector3 move, float h, bool passTunnel)
    {
        float skin = 0.01f * h;
        for (int i = 0; i < 3; i++)
        {
            float distance = move.magnitude;
            if (distance < 1e-6f)
                break;
            var direction = move / distance;
            Body(feet, h, out var low, out var high, out float radius);
            int count = Physics.CapsuleCastNonAlloc(low, high, radius, direction, s_Hits, distance + skin, ~0, QueryTriggerInteraction.Ignore);
            RaycastHit nearest = default;
            bool blocked = false;
            for (int j = 0; j < count; j++)
            {
                var hit = s_Hits[j];
                // Starting overlaps (distance 0) are Unstick's; walls it is already moving away from don't stop it.
                if (hit.distance <= 0f || Vector3.Dot(hit.normal, direction) >= 0f || !Blocks(hit.collider, passTunnel))
                    continue;
                if (!blocked || hit.distance < nearest.distance)
                {
                    nearest = hit;
                    blocked = true;
                }
            }
            if (!blocked)
                return feet + move;

            float travel = Mathf.Max(0f, nearest.distance - skin);
            feet += direction * travel;
            NoteBump(nearest.collider);
            var normal = Vector3.ProjectOnPlane(nearest.normal, Vector3.up);
            if (normal.sqrMagnitude < 1e-6f)
                break;
            normal.Normalize();
            var left = direction * (distance - travel);
            move = left - normal * Mathf.Min(0f, Vector3.Dot(left, normal));
        }
        return feet;
    }

    /// <summary>Pushes the body sideways out of any wall it overlaps (the walls are thin boxes).</summary>
    Vector3 Unstick(Vector3 feet, float h, bool passTunnel)
    {
        Body(feet, h, out var low, out var high, out float radius);
        int count = Physics.OverlapCapsuleNonAlloc(low, high, radius, s_Overlaps, ~0, QueryTriggerInteraction.Ignore);
        var push = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            var wall = s_Overlaps[i];
            if (!Blocks(wall, passTunnel))
                continue;
            // The nearest point of the wall to the bottom, middle and top of the body.
            for (int k = 0; k < 3; k++)
            {
                var at = Vector3.Lerp(low, high, k * 0.5f) + push;
                var away = at - wall.ClosestPoint(at);
                float gap = away.magnitude;
                var sideways = Vector3.ProjectOnPlane(away, Vector3.up);
                if (gap >= radius || sideways.sqrMagnitude < 1e-8f)
                    continue; // clear of it, or over or under it (a lid or a floor), which isn't a sideways push
                push += sideways.normalized * (radius - gap);
            }
        }
        float most = radius; // at most a body's width a frame, so it can't be flung
        if (push.sqrMagnitude > most * most)
            push = push.normalized * most;
        if (push.sqrMagnitude > 1e-10f)
            NoteBump(null);
        return feet + push;
    }

    /// <summary>Whether its head would hit something going up by <paramref name="rise"/>, and how far it can go.</summary>
    bool HeadRoom(Vector3 feet, float rise, float h, bool passTunnel, out float room)
    {
        Body(feet, h, out _, out var high, out float radius);
        float skin = 0.01f * h;
        room = rise;
        bool hit = false;
        int count = Physics.SphereCastNonAlloc(high, radius * 0.8f, Vector3.up, s_Hits, rise + skin, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var h1 = s_Hits[i];
            if (h1.distance <= 0f || h1.normal.y > -0.3f || !Blocks(h1.collider, passTunnel))
                continue;
            room = Mathf.Min(room, Mathf.Max(0f, h1.distance - skin));
            hit = true;
        }
        if (hit)
            NoteBump(null);
        return hit;
    }

    // Whether it is standing in a mixed reality box, where the tunnel's walls don't touch it.
    static bool InMixedRealityBox(Vector3 feet, float h)
    {
        var container = TunnelContainer.At(feet + Vector3.up * (0.05f * h));
        return container != null && container.isMixedReality;
    }

    // Whether a collider stops it: one of the tunnel's walls (unless it's in a mixed reality box) or a Container's.
    static bool Blocks(Collider c, bool passTunnel)
    {
        var solid = SolidOf(c);
        return solid == Solid.Container || (solid == Solid.Tunnel && !passTunnel);
    }

    static Solid SolidOf(Collider c)
    {
        if (c == null)
            return Solid.No;
        if (s_Solids.TryGetValue(c, out var solid))
            return solid;
        if (s_Solids.Count > 4096)
            s_Solids.Clear(); // colliders come and go; start the lookups again now and then
        solid = Solid.No;
        if (!c.isTrigger && !Touchless(c))
        {
            var parent = c.transform.parent;
            if (parent != null && parent.name == "Colliders" && parent.parent != null && parent.parent.GetComponent<TunnelSegmentGeometry>() != null)
                solid = Solid.Tunnel; // a segment's wall, floor or arch boxes
            else if (c.gameObject.layer == 2 && c.GetComponentInParent<TunnelContainer>() != null)
                solid = Solid.Container; // a Container's walls (on Ignore Raycast)
        }
        s_Solids[c] = solid;
        return solid;
    }

    // Grab zones found by the hands and lasers but pushing nothing (a box's edges and corner balls, the figure's own).
    static bool Touchless(Collider c) => c.excludeLayers.value == ~0;

    // A line in the log when it first runs into something, so headset logs show it (at most one a second).
    void NoteBump(Collider wall)
    {
        if (wall == m_LastBumped || Time.unscaledTime - m_LastBumpLog < 1f)
            return;
        m_LastBumped = wall;
        m_LastBumpLog = Time.unscaledTime;
        if (wall != null)
            Debug.Log($"[Figure] {name} stopped by {(SolidOf(wall) == Solid.Tunnel ? "the tunnel's " + wall.name : "a container's wall")} at {transform.position:F2}");
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
