using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TLab.WebView;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Templates.MRTTabletopAssets;
using XRMultiplayer;

/// <summary>
/// Takes a snapshot of everything set up in the tunnel (see <see cref="TunnelSavedState"/>) and puts one back, for the
/// TS Menu's Saved states list (see <see cref="TunnelSaveStateMenu"/>). Both happen on the host (or here, when not
/// connected): the host sees every player's things where they are, and what it puts back is spawned for everyone.
/// Putting a state back first takes away everything now in the tunnel (on the walls too, unlike Clear all), sets the
/// number of segments (adding them at the tunnel's end, or removing them from it), gives each its colour, texture and
/// paint, then makes every saved thing again where and as it was. It takes a few frames (paint is replayed a few
/// thousand stamps a frame), so it runs on a small helper object of its own that no segment removal can take away.
/// </summary>
public class TunnelSaveState : MonoBehaviour
{
    /// <summary>The prefabs a state is made from (the TS Menu's own, given by the Saved states list).</summary>
    public struct Prefabs
    {
        public TunnelPrefabRegistry registry;
        public GameObject splatBox;
        public SpawnNozzle nozzle;
        public TunnelFigure figure;
        public TunnelSign sign;
    }

    static TunnelSaveState s_Runner;

    /// <summary>Whether a state is being put back now (here, on the host).</summary>
    public static bool busy { get; private set; }

    static bool online => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    static TunnelSaveState runner
    {
        get
        {
            if (s_Runner == null)
            {
                var go = new GameObject("Saved States Runner") { hideFlags = HideFlags.HideInHierarchy };
                s_Runner = go.AddComponent<TunnelSaveState>();
            }
            return s_Runner;
        }
    }

    /// <summary>Runs a coroutine on the helper object (also used by the Editor test).</summary>
    public static Coroutine Run(IEnumerator routine) => runner.StartCoroutine(routine);

    #region Segments

    /// <summary>
    /// The tunnel's segments in order, each by its geometry (the frame things are kept in): every tunnel from its first
    /// segment to its last, the longest tunnel first; segments that aren't linked come last.
    /// </summary>
    public static List<TunnelSegmentGeometry> Segments()
    {
        var links = FindObjectsByType<TunnelSegmentLink>(FindObjectsSortMode.None).Where(l => l != null && l.isActiveAndEnabled).ToList();
        var chains = new List<List<TunnelSegmentLink>>();
        foreach (var head in links.Where(l => l.previous == null))
        {
            var chain = new List<TunnelSegmentLink>();
            for (var link = head; link != null && chain.Count < 256 && !chain.Contains(link); link = link.next)
                chain.Add(link);
            chains.Add(chain);
        }
        var ordered = chains.OrderByDescending(c => c.Count)
            .ThenBy(c => c[0].transform.position.x).ThenBy(c => c[0].transform.position.z).SelectMany(c => c);
        var result = new List<TunnelSegmentGeometry>();
        foreach (var link in ordered)
        {
            var geometry = link.GetComponentInChildren<TunnelSegmentGeometry>(true);
            if (geometry != null && !result.Contains(geometry))
                result.Add(geometry);
        }
        foreach (var geometry in TunnelSegmentGeometry.all)
            if (geometry != null && geometry.isActiveAndEnabled && !result.Contains(geometry))
                result.Add(geometry);
        return result;
    }

    // The segment a point is in: the one whose length it lies along, else the nearest by that measure.
    static int SegmentOf(List<TunnelSegmentGeometry> segments, Vector3 world)
    {
        int best = 0;
        float bestOutside = float.MaxValue, bestSide = float.MaxValue;
        for (int i = 0; i < segments.Count; i++)
        {
            var t = segments[i].transform;
            var p = Quaternion.Inverse(t.rotation) * (world - t.position);
            float outside = Mathf.Max(0f, Mathf.Abs(p.z) - segments[i].length * 0.5f);
            float side = Mathf.Abs(p.x);
            if (outside < bestOutside - 1e-4f || (Mathf.Abs(outside - bestOutside) <= 1e-4f && side < bestSide))
            {
                best = i;
                bestOutside = outside;
                bestSide = side;
            }
        }
        return best;
    }

    static Placement Place(List<TunnelSegmentGeometry> segments, Transform thing)
    {
        int index = SegmentOf(segments, thing.position);
        var frame = segments[index].transform;
        var inverse = Quaternion.Inverse(frame.rotation);
        return new Placement
        {
            segment = index,
            position = inverse * (thing.position - frame.position),
            rotation = inverse * thing.rotation,
            scale = thing.localScale,
        };
    }

    static Pose WorldPose(List<TunnelSegmentGeometry> segments, Placement at)
    {
        var frame = segments[Mathf.Clamp(at.segment, 0, segments.Count - 1)].transform;
        return new Pose(frame.position + frame.rotation * at.position, frame.rotation * at.rotation);
    }

    static Quaternion FrameRotation(List<TunnelSegmentGeometry> segments, int index) =>
        segments[Mathf.Clamp(index, 0, segments.Count - 1)].transform.rotation;

    static T OnSegment<T>(TunnelSegmentGeometry geometry) where T : Component
    {
        var root = geometry.GetComponentInParent<TunnelSegmentLink>(true);
        var from = root != null ? root.transform : geometry.transform;
        return from.GetComponentInChildren<T>(true);
    }

    #endregion

    #region Saving

    /// <summary>Everything in the tunnel now, as a state called <paramref name="name"/> (on the host or when not connected).</summary>
    public static TunnelSavedState Capture(string name)
    {
        var state = new TunnelSavedState
        {
            name = name,
            saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            savedFrom = Application.version,
            magnification = TunnelMoleculeMenu.currentMagnification,
        };
        var segments = Segments();
        if (segments.Count == 0)
        {
            Debug.LogWarning("[SaveState] no tunnel segment to save");
            return state;
        }

        foreach (var segment in segments)
        {
            var appearance = OnSegment<TunnelSegmentAppearance>(segment);
            var texture = OnSegment<TunnelSegmentTexture>(segment);
            var paint = OnSegment<TunnelSegmentPaint>(segment);
            state.segments.Add(new SegmentState
            {
                color = appearance != null ? appearance.colorHsva : Vector4.zero,
                texture = texture != null ? texture.textureName : "",
                paint = paint != null ? paint.SaveMarks() : new PaintMarks(),
            });
        }

        // Containers, each with the movie's starting-state format; then every molecule, in the container it is in (the
        // smallest, as the medium decides) or loose.
        var containers = new Dictionary<TunnelContainer, PlacedContainer>();
        foreach (var container in FindObjectsByType<TunnelContainer>(FindObjectsSortMode.None))
        {
            if (container == null || !container.gameObject.activeInHierarchy)
                continue;
            var placed = new PlacedContainer { at = Place(segments, container.transform) };
            var movie = container.movie;
            if (movie != null)
            {
                placed.state = movie.Capture();
                placed.state.molecules.Clear(); // filled below, so each molecule is kept once
                placed.movie = JsonUtility.ToJson(movie.definition);
                placed.controlsShown = movie.controlsShown;
            }
            var c = placed.state.container;
            c.shape = container.shape.ToString();
            c.mixedReality = container.isMixedReality;
            c.setInWall = container.isSetInWall;
            c.frontFace = container.frontFace;
            c.position = container.transform.position;
            c.rotation = container.transform.rotation;
            c.size = container.transform.lossyScale;
            c.viscosity = container.viscosity;
            c.temperatureSetting = container.temperature;
            c.temperatureCelsius = TunnelContainer.Kelvin(container.temperature) - 273.15f;
            c.motion = container.motion.ToString();
            if (container.TryGetComponent(out SplatBox splat))
            {
                placed.splatAsset = splat.assetId;
                placed.splatHeight = splat.targetHeight;
            }
            containers[container] = placed;
            state.containers.Add(placed);
        }

        var loose = new List<MoleculeGravity>();
        foreach (var molecule in MoleculeGravity.all)
        {
            if (molecule == null || !molecule.isActiveAndEnabled || !molecule.gameObject.activeInHierarchy)
                continue;
            var center = molecule.body != null ? molecule.body.worldCenterOfMass : molecule.transform.position;
            var inside = TunnelContainer.At(center);
            if (inside != null && containers.TryGetValue(inside, out var placed))
            {
                var t = inside.transform;
                placed.state.molecules.Add(StateOf(molecule, t.position, t.rotation));
                continue;
            }
            int index = SegmentOf(segments, molecule.transform.position);
            var frame = segments[index].transform;
            state.molecules.Add(new PlacedMolecule
            {
                at = Place(segments, molecule.transform),
                molecule = StateOf(molecule, frame.position, frame.rotation),
            });
            loose.Add(molecule);
        }

        foreach (var panel in FindObjectsByType<BrowserPanel>(FindObjectsSortMode.None))
        {
            if (panel == null || !panel.gameObject.activeInHierarchy)
                continue;
            var placed = new PlacedPanel
            {
                at = Place(segments, panel.transform),
                width = panel.width,
                aspect = panel.aspect,
                free = panel.isFree,
            };
            var viewer = panel.GetComponentInChildren<ViewerPanel>(true);
            if (viewer != null)
            {
                placed.viewer = true;
                placed.contentOnly = viewer.contentOnly;
                if (viewer.hasContent)
                {
                    placed.mediaKind = (int)viewer.kind;
                    placed.mediaSource = viewer.source;
                    placed.mediaIsUrl = viewer.isUrl;
                    placed.mediaTitle = viewer.title;
                    placed.playing = viewer.isPlaying;
                    placed.loop = viewer.isLooping;
                    placed.time = viewer.isVideo ? viewer.currentTime : 0;
                }
            }
            else
            {
                var sync = panel.GetComponentInChildren<BrowserSync>(true);
                placed.url = sync != null ? sync.currentUrl : "";
                var contentOnly = panel.GetComponentInChildren<BrowserContentOnly>(true);
                placed.contentOnly = contentOnly != null && contentOnly.contentOnly;
            }
            state.panels.Add(placed);
        }

        foreach (var sign in TunnelSign.all)
        {
            if (sign == null || !sign.gameObject.activeInHierarchy)
                continue;
            state.signs.Add(new PlacedSign
            {
                at = Place(segments, sign.transform),
                record = SignRecord.From(sign.saveName, sign.text, sign.style, sign.fonts, sign.transform.localScale.x),
            });
        }

        foreach (var palette in TunnelPalette.all)
            if (palette != null && palette.gameObject.activeInHierarchy)
                state.palettes.Add(Place(segments, palette.transform));

        foreach (var copy in PaintingCopy.all)
        {
            if (copy == null || !copy.gameObject.activeInHierarchy)
                continue;
            var paint = copy.GetComponent<TunnelSegmentPaint>();
            state.paintingCopies.Add(new PlacedCopy
            {
                at = Place(segments, copy.transform),
                meters = copy.paintCanvasMeters,
                contentOnly = copy.contentOnly,
                paint = paint != null ? paint.SaveMarks() : new PaintMarks(),
            });
        }

        foreach (var figure in TunnelFigure.all)
            if (figure != null && figure.gameObject.activeInHierarchy)
                state.figures.Add(Place(segments, figure.transform));

        foreach (var nozzle in FindObjectsByType<SpawnNozzle>(FindObjectsSortMode.None))
        {
            if (nozzle == null || !nozzle.gameObject.activeInHierarchy)
                continue;
            state.nozzles.Add(new PlacedNozzle
            {
                at = Place(segments, nozzle.transform),
                count = nozzle.count,
                loaded = nozzle.loaded != null ? loose.IndexOf(nozzle.loaded) : -1,
            });
        }

        Debug.Log($"[SaveState] captured \"{name}\": {state.Summary()} ({state.molecules.Count} loose molecules, {state.containers.Count} containers, " +
            $"{state.panels.Count} panels, {state.signs.Count} signs, {state.palettes.Count} palettes, {state.paintingCopies.Count} painting copies, " +
            $"{state.figures.Count} figures, {state.nozzles.Count} nozzles)");
        return state;
    }

    // A molecule in a frame (a container's, turned with it but not scaled, or a segment's), as the movie keeps them.
    static MoleculeState StateOf(MoleculeGravity molecule, Vector3 origin, Quaternion rotation)
    {
        var inverse = Quaternion.Inverse(rotation);
        var body = molecule.body;
        var t = molecule.transform;
        bool moving = body != null && !body.isKinematic;
        return new MoleculeState
        {
            molecule = CapsidSites.MoleculeName(molecule.gameObject),
            position = inverse * (t.position - origin),
            rotation = inverse * t.rotation,
            scale = t.lossyScale.x,
            velocity = moving ? inverse * body.linearVelocity : Vector3.zero,
            spin = moving ? inverse * body.angularVelocity : Vector3.zero,
            parked = molecule.isParked,
            antibodiesBound = molecule.TryGetComponent(out CapsidSites capsid) ? capsid.bindingCount : 0,
            assetId = molecule.TryGetComponent(out RepositoryMolecule repository) ? repository.assetId : "",
            frozen = molecule.isFrozen,
        };
    }

    #endregion

    #region Taking away

    /// <summary>
    /// Everything a state can hold that is in the tunnel now (each thing's top object): molecules, containers,
    /// palettes and markers, painting copies, the spawn nozzle, signs, New figures, Viewers and browsers, on the walls
    /// or not. Never the tunnel, its menus, the eraser or people.
    /// </summary>
    public static List<GameObject> Removables()
    {
        var result = new List<GameObject>();
        var types = TunnelEraser.deletableTypes.Append(typeof(BrowserPanel));
        foreach (var type in types)
            foreach (var found in FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var part = (Component)found;
                if (part == null || part.GetComponentInParent<TunnelEraser>() != null)
                    continue;
                var root = part.GetComponentInParent<NetworkObject>(true);
                var go = root != null && root.GetComponentInParent<TunnelSegmentLink>() == null ? root.gameObject : part.gameObject;
                if (go.GetComponentInParent<TunnelSegmentLink>() != null)
                    continue; // part of a segment (its menus)
                if (!result.Contains(go))
                    result.Add(go);
            }
        return result;
    }

    static void Remove(GameObject target)
    {
        if (target == null)
            return;
        if (target.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned)
        {
            if (networkObject.NetworkManager != null && networkObject.NetworkManager.IsServer)
                networkObject.Despawn(true);
            return;
        }
        Destroy(target);
    }

    #endregion

    #region Putting back

    /// <summary>
    /// Puts a state back (on the host, or here when not connected), replacing everything now in the tunnel, for
    /// everyone. <paramref name="requester"/> is the player who asked (they drive the New figures); <paramref name="done"/>
    /// is told what came back. False if a state is already being put back.
    /// </summary>
    public static bool Restore(TunnelSavedState state, Prefabs prefabs, ulong requester, Action<string> done)
    {
        if (state == null || busy)
            return false;
        busy = true;
        Run(Guarded(runner.RestoreRoutine(state, prefabs, requester, done), done));
        return true;
    }

    // Steps through a routine so that if it fails part way, the failure is logged and reported and another state can
    // still be put back afterwards.
    static IEnumerator Guarded(IEnumerator routine, Action<string> done)
    {
        while (true)
        {
            object current;
            try
            {
                if (!routine.MoveNext())
                    break;
                current = routine.Current;
            }
            catch (Exception e)
            {
                busy = false;
                Debug.LogError($"[SaveState] putting back a state failed: {e}");
                done?.Invoke($"Putting the state back stopped part way: {e.Message}");
                yield break;
            }
            yield return current;
        }
        busy = false;
    }

    IEnumerator RestoreRoutine(TunnelSavedState state, Prefabs prefabs, ulong requester, Action<string> done)
    {
        string report;
        var removed = Removables();
        foreach (var target in removed)
            Remove(target);
        Debug.Log($"[SaveState] putting back \"{state.name}\": took away {removed.Count} things ({(online ? "for everyone" : "not connected, here only")})");
        yield return null;
        yield return null;

        // The segments: as many as were saved, added at the tunnel's end or removed from it.
        var segments = Segments();
        int want = Mathf.Max(1, state.segments.Count);
        if (segments.Count == 0)
        {
            done?.Invoke($"Couldn't put back {state.name}: there's no tunnel segment.");
            yield break;
        }
        int added = 0, dropped = 0;
        while (segments.Count > want)
        {
            var last = segments[segments.Count - 1];
            var root = last.GetComponentInParent<TunnelSegmentLink>(true);
            Remove(root != null ? root.gameObject : last.gameObject);
            segments.RemoveAt(segments.Count - 1);
            dropped++;
        }
        for (int guard = 0; segments.Count < want && guard < 64; guard++)
        {
            var actions = segments[segments.Count - 1].GetComponentInParent<TunnelSegmentActions>(true);
            var newSegment = actions != null ? actions.AddSegmentAtEnd() : null;
            yield return null;
            var now = Segments();
            if (newSegment == null || now.Count <= segments.Count)
            {
                Debug.LogWarning("[SaveState] couldn't add a segment at the tunnel's end");
                break;
            }
            segments = now;
            added++;
        }
        yield return null;
        segments = Segments();
        if (dropped > 0 || added > 0)
            Debug.Log($"[SaveState] segments: {(added > 0 ? $"added {added}" : $"removed {dropped}")}, now {segments.Count}");

        // Each segment's look and paint.
        var painting = new List<Coroutine>();
        for (int i = 0; i < segments.Count && i < state.segments.Count; i++)
        {
            var saved = state.segments[i];
            var appearance = OnSegment<TunnelSegmentAppearance>(segments[i]);
            if (appearance != null && saved.color.w > 0f)
                appearance.SetColor(saved.color);
            var texture = OnSegment<TunnelSegmentTexture>(segments[i]);
            if (texture != null && texture.textureName != (saved.texture ?? ""))
                texture.SetTexture(saved.texture ?? "");
            var paint = OnSegment<TunnelSegmentPaint>(segments[i]);
            if (paint != null && (paint.hasMarks || saved.paint.ops.Count > 0))
                painting.Add(StartCoroutine(paint.RestoreMarks(saved.paint)));
        }

        int made = 0, missing = 0;

        // Containers, set up before they appear, then their movies and the molecules in them.
        foreach (var saved in state.containers)
        {
            var container = MakeContainer(segments, saved, prefabs);
            if (container == null)
            {
                missing++;
                continue;
            }
            made++;
            var movie = container.movie;
            if (movie != null && !string.IsNullOrEmpty(saved.movie))
            {
                var definition = JsonUtility.FromJson<MovieDefinition>(saved.movie);
                if (definition != null && definition.id != movie.definition.id)
                    movie.SetMovie(definition);
                var s = saved.state;
                if (s.bindingTime > 0f)
                    movie.SetBindingTime(s.bindingTime);
                if (s.siteDiameter > 0f)
                    movie.SetSiteDiameter(s.siteDiameter);
                if (s.captureRadius > 0f)
                    movie.SetCaptureRadius(s.captureRadius);
                // A made movie sets the medium when chosen: the saved medium wins.
                var c = s.container;
                container.SetViscosity(c.viscosity);
                container.SetTemperature(c.temperatureSetting);
                if (Enum.TryParse(c.motion, out TunnelContainer.Motion motion))
                    container.SetMotion(motion);
                if (saved.controlsShown)
                    movie.SetControls(true);
            }
            var t = container.transform;
            foreach (var m in saved.state.molecules)
            {
                if (MakeMolecule(prefabs, m, t.position + t.rotation * m.position, t.rotation * m.rotation, t.rotation * m.velocity, t.rotation * m.spin) != null)
                    made++;
                else
                    missing++;
            }
        }

        // Loose molecules.
        var looseMade = new List<GameObject>();
        foreach (var saved in state.molecules)
        {
            var pose = WorldPose(segments, saved.at);
            var frame = FrameRotation(segments, saved.at.segment);
            var molecule = MakeMolecule(prefabs, saved.molecule, pose.position, pose.rotation, frame * saved.molecule.velocity, frame * saved.molecule.spin);
            looseMade.Add(molecule);
            if (molecule != null)
                made++;
            else
                missing++;
        }

        // Viewers and browsers.
        var viewers = new List<(ViewerPanel viewer, PlacedPanel saved)>();
        foreach (var saved in state.panels)
        {
            var panel = MakePanel(segments, saved, prefabs, out var viewer);
            if (panel == null)
            {
                missing++;
                continue;
            }
            made++;
            if (viewer != null)
            {
                viewers.Add((viewer, saved));
                if (!string.IsNullOrEmpty(saved.mediaSource))
                    viewer.OpenSaved((ViewerMediaKind)saved.mediaKind, saved.mediaSource, saved.mediaIsUrl, saved.mediaTitle);
                if (saved.contentOnly)
                    viewer.SetContentOnly(true);
            }
            else if (saved.contentOnly)
            {
                var contentOnly = panel.GetComponentInChildren<BrowserContentOnly>(true);
                if (contentOnly != null)
                    contentOnly.SetContentOnly(true);
            }
        }

        // Signs, palettes, painting copies, figures and nozzles.
        foreach (var saved in state.signs)
            if (MakeSign(segments, saved, prefabs) != null) made++; else missing++;
        foreach (var saved in state.palettes)
            if (MakePalette(segments, saved, prefabs)) made++; else missing++;
        foreach (var saved in state.paintingCopies)
        {
            var copy = MakeCopy(segments, saved, prefabs, requester);
            if (copy == null)
            {
                missing++;
                continue;
            }
            made++;
            var paint = copy.GetComponent<TunnelSegmentPaint>();
            if (paint != null && saved.paint.ops.Count > 0)
                painting.Add(StartCoroutine(PaintWhenReady(paint, saved.paint)));
            if (saved.contentOnly)
                copy.SetContentOnly(true);
        }
        foreach (var saved in state.figures)
            if (MakeFigure(segments, saved, prefabs, requester) != null) made++; else missing++;
        var nozzles = new List<(SpawnNozzle nozzle, PlacedNozzle saved)>();
        foreach (var saved in state.nozzles)
        {
            var nozzle = MakeNozzle(segments, saved, prefabs);
            if (nozzle == null)
            {
                missing++;
                continue;
            }
            made++;
            nozzles.Add((nozzle, saved));
            if (saved.count > 0)
                nozzle.SetCount(saved.count);
        }

        // The Molecules list's magnification last: choosing a container's movie may have changed it.
        if (state.magnification > 0)
            TunnelMoleculeMenu.SetMagnification(state.magnification);

        // Once everything has appeared: videos to their place, molecules back on the nozzle's cup.
        yield return null;
        yield return null;
        foreach (var (viewer, saved) in viewers)
        {
            if (viewer == null || string.IsNullOrEmpty(saved.mediaSource))
                continue;
            if (saved.loop)
                viewer.SetLoop(true);
            if (ViewerLibrary.IsVideoKind((ViewerMediaKind)saved.mediaKind))
            {
                if (saved.time > 0.1)
                    viewer.Seek(saved.time);
                if (!saved.playing)
                    viewer.SetPlaying(false);
            }
        }
        foreach (var (nozzle, saved) in nozzles)
        {
            if (nozzle == null || saved.loaded < 0 || saved.loaded >= looseMade.Count || looseMade[saved.loaded] == null)
                continue;
            if (looseMade[saved.loaded].TryGetComponent(out MoleculeGravity loaded))
                nozzle.Attach(loaded);
        }

        foreach (var routine in painting)
            yield return routine;

        report = $"Put back {state.name}: {segments.Count} segment{(segments.Count == 1 ? "" : "s")}, {made} thing{(made == 1 ? "" : "s")}" +
            (missing > 0 ? $"; {missing} couldn't be made (see the log)" : "") + ".";
        Debug.Log($"[SaveState] {report}");
        done?.Invoke(report);
    }

    // Paints a copy's saved marks once its canvas size has reached this headset.
    static IEnumerator PaintWhenReady(TunnelSegmentPaint paint, PaintMarks marks)
    {
        yield return null;
        if (paint != null)
            yield return paint.RestoreMarks(marks);
    }

    // Instantiated under a switched-off holder, so it is placed, sized and set up before it wakes (its network transform
    // takes its pose when it's created), as the TS Menu's lists make things.
    static GameObject Prepare(GameObject prefab, Pose pose, Vector3 scale, out GameObject holder)
    {
        holder = new GameObject("Saved State Holder");
        holder.SetActive(false);
        var instance = Instantiate(prefab, holder.transform);
        instance.transform.localScale = scale;
        instance.transform.SetPositionAndRotation(pose.position, pose.rotation);
        return instance;
    }

    static void Release(GameObject instance, GameObject holder, ulong? owner = null)
    {
        instance.transform.SetParent(null, true);
        Destroy(holder);
        if (online && instance.TryGetComponent(out NetworkObject networkObject))
        {
            if (owner.HasValue && owner.Value != NetworkManager.ServerClientId)
                networkObject.SpawnWithOwnership(owner.Value, true);
            else
                networkObject.Spawn(true);
        }
    }

    static GameObject PrefabFor(TunnelPrefabRegistry registry, string molecule)
    {
        if (registry == null || string.IsNullOrEmpty(molecule))
            return null;
        foreach (var entry in registry.molecules)
            if (entry != null && entry.prefab != null && string.Equals(entry.name, molecule, StringComparison.OrdinalIgnoreCase))
                return entry.prefab;
        foreach (var entry in registry.molecules)
            if (entry != null && entry.prefab != null && string.Equals(CapsidSites.MoleculeName(entry.prefab), molecule, StringComparison.OrdinalIgnoreCase))
                return entry.prefab;
        return null;
    }

    static GameObject MakeMolecule(Prefabs prefabs, MoleculeState m, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 spin)
    {
        bool repository = !string.IsNullOrEmpty(m.assetId);
        var prefab = repository ? prefabs.registry != null ? prefabs.registry.repositoryMolecule : null : PrefabFor(prefabs.registry, m.molecule);
        if (prefab == null)
        {
            Debug.LogWarning($"[SaveState] no {(repository ? $"repository toy for {m.assetId}" : m.molecule)} in the Molecules list to put back");
            return null;
        }
        var scale = m.scale > 0f ? Vector3.one * m.scale : prefab.transform.localScale;
        var molecule = Prepare(prefab, new Pose(position, rotation), scale, out var holder);
        if (repository && molecule.TryGetComponent(out RepositoryMolecule toy))
            toy.Preset(m.assetId);
        if (molecule.TryGetComponent(out NetworkPhysicsInteractable networkPhysics))
            networkPhysics.spawnLocked = m.parked;
        if (m.frozen && molecule.TryGetComponent(out MoleculeGravity physics))
            physics.PresetFrozen(true);
        Release(molecule, holder);
        if (molecule.TryGetComponent(out Rigidbody body))
        {
            if (m.parked && !online)
                body.constraints = RigidbodyConstraints.FreezeAll;
            else if (!m.parked && !m.frozen)
            {
                body.constraints = RigidbodyConstraints.None;
                body.isKinematic = false;
                body.linearVelocity = velocity;
                body.angularVelocity = spin;
            }
        }
        return molecule;
    }

    static TunnelContainer MakeContainer(List<TunnelSegmentGeometry> segments, PlacedContainer saved, Prefabs prefabs)
    {
        var c = saved.state.container;
        Enum.TryParse(c.shape, out TunnelContainer.Shape shape);
        var prefab = shape == TunnelContainer.Shape.SplatBox ? prefabs.splatBox
            : prefabs.registry == null ? null : shape == TunnelContainer.Shape.Sphere ? prefabs.registry.containerSphere : prefabs.registry.containerBox;
        if (prefab == null)
        {
            Debug.LogWarning($"[SaveState] no {shape} prefab to put back");
            return null;
        }
        var instance = Prepare(prefab, WorldPose(segments, saved.at), saved.at.scale, out var holder);
        var container = instance.GetComponent<TunnelContainer>();
        Enum.TryParse(c.motion, out TunnelContainer.Motion motion);
        container.Preset(c.mixedReality, c.frontFace, c.viscosity, c.temperatureSetting, motion);
        if (shape == TunnelContainer.Shape.SplatBox && instance.TryGetComponent(out SplatBox splat) && !string.IsNullOrEmpty(saved.splatAsset))
            splat.Preset(saved.splatAsset, saved.splatHeight);
        Release(instance, holder);
        return container;
    }

    static GameObject MakePanel(List<TunnelSegmentGeometry> segments, PlacedPanel saved, Prefabs prefabs, out ViewerPanel viewer)
    {
        viewer = null;
        var prefab = prefabs.registry == null ? null : saved.viewer ? prefabs.registry.viewerPanel : prefabs.registry.browserPanel;
        if (prefab == null)
        {
            Debug.LogWarning($"[SaveState] no {(saved.viewer ? "Viewer" : "browser")} prefab to put back");
            return null;
        }
        var panel = Prepare(prefab, WorldPose(segments, saved.at), saved.at.scale, out var holder);
        var frame = panel.GetComponentInChildren<BrowserPanel>(true);
        if (frame != null)
            frame.Preset(saved.width, saved.aspect, saved.free);
        if (!saved.viewer && !string.IsNullOrEmpty(saved.url))
        {
            var container = panel.GetComponentInChildren<BrowserContainer>(true);
            var browser = container != null ? container.browser : null;
            if (browser != null)
                browser.InitOption(saved.url, browser.fps, browser.downloadOption);
        }
        Release(panel, holder);
        viewer = panel.GetComponentInChildren<ViewerPanel>(true);
        return panel;
    }

    static TunnelSign MakeSign(List<TunnelSegmentGeometry> segments, PlacedSign saved, Prefabs prefabs)
    {
        if (prefabs.sign == null || saved.record == null)
        {
            Debug.LogWarning("[SaveState] no sign prefab to put back a sign");
            return null;
        }
        var r = saved.record;
        var instance = Prepare(prefabs.sign.gameObject, WorldPose(segments, saved.at), saved.at.scale, out var holder);
        var sign = instance.GetComponent<TunnelSign>();
        sign.Configure(r.text, r.ToStyle(sign.fonts), r.name, r.scale > 0f ? r.scale : saved.at.scale.x, ulong.MaxValue);
        Release(instance, holder);
        return sign;
    }

    static bool MakePalette(List<TunnelSegmentGeometry> segments, Placement saved, Prefabs prefabs)
    {
        var registry = prefabs.registry;
        if (registry == null || registry.palette == null || registry.marker == null)
        {
            Debug.LogWarning("[SaveState] no palette prefab to put back");
            return false;
        }
        var palette = Prepare(registry.palette, WorldPose(segments, saved), saved.scale, out var holder);
        var paletteComponent = palette.GetComponent<TunnelPalette>();
        var marker = Instantiate(registry.marker, holder.transform);
        if (paletteComponent.dock != null)
            marker.transform.SetPositionAndRotation(paletteComponent.dock.position, paletteComponent.dock.rotation);
        palette.transform.SetParent(null, true);
        marker.transform.SetParent(null, true);
        Destroy(holder);
        if (online && palette.TryGetComponent(out NetworkObject paletteObject) && marker.TryGetComponent(out NetworkObject markerObject))
        {
            paletteObject.Spawn(true);
            markerObject.Spawn(true);
        }
        marker.GetComponent<TunnelMarker>().SetPalette(paletteComponent);
        return true;
    }

    static PaintingCopy MakeCopy(List<TunnelSegmentGeometry> segments, PlacedCopy saved, Prefabs prefabs, ulong requester)
    {
        if (prefabs.registry == null || prefabs.registry.paintCopy == null)
        {
            Debug.LogWarning("[SaveState] no painting copy prefab to put back");
            return null;
        }
        var instance = Prepare(prefabs.registry.paintCopy, WorldPose(segments, saved.at), saved.at.scale, out var holder);
        var copy = instance.GetComponent<PaintingCopy>();
        // No painting to copy from (request 0): its saved marks are painted on once it is out.
        copy.Setup(saved.meters, 0, online ? NetworkManager.ServerClientId : 0);
        Release(instance, holder);
        return copy;
    }

    static TunnelFigure MakeFigure(List<TunnelSegmentGeometry> segments, Placement saved, Prefabs prefabs, ulong requester)
    {
        if (prefabs.figure == null)
        {
            Debug.LogWarning("[SaveState] no figure prefab to put back (run Tunnel > Figure > Build Figure)");
            return null;
        }
        var instance = Prepare(prefabs.figure.gameObject, WorldPose(segments, saved), saved.scale, out var holder);
        Release(instance, holder, requester);
        return instance.GetComponent<TunnelFigure>();
    }

    static SpawnNozzle MakeNozzle(List<TunnelSegmentGeometry> segments, PlacedNozzle saved, Prefabs prefabs)
    {
        if (prefabs.nozzle == null)
        {
            Debug.LogWarning("[SaveState] no spawn nozzle prefab to put back");
            return null;
        }
        var instance = Prepare(prefabs.nozzle.gameObject, WorldPose(segments, saved.at), saved.at.scale, out var holder);
        Release(instance, holder);
        return instance.GetComponent<SpawnNozzle>();
    }

    #endregion
}
