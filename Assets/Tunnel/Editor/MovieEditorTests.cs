using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Checks the Antibody action (see <see cref="TunnelMovie"/>), the containers' Clear contents and the TS Menu's eraser
/// without a headset or a session: the green shift (none, half, all sites taken); a capsid taking an antibody on every
/// one of its sites (no limit of three) and turning fully green, then back as they let go; the binding time slider
/// (10 s to 1 min) applying to new bindings only; an antibody letting go when its time is up (not while paused),
/// pushed off its site and resting; Play again letting every antibody go, counting down 5 s and putting the starting
/// state back; Clear contents emptying the container but leaving it; the menu's name and its wall snap; and the
/// eraser: Delete calls it up and turns the label red, it deletes only while held, never itself, the menus or the
/// tunnel, nor a container whose wall it touches, and Delete again removes it. Runs from Tunnel > Movie > Test
/// Antibody Action (in Play mode); the Play again part finishes some seconds later and logs PASS or FAIL with the
/// tag [Movie].
/// </summary>
static class MovieEditorTests
{
    const BindingFlags k_Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";

    static object Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, k_Flags).Invoke(target, args.Length == 0 ? null : args);

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, k_Flags).GetValue(target);

    [MenuItem("Tunnel/Movie/Test Antibody Action (in Play mode)")]
    public static string Test()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Movie] enter Play mode (any scene) first, then run Tunnel > Movie > Test Antibody Action");
            return "needs Play mode";
        }

        var report = new StringBuilder("[Movie] Antibody action, Clear contents and eraser test (not connected)\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }

        // The green shift.
        var red = new Color(0.8f, 0.2f, 0.1f, 1f);
        var green = new Color(0.18f, 0.85f, 0.25f, 1f);
        bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a) < 1e-3f;
        Check(CapsidSites.CoveredFraction(0, 12) == 0f && CapsidSites.CoveredFraction(6, 12) == 0.5f && CapsidSites.CoveredFraction(12, 12) == 1f
            && CapsidSites.CoveredFraction(3, 0) == 0f && CapsidSites.CoveredFraction(20, 12) == 1f, "covered fraction: 0, half, all, no sites, more than all");
        Check(Near(CapsidSites.CoveredColour(red, green, 0f), red), "no sites taken: its own colour");
        Check(Near(CapsidSites.CoveredColour(red, green, 1f), green), "every site taken: green");
        Check(Near(CapsidSites.CoveredColour(red, green, 0.5f), Color.Lerp(red, green, 0.5f)), "half the sites: half way to green");
        Check(Mathf.Approximately(CapsidSites.CoveredColour(new Color(0.5f, 0.5f, 0.5f, 0.4f), green, 1f).a, 0.4f), "it keeps its own opacity");

        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>("Assets/Tunnel/TunnelPrefabs.asset");
        GameObject Prefab(string name) => registry.molecules.First(m => m.name == name).prefab;

        // A tunnel segment of its own (its TS Menu and Containers list), and a 1 m box away from everything.
        var origin = new Vector3(-40f, 1.5f, -40f);
        var segment = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(k_SegmentPath), origin + new Vector3(0f, -1.5f, 6f), Quaternion.identity);
        segment.name = "Movie Test Segment";
        var made = new List<GameObject> { segment };
        var box = Object.Instantiate(registry.containerBox, origin, Quaternion.identity);
        box.transform.localScale = Vector3.one;
        made.Add(box);
        var container = box.GetComponent<TunnelContainer>();
        var movie = container.movie;
        Check(movie != null && TunnelContainer.current == container, "the box carries the Antibody action and is the current container");

        GameObject Make(string name, Vector3 at, float magnification = 1f)
        {
            var prefab = Prefab(name);
            var go = Object.Instantiate(prefab, at, Quaternion.identity);
            go.transform.localScale = prefab.transform.localScale * magnification;
            made.Add(go);
            return go;
        }
        var polio = Make("2PLV", origin).GetComponent<CapsidSites>();
        var igg = Make("IGG", origin + new Vector3(0.35f, 0.3f, 0.3f));
        Physics.SyncTransforms();
        Call(polio, "Update");
        Check(polio.movie == movie && !polio.sitesActive, $"2PLV in the box: its sites are not live before Start ({polio.siteCount} sites)");

        // Start.
        movie.StartMovie();
        Call(polio, "Update");
        Check(movie.isPlaying && polio.sitesActive, $"Start plays it and makes the sites live (\"{movie.message}\")");
        Check(movie.startingState != null && movie.startingState.molecules.Count == 2 && movie.startingState.scenario == TunnelMovie.k_Title,
            $"the starting state is kept: {movie.startingState?.molecules.Count} molecules, \"{movie.startingState?.scenario}\"");

        // The binding time slider.
        Check(Mathf.Approximately(movie.bindingTime, 30f), $"binding time starts at 30 s ({movie.bindingTime})");
        movie.SetBindingTime(5f);
        Check(Mathf.Approximately(movie.bindingTime, 10f), "set to 5 s it is 10 s (the least)");
        movie.SetBindingTime(500f);
        Check(Mathf.Approximately(movie.bindingTime, 60f), "set to 500 s it is 1 min (the most)");
        movie.SetBindingTime(30f);

        // An antibody on every site: no limit of three, and fully green.
        CapsidSites.Binding BindingAt(int site)
        {
            var axis = polio.SiteAxis(site);
            var at = polio.transform.InverseTransformPoint(polio.SiteBase(site) + axis * 0.02f);
            var rotation = Quaternion.Inverse(polio.transform.rotation) * Quaternion.LookRotation(axis);
            return new CapsidSites.Binding
            {
                site = (byte)site, startPosition = at, startRotation = rotation, position = at, rotation = rotation,
                scale = igg.transform.lossyScale.x / polio.transform.lossyScale.x, volume = 0f,
            };
        }
        void Bind(int site)
        {
            var stand = new GameObject($"Stand-in Antibody {site + 1}");
            Call(polio, "Bind", stand, BindingAt(site));
        }
        var colours = Field<List<(Material material, Color color)>>(polio, "m_Colours");
        var covered = Field<Color>(polio, "m_CoveredColour");
        for (int i = 0; i < polio.siteCount; i++)
            Bind(i);
        Call(polio, "Update");
        Check(polio.bindingCount == polio.siteCount && polio.bindingCount > 4, $"every site takes an antibody: {polio.bindingCount} of {polio.siteCount}, more than the old limit of 3");
        Check(polio.fullyCovered && movie.CountCovered() == 1 && movie.CountBound() == polio.siteCount, "it is fully covered, and the menu counts it");
        Check(colours.Count > 0 && colours.All(c => Near(c.material.GetColor("_BaseColor"), CapsidSites.CoveredColour(c.color, covered, 1f))),
            $"every one of its own colours is the green ({colours.Count} materials)");
        Check(polio.transform.Cast<Transform>().Count(t => t.name.StartsWith("Bound Antibody")) == polio.siteCount, "everyone sees an antibody on every site");

        // Letting go when the time is up (counted only while playing).
        Check(Mathf.Abs(polio.TimeLeft(0) - 30f) < 0.5f, $"bound at 30 s: {polio.TimeLeft(0):0.0} s left");
        movie.SetBindingTime(10f);
        Check(Mathf.Abs(polio.TimeLeft(0) - 30f) < 0.5f, "moving the slider to 10 s leaves those already bound at 30 s");
        var free = Field<List<(Transform root, CapsidSites.Binding binding, float since)>>(polio, "m_Bound");
        polio.Release(0, "test");
        Call(polio, "Update");
        Bind(0); // now for 10 s
        Call(polio, "Update");
        Check(Mathf.Abs(polio.TimeLeft(0) - 10f) < 0.5f, $"a new binding takes the new time: {polio.TimeLeft(0):0.0} s");
        movie.Pause();
        polio.CountDownBindings(20f);
        Check(!polio.SiteOpen(0), "paused, the time doesn't run: still bound after 20 s");
        movie.Play();
        float half = polio.TimeLeft(0);
        polio.CountDownBindings(half - 0.1f);
        Check(!polio.SiteOpen(0), $"still bound 0.1 s before its time");
        var before = new HashSet<MoleculeGravity>(MoleculeGravity.all);
        var baseAt = polio.SiteBase(0);
        var axis0 = polio.SiteAxis(0);
        polio.CountDownBindings(0.2f);
        Call(polio, "Update");
        var released = MoleculeGravity.all.FirstOrDefault(m => m != null && !before.Contains(m));
        if (released != null)
            made.Add(released.gameObject);
        Check(polio.SiteOpen(0) && polio.bindingCount == polio.siteCount - 1, "when its time is up it lets go and the site is free");
        Check(released != null && released.TryGetComponent(out AntibodyBinder binder) && binder.resting, "it is a free antibody again, resting so it doesn't bind straight back");
        Check(released != null && Vector3.Dot(released.transform.position - baseAt, axis0) > 0f && Vector3.Dot(released.body.linearVelocity, axis0) > 0.1f,
            $"it starts outside the site and is pushed off along its axis ({(released != null ? released.body.linearVelocity.magnitude : 0f):0.00} m/s)");
        Check(Mathf.Abs(released != null ? released.transform.lossyScale.x - igg.transform.lossyScale.x : 1f) < 1e-4f, "at the size it was bound at");
        Check(!polio.fullyCovered && colours.All(c => Near(c.material.GetColor("_BaseColor"), CapsidSites.CoveredColour(c.color, covered, polio.coveredFraction))),
            $"its colour goes back a step ({polio.coveredFraction:P0} green)");
        Check(polio.transform.Cast<Transform>().Count(t => t.name.StartsWith("Bound Antibody") && t.parent == polio.transform) == polio.siteCount - 1, "its bound antibody is gone from the capsid");

        // The menu: its name, and let go of near a wall it lies on the wall.
        movie.SetControls(true);
        Call(movie, "Update");
        var panel = movie.panel;
        Check(panel != null && panel.gameObject.activeSelf, "3D movie controls shows its menu");
        if (panel != null)
        {
            Call(panel, "Update");
            Check(Field<TMP_Text>(panel, "m_Title").text.StartsWith(TunnelMovie.k_Title), $"the menu is called \"{TunnelMovie.k_Title}\"");
            Check(Field<Button>(panel, "m_PlayAgainRow") != null && Field<Slider>(panel, "m_BindingTimeSlider") != null && Field<XRGrabInteractable>(panel, "m_Grab") != null,
                "it has Play again, the Binding time slider and a bar to carry it by");
            var geometry = segment.GetComponentInChildren<UnityEngine.XR.Templates.MRTTabletopAssets.TunnelSegmentGeometry>();
            var frame = geometry.transform;
            panel.transform.SetPositionAndRotation(frame.TransformPoint(new Vector3(geometry.width * 0.5f - 0.15f, 1.3f, 0f)), Quaternion.Euler(10f, 50f, 5f));
            bool onWall = panel.Settle();
            var face = -panel.transform.forward;
            Check(onWall && Vector3.Angle(face, frame.TransformDirection(Vector3.left)) < 0.5f && movie.panelMoved,
                "let go of 15 cm from the wall, the menu lies flat on it facing in, for everyone");
            Check(Mathf.Abs(frame.InverseTransformPoint(panel.transform.position).x - (geometry.width * 0.5f - panel.back)) < 0.002f, "its back just off the wall");
            movie.SetControls(false);
            Check(!movie.panelMoved, "hiding the controls puts it back beside the container next time");
        }

        // The Containers list's rows.
        var containerMenu = segment.GetComponent<TunnelContainerMenu>();
        containerMenu.ShowList(true);
        Call(containerMenu, "Update");
        var movieRow = Field<Button>(containerMenu, "m_MovieRow");
        var clearRow = Field<Button>(containerMenu, "m_ClearRow");
        Check(movieRow.GetComponentInChildren<TMP_Text>(true).text.Contains("3D movie controls"), "the Containers list's row reads 3D movie controls");
        Check(clearRow != null && clearRow.interactable, "the Containers list has Clear contents");

        // Play again: all let go at once, a 5 s countdown, then the starting state.
        int boundBefore = polio.bindingCount;
        var beforePlayAgain = new HashSet<MoleculeGravity>(MoleculeGravity.all);
        movie.PlayAgain();
        foreach (var m in MoleculeGravity.all)
            if (m != null && !beforePlayAgain.Contains(m))
                made.Add(m.gameObject);
        Call(polio, "Update");
        Check(movie.isRestarting && movie.countdown == 5, $"Play again: counting down from {movie.countdown} s");
        Check(polio.bindingCount == 0 && movie.CountAntibodies() >= boundBefore, $"every bound antibody lets go at once ({boundBefore}; {movie.CountAntibodies()} free now)");
        Check(!polio.sitesActive, "no binding during the countdown");

        double started = EditorApplication.timeSinceStartup;
        void Watch()
        {
            if (!Application.isPlaying)
            {
                EditorApplication.update -= Watch;
                Debug.LogWarning("[Movie] test stopped: Play mode ended");
                return;
            }
            bool timedOut = EditorApplication.timeSinceStartup - started > 12.0;
            if (movie != null && movie.isRestarting && !timedOut)
                return;
            EditorApplication.update -= Watch;
            Check(!timedOut && movie.isPlaying, $"after the countdown it plays again ({EditorApplication.timeSinceStartup - started:0.0} s)");
            var inside = container.Contents(true);
            var names = inside.Select(g => CapsidSites.MoleculeName(g)).OrderBy(n => n).ToList();
            Check(names.SequenceEqual(new[] { "2PLV", "IGG" }), $"the starting state is back: {string.Join(", ", names)}");
            var newPolio = inside.Select(g => g.GetComponent<CapsidSites>()).FirstOrDefault(c => c != null);
            Check(newPolio != null && newPolio.bindingCount == 0 && Vector3.Distance(newPolio.transform.position, origin) < 0.01f, "2PLV is back where it started, with nothing bound");
            made.AddRange(inside);

            // Clear contents, from the Containers list.
            clearRow.onClick.Invoke();
            Check(container != null && container.Contents().Count == 0, "Clear contents empties it");
            Check(container != null && container.gameObject.activeInHierarchy, "the container itself stays");

            var gone = new List<GameObject>();
            TestEraser(segment, registry, origin + new Vector3(3f, 0f, 0f), made, gone, Check);
            // Destroyed things go at the end of a frame: look a moment later.
            double erasedAt = EditorApplication.timeSinceStartup;
            void After()
            {
                if (Application.isPlaying && EditorApplication.timeSinceStartup - erasedAt < 0.5)
                    return;
                EditorApplication.update -= After;
                Check(TunnelEraser.current == null, "Delete again removes the eraser");
                Check(gone.Count == 2 && gone.All(g => g == null), $"what it touched is gone ({gone.Count(g => g == null)} of {gone.Count})");
                foreach (var go in made)
                    if (go != null)
                        Object.Destroy(go);
                report.AppendLine(failures == 0 ? "  PASS" : $"  FAIL ({failures})");
                Debug.Log(report.ToString());
            }
            EditorApplication.update += After;
        }
        EditorApplication.update += Watch;
        string sofar = report.ToString() + "  (Play again counting down: the result follows in the log)";
        Debug.Log(sofar);
        return sofar;
    }

    static void TestEraser(GameObject segment, TunnelPrefabRegistry registry, Vector3 at, List<GameObject> made, List<GameObject> gone, System.Action<bool, string> Check)
    {
        var menu = segment.GetComponentInChildren<TunnelMenu>(true);
        if (TunnelEraser.current != null)
            Object.DestroyImmediate(TunnelEraser.current.gameObject);
        Check(!TunnelMenu.eraserMode && !TunnelMenu.deleteMode, "Delete starts off");
        Call(menu, "ToggleDeleteMode");
        Call(menu, "Update");
        var eraser = TunnelEraser.current;
        if (eraser != null)
            made.Add(eraser.gameObject);
        Check(TunnelMenu.eraserMode && eraser != null, "Delete calls up the eraser");
        Check(Field<TMP_Text>(menu, "m_DeleteLabel").text.Contains("Delete: ON") && Field<TMP_Text>(menu, "m_DeleteLabel").text.Contains("#F55"),
            "and its label turns red: Delete: ON");
        Check(!TunnelMenu.deleteMode, "grabbing things no longer deletes them");
        if (eraser == null)
            return;
        var head = Camera.main != null ? Camera.main.transform : null;
        if (head != null)
            Check(Vector3.Distance(eraser.transform.position, head.position) < 0.6f, "it appears in front of whoever pressed Delete");

        var felt = Field<Transform>(eraser, "m_Felt");
        var prefab = registry.molecules.First(m => m.name == "IGG").prefab;
        var target = Object.Instantiate(prefab, at, Quaternion.identity);
        made.Add(target);
        var targetCollider = target.GetComponentInChildren<Collider>();
        void Touch(Vector3 point)
        {
            // The felt's face (its -Y side) on the point.
            eraser.transform.SetPositionAndRotation(point + Vector3.up * (felt.lossyScale.y + 0.002f) - (felt.position - eraser.transform.position), Quaternion.identity);
            Physics.SyncTransforms();
        }
        Touch(targetCollider.bounds.center + Vector3.up * targetCollider.bounds.extents.y * 0.5f);
        Check(eraser.EraseTouched() == 0 && target.activeInHierarchy, "lying loose, touching an antibody, it deletes nothing");

        // Held in a hand.
        var manager = Object.FindFirstObjectByType<XRInteractionManager>();
        var hand = new GameObject("Eraser Test Hand");
        made.Add(hand);
        hand.transform.position = eraser.transform.position;
        hand.SetActive(false);
        var interactor = hand.AddComponent<XRDirectInteractor>();
        if (manager != null)
            interactor.interactionManager = manager;
        hand.SetActive(true);
        var grab = eraser.GetComponent<XRGrabInteractable>();
        // A scene without a network manager (the app always has one): the network interactable's grab callback needs
        // it, so it sits this test out.
        if (Unity.Netcode.NetworkManager.Singleton == null)
            foreach (var network in eraser.GetComponents<XRMultiplayer.NetworkBaseInteractable>())
                network.enabled = false;
        if (manager != null)
            manager.SelectEnter((IXRSelectInteractor)interactor, (IXRSelectInteractable)grab);
        Check(eraser.held, "taken in a hand, it is held");
        Touch(targetCollider.bounds.center + Vector3.up * targetCollider.bounds.extents.y * 0.5f);
        Check(eraser.EraseTouched() == 1, "held, touching the antibody deletes it");
        gone.Add(target);
        Check(eraser != null && eraser.gameObject.activeInHierarchy && eraser.erased == 1, "it never deletes itself");

        // Things it must not delete.
        var panelPrefab = AssetDatabase.LoadAssetAtPath<TunnelMoviePanel>("Assets/Tunnel/Movie/Movie_Panel.prefab");
        var bar = panelPrefab.GetComponentInChildren<BoxCollider>(true);
        Check(TunnelEraser.DeletableAt(bar) == null, "not the Antibody action menu");
        var segmentColliders = segment.GetComponentsInChildren<Collider>(true);
        Check(segmentColliders.Length > 0 && segmentColliders.All(c => TunnelEraser.DeletableAt(c) == null), $"not the tunnel segment or its TS Menu ({segmentColliders.Length} colliders)");
        Check(TunnelEraser.DeletableAt(eraser.GetComponentInChildren<Collider>()) == null, "not the eraser");

        // A container's wall (inside, away from its edges) isn't its edge: touching it there leaves it.
        var box = Object.Instantiate(registry.containerBox, at + new Vector3(2f, 0f, 0f), Quaternion.identity);
        box.transform.localScale = Vector3.one;
        made.Add(box);
        Physics.SyncTransforms();
        Touch(box.transform.TransformPoint(new Vector3(0f, -0.5f, 0f)) + Vector3.up * 0.005f);
        Check(eraser.EraseTouched() == 0 && box.activeInHierarchy, "touching a container's floor from inside leaves it");
        Touch(box.transform.TransformPoint(new Vector3(0f, 0.5f, 0.5f)));
        Check(eraser.EraseTouched() == 1, "touching its top front edge deletes it");
        gone.Add(box);

        if (manager != null)
            manager.SelectExit((IXRSelectInteractor)interactor, (IXRSelectInteractable)grab);
        Check(!eraser.held, "let go of, it is no longer held");

        // Delete again: the eraser goes and the label turns back.
        Call(menu, "ToggleDeleteMode");
        Call(menu, "Update");
        Check(!TunnelMenu.eraserMode && Field<TMP_Text>(menu, "m_DeleteLabel").text == "Delete", "Delete again turns it off and the label back");
    }
}
