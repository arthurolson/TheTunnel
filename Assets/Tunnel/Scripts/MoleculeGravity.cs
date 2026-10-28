using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using XRMultiplayer;

/// <summary>
/// Lets a molecule follow the host's palm Gravity button (which switches <see cref="Physics.gravity"/> for everyone):
/// with gravity on it falls like the other toys (scaled like their small-scale physics) and its drift-stopping drag is
/// lifted; with gravity off it floats where it's left, held still by heavy drag. A molecule just placed from the TS
/// Menu stays frozen on the wall until someone first grabs it, connected or not (in a session the network
/// interactable's spawn lock does the same for the headset that simulates it).
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

    Rigidbody m_Rigidbody;
    XRGrabInteractable m_Grab;
    float m_FloatingDrag;
    float m_FloatingAngularDrag;
    bool? m_GravityApplied;

    void Awake()
    {
        m_Rigidbody = GetComponent<Rigidbody>();
        m_Rigidbody.useGravity = false; // applied below at the chosen scale
        m_FloatingDrag = m_Rigidbody.linearDamping;
        m_FloatingAngularDrag = m_Rigidbody.angularDamping;
    }

    void Start()
    {
        if (TryGetComponent(out Unity.Netcode.NetworkObject networkObject))
            Debug.Log($"[Molecules] {name} appeared at {transform.position:F2} (networked {networkObject.IsSpawned}, owner {networkObject.OwnerClientId})");

        // Parked where it was placed until it's first picked up, unless whoever made it (e.g. the table's object
        // dispenser) asked for it to move freely straight away.
        if (TryGetComponent(out NetworkPhysicsInteractable networkPhysics) && !networkPhysics.spawnLocked)
            return;
        m_Rigidbody.constraints = RigidbodyConstraints.FreezeAll;
        if (TryGetComponent(out m_Grab))
            m_Grab.selectEntered.AddListener(OnGrabbed);
    }

    void OnDestroy()
    {
        if (m_Grab != null)
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        m_Rigidbody.constraints = RigidbodyConstraints.None;
        m_Grab.selectEntered.RemoveListener(OnGrabbed);
    }

    void FixedUpdate()
    {
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
