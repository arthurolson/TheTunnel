using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// A molecule's physics. It weighs what its volume would at the density of water: the volume enclosed by its
/// surface (worked out in the Editor, see Tunnel > Molecules > Update Molecule Library) at its current size, so a
/// larger molecule, or one made larger, is heavier. When something pushes it (another molecule, a container, a held
/// molecule) it moves off in that direction and bounces, slowing down over a second or two; inside a Container it
/// slows by that container's viscosity instead (see <see cref="TunnelContainer.viscosity"/>), and the container's
/// temperature keeps it moving (see <see cref="ApplyHeat"/>). With the host's palm
/// Gravity button on (which switches <see cref="Physics.gravity"/> for everyone) it falls like the other toys
/// (scaled like their small-scale physics). A molecule just placed from the TS Menu stays frozen on the wall until
/// someone first grabs it or something pushes it, connected or not (in a session the network interactable's spawn
/// lock does the same for the headset that simulates it). While the TS Menu's Delete is on, grabbing it deletes it.
/// Inside a Container's mixed reality box it passes through the tunnel's walls (the box is set into them).
/// In a session only its owner simulates it and the others follow (see <see cref="FollowOwner"/>).
/// While the 3D movie of the container it is in is paused (see <see cref="TunnelMovie"/>), it stands still where it is
/// and goes on as it was when the movie plays again; it can still be moved by hand. Antibodies bound to a capsid add
/// their volume, and so their mass, to it (see <see cref="extraVolume"/>).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class MoleculeGravity : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Fraction of gravity applied, as for the other sandbox toys (SmallScalePhysicsModifier).")]
    float m_GravityScale = 0.5f;

    [SerializeField]
    [Tooltip("Drag while gravity is on, so it falls instead of sinking slowly.")]
    float m_FallingDrag = 0.2f;

    [SerializeField]
    float m_FallingAngularDrag = 1f;

    [SerializeField]
    [Tooltip("Drag while gravity is off: how quickly a pushed molecule slows down (per second).")]
    float m_FloatingDrag = 1f;

    [SerializeField]
    float m_FloatingAngularDrag = 1f;

    [SerializeField]
    [Tooltip("Volume enclosed by the molecule's surface, in the toy's own units (set by the molecule library).")]
    float m_Volume;

    [SerializeField]
    [Tooltip("Mass per cubic meter of that volume (kg): water's.")]
    float m_Density = 1000f;

    [SerializeField]
    [Range(0f, 1f)]
    [Tooltip("How much of its speed a molecule keeps when it bounces off something.")]
    float m_Bounciness = 0.6f;

    [SerializeField]
    [Tooltip("Thermal speed at room temperature (a container's 10) of a molecule weighing the reference mass (m/s).")]
    float m_RoomSpeed = 0.25f;

    [SerializeField]
    [Tooltip("Mass with that speed (kg): 2PLV's at x1. Lighter molecules move faster, heavier slower (speed as 1/sqrt(mass)).")]
    float m_ReferenceMass = 0.0766f;

    [SerializeField]
    [Tooltip("No molecule moves faster than this from heat alone (m/s).")]
    float m_MaxThermalSpeed = 1f;

    [SerializeField]
    [Tooltip("Random motion: how often (per second) the medium turns it, at least (more with more viscosity).")]
    float m_MinJostleRate = 0.5f;

    [SerializeField]
    [Tooltip("Random motion: its spin from heat, as a fraction of the physical (equipartition) spin, which looks frantic at these sizes.")]
    float m_SpinScale = 0.25f;

    [SerializeField]
    [Tooltip("Random motion: no molecule spins faster than this from heat alone (radians per second, per axis).")]
    float m_MaxThermalSpin = 6f;

    [SerializeField]
    [Tooltip("Ballistic motion: how quickly (per second) its speed returns to the thermal speed after a collision.")]
    float m_SpeedRecovery = 1.5f;

    public float volume
    {
        get => m_Volume;
        set => m_Volume = value;
    }

    /// <summary>
    /// Volume carried along besides its own (toy units at its own size): antibodies bound to a capsid, which weigh
    /// as much as they did on their own.
    /// </summary>
    public float extraVolume
    {
        get => m_ExtraVolume;
        set
        {
            m_ExtraVolume = Mathf.Max(0f, value);
            UpdateMass();
        }
    }

    /// <summary>Every molecule in the scene.</summary>
    public static readonly System.Collections.Generic.List<MoleculeGravity> all = new();

    /// <summary>Whether it is parked where it was placed, frozen until first grabbed or pushed.</summary>
    public bool isParked => m_Rigidbody != null && m_Rigidbody.constraints != RigidbodyConstraints.None;

    /// <summary>Whether this headset moves it (it isn't networked, or this player owns it).</summary>
    public bool simulatedHere => m_NetworkPhysics == null || !m_NetworkPhysics.IsSpawned || m_NetworkPhysics.IsOwner;

    public Rigidbody body => m_Rigidbody;

    /// <summary>Whether someone is holding it here.</summary>
    public bool isHeld => m_Grab != null && m_Grab.isSelected;

    static PhysicsMaterial s_Material;

    Rigidbody m_Rigidbody;
    XRGrabInteractable m_Grab;
    NetworkPhysicsInteractable m_NetworkPhysics;
    bool? m_GravityApplied;
    Vector3 m_MassForScale;
    Collider[] m_Colliders;
    bool m_PassingWalls;
    int m_WallsVersion = -1;
    float m_ExtraVolume;
    bool m_Paused;
    Vector3 m_PausedVelocity, m_PausedSpin;

    void Awake()
    {
        m_Rigidbody = GetComponent<Rigidbody>();
        m_Rigidbody.useGravity = false; // applied below at the chosen scale
        m_Rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        TryGetComponent(out m_NetworkPhysics);

        // Bouncy and nearly frictionless, so a push sends it off rather than rolling it along.
        if (s_Material == null)
            s_Material = new PhysicsMaterial("Molecule")
            {
                bounciness = m_Bounciness,
                bounceCombine = PhysicsMaterialCombine.Maximum,
                dynamicFriction = 0.05f,
                staticFriction = 0.05f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
            };
        m_Colliders = GetComponentsInChildren<Collider>(true);
        foreach (var collider in m_Colliders)
            collider.sharedMaterial = s_Material;
        UpdateMass();

        if (TryGetComponent(out m_Grab))
            m_Grab.selectEntered.AddListener(OnGrabbed);
    }

    void OnEnable() => all.Add(this);

    void OnDisable() => all.Remove(this);

    /// <summary>
    /// Call when colliders are added to it (antibodies bound to a capsid): they get the molecules' bounce and pass
    /// through the tunnel's walls with the rest of it.
    /// </summary>
    public void CollidersChanged()
    {
        m_Colliders = GetComponentsInChildren<Collider>(true);
        foreach (var collider in m_Colliders)
            if (!collider.isTrigger)
                collider.sharedMaterial = s_Material;
        m_WallsVersion = -1;
        if (m_PassingWalls)
            TunnelSegmentGeometry.IgnoreWalls(m_Colliders, true);
    }

    void Start()
    {
        if (TryGetComponent(out Unity.Netcode.NetworkObject networkObject))
            Debug.Log($"[Molecules] {name} appeared at {transform.position:F2} (networked {networkObject.IsSpawned}, owner {networkObject.OwnerClientId}), mass {m_Rigidbody.mass:G3} kg");

        // Parked where it was placed until it's first picked up or pushed, unless whoever made it (e.g. the table's
        // object dispenser) asked for it to move freely straight away.
        if (m_NetworkPhysics != null && !m_NetworkPhysics.spawnLocked)
            return;
        m_Rigidbody.constraints = RigidbodyConstraints.FreezeAll;
    }

    void OnDestroy()
    {
        if (m_Grab != null)
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (TunnelMenu.deleteMode)
        {
            TunnelMenu.DeleteGrabbed(this, m_Grab);
            return;
        }

        m_Rigidbody.constraints = RigidbodyConstraints.None;
    }

    void OnCollisionEnter(Collision collision)
    {
        // Something moving into a parked molecule sets it free, so the push moves it.
        if (m_Rigidbody.constraints == RigidbodyConstraints.None || collision.rigidbody == null)
            return;
        m_Rigidbody.constraints = RigidbodyConstraints.None;
        if (m_NetworkPhysics != null)
            m_NetworkPhysics.ReleaseSpawnLock();
    }

    /// <summary>Mass from the enclosed volume at the molecule's current size.</summary>
    void UpdateMass()
    {
        m_MassForScale = transform.lossyScale;
        if (m_Volume <= 0f)
            return;
        float scale = Mathf.Abs(m_MassForScale.x * m_MassForScale.y * m_MassForScale.z);
        m_Rigidbody.mass = Mathf.Max(m_Density * (m_Volume + m_ExtraVolume) * scale, 1e-5f);
    }

    /// <summary>
    /// This molecule's thermal speed in a container: its kinetic energy is proportional to the temperature
    /// (equipartition), so the speed goes as the square root of temperature over mass.
    /// </summary>
    float ThermalSpeed(float temperature)
    {
        float mass = Mathf.Max(m_Rigidbody.mass, 1e-6f);
        float speed = m_RoomSpeed * Mathf.Sqrt(temperature / TunnelContainer.k_RoomTemperature * m_ReferenceMass / mass);
        return Mathf.Min(speed, m_MaxThermalSpeed);
    }

    /// <summary>
    /// Heat in a container. Random motion is Brownian: the medium's own molecules keep jostling it, an
    /// Ornstein-Uhlenbeck process that slows it at the jostling rate (the medium's drag, but at least a minimum) while
    /// random kicks keep its average speed at the thermal speed, so its direction keeps wandering; with more viscosity
    /// it turns more often and wanders less far. Ballistic motion is a gas: it flies straight on, its speed drawn back
    /// to the thermal speed, and only a collision (another molecule, the container's walls) changes its direction.
    /// In random motion the jostling turns it too: its spin about each of its principal axes is the same kind of
    /// process, with the thermal spin of equipartition (the same energy per axis as its motion, so its spin about an
    /// axis goes as the square root of mass over moment of inertia: small or long thin molecules spin faster about
    /// their thin axis), scaled down so it doesn't look frantic.
    /// </summary>
    void ApplyHeat(TunnelContainer container, float drag, float angularDrag)
    {
        float speed = ThermalSpeed(container.temperature);
        float dt = Time.fixedDeltaTime;
        var velocity = m_Rigidbody.linearVelocity;
        if (container.motion == TunnelContainer.Motion.Random)
        {
            float rate = Mathf.Max(drag, m_MinJostleRate);
            float keep = Mathf.Exp(-rate * dt);
            float kick = speed / Mathf.Sqrt(3f) * Mathf.Sqrt(1f - keep * keep); // per axis
            velocity = velocity * keep + new Vector3(Gaussian(), Gaussian(), Gaussian()) * kick;
            ApplySpin(speed / Mathf.Sqrt(3f), Mathf.Max(angularDrag, m_MinJostleRate), dt);
        }
        else
        {
            float current = velocity.magnitude;
            var direction = current > 1e-4f ? velocity / current : Random.onUnitSphere;
            current += (speed - current) * (1f - Mathf.Exp(-m_SpeedRecovery * dt));
            velocity = direction * current;
        }

        m_Rigidbody.linearVelocity = velocity;
    }

    // Brownian spin, per principal axis: thermal spin = per-axis thermal speed * sqrt(mass / moment of inertia).
    void ApplySpin(float axisSpeed, float rate, float dt)
    {
        var inertia = m_Rigidbody.inertiaTensor;
        if (inertia.x <= 0f || inertia.y <= 0f || inertia.z <= 0f)
            return;
        float mass = Mathf.Max(m_Rigidbody.mass, 1e-6f);
        var axes = m_Rigidbody.rotation * m_Rigidbody.inertiaTensorRotation;
        var spin = Quaternion.Inverse(axes) * m_Rigidbody.angularVelocity;
        float keep = Mathf.Exp(-rate * dt);
        float kick = Mathf.Sqrt(1f - keep * keep);
        spin.x = spin.x * keep + Gaussian() * kick * Mathf.Min(m_SpinScale * axisSpeed * Mathf.Sqrt(mass / inertia.x), m_MaxThermalSpin);
        spin.y = spin.y * keep + Gaussian() * kick * Mathf.Min(m_SpinScale * axisSpeed * Mathf.Sqrt(mass / inertia.y), m_MaxThermalSpin);
        spin.z = spin.z * keep + Gaussian() * kick * Mathf.Min(m_SpinScale * axisSpeed * Mathf.Sqrt(mass / inertia.z), m_MaxThermalSpin);
        m_Rigidbody.angularVelocity = axes * spin;
    }

    static float Gaussian()
    {
        // Box-Muller.
        float u = 1f - Random.value, v = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u)) * Mathf.Cos(2f * Mathf.PI * v);
    }

    /// <summary>
    /// In a session only the molecule's owner (the player who last held it, or the host) simulates it; everyone
    /// else's copy is kinematic and just follows the network, so all players see the same motion instead of each
    /// headset's physics bouncing its own copy around. While held, the interaction toolkit has the say.
    /// While its container's movie is paused, its owner holds it still too, and lets it go on as it was afterwards.
    /// </summary>
    void FollowOwner(bool paused)
    {
        if (m_Grab != null && m_Grab.isSelected)
        {
            // What the hand does with it is what it does next.
            m_Paused = false;
            m_PausedVelocity = m_PausedSpin = Vector3.zero;
            return;
        }
        bool follower = m_NetworkPhysics != null && m_NetworkPhysics.IsSpawned && !m_NetworkPhysics.IsOwner;
        bool pause = paused && !follower;
        if (pause != m_Paused)
        {
            if (pause && !m_Rigidbody.isKinematic)
            {
                m_PausedVelocity = m_Rigidbody.linearVelocity;
                m_PausedSpin = m_Rigidbody.angularVelocity;
            }
            else if (!pause)
            {
                // Going on: moving again, as it was when paused.
                m_Rigidbody.isKinematic = follower;
                if (!follower)
                {
                    m_Rigidbody.linearVelocity = m_PausedVelocity;
                    m_Rigidbody.angularVelocity = m_PausedSpin;
                }
                m_PausedVelocity = m_PausedSpin = Vector3.zero;
            }
            m_Paused = pause;
        }

        bool kinematic = follower || m_Paused;
        if (m_Rigidbody.isKinematic == kinematic)
            return;
        if (kinematic)
        {
            m_Rigidbody.linearVelocity = Vector3.zero;
            m_Rigidbody.angularVelocity = Vector3.zero;
        }
        m_Rigidbody.isKinematic = kinematic;
    }

    void FixedUpdate()
    {
        // Inside a container its viscosity scales the drag (1 outside them all), and its heat may move it.
        var container = TunnelContainer.At(m_Rigidbody.worldCenterOfMass);
        FollowOwner(container != null && container.movie != null && container.movie.isPaused);
        if (transform.lossyScale != m_MassForScale)
            UpdateMass();

        bool gravityOn = Physics.gravity.sqrMagnitude > 0.01f;
        if (gravityOn != m_GravityApplied)
        {
            m_GravityApplied = gravityOn;
            if (gravityOn)
                m_Rigidbody.WakeUp();
        }

        float viscosity = container != null ? container.viscosity : 1f;
        // In a mixed reality box (set into a tunnel wall, or on its way) the tunnel's walls don't touch it.
        bool passWalls = container != null && container.isMixedReality;
        if (passWalls != m_PassingWalls || (passWalls && m_WallsVersion != TunnelSegmentGeometry.collidersVersion))
        {
            m_PassingWalls = passWalls;
            m_WallsVersion = TunnelSegmentGeometry.collidersVersion;
            TunnelSegmentGeometry.IgnoreWalls(m_Colliders, passWalls);
        }
        float drag = (gravityOn ? m_FallingDrag : m_FloatingDrag) * viscosity;
        float angularDrag = (gravityOn ? m_FallingAngularDrag : m_FloatingAngularDrag) * viscosity;
        // Only the player whose headset moves it (its owner) heats it; the others see it through the network.
        bool heated = container != null && container.temperature > 0f && !m_Rigidbody.isKinematic && simulatedHere;
        if (heated)
        {
            // Heat moves a molecule still parked where it was placed, as a push does.
            if (m_Rigidbody.constraints != RigidbodyConstraints.None)
            {
                m_Rigidbody.constraints = RigidbodyConstraints.None;
                if (m_NetworkPhysics != null)
                    m_NetworkPhysics.ReleaseSpawnLock();
            }
            ApplyHeat(container, drag, angularDrag);
            drag = 0f; // the heat sets its speed, the drag included
            if (container.motion == TunnelContainer.Motion.Random)
                angularDrag = 0f; // and its spin
        }

        if (m_Rigidbody.linearDamping != drag)
            m_Rigidbody.linearDamping = drag;
        if (m_Rigidbody.angularDamping != angularDrag)
            m_Rigidbody.angularDamping = angularDrag;

        if (gravityOn && !m_Rigidbody.isKinematic)
            m_Rigidbody.AddForce(Physics.gravity * m_GravityScale, ForceMode.Acceleration);
    }
}
