using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Checks the Molecules list's ×20 Magnification slider and 3D movies as data (see <see cref="MovieDefinition"/>,
/// <see cref="TunnelNewMoviePanel"/>) without a headset or a session: the slider's stops (×1 to ×10 one at a time, then
/// to ×20), a molecule placed at ×20 and its capture sphere and grab collider at that size; the two built-in movies'
/// receptors, binders and colours; the movie menu listing them; New movie... opening the New movie menu beside it with
/// the receptors that have binding-site data; a movie made on it (TBSV and IGG, two and twelve, 20% sites, 15 s, solid
/// magenta, viscosity 0.5, room temperature) created: stored, listed, chosen, the container filled at the Molecules
/// list's magnification and set as made; played (its receptors' sites live, solid magenta when an antibody binds),
/// Play again putting its starting state back, its starting state saved under its name; the picker stopping it to
/// choose another; still there after the store is read again (a restart); Delete asking first, then deleting it for
/// everyone and the container going back to Antibody action; and a built-in movie not deletable. Works in a folder of its
/// own (the app's movies are untouched). Runs from Tunnel > Movie > Test New Movie (in Play mode); the Play again part
/// finishes some seconds later and logs PASS or FAIL with the tag [Movie].
/// </summary>
static class NewMovieEditorTests
{
    const BindingFlags k_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const float k_UnitsPerAngstrom = 0.2f / 167f;

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, k_Flags).GetValue(target);

    static object Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, k_Flags).Invoke(target, args.Length == 0 ? null : args);

    static void Press(object owner, string button) => Field<Button>(owner, button).onClick.Invoke();

    static void Field<TOwner, T>(TOwner target, string name, T value) => target.GetType().GetField(name, k_Flags).SetValue(target, value);

    [MenuItem("Tunnel/Movie/Test New Movie (in Play mode)")]
    public static string Test()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Movie] enter Play mode (any scene) first, then run Tunnel > Movie > Test New Movie");
            return "needs Play mode";
        }
        var report = new StringBuilder("[Movie] New movie and ×20 magnification test (not connected)\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }
        bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 2e-3f;

        string testFolder = Path.Combine(Application.temporaryCachePath, "Movie definitions test");
        if (Directory.Exists(testFolder))
            Directory.Delete(testFolder, true);
        MovieLibrary.folder = testFolder;
        MovieLibrary.Reload();
        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>("Assets/Tunnel/TunnelPrefabs.asset");
        var made = new List<GameObject>();

        // The Magnification slider, ×1 to ×20.
        var origin = new Vector3(-60f, 1.5f, 60f);
        var segment = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(k_SegmentPath), origin + new Vector3(0f, -1.5f, 6f), Quaternion.identity);
        segment.name = "New Movie Test Segment";
        made.Add(segment);
        var moleculeMenu = segment.GetComponent<TunnelMoleculeMenu>();
        moleculeMenu.ShowList(true);
        var slider = Field<Slider>(moleculeMenu, "m_MagnificationSlider");
        var text = Field<TMP_Text>(moleculeMenu, "m_MagnificationText");
        Check(slider.minValue == 0f && slider.maxValue == TunnelMoleculeMenu.k_Steps.Length - 1 && TunnelMoleculeMenu.MagnificationAt(slider.maxValue) == 20,
            $"the slider has {TunnelMoleculeMenu.k_Steps.Length} stops, ×1 to ×20");
        slider.value = 1;
        Check(TunnelMoleculeMenu.currentMagnification == 2 && text.text.Contains("×2"), $"its second stop is ×2 ({text.text}): the low end goes one at a time");
        slider.value = 9;
        Check(TunnelMoleculeMenu.currentMagnification == 10, "its tenth stop is ×10");
        slider.value = slider.maxValue;
        Check(TunnelMoleculeMenu.currentMagnification == 20 && text.text.Contains("×20"), $"its last stop is ×20 ({text.text})");
        var ticks = slider.transform.parent.Find("Ticks");
        Check(ticks != null && ticks.childCount == TunnelMoleculeMenu.k_Steps.Length && ticks.GetChild(ticks.childCount - 1).GetComponent<TMP_Text>().text == "20",
            $"its ticks run 1 to 20 ({(ticks != null ? ticks.childCount : 0)} ticks)");
        var content = Field<RectTransform>(moleculeMenu, "m_ListContent");
        Button Row(string molecule) => content.GetComponentsInChildren<Button>(true).First(b => b.name == "Row " + molecule);
        var before = new HashSet<MoleculeGravity>(Object.FindObjectsByType<MoleculeGravity>(FindObjectsInactive.Include));
        Row("EGFR").onClick.Invoke();
        Row("EGFR ligand").onClick.Invoke();
        var placed = Object.FindObjectsByType<MoleculeGravity>(FindObjectsInactive.Include).Where(m => !before.Contains(m)).ToList();
        made.AddRange(placed.Select(m => m.gameObject));
        var bigEgfr = placed.Select(m => m.GetComponent<CapsidSites>()).FirstOrDefault(c => c != null);
        var bigLigand = placed.FirstOrDefault(m => m.GetComponent<CapsidSites>() == null);
        Check(bigEgfr != null && Mathf.Abs(bigEgfr.transform.localScale.x - 0.15f * 20f) < 1e-4f,
            $"EGFR placed at ×20 is twenty times its ×1 size (scale {bigEgfr.transform.localScale.x:0.00})");
        Check(bigEgfr != null && Mathf.Abs(bigEgfr.captureRadius - 10f * k_UnitsPerAngstrom * 3f) < 1e-5f,
            $"its capture sphere is 10 Å at ×20 ({bigEgfr.captureRadius * 100f:0.0} cm)");
        if (bigLigand != null && bigLigand.TryGetComponent(out MoleculeGrabSize grab))
        {
            grab.Fit();
            Check(bigLigand.GetComponentInChildren<BoxCollider>().transform.localScale == Vector3.one, "the ligand at ×20 needs no grab stretch (its collider fits it)");
        }
        foreach (var m in placed)
            Object.Destroy(m.gameObject);
        slider.value = 0;
        Check(TunnelMoleculeMenu.currentMagnification == 1, "back to ×1");

        // The built-in movies.
        GameObject Prefab(string name) => registry.molecules.First(m => m.name == name).prefab;
        CapsidSites Sites(string name) => Prefab(name).GetComponent<CapsidSites>();
        var antibody = MovieDefinition.AntibodyAction;
        var ligandBinding = MovieDefinition.LigandBinding;
        Check(new[] { "2PLV", "SARS", "TBSV", "Zika" }.All(v => antibody.Includes(Sites(v))) && !antibody.Includes(Sites("EGFR")),
            "Antibody action's receptors are the four viruses, not EGFR");
        Check(ligandBinding.Includes(Sites("EGFR")) && !ligandBinding.Includes(Sites("2PLV")), "Ligand binding's receptor is EGFR alone");
        Check(antibody.IsBinder(Prefab("IGG")) && !antibody.IsBinder(Prefab("EGFR ligand")) && ligandBinding.IsBinder(Prefab("EGFR ligand")), "their binders: IGG, EGFR ligand");
        Check(antibody.colourEffect == MovieColourEffect.ShiftBySitesTaken && ligandBinding.colourEffect == MovieColourEffect.SolidWhileBound,
            "the viruses shift toward green by sites taken; EGFR is light blue while bound");

        // A box with its movie menu.
        var box = Object.Instantiate(registry.containerBox, origin, Quaternion.identity);
        box.transform.localScale = Vector3.one * 0.5f;
        made.Add(box);
        var container = box.GetComponent<TunnelContainer>();
        var movie = container.movie;
        Check(movie.movies.Select(m => m.name).SequenceEqual(new[] { "Antibody action", "Ligand binding" }), "the menu lists the two built-in movies");
        movie.SetControls(true);
        Call(movie, "Update");
        var panel = movie.panel;
        Call(panel, "Update");
        Press(panel, "m_NextMovieButton");
        Check(movie.definition.id == MovieDefinition.k_LigandBindingId && TunnelMoleculeMenu.currentMagnification == 20 && text.text.Contains("×20"),
            $"► chooses Ligand binding, and the Molecules list goes to its ×20 ({text.text})");
        Call(panel, "Update");
        string pickerText = Field<Button>(panel, "m_ScenarioRow").GetComponentInChildren<TMP_Text>(true).text;
        Check(pickerText.Contains("Ligand binding ×20") && pickerText.Contains("2 of 2"), $"the picker shows its magnification ({pickerText})");
        Press(panel, "m_NextMovieButton");
        Check(movie.definition.id == MovieDefinition.k_AntibodyActionId && TunnelMoleculeMenu.currentMagnification == 1,
            "► again goes round to Antibody action, and the list to its ×1");

        // New movie...
        Press(panel, "m_NewMovieRow");
        var menu = panel.newMovieMenu;
        Check(menu != null && menu.gameObject.activeSelf, "New movie... opens the New movie menu");
        if (menu == null)
            return Finish();
        float apart = Vector3.Distance(menu.transform.position + menu.transform.up * (menu.height * 0.5f), panel.transform.position + panel.transform.up * (panel.height * 0.5f));
        Check(Mathf.Abs(apart - (panel.width + menu.width) * 0.5f - 0.02f) < 0.005f && Quaternion.Angle(menu.transform.rotation, panel.transform.rotation) < 0.1f,
            $"beside the movie menu, tops level, turned as it is ({apart * 100f:0.0} cm between their middles)");
        Check(Vector3.Dot(menu.transform.position - panel.transform.position, panel.awayFromContainer) > 0f, "on the side away from the container");
        var names = menu.receptors.Select(r => $"{r.name}+{r.binder}").ToList();
        Check(names.Contains("EGFR+EGFR ligand") && names.Contains("TBSV+IGG") && names.Contains("SARS+IGG") && !names.Any(n => n.StartsWith("IGG") || n.StartsWith("EGFR ligand")),
            $"receptors offered with their binders: {string.Join(", ", names)}");
        Check(menu.draft.receptor == "EGFR" && menu.draft.binder == "EGFR ligand" && menu.draft.colourEffect == MovieColourEffect.SolidWhileBound,
            "it starts on EGFR and its ligand, solid while bound");
        for (int i = 0; i < 10 && menu.draft.receptor != "TBSV"; i++)
            Press(menu, "m_ReceptorNext");
        Check(menu.draft.receptor == "TBSV" && menu.draft.binder == "IGG" && !menu.draft.pocket, "► on the receptor row chooses TBSV, and its binder is IGG");
        menu.SetName("Test movie\nsecond line");
        Field<Slider>(menu, "m_ReceptorsSlider").value = 2;
        Field<Slider>(menu, "m_BindersSlider").value = 12;
        Field<Slider>(menu, "m_StrengthSlider").value = 20;
        Field<Slider>(menu, "m_TimeSlider").value = 15;
        Press(menu, "m_SolidRow");
        Field<Button[]>(menu, "m_Swatches")[4].onClick.Invoke();
        Field<Slider>(menu, "m_ViscositySlider").value = 5;
        Field<Slider>(menu, "m_HeatSlider").value = TunnelContainer.k_RoomTemperature;
        Check(menu.draft.magnification == 1, $"its magnification starts at the Molecules list's (×{menu.draft.magnification})");
        Field<Slider>(menu, "m_MagnificationSlider").value = TunnelMoleculeMenu.StepOf(2);
        Call(menu, "Update");
        var draft = menu.draft;
        Check(draft.name == "Test movie second line" && draft.receptors == 2 && draft.binders == 12 && Mathf.Approximately(draft.siteDiameter, 0.2f)
            && Mathf.Approximately(draft.bindingTime, 15f) && draft.colourEffect == MovieColourEffect.SolidWhileBound && Near(draft.colour, TunnelNewMoviePanel.k_Colours[4])
            && Mathf.Approximately(draft.viscosity, 0.5f) && Mathf.Approximately(draft.temperature, 10f),
            $"its settings: \"{draft.name}\", {draft.receptors} {draft.receptor}, {draft.binders} {draft.binder}, {draft.siteDiameter * 100f:0}%, {draft.bindingTime:0} s, viscosity {draft.viscosity}");
        Check(Field<TMP_Text>(menu, "m_StrengthText").text.StartsWith("Binding site diameter: 20%"), "its slider reads as the sites' size for a virus");
        menu.SetName("Test movie");

        // Create.
        Press(menu, "m_CreateButton");
        Check(!menu.gameObject.activeSelf, "Create puts the New movie menu away");
        var stored = MovieLibrary.All();
        Check(stored.Count == 1 && stored[0].magnification == 2, $"it keeps its own magnification (×{(stored.Count > 0 ? stored[0].magnification : 0)})");
        Check(TunnelMoleculeMenu.currentMagnification == 2, "chosen, it sets the Molecules list to ×2");
        Check(stored.Count == 1 && stored[0].name == "Test movie" && File.Exists(Path.Combine(testFolder, stored[0].id + ".json")),
            $"the movie is stored on the host: {string.Join(", ", stored.Select(m => m.name))}");
        Check(movie.movies.Count == 3 && movie.movies[2].name == "Test movie", "the menu lists it after the built-in two");
        Check(movie.definition.name == "Test movie" && movie.title == "Test movie" && movie.state == TunnelMovie.State.Ready, $"it is chosen here (\"{movie.message}\")");
        Physics.SyncTransforms();
        var inside = container.Contents(true);
        int tbsv = inside.Count(g => CapsidSites.MoleculeName(g) == "TBSV"), iggs = inside.Count(g => CapsidSites.MoleculeName(g) == "IGG");
        Check(tbsv == 2 && iggs == 12, $"the container is filled: {tbsv} TBSV and {iggs} IGG");
        Check(inside.All(g => Mathf.Abs(movie.MagnificationOf(g) - 2f) < 0.01f), "all at the movie's own ×2");
        Check(movie.sizeNote.Length == 0 && !movie.canMatchSize, "they match its size: no size note, Match size off");
        Check(inside.All(g => g.GetComponent<Rigidbody>().constraints == RigidbodyConstraints.None && g.GetComponent<Rigidbody>().linearVelocity.magnitude > 0.01f),
            "scattered and moving, none parked");
        Check(Mathf.Approximately(container.viscosity, 0.5f) && Mathf.Approximately(container.temperature, 10f), "the container's viscosity and heat are as made");
        Check(Mathf.Approximately(movie.siteDiameter, 0.2f) && Mathf.Approximately(movie.bindingTime, 15f), "the movie menu's sliders take its settings");
        made.AddRange(inside);
        var receptors = inside.Select(g => g.GetComponent<CapsidSites>()).Where(c => c != null).ToList();

        // Playing it.
        movie.StartMovie();
        foreach (var r in receptors)
            Call(r, "Update");
        Check(movie.isPlaying && receptors.All(r => r.sitesActive), $"Start plays it and makes TBSV's sites live ({movie.message})");
        var first = receptors[0];
        var axis = first.SiteAxis(0);
        var at = first.transform.InverseTransformPoint(first.SiteBase(0) + axis * 0.02f);
        var turn = Quaternion.Inverse(first.transform.rotation) * Quaternion.LookRotation(axis);
        Call(first, "Bind", new GameObject("Stand-in Antibody"), new CapsidSites.Binding { site = 0, startPosition = at, startRotation = turn, position = at, rotation = turn, scale = 1f });
        Call(first, "Update");
        var colours = Field<List<(Material material, Color color)>>(first, "m_Colours");
        Check(first.bindingCount == 1 && colours.Count > 0 && colours.All(c => Near(c.material.GetColor("_BaseColor"), TunnelNewMoviePanel.k_Colours[4])),
            $"one antibody bound of {first.siteCount} sites: TBSV is solid magenta");

        // Saving its starting state, under its name.
        var snapshot = movie.StateToSave(out bool atStart);
        string savedName = TunnelMovie.NextName(movie.title);
        string savedPath = movie.Save(savedName, snapshot);
        var saved = JsonUtility.FromJson<MovieSnapshot>(File.ReadAllText(savedPath));
        Check(atStart && savedName.StartsWith("Test movie ") && saved.scenario == "Test movie" && saved.movie == stored[0].id && saved.molecules.Count == 14
            && saved.magnification == 2 && Mathf.Abs(saved.molecules[0].scale - 0.3f) < 1e-3f,
            $"its starting state saves as \"{savedName}\" with {saved.molecules.Count} molecules");
        File.Delete(savedPath);

        // Play again.
        var beforeAgain = new HashSet<MoleculeGravity>(MoleculeGravity.all);
        movie.PlayAgain();
        foreach (var m in MoleculeGravity.all)
            if (m != null && !beforeAgain.Contains(m))
                made.Add(m.gameObject);
        Check(movie.isRestarting && first.bindingCount == 0, "Play again lets go and counts down");
        double started = EditorApplication.timeSinceStartup;
        void Watch()
        {
            if (!Application.isPlaying)
            {
                EditorApplication.update -= Watch;
                return;
            }
            bool timedOut = EditorApplication.timeSinceStartup - started > 12.0;
            if (movie != null && movie.isRestarting && !timedOut)
                return;
            EditorApplication.update -= Watch;
            Check(!timedOut && movie.isPlaying && movie.title == "Test movie", "after the countdown it plays Test movie again");
            var again = container.Contents(true);
            made.AddRange(again);
            Check(again.Count(g => CapsidSites.MoleculeName(g) == "TBSV") == 2 && again.Count(g => CapsidSites.MoleculeName(g) == "IGG") == 12, "its starting state is back: 2 TBSV, 12 IGG");

            // The picker stops it to choose another.
            Call(panel, "Update");
            Press(panel, "m_NextMovieButton");
            Check(movie.state == TunnelMovie.State.Ready && movie.definition.id == MovieDefinition.k_AntibodyActionId, "► while it plays stops it and chooses Antibody action");
            Press(panel, "m_PrevMovieButton");
            Check(movie.title == "Test movie", "◄ chooses Test movie again");

            // A movie stored before movies had a magnification still loads, and uses the Molecules list's.
            File.WriteAllText(Path.Combine(testFolder, "user-old.json"),
                "{\"id\":\"user-old\",\"name\":\"Old movie\",\"builtIn\":false,\"receptor\":\"EGFR\",\"binder\":\"EGFR ligand\",\"pocket\":true,\"receptors\":1,\"binders\":10,"
                + "\"captureRadius\":10.0,\"siteDiameter\":0.1,\"bindingTime\":30.0,\"colourEffect\":1,\"colour\":{\"r\":0.55,\"g\":0.82,\"b\":1.0,\"a\":1.0},"
                + "\"hasStart\":true,\"viscosity\":1.0,\"temperature\":10.0,\"motion\":\"Random\"}");
            MovieLibrary.Reload();
            var old = MovieLibrary.Find("user-old");
            Check(old != null && old.magnification == 0 && old.sizeToUse == TunnelMoleculeMenu.currentMagnification && old.sizeLabel == "",
                $"an older movie with no magnification loads and uses the list's (×{old?.sizeToUse})");
            MovieLibrary.Delete("user-old");

            // A restart: the store is read again.
            MovieLibrary.Reload();
            Check(MovieLibrary.All().Any(m => m.name == "Test movie"), "after a restart it is still there");

            // Delete: asks first, then deletes it for everyone; a built-in movie can't be.
            Press(panel, "m_DeleteMovieRow");
            Check(MovieLibrary.All().Count == 1, "the first tap of Delete only asks");
            Press(panel, "m_DeleteMovieRow");
            Check(MovieLibrary.All().Count == 0 && !Directory.GetFiles(testFolder).Any(), "the second deletes it");
            Check(movie.definition.id == MovieDefinition.k_AntibodyActionId && movie.movies.Count == 2, "the container goes back to Antibody action; two movies listed");
            Press(panel, "m_DeleteMovieRow");
            Press(panel, "m_DeleteMovieRow");
            Check(movie.movies.Count == 2 && movie.definition.id == MovieDefinition.k_AntibodyActionId, "Antibody action can't be deleted");
            TestMatchSize();
            Finish();
        }
        EditorApplication.update += Watch;
        string sofar = report + "  (Play again counting down: the result follows in the log)";
        Debug.Log(sofar);
        return sofar;

        // Match size: EGFR at ×10 in a box, Ligand binding (×20) chosen.
        void TestMatchSize()
        {
            container.ClearContents();
            slider.value = TunnelMoleculeMenu.StepOf(10);
            var egfrGo = Object.Instantiate(Prefab("EGFR"), box.transform.position + new Vector3(0.05f, 0.02f, -0.03f), Quaternion.identity);
            egfrGo.transform.localScale = Prefab("EGFR").transform.localScale * 10f;
            made.Add(egfrGo);
            var egfr = egfrGo.GetComponent<CapsidSites>();
            Physics.SyncTransforms();
            Press(panel, "m_NextMovieButton");
            Call(egfr, "Update");
            Check(movie.definition.id == MovieDefinition.k_LigandBindingId && TunnelMoleculeMenu.currentMagnification == 20, "Ligand binding chosen: the list is ×20");
            Check(movie.sizeNote == "EGFR here is ×10; this movie is ×20." && movie.canMatchSize, $"the menu says so (\"{movie.sizeNote}\") and offers Match size");
            Check(Mathf.Abs(egfr.captureRadius - 10f * k_UnitsPerAngstrom * 1.5f) < 1e-5f, "the ×10 EGFR keeps a ×10 capture sphere in a ×20 movie");
            Call(panel, "Update");
            Field<TunnelMoviePanel, float>(panel, "m_NextRefresh", 0f);
            Call(panel, "Update");
            var matchRow = Field<Button>(panel, "m_MatchSizeRow");
            Check(matchRow != null && matchRow.interactable && matchRow.GetComponentInChildren<TMP_Text>(true).text == "Match ×20"
                && Field<TMP_Text>(panel, "m_Status").text.Contains("EGFR here is ×10"), "its Match size button and status line show it");
            var offset = box.transform.InverseTransformPoint(egfrGo.transform.position);
            matchRow.onClick.Invoke();
            Physics.SyncTransforms();
            Call(egfr, "Update");
            Check(Mathf.Abs(egfrGo.transform.localScale.x - Prefab("EGFR").transform.localScale.x * 20f) < 1e-4f, $"Match size makes EGFR ×20 ({movie.message})");
            Check((box.transform.InverseTransformPoint(egfrGo.transform.position) - offset).magnitude < 1e-3f, "staying where it was in the container");
            Check(movie.sizeNote.Length == 0 && !movie.canMatchSize, "and the size note goes");
            Check(Mathf.Abs(egfr.captureRadius - 10f * k_UnitsPerAngstrom * 3f) < 1e-5f, "its capture sphere is ×20 with it");
        }

        string Finish()
        {
            movie?.SetControls(false);
            foreach (var go in made)
                if (go != null)
                    Object.Destroy(go);
            MovieLibrary.folder = null;
            MovieLibrary.Reload();
            report.AppendLine(failures == 0 ? "  PASS" : $"  FAIL ({failures})");
            Debug.Log(report.ToString());
            return report.ToString();
        }
    }
}
