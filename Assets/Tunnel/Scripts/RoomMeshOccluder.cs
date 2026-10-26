using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Makes the real room hide virtual things behind it, using the room mesh the headset made in Space Setup (Quest 3
/// and 3S): every mesh chunk is drawn with an invisible depth-only material, so the tunnel's arch above a low real
/// ceiling, or a segment running through a real wall, is hidden there. Unlike the live depth occlusion, the room
/// mesh is complete and steady at any distance; it doesn't know about things moved after Space Setup or about people,
/// which the live depth still handles nearby.
/// </summary>
[RequireComponent(typeof(ARMeshManager))]
public class RoomMeshOccluder : MonoBehaviour
{
    [SerializeField]
    Material m_OccluderMaterial;

    ARMeshManager m_Meshes;

    void Awake() => m_Meshes = GetComponent<ARMeshManager>();

    void OnEnable() => m_Meshes.meshesChanged += OnMeshesChanged;

    void OnDisable() => m_Meshes.meshesChanged -= OnMeshesChanged;

    void OnMeshesChanged(ARMeshesChangedEventArgs args)
    {
        foreach (var mesh in args.added)
            Setup(mesh);
        foreach (var mesh in args.updated)
            Setup(mesh);
        if (args.added.Count > 0)
            Debug.Log($"[Occlusion] room mesh: {m_Meshes.meshes.Count} piece(s) hide what's behind real walls and ceilings");
    }

    void Setup(MeshFilter mesh)
    {
        if (!mesh.TryGetComponent<MeshRenderer>(out var meshRenderer))
            meshRenderer = mesh.gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = m_OccluderMaterial;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        // The room mesh is for occlusion only, not for physics or pointing.
        if (mesh.TryGetComponent<MeshCollider>(out var meshCollider))
            meshCollider.enabled = false;
    }
}
