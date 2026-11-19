using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Templates.MRTTabletopAssets;

/// <summary>
/// The TS Menu's Clear all (pressed twice): takes away, for everyone, everything placed in the tunnel that isn't on a
/// tunnel wall, so the room can start afresh:
///  - molecules and viruses, except those inside a box set into a wall;
///  - containers and splat-boxes, except those set into a wall;
///  - Viewers and browsers, except those hanging on a wall;
///  - signs, palettes (with their markers) and painting copies, except those lying on a wall;
///  - the spawn nozzle.
/// The tunnel, its menus, the eraser and people are never touched. The host does the clearing; works locally when
/// not connected.
/// </summary>
public static class TunnelClearAll
{
    /// <summary>How far from a wall's surface something still counts as lying on it (meters).</summary>
    const float k_OnWallDistance = 0.1f;

    /// <summary>What Clear all would take away now (each thing's top object).</summary>
    public static List<GameObject> Targets()
    {
        var targets = new List<GameObject>();
        var seen = new HashSet<GameObject>();
        void Add(Component part)
        {
            if (part == null)
                return;
            var root = part.GetComponentInParent<NetworkObject>(true);
            var go = root != null ? root.gameObject : part.gameObject;
            if (go.activeInHierarchy && seen.Add(go))
                targets.Add(go);
        }

        var inWall = new List<TunnelContainer>();
        foreach (var container in Object.FindObjectsByType<TunnelContainer>(FindObjectsSortMode.None))
        {
            if (container.isSetInWall)
                inWall.Add(container);
            else
                Add(container);
        }
        foreach (var molecule in Object.FindObjectsByType<MoleculeGravity>(FindObjectsSortMode.None))
        {
            bool kept = false;
            foreach (var box in inWall)
                if (box != null && box.Contains(molecule.transform.position))
                {
                    kept = true;
                    break;
                }
            if (!kept)
                Add(molecule);
        }
        foreach (var panel in Object.FindObjectsByType<BrowserPanel>(FindObjectsSortMode.None))
            if (!panel.isOnWall)
                Add(panel);
        foreach (var sign in Object.FindObjectsByType<TunnelSign>(FindObjectsSortMode.None))
            if (!sign.IsOnWall(out _))
                Add(sign);
        foreach (var palette in Object.FindObjectsByType<TunnelPalette>(FindObjectsSortMode.None))
            if (!LiesOnWall(palette.transform.position))
            {
                Add(palette);
                foreach (var marker in TunnelMarker.all)
                    if (marker != null && marker.palette == palette)
                        Add(marker);
            }
        foreach (var marker in TunnelMarker.all)
            if (marker != null && marker.palette == null)
                Add(marker);
        foreach (var copy in Object.FindObjectsByType<PaintingCopy>(FindObjectsSortMode.None))
            if (!LiesOnWall(copy.transform.position))
                Add(copy);
        foreach (var nozzle in Object.FindObjectsByType<SpawnNozzle>(FindObjectsSortMode.None))
            Add(nozzle);
        return targets;
    }

    /// <summary>Whether a point is within <see cref="k_OnWallDistance"/> of a tunnel segment's flat wall.</summary>
    static bool LiesOnWall(Vector3 world)
    {
        foreach (var segment in TunnelSegmentGeometry.all)
        {
            if (segment == null || !segment.isActiveAndEnabled)
                continue;
            var p = segment.transform.InverseTransformPoint(world);
            if (Mathf.Abs(p.z) > segment.length * 0.5f + 0.05f || p.y < 0f || p.y > segment.wallHeight)
                continue;
            if (Mathf.Abs(segment.width * 0.5f - Mathf.Abs(p.x)) < k_OnWallDistance)
                return true;
        }
        return false;
    }

    /// <summary>Takes them all away (for everyone, on the host). Returns how many.</summary>
    public static int Clear()
    {
        var targets = Targets();
        foreach (var target in targets)
        {
            Debug.Log($"[Clear all] removing {target.name}");
            if (target.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned)
                networkObject.Despawn(true);
            else
                Object.Destroy(target);
        }
        Debug.Log($"[Clear all] removed {targets.Count} things not on a tunnel wall");
        return targets.Count;
    }
}
