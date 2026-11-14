using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Checks the spawn nozzle (see <see cref="SpawnNozzle"/>) without a headset or a session: attaching a molecule let
/// go of at the cup (and not one let go of further off), refusing a second, pulling one off and attaching another in
/// its place, the attached molecule riding on the nozzle, the copies count (1 to 100) and the triangle budget, the
/// mouth waiting while it's blocked, a real spray (the right number of copies of the attached molecule at its size,
/// out of the front, none starting inside another), and removing the nozzle leaving its molecule free.
/// Runs from Tunnel > Molecules > Test Spawn Nozzle (in Play mode); the spray part finishes a few seconds later and
/// logs PASS or FAIL with the tag [Nozzle].
/// </summary>
static class NozzleEditorTests
{
    const BindingFlags k_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static object Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, k_Flags).Invoke(target, args.Length == 0 ? null : args);

    [MenuItem("Tunnel/Molecules/Test Spawn Nozzle (in Play mode)")]
    public static string Test()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Nozzle] enter Play mode (any scene) first, then run Tunnel > Molecules > Test Spawn Nozzle");
            return "needs Play mode";
        }

        var report = new StringBuilder("[Nozzle] spawn nozzle test (not connected)\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }

        // The count and the triangle budget.
        Check(SpawnNozzle.CopiesFor(0, 1000, 3000000) == 1, "a count under 1 makes 1");
        Check(SpawnNozzle.CopiesFor(250, 1000, 3000000) == 100, "a count over 100 makes 100");
        Check(SpawnNozzle.CopiesFor(37, 1000, 3000000) == 37, "37 small ones make 37");
        Check(SpawnNozzle.CopiesFor(100, 300000, 3000000) == 10, "100 of a 300k-triangle model make 10 (3M budget)");
        Check(SpawnNozzle.CopiesFor(100, 10000000, 3000000) == 1, "a model over the whole budget still makes 1");

        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>("Assets/Tunnel/TunnelPrefabs.asset");
        GameObject Prefab(string name) => registry.molecules.First(m => m.name == name).prefab;
        foreach (var entry in registry.molecules)
            report.AppendLine($"  {entry.name}: {SpawnNozzle.Triangles(entry.prefab)} triangles, at most {SpawnNozzle.CopiesFor(100, SpawnNozzle.Triangles(entry.prefab), 3000000)} per spray");

        // A nozzle, away from everything, pointing along +X, and two molecules (2PLV at x1, IGG at x3).
        if (SpawnNozzle.current != null)
            Object.DestroyImmediate(SpawnNozzle.current.gameObject);
        var origin = new Vector3(20f, 1.5f, 20f);
        var nozzle = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(NozzleBuilder.k_PrefabPath), origin, Quaternion.LookRotation(Vector3.right)).GetComponent<SpawnNozzle>();
        Check(SpawnNozzle.current == nozzle, "the nozzle is the current one");
        var cradle = (Transform)typeof(SpawnNozzle).GetField("m_Cradle", k_Flags).GetValue(nozzle);
        var mouth = (Transform)typeof(SpawnNozzle).GetField("m_Mouth", k_Flags).GetValue(nozzle);

        MoleculeGravity Make(string name, float magnification, Vector3 at)
        {
            var prefab = Prefab(name);
            var go = Object.Instantiate(prefab, at, Quaternion.identity);
            go.transform.localScale = prefab.transform.localScale * magnification;
            return go.GetComponent<MoleculeGravity>();
        }
        var polio = Make("2PLV", 1f, origin + Vector3.up * 1f);
        var igg = Make("IGG", 3f, origin + Vector3.up * 2f);
        float Radius(MoleculeGravity m) => TunnelMoleculeMenu.WorldRadius(m.gameObject);
        void LetGo(MoleculeGravity m) => Call(nozzle, "OnMoleculeReleased", m);
        void Frame()
        {
            Physics.SyncTransforms();
            Call(nozzle, "LateUpdate");
            foreach (var m in new[] { polio, igg })
                if (m != null)
                    Call(m, "FixedUpdate");
        }

        // Let go of well away from the cup: nothing.
        polio.transform.position = cradle.position + cradle.forward * (Radius(polio) + 0.2f);
        LetGo(polio);
        Frame();
        Check(nozzle.loaded == null, "a molecule let go of 20 cm from the cup isn't attached");

        // Let go of at the cup: attached, sat on it, held still.
        polio.transform.position = cradle.position + cradle.forward * (Radius(polio) + 0.03f) + Vector3.up * 0.01f;
        LetGo(polio);
        Frame();
        Check(nozzle.loaded == polio, $"2PLV let go of 3 cm from the cup is attached ({nozzle.message})");
        Check(polio.dock == nozzle && polio.body.isKinematic, "it is held still on the cup");
        float seated = Vector3.Distance(polio.transform.position, cradle.position) - Radius(polio);
        Check(Mathf.Abs(seated) < 0.003f, $"its surface sits on the cup ({seated * 1000f:0.0} mm)");
        Check(nozzle.loadedName == "2PLV" && Mathf.Abs(nozzle.loadedMagnification - 1f) < 0.01f, $"the menu shows {nozzle.loadedName} x{nozzle.loadedMagnification:0.#}");

        // The nozzle moves: the molecule rides on it.
        nozzle.transform.SetPositionAndRotation(origin + new Vector3(0.3f, 0.2f, -0.1f), Quaternion.Euler(20f, 60f, 0f));
        Frame();
        seated = Vector3.Distance(polio.transform.position, cradle.position) - Radius(polio);
        float onAxis = Vector3.Angle(polio.transform.position - cradle.position, cradle.forward);
        Check(Mathf.Abs(seated) < 0.003f && onAxis < 1f, $"moved and turned, the nozzle carries it ({seated * 1000f:0.0} mm, {onAxis:0.0} deg off the axis)");

        // A second one at a full nozzle: refused.
        igg.transform.position = cradle.position + (cradle.up + cradle.right).normalized * (Radius(igg) + 0.02f);
        LetGo(igg);
        Frame();
        Check(nozzle.loaded == polio && igg.dock == null, $"IGG let go of at a full cup is refused (\"{nozzle.message}\")");

        // Pulled off, then IGG in its place.
        nozzle.Detach();
        Frame();
        Check(nozzle.loaded == null && polio.dock == null, "2PLV pulled off: the cup is empty and 2PLV is free");
        polio.transform.position += polio.transform.position - cradle.position; // out of the way
        igg.transform.position = cradle.position + cradle.forward * (Radius(igg) + 0.01f);
        LetGo(igg);
        Frame();
        Check(nozzle.loaded == igg && igg.dock == nozzle, $"IGG attached in its place ({nozzle.loadedName} x{nozzle.loadedMagnification:0.#})");

        // The count.
        nozzle.SetCount(0);
        Check(nozzle.count == 1, "copies set to 0 shows 1");
        nozzle.SetCount(500);
        Check(nozzle.count == 100, "copies set to 500 shows 100");
        nozzle.SetCount(12);
        Check(nozzle.count == 12, "copies set to 12 shows 12");

        // The mouth waits while something fills it.
        float iggRadius = Radius(igg);
        var spot = mouth.position + mouth.forward * (iggRadius + 0.01f);
        polio.body.constraints = RigidbodyConstraints.FreezeAll;
        polio.transform.position = spot;
        Physics.SyncTransforms();
        Check((bool)Call(nozzle, "MouthBlocked", spot, iggRadius), "a molecule in front of the mouth blocks it");
        polio.transform.position = spot + Vector3.up * 2f; // well out of the spray's way
        Physics.SyncTransforms();
        Check(!(bool)Call(nozzle, "MouthBlocked", spot, iggRadius), "with it gone, the mouth is clear");
        Check(!(bool)Call(nozzle, "MouthBlocked", cradle.position + cradle.forward * iggRadius, iggRadius), "the attached molecule itself never blocks");

        // A real spray: 12 IGG at x3 out of the front, over the next frames.
        var before = new HashSet<MoleculeGravity>(MoleculeGravity.all);
        uint iggId = Prefab("IGG").GetComponent<NetworkObject>().PrefabIdHash;
        var expectedScale = igg.transform.localScale;
        nozzle.SpawnOrStop();
        Check(nozzle.spraying, $"Spawn starts a spray (\"{nozzle.message}\")");

        var seen = new HashSet<MoleculeGravity>();
        int overlaps = 0, behind = 0, wrong = 0;
        double started = EditorApplication.timeSinceStartup;
        void Watch()
        {
            if (!Application.isPlaying || nozzle == null)
            {
                EditorApplication.update -= Watch;
                Debug.LogWarning("[Nozzle] test stopped: Play mode ended or the nozzle went");
                return;
            }
            foreach (var m in MoleculeGravity.all.ToList())
            {
                if (m == null || before.Contains(m) || seen.Contains(m))
                    continue;
                seen.Add(m);
                if (!m.TryGetComponent(out NetworkObject no) || no.PrefabIdHash != iggId || (m.transform.localScale - expectedScale).magnitude > 1e-4f)
                    wrong++;
                if (Vector3.Dot(m.transform.position - mouth.position, mouth.forward) <= 0f)
                    behind++;
                // Started inside another copy (deeper than a few millimetres)?
                var mine = m.GetComponentInChildren<Collider>();
                foreach (var other in seen)
                {
                    var theirs = other != null && other != m ? other.GetComponentInChildren<Collider>() : null;
                    if (mine != null && theirs != null && Physics.ComputePenetration(mine, mine.transform.position, mine.transform.rotation,
                            theirs, theirs.transform.position, theirs.transform.rotation, out _, out float depth) && depth > 0.005f)
                        overlaps++;
                }
            }
            bool timedOut = EditorApplication.timeSinceStartup - started > 20.0;
            if (nozzle.spraying && !timedOut)
                return;
            EditorApplication.update -= Watch;

            Check(!timedOut, "the spray finished within 20 s");
            Check(seen.Count == 12, $"it made 12 copies ({seen.Count})");
            Check(wrong == 0, $"each is an IGG at the attached one's size ({wrong} not)");
            Check(behind == 0, $"each came out of the front ({behind} behind the mouth)");
            Check(overlaps == 0, $"none started inside another ({overlaps} did)");
            Check(nozzle.message.StartsWith("Done"), $"the menu says \"{nozzle.message}\"");

            // Removing the nozzle leaves its molecule where it is, free.
            var at = igg.transform.position;
            Object.DestroyImmediate(nozzle.gameObject);
            Check(SpawnNozzle.current == null, "the nozzle is gone");
            Check(igg.dock == null && Vector3.Distance(igg.transform.position, at) < 1e-4f, "IGG stays where it was, no longer attached");

            foreach (var m in seen.Concat(new[] { polio, igg }))
                if (m != null)
                    Object.Destroy(m.gameObject);
            report.AppendLine(failures == 0 ? "  PASS" : $"  FAIL ({failures})");
            Debug.Log(report.ToString());
        }
        EditorApplication.update += Watch;
        string sofar = report.ToString() + "  (spraying: the result follows in the log)";
        Debug.Log(sofar);
        return sofar;
    }
}
