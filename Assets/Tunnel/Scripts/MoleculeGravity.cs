using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using XRMultiplayer;

/// <summary>
/// A molecule's physics. It weighs what its volume would at the density of water: the volume enclosed by its
/// surface (worked out in the Editor, see Tunnel > Molecules > Update Molecule Library) at its current size, so a
/// larger molecule, or one made larger, is heavier. When something pushes it (another molecule, a container, a held
/// molecule) it moves off in that direction and bounces, slowing down over a second or two. With the host's palm
/// Gravity button on (which switches <see cref="Physics.gravity"/> for everyone) it falls like the other toys
/// (scaled like their small-scale physics). A molecule just placed from the TS Menu stays frozen on the wall until
/// someone first grabs it or something pushes it, connected or not (in a session the network interactable's spawn
/// lock does the same for the headset that simulates it). While the TS Menu's Delete is on, grabbing it deletes it.
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

    public float volume
    {
        get => m_Volume;
        set => m_Volume = value;
    }

    static PhysicsMaterial s_Material;

    Rigidbody m_Rigidbody;
    XRGrabInteractable m_Grab;
    NetworkPhysicsInteractable m_NetworkPhysics;
    bool? m_GravityApplied;
    Vector3 m_MassForScale;

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
        foreach (var collider in GetComponentsInChildren<Collider>(true))
            collider.sharedMaterial = s_Material;
        UpdateMass();

        if (TryGetComponent(out m_Grab))
            m_Grab.selectEntered.AddListener(OnGrabbed);
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
            TunnelMenu.Delete(gameObject);
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
        m_Rigidbody.mass = Mathf.Max(m_Density * m_Volume * scale, 1e-5f);
    }

    void FixedUpdate()
    {
        if (transform.lossyScale != m_MassForScale)
            UpdateMass();

        bool gravityOn = Physics.gravity.sqrMagnitude > 0.01f;
        if (gravityOn != m_GravityApplied)
        {
            m_GravityApplied = gravityOn;
            m_Rigidbody.linearDamping = gravityOn ? m_FallingDrag : m_FloatingDrag;
            m_Rigidbody.angularDamping = gravityOn ? m_FallingAngularDrag : m_FloatingAngularDrag;
            if (gravityOn)
                m_Rigidbody.WakeUp();
        }

        if (gravityOn && !m_Rigidbody.isKinematic)
            m_Rigidbody.AddForce(Physics.gravity * m_GravityScale, ForceMode.Acceleration);
    }
}
