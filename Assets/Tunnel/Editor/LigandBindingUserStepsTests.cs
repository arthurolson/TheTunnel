using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Ligand binding set up the way a person sets it up, through the menus' own code (see <see cref="TunnelMovie"/>):
/// the TS Menu's Containers list places a box (Box, then Place Container), the Molecules list's Magnification slider is
/// set and EGFR and 15 EGFR ligands are placed from the list (they appear parked beside it), each is taken in hand and
/// let go of inside the box (the box scaled up first, as two hands would, when EGFR is large), the container keeps the
/// heat a new one has (none), 3D movie controls opens the movie menu, its picker chooses Ligand binding, its slider
/// sets the capture radius, and Start plays it. One run fills the box from the spawn nozzle instead, and one presses
/// Start first with Antibody action still chosen, as the user did with 15.11.2026.1 (Ligand binding is then chosen for
/// them). First, Start in an empty box must say which movie is chosen, what it needs, what is outside the box, and how
/// to choose another. Gravity is off, as in the app until the host turns it on. Each run waits
/// (game time) for the first binding, up to 30 s at ×10 and ×20 (and at ×5 with 30 Å), which must see one; at ×5 with
/// 10 Å and at ×1 (EGFR 1.2 cm across in a 30 cm box) it waits a minute and only reports when one came. Runs at ×1, ×5, ×10 and ×20 with the default capture radius (10 Å) and at 30 Å,
/// plus the nozzle at ×10. Runs from Tunnel > Movie > Test Ligand Binding as a Person Would (in Play mode); it takes a
/// minute or two and logs PASS or FAIL with the tag [Movie].
/// </summary>
static class LigandBindingUserStepsTests
{
    const BindingFlags k_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, k_Flags).GetValue(target);

    static object Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, k_Flags).Invoke(target, args.Length == 0 ? null : args);

    struct Run
    {
        public int magnification;
        public float radius;
        public bool nozzle;
        public float limit;
        public bool required;
        public bool startFirst; // Start pressed with Antibody action still chosen, as the user did on 15.11.2026.1
    }

    [MenuItem("Tunnel/Movie/Test Ligand Binding as a Person Would (in Play mode)")]
    public static string Test()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Movie] enter Play mode (any scene) first, then run Tunnel > Movie > Test Ligand Binding as a Person Would");
            return "needs Play mode";
        }
        var runs = new Queue<Run>(new[]
        {
            new Run { magnification = 10, radius = 10, limit = 30, required = true, startFirst = true },
            new Run { magnification = 20, radius = 10, limit = 30, required = true },
            new Run { magnification = 5, radius = 10, limit = 60, required = false }, // a 6 cm EGFR in a 30 cm box: 2 to 33 s in Editor runs
            new Run { magnification = 1, radius = 10, limit = 60, required = false },
            new Run { magnification = 10, radius = 30, limit = 30, required = true },
            new Run { magnification = 20, radius = 30, limit = 30, required = true },
            new Run { magnification = 5, radius = 30, limit = 30, required = true },
            new Run { magnification = 1, radius = 30, limit = 60, required = false },
            new Run { magnification = 10, radius = 10, limit = 30, required = true, nozzle = true },
        });
        var report = new StringBuilder("[Movie] Ligand binding as a person would set it up (not connected)\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }

        var segment = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(k_SegmentPath), new Vector3(70f, 0f, -70f), Quaternion.identity);
        segment.name = "Ligand Binding Steps Segment";
        var containers = segment.GetComponent<TunnelContainerMenu>();
        var moleculeMenu = segment.GetComponent<TunnelMoleculeMenu>();
        containers.ShowList(true);
        moleculeMenu.ShowList(true);
        var content = Field<RectTransform>(moleculeMenu, "m_ListContent");
        Button Row(string molecule) => content.GetComponentsInChildren<Button>(true).First(b => b.name == "Row " + molecule);
        // The ligand weighs what a ligand does (a toy rebuilt without its own volume weighs as much as a virus, and then
        // barely moves).
        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>("Assets/Tunnel/TunnelPrefabs.asset");
        float VolumeOf(string molecule) => registry.molecules.First(m => m.name == molecule).prefab.GetComponent<MoleculeGravity>().volume;
        Check(VolumeOf("EGFR ligand") > 0f && VolumeOf("EGFR ligand") < VolumeOf("EGFR") * 0.05f,
            $"the ligand's volume is a ligand's ({VolumeOf("EGFR ligand") / Mathf.Pow(0.2f / 167f, 3f):N0} Å³, EGFR's {VolumeOf("EGFR") / Mathf.Pow(0.2f / 167f, 3f):N0} Å³)");
        // No gravity, as in the app until the host turns it on (the Editor's scenes have it on).
        var keepGravity = Physics.gravity;
        Physics.gravity = Vector3.zero;
        var slider = Field<Slider>(moleculeMenu, "m_MagnificationSlider");
        Check(slider != null && Mathf.Approximately(TunnelMoleculeMenu.MagnificationAt(slider.maxValue), 20f), "the Magnification slider goes up to ×20");

        // An empty box, Start: the menu says which movie is chosen, what it needs, and how to choose another; with a
        // ligand outside the box, that it is outside.
        Field<Button>(containers, "m_BoxRow").onClick.Invoke();
        Field<Button>(containers, "m_PlaceButton").onClick.Invoke();
        var empty = TunnelContainer.current;
        empty.movie.SetScenario(TunnelMovie.Scenario.LigandBinding);
        var outside = new HashSet<MoleculeGravity>(Object.FindObjectsByType<MoleculeGravity>(FindObjectsInactive.Include));
        Row("EGFR ligand").onClick.Invoke();
        var stray = Object.FindObjectsByType<MoleculeGravity>(FindObjectsInactive.Include).Where(m => !outside.Contains(m)).ToList();
        Check(Field<TMPro.TMP_Text>(moleculeMenu, "m_StatusText").text.Contains("mm") || Field<TMPro.TMP_Text>(moleculeMenu, "m_StatusText").text.Contains("cm across"),
            $"the Molecules list says how large what it placed is ({Field<TMPro.TMP_Text>(moleculeMenu, "m_StatusText").text})");
        empty.movie.StartMovie();
        string notReady = empty.movie.message;
        Check(!empty.movie.isPlaying && notReady.StartsWith("Ligand binding is chosen: it needs an EGFR and some EGFR ligands") && notReady.Contains("1 ligand is outside it")
            && notReady.Contains("◄ ►"), $"Start in an empty box names the movie, what it needs, what is outside, and the picker (\"{notReady}\")");
        foreach (var m in stray)
            Object.Destroy(m.gameObject);
        Object.Destroy(empty.gameObject);

        void Next()
        {
            if (runs.Count == 0)
            {
                Physics.gravity = keepGravity;
                Object.Destroy(segment);
                report.AppendLine(failures == 0 ? "  PASS" : $"  FAIL ({failures})");
                Debug.Log(report.ToString());
                return;
            }
            One(runs.Dequeue());
        }

        void One(Run run)
        {
            string label = $"×{run.magnification}, {run.radius:0} Å{(run.nozzle ? ", ligands from the spawn nozzle" : "")}";
            // The box, placed from the Containers list.
            Field<Button>(containers, "m_BoxRow").onClick.Invoke();
            Field<Button>(containers, "m_PlaceButton").onClick.Invoke();
            var container = TunnelContainer.current;
            var movie = container.movie;
            // The magnification, on the Molecules list's slider.
            slider.value = TunnelMoleculeMenu.StepOf(run.magnification);
            Check(TunnelMoleculeMenu.currentMagnification == run.magnification, $"{label}: the slider is at ×{TunnelMoleculeMenu.currentMagnification}");
            // EGFR and the ligands, placed from the list.
            var before = new HashSet<MoleculeGravity>(Object.FindObjectsByType<MoleculeGravity>(FindObjectsInactive.Include));
            Row("EGFR").onClick.Invoke();
            if (!run.nozzle)
                for (int i = 0; i < 15; i++)
                    Row("EGFR ligand").onClick.Invoke();
            else
                Row("EGFR ligand").onClick.Invoke(); // the one that goes on the nozzle
            var made = Object.FindObjectsByType<MoleculeGravity>(FindObjectsInactive.Include).Where(m => !before.Contains(m)).ToList();
            var egfr = made.Select(m => m.GetComponent<CapsidSites>()).FirstOrDefault(c => c != null);
            // A box at least twice and a half EGFR's size (two hands scale it).
            var drawn = egfr.GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; });
            float egfrSize = drawn.size.magnitude / Mathf.Sqrt(3f); // across, on average
            float boxSize = Mathf.Max(0.3f, egfrSize * 2.5f);
            container.transform.localScale = Vector3.one * boxSize;
            var random = new System.Random(run.magnification * 100 + (int)run.radius);
            float R() => (float)random.NextDouble() - 0.5f;

            int frame = 0;
            float startedAt = -1f;
            SpawnNozzle nozzle = null;
            void Tick()
            {
                if (!Application.isPlaying)
                {
                    EditorApplication.update -= Tick;
                    Physics.gravity = keepGravity;
                    return;
                }
                frame++;
                if (frame == 3)
                {
                    // Taken in hand (which frees a parked molecule) and let go of inside the box.
                    foreach (var molecule in made)
                    {
                        if (molecule == null)
                            continue;
                        Call(molecule, "OnGrabbed", new object[] { null });
                        bool isEgfr = molecule.GetComponent<CapsidSites>() != null;
                        // EGFR in the middle; each ligand well clear of it (2 cm past its surface), so none starts at
                        // its pocket and the first binding is one the ligands found by moving.
                        var local = Vector3.zero;
                        float clear = (egfrSize * 0.5f + 0.02f) / boxSize;
                        if (!isEgfr)
                            for (int tries = 0; tries < 100 && local.magnitude < clear; tries++)
                                local = new Vector3(R(), R(), R()) * 0.85f;
                        molecule.transform.position = container.transform.TransformPoint(local);
                        molecule.body.position = molecule.transform.position;
                        molecule.body.linearVelocity = Vector3.zero;
                    }
                    Physics.SyncTransforms();
                    if (run.nozzle)
                    {
                        // The spawn nozzle, its mouth inside the box, the ligand attached at its cup, 14 more sprayed.
                        if (SpawnNozzle.current != null)
                            Object.DestroyImmediate(SpawnNozzle.current.gameObject);
                        nozzle = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(NozzleBuilder.k_PrefabPath)).GetComponent<SpawnNozzle>();
                        var mouth = Field<Transform>(nozzle, "m_Mouth");
                        var cradle = Field<Transform>(nozzle, "m_Cradle");
                        var corner = container.transform.TransformPoint(new Vector3(-0.35f, 0.3f, -0.35f));
                        nozzle.transform.rotation = Quaternion.LookRotation((container.transform.position - corner).normalized);
                        nozzle.transform.position += corner - mouth.position;
                        Physics.SyncTransforms();
                        var ligand = made.First(m => m.GetComponent<CapsidSites>() == null);
                        ligand.transform.position = cradle.position + cradle.forward * (TunnelMoleculeMenu.WorldRadius(ligand.gameObject) + 0.01f);
                        Call(nozzle, "OnMoleculeReleased", ligand);
                        nozzle.SetCount(14);
                        nozzle.SpawnOrStop();
                    }
                }
                if (frame == 6)
                {
                    // 3D movie controls, the picker to Ligand binding, the capture radius slider, Start.
                    Field<Button>(containers, "m_MovieRow").onClick.Invoke();
                    Call(movie, "Update");
                    var panel = movie.panel;
                    if (run.startFirst)
                    {
                        // Start with Antibody action still chosen: the container holds EGFR and its ligands, so
                        // Ligand binding is chosen for them.
                        Field<Button>(panel, "m_StartRow").onClick.Invoke();
                        Check(!movie.isPlaying && movie.definition.id == MovieDefinition.k_LigandBindingId && movie.message.Contains("Ligand binding is chosen now"),
                            $"{label}: Start with Antibody action chosen and EGFR and ligands in the box chooses Ligand binding (\"{movie.message}\")");
                    }
                    for (int i = 0; i < 5 && movie.definition.id != MovieDefinition.k_LigandBindingId; i++)
                        Field<Button>(panel, "m_NextMovieButton").onClick.Invoke();
                    Call(panel, "Update");
                    Field<Slider>(panel, "m_DiameterSlider").value = run.radius;
                    Check(movie.definition.id == MovieDefinition.k_LigandBindingId && Mathf.Approximately(movie.captureRadius, run.radius),
                        $"{label}: Ligand binding chosen on the menu, capture radius {movie.captureRadius:0} Å");
                }
                // With the nozzle: Start once its spray is done (or after 15 s).
                bool spraying = nozzle != null && Field<Coroutine>(nozzle, "m_Spraying") != null && frame < 900;
                if (frame >= 8 && startedAt < 0f && !spraying)
                {
                    var panel = movie.panel;
                    Field<Button>(panel, "m_StartRow").onClick.Invoke();
                    startedAt = Time.time;
                    Check(movie.isPlaying && container.temperature > 0f, $"{label}: Start plays it, the container warmed from absolute zero ({movie.message})");
                    Check(movie.CountCapsids() == 1 && movie.CountAntibodies() >= 14, $"{label}: the menu counts 1 EGFR and {movie.CountAntibodies()} free ligands in the box ({boxSize * 100f:0} cm)");
                }
                if (startedAt < 0f)
                    return;
                float waited = Time.time - startedAt;
                bool bound = movie.CountBound() > 0;
                if (!bound && waited < run.limit)
                    return;
                EditorApplication.update -= Tick;
                if (run.required)
                    Check(bound, $"{label}: {(bound ? $"a ligand bound {waited:0.0} s after Start" : $"nothing bound in {run.limit:0} s")}");
                else
                    report.AppendLine($"  info {label}: {(bound ? $"a ligand bound {waited:0.0} s after Start" : $"nothing bound in {run.limit:0} s")}");
                Debug.Log($"[Movie] user-steps run {label}: {(bound ? $"first binding at {waited:0.0} s" : "no binding")}");
                // Clear contents from the Containers list, then the box goes.
                Field<Button>(containers, "m_ClearRow").onClick.Invoke();
                movie.SetControls(false);
                if (nozzle != null)
                    Object.Destroy(nozzle.gameObject);
                Object.Destroy(container.gameObject);
                Next();
            }
            EditorApplication.update += Tick;
        }

        Next();
        string sofar = report + "  (the runs follow in the log, a minute or two)";
        Debug.Log(sofar);
        return sofar;
    }
}
