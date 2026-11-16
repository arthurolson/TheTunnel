using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Checks the Ligand binding movie (see <see cref="TunnelMovie"/>, <see cref="CapsidSites.pocket"/>) without a headset
/// or a session: choosing it on the movie's menu; EGFR's capture sphere (on its pocket's mouth) live only once it plays, sized by the Capture
/// radius slider (2 to 30 Å at the molecules' true scale, 10 Å at first) so that a ligand 20 Å from the pocket is caught
/// at 30 Å but not at 10 Å; a ligand touching the sphere binding at its pose from the crystal structure, shown at the
/// protein's size, and gliding there; EGFR turning light blue while it is bound and back when it lets go; the binding
/// time releasing it out of the pocket, resting and at the size it came in at; Cys775 shown on EGFR only once the
/// ligand has settled, hidden while it glides and the moment it lets go; an antibody not binding in the pocket;
/// and Play again letting it go, counting down and putting the starting state back. Runs from Tunnel > Movie > Test
/// Ligand Binding (in Play mode); the glide and Play again parts finish some seconds later and log PASS or FAIL with
/// the tag [Movie].
/// </summary>
static class LigandBindingEditorTests
{
    const BindingFlags k_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    const float k_UnitsPerAngstrom = 0.2f / 167f; // as the molecule library

    static object Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, k_Flags).Invoke(target, args.Length == 0 ? null : args);

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, k_Flags).GetValue(target);

    [MenuItem("Tunnel/Movie/Test Ligand Binding (in Play mode)")]
    public static string Test()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Movie] enter Play mode (any scene) first, then run Tunnel > Movie > Test Ligand Binding");
            return "needs Play mode";
        }

        var report = new StringBuilder("[Movie] Ligand binding test (not connected)\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 2e-3f;

        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>("Assets/Tunnel/TunnelPrefabs.asset");
        GameObject Prefab(string name) => registry.molecules.First(m => m.name == name).prefab;
        var made = new List<GameObject>();

        // A 1 m box away from everything, playing Ligand binding.
        var origin = new Vector3(-40f, 1.5f, 40f);
        var box = Object.Instantiate(registry.containerBox, origin, Quaternion.identity);
        box.transform.localScale = Vector3.one;
        made.Add(box);
        var container = box.GetComponent<TunnelContainer>();
        container.SetTemperature(0f); // still, so nothing drifts during the test
        var movie = container.movie;
        Check(movie != null && movie.scenario == TunnelMovie.Scenario.AntibodyAction, "a new container's movie is Antibody action");
        movie.SetScenario(TunnelMovie.Scenario.LigandBinding);
        Check(movie.scenario == TunnelMovie.Scenario.LigandBinding && movie.title == "Ligand binding", $"the menu's choice makes it Ligand binding (\"{movie.title}\")");

        GameObject Make(string name, Vector3 at, float magnification)
        {
            var prefab = Prefab(name);
            var go = Object.Instantiate(prefab, at, Quaternion.identity);
            go.transform.localScale = prefab.transform.localScale * magnification;
            if (go.TryGetComponent(out MoleculeGrabSize grabSize))
                grabSize.Fit(); // as its next frame would
            made.Add(go);
            return go;
        }

        // EGFR at ×10 (12 cm), and a ligand at ×10 off to one side.
        const float magnification = 10f;
        var egfr = Make("EGFR", origin, magnification).GetComponent<CapsidSites>();
        var ligand = Make("EGFR ligand", origin + new Vector3(0.3f, 0.2f, 0f), magnification);
        Physics.SyncTransforms();
        Call(egfr, "Update");
        Check(egfr != null && egfr.pocket && egfr.siteCount == 1 && egfr.movie == movie && !egfr.sitesActive, "EGFR in the box has one pocket, not live before Start");

        movie.StartMovie();
        Check(container.temperature > 0f, "Start warms a container with no heat (nothing would move)");
        container.SetTemperature(0f); // still again for the rest of this test, so nothing drifts or spins
        Call(egfr, "Update");
        Check(movie.isPlaying && egfr.sitesActive, $"Start plays it and makes the capture sphere live (\"{movie.message}\")");
        Check(movie.startingState != null && movie.startingState.scenario == "Ligand binding" && movie.startingState.molecules.Count == 2,
            $"the starting state is kept, named after the movie (\"{movie.startingState?.scenario}\", {movie.startingState?.molecules.Count} molecules)");
        Check(TunnelMovie.NextName(movie.title).StartsWith("Ligand binding "), $"saved states are offered as {TunnelMovie.NextName(movie.title)}");

        // The capture radius slider.
        Check(Mathf.Approximately(movie.captureRadius, 10f), $"the capture radius starts at 10 Å ({movie.captureRadius})");
        movie.SetCaptureRadius(0.5f);
        Check(Mathf.Approximately(movie.captureRadius, 2f), "set to 0.5 Å it is 2 Å (the least)");
        movie.SetCaptureRadius(100f);
        Check(Mathf.Approximately(movie.captureRadius, 30f), "set to 100 Å it is 30 Å (the most)");
        movie.SetCaptureRadius(10f);
        Call(egfr, "Update");
        var sphere = egfr.GetComponentsInChildren<SphereCollider>(true).FirstOrDefault(c => c.isTrigger);
        float worldPerAngstrom = k_UnitsPerAngstrom * egfr.transform.lossyScale.x;
        Check(sphere != null && sphere.enabled && Mathf.Abs(sphere.radius * sphere.transform.lossyScale.x - 10f * worldPerAngstrom) < 1e-5f,
            $"the capture sphere's radius is 10 Å at true scale ({(sphere != null ? sphere.radius * sphere.transform.lossyScale.x * 100f : 0f):0.00} cm at ×{magnification})");
        Check(sphere != null && Vector3.Distance(sphere.transform.position, egfr.SiteBase(0)) < 1e-5f, "centred on the pocket's mouth");
        float mouthDepth = (egfr.SiteBase(0) - egfr.transform.TransformPoint(egfr.boundPosition)).magnitude / worldPerAngstrom;
        Check(mouthDepth > 10f && mouthDepth < 30f, $"the mouth is {mouthDepth:0} Å out from where the ligand sits bound");

        // A ligand whose collider is 20 Å from the pocket's centre: caught at 30 Å, not at 10 Å.
        var ligandCollider = ligand.GetComponentInChildren<Collider>();
        var centre = egfr.SiteBase(0);
        var outward = (centre - egfr.transform.position).normalized;
        ligand.transform.position = centre + outward * 0.5f;
        Physics.SyncTransforms();
        var surface = ligandCollider.ClosestPoint(centre);
        ligand.transform.position += (centre + outward * 20f * worldPerAngstrom) - surface;
        Physics.SyncTransforms();
        bool Touching() => sphere != null && Physics.ComputePenetration(sphere, sphere.transform.position, sphere.transform.rotation,
            ligandCollider, ligandCollider.transform.position, ligandCollider.transform.rotation, out _, out _);
        Check(!Touching(), "a ligand 20 Å from the pocket is not caught at a 10 Å capture radius");
        movie.SetCaptureRadius(30f);
        Call(egfr, "Update");
        Physics.SyncTransforms();
        Check(Touching(), "moving the slider to 30 Å catches it");
        movie.SetCaptureRadius(10f);
        Call(egfr, "Update");

        // Playing, the ligand's collider fits it (no minimum grab size), so it moves and reaches the pocket as itself.
        var ligandGrab = ligand.GetComponent<MoleculeGrabSize>();
        ligandGrab.Fit();
        Check(ligand.GetComponentInChildren<Collider>().transform.localScale == Vector3.one, "while the movie plays, the ligand's collider fits it (no 2 cm grab minimum)");

        // EGFR's body leaves its pocket open: sliding the ligand in from outside, from many directions and turned many
        // ways, how close does its collider get to the pocket's centre before it meets the body?
        var body = egfr.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToList();
        var axis = egfr.SiteAxis(0);
        var keepPosition = ligand.transform.position;
        var keepRotation = ligand.transform.rotation;
        float closest = float.MaxValue;
        var random = new System.Random(5);
        float Next() => (float)random.NextDouble() * 2f - 1f;
        for (int trial = 0; trial < 150; trial++)
        {
            var direction = trial == 0 ? axis : (axis + new Vector3(Next(), Next(), Next()) * 1.2f).normalized;
            var turn = Quaternion.Euler(Next() * 180f, Next() * 180f, Next() * 180f);
            for (float d = 60f; d >= 0f; d -= 1f)
            {
                ligand.transform.SetPositionAndRotation(centre + direction * d * worldPerAngstrom, turn);
                Physics.SyncTransforms();
                if (body.Any(c => Physics.ComputePenetration(ligandCollider, ligandCollider.transform.position, ligandCollider.transform.rotation,
                        c, c.transform.position, c.transform.rotation, out _, out _)))
                    break;
                closest = Mathf.Min(closest, (ligandCollider.ClosestPoint(centre) - centre).magnitude / worldPerAngstrom);
            }
        }
        ligand.transform.SetPositionAndRotation(keepPosition, keepRotation);
        Physics.SyncTransforms();
        Check(closest < 10f, $"a free ligand can reach the 10 Å capture sphere past EGFR's body (to {closest:0.0} Å from the pocket's centre)");

        // An antibody touching the pocket doesn't bind there.
        var igg = Make("IGG", origin + new Vector3(-0.3f, 0f, 0f), 1f);
        Call(igg.GetComponent<AntibodyBinder>(), "OnTriggerStay", sphere);
        Check(egfr.bindingCount == 0 && igg.GetComponent<AntibodyBinder>().target == null, "an antibody touching EGFR's capture sphere doesn't bind in it");

        // The ligand touches it: it binds at its pose from the crystal structure, at the protein's size.
        var colours = new List<(Material material, Color color)>();
        Call(egfr, "Update"); // the colours are kept on the first change
        var binder = ligand.GetComponent<AntibodyBinder>();
        Check(binder != null && binder.ligand, "the ligand has a binder with no Fab arms");
        float ligandScale = ligand.transform.lossyScale.x;
        Call(binder, "OnTriggerStay", sphere);
        Call(egfr, "Update");
        Check(egfr.bindingCount == 1 && !egfr.SiteOpen(0) && movie.CountBound() == 1 && movie.CountCovered() == 1, "touching the capture sphere, it binds (the menu counts it)");
        var bound = Field<List<(Transform root, CapsidSites.Binding binding, float since)>>(egfr, "m_Bound");
        var binding = bound.Count == 1 ? bound[0].binding : default;
        Check(bound.Count == 1 && binding.position == egfr.boundPosition && binding.rotation == egfr.boundRotation && Mathf.Approximately(binding.scale, 1f),
            "it binds at its pose in the crystal structure, at the protein's size");
        Check(bound.Count == 1 && bound[0].root.name == "Bound Ligand" && bound[0].root.GetComponentsInChildren<Collider>().Length == 0,
            "everyone sees it in the pocket (no collider of its own there)");
        colours = Field<List<(Material material, Color color)>>(egfr, "m_Colours");
        var blue = Field<Color>(egfr, "m_CoveredColour");
        Check(colours.Count > 0 && colours.All(c => Near(c.material.GetColor("_BaseColor"), blue)), $"EGFR turns light blue ({blue})");
        Check(Mathf.Abs(egfr.TimeLeft(0) - movie.bindingTime) < 0.5f, $"bound for the binding time ({egfr.TimeLeft(0):0} s)");
        var cys = egfr.shownWhileBound;
        Check(cys != null && !cys.activeSelf, "Cys775 stays hidden while the ligand glides in");

        // The glide, a second of play later (game time: a slow Editor frame doesn't count for more); then the binding
        // time lets it go.
        double started = EditorApplication.timeSinceStartup;
        float boundAt = Time.time;
        void Glide()
        {
            if (!Application.isPlaying)
            {
                EditorApplication.update -= Glide;
                return;
            }
            if (Time.time - boundAt < 1f && EditorApplication.timeSinceStartup - started < 30.0)
                return;
            EditorApplication.update -= Glide;
            var root = bound.Count == 1 ? bound[0].root : null;
            Check(root != null && (root.localPosition - egfr.boundPosition).magnitude < 1e-5f && Quaternion.Angle(root.localRotation, egfr.boundRotation) < 0.01f
                && Mathf.Abs(root.localScale.x - 1f) < 1e-5f, "it has glided into the pocket");
            Check(cys != null && cys.activeSelf, "settled in the pocket, it shows bonded to Cys775");

            // Its time is up: let go out of the pocket, resting, at the size it came in at, and EGFR is blue no more.
            var before = new HashSet<MoleculeGravity>(MoleculeGravity.all);
            egfr.CountDownBindings(egfr.TimeLeft(0) + 0.1f);
            Call(egfr, "Update");
            var released = MoleculeGravity.all.FirstOrDefault(m => m != null && !before.Contains(m));
            if (released != null)
                made.Add(released.gameObject);
            Check(egfr.SiteOpen(0) && egfr.bindingCount == 0, "when its binding time is up it lets go");
            Check(cys != null && !cys.activeSelf, "Cys775 is hidden the moment it lets go");
            Check(released != null && released.GetComponentsInChildren<MeshRenderer>(true).All(r => !r.name.Contains("Cys")) && released.GetComponentsInChildren<MeshRenderer>(true).Length == 3,
                "the free ligand is the inhibitor alone");
            Check(released != null && released.TryGetComponent(out AntibodyBinder freed) && freed.ligand && freed.resting, "a free ligand again, resting so it doesn't bind straight back");
            float protein = TunnelMoleculeMenu.WorldRadius(egfr.gameObject);
            Check(released != null && Vector3.Distance(released.transform.position, egfr.transform.position) > protein * 0.9f
                && Vector3.Dot(released.body.linearVelocity, (released.transform.position - egfr.transform.position).normalized) > 0.1f,
                "it comes out of the pocket and is pushed away");
            Check(released != null && Mathf.Abs(released.transform.lossyScale.x - ligandScale) < 1e-5f, "at the size it came in at");
            Check(colours.All(c => Near(c.material.GetColor("_BaseColor"), c.color)), "EGFR's own colour is back");

            // Play again: bind one again, then everything lets go, a 5 s countdown, and the starting state.
            var again = Make("EGFR ligand", egfr.SiteBase(0), magnification);
            Physics.SyncTransforms();
            Call(again.GetComponent<AntibodyBinder>(), "OnTriggerStay", sphere);
            Call(egfr, "Update");
            Check(egfr.bindingCount == 1, "another ligand binds");
            var beforePlayAgain = new HashSet<MoleculeGravity>(MoleculeGravity.all);
            movie.PlayAgain();
            foreach (var m in MoleculeGravity.all)
                if (m != null && !beforePlayAgain.Contains(m))
                    made.Add(m.gameObject);
            Call(egfr, "Update");
            Check(movie.isRestarting && movie.countdown == 5 && egfr.bindingCount == 0, $"Play again lets it go and counts down from {movie.countdown} s");
            Check(!egfr.sitesActive, "no binding during the countdown");

            double restarted = EditorApplication.timeSinceStartup;
            void Restart()
            {
                if (!Application.isPlaying)
                {
                    EditorApplication.update -= Restart;
                    return;
                }
                bool timedOut = EditorApplication.timeSinceStartup - restarted > 12.0;
                if (movie != null && movie.isRestarting && !timedOut)
                    return;
                EditorApplication.update -= Restart;
                Check(!timedOut && movie.isPlaying && movie.scenario == TunnelMovie.Scenario.LigandBinding, "after the countdown it plays Ligand binding again");
                var inside = container.Contents(true);
                var names = inside.Select(g => CapsidSites.MoleculeName(g)).OrderBy(n => n).ToList();
                Check(names.SequenceEqual(new[] { "EGFR", "EGFR ligand" }), $"the starting state is back: {string.Join(", ", names)}");
                var newEgfr = inside.Select(g => g.GetComponent<CapsidSites>()).FirstOrDefault(c => c != null);
                Check(newEgfr != null && newEgfr.bindingCount == 0 && Vector3.Distance(newEgfr.transform.position, origin) < 0.01f, "EGFR is back where it started, nothing bound");
                made.AddRange(inside);

                // The menu: its movie row and the slider in ångströms.
                movie.SetControls(true);
                Call(movie, "Update");
                var panel = movie.panel;
                if (panel != null)
                {
                    Call(panel, "Update");
                    var scenarioRow = Field<Button>(panel, "m_ScenarioRow");
                    var slider = Field<Slider>(panel, "m_DiameterSlider");
                    var sliderText = Field<TMP_Text>(panel, "m_DiameterText");
                    Check(Field<TMP_Text>(panel, "m_Title").text.StartsWith("Ligand binding"), "the menu is called Ligand binding");
                    string rowText = scenarioRow != null ? scenarioRow.GetComponentInChildren<TMP_Text>(true).text : "";
                    Check(rowText.Contains("Ligand binding") && rowText.Contains("stops it"),
                        $"its movie picker shows Ligand binding, and that choosing another stops it ({rowText})");
                    Check(slider != null && Mathf.Approximately(slider.minValue, 2f) && Mathf.Approximately(slider.maxValue, 30f) && Mathf.Approximately(slider.value, 10f)
                        && sliderText.text.StartsWith("Capture radius: 10 Å"), $"its slider is the capture radius, 2 to 30 Å ({sliderText?.text})");
                    movie.SetControls(false);
                }
                else
                {
                    Check(false, "the menu shows");
                }

                foreach (var go in made)
                    if (go != null)
                        Object.Destroy(go);
                report.AppendLine(failures == 0 ? "  PASS" : $"  FAIL ({failures})");
                Debug.Log(report.ToString());
            }
            EditorApplication.update += Restart;
        }
        EditorApplication.update += Glide;
        string sofar = report + "  (the glide and Play again follow in the log)";
        Debug.Log(sofar);
        return sofar;
    }
}
