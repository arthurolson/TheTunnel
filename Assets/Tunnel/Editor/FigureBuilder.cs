using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Makes the TS Menu's figure (see <see cref="TunnelFigure"/>): a little person you walk around with the left stick.
/// From a rigged model of a real person (Assets/Tunnel/Figure/Model/Figure.fbx, made in Blender from a video of them)
/// and humanoid idle, walk, jog, sprint and jump animations (Assets/Tunnel/Figure/Animations: Quaternius's Universal
/// Animation Library, CC0), it sets both up as Unity
/// Humanoids (so any person's animations play on any other person's body), makes the animator (stand, walk and run
/// blend by speed; jump and fall while off the ground), and the prefab: a copy of the 2PLV molecule's toy without its
/// molecule, so it is grabbed and shared the same way, with the body inside, 30 cm tall to start. Lists it on the
/// network and adds the TS Menu's New figure button under Clear all. Runs from Tunnel > Figure > Build Figure.
/// </summary>
static class FigureBuilder
{
    static string k_TemplatePath => MoleculeLibraryBuilder.TemplatePath;
    public const string k_Folder = "Assets/Tunnel/Figure";
    public const string k_ModelPath = k_Folder + "/Model/Figure.fbx";
    const string k_AnimationsFolder = k_Folder + "/Animations";
    public const string k_ControllerPath = k_Folder + "/Figure.controller";
    public const string k_PrefabPath = k_Folder + "/Figure_Networked.prefab";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_NetworkListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";

    /// <summary>How tall it is when it first appears (meters).</summary>
    const float k_StartHeight = 0.3f;

    [MenuItem("Tunnel/Figure/Build Figure")]
    public static string Build() => BuilderScratchScene.Run(BuildAll);

    static string BuildAll()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(k_ModelPath);
        if (model == null)
            return Report($"FAILED: no model at {k_ModelPath}");
        string humanoid = MakeHumanoid(k_ModelPath, false);
        if (humanoid != null)
            return Report($"FAILED: {humanoid}");

        var clips = LoadClips(out string clipReport);
        var controller = BuildController(clips);
        var prefab = BuildPrefab(model, controller, clips.idle, out float height);

        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_NetworkListPath);
        foreach (var entry in list.PrefabList.Where(p => p.Prefab == null).ToList())
            list.Remove(entry);
        if (!list.PrefabList.Any(p => p.Prefab == prefab))
            list.Add(new NetworkPrefab { Prefab = prefab });
        EditorUtility.SetDirty(list);

        string menu = LinkMenu(prefab.GetComponent<TunnelFigure>());
        AssetDatabase.SaveAssets();
        // The animator was made anew, so the prefab held in memory can still point at the old (deleted) one even
        // though the file names the new one: read the prefab back from its file.
        AssetDatabase.ImportAsset(k_PrefabPath, ImportAssetOptions.ForceUpdate);
        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(k_PrefabPath);
        if (prefab.GetComponentInChildren<Animator>().runtimeAnimatorController == null)
            return Report($"FAILED: {k_PrefabPath} has no animator controller after building");
        return Report($"built {k_PrefabPath} (the person is {height:F2} m tall, appears {k_StartHeight:F2} m); {clipReport}; network prefab listed; {menu}");
    }

    static string Report(string text)
    {
        Debug.Log($"[Figure] {text}");
        return $"[Figure] {text}";
    }

    // A model or animation file set up as a Unity Humanoid. Returns null when it is one, else what went wrong.
    static string MakeHumanoid(string path, bool animation)
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        bool changed = false;
        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            changed = true;
        }
        if (!importer.bakeAxisConversion)
        {
            importer.bakeAxisConversion = true; // as the animation library asks, so Blender's axes come out upright
            changed = true;
        }
        if (!animation && importer.importAnimation)
        {
            importer.importAnimation = false;
            changed = true;
        }
        if (animation)
        {
            // Loop the steady ones; keep feet on the ground and facing forward (the script does the moving).
            var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
            foreach (var clip in clips)
            {
                string n = clip.name.ToLowerInvariant();
                bool loop = n.EndsWith("_loop") || n.Contains("idle");
                clip.loopTime = loop;
                clip.lockRootRotation = true;
                clip.lockRootHeightY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalOrientation = true;
                clip.keepOriginalPositionY = true;
                clip.keepOriginalPositionXZ = true;
            }
            importer.clipAnimations = clips;
            changed = true;
        }
        if (changed)
            importer.SaveAndReimport();
        var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
            return $"{path} is not a valid humanoid (its skeleton needs hips, spine, head, arms and legs Unity can recognise)";
        return null;
    }

    struct Clips
    {
        public AnimationClip idle, walk, jog, sprint, jump, fall, land;
    }

    // The clips used, by name, from Quaternius's Universal Animation Library (CC0; the first name found wins).
    static readonly (string field, string[] names)[] k_ClipNames =
    {
        ("idle", new[] { "Idle_Loop", "Idle" }),
        ("walk", new[] { "Walk_Loop", "Walk" }),
        ("jog", new[] { "Jog_Fwd_Loop", "Run" }),
        ("sprint", new[] { "Sprint_Loop", "Sprint" }),
        ("jump", new[] { "Jump_Start", "Jump" }),
        ("fall", new[] { "Jump_Loop", "Fall" }),
        ("land", new[] { "Jump_Land", "Land" }),
    };

    static Clips LoadClips(out string report)
    {
        var found = new System.Collections.Generic.Dictionary<string, AnimationClip>();
        if (AssetDatabase.IsValidFolder(k_AnimationsFolder))
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { k_AnimationsFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string problem = MakeHumanoid(path, true);
                if (problem != null)
                {
                    Debug.LogWarning($"[Figure] {problem}");
                    continue;
                }
                // Blender names a clip after its rig too ("Rig|Walk_Loop"): the part after the bar is the clip's own name.
                foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
                    if (!clip.name.StartsWith("__preview__"))
                        found.TryAdd(clip.name.Substring(clip.name.LastIndexOf('|') + 1), clip);
            }
        AnimationClip Pick(string field)
        {
            foreach (var name in k_ClipNames.First(c => c.field == field).names)
                if (found.TryGetValue(name, out var clip))
                    return clip;
            return null;
        }
        var clips = new Clips
        {
            idle = Pick("idle"), walk = Pick("walk"), jog = Pick("jog"), sprint = Pick("sprint"),
            jump = Pick("jump"), fall = Pick("fall"), land = Pick("land"),
        };
        string Name(AnimationClip c) => c != null ? c.name : "none";
        report = $"animations: {Name(clips.idle)}, {Name(clips.walk)}, {Name(clips.jog)}, {Name(clips.sprint)}, " +
            $"{Name(clips.jump)}, {Name(clips.fall)}, {Name(clips.land)}";
        return clips;
    }

    // Grounded: idle, walk, jog and sprint blended by Speed (heights per second). Off the ground: the jump's start while
    // rising, its in-the-air loop after; touching down plays the landing.
    static AnimatorController BuildController(Clips clips)
    {
        AssetDatabase.DeleteAsset(k_ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(k_ControllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("VerticalSpeed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
        controller.parameters = controller.parameters.Select(p =>
        {
            if (p.name == "Grounded")
                p.defaultBool = true;
            return p;
        }).ToArray();
        var machine = controller.layers[0].stateMachine;

        var tree = new BlendTree { name = "Locomotion", blendParameter = "Speed", useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(tree, controller);
        // Thresholds in the figure's heights per second: a person walks about 0.8, jogs 1.7 and sprints 3.
        if (clips.idle != null) tree.AddChild(clips.idle, 0f);
        if (clips.walk != null) tree.AddChild(clips.walk, 0.8f);
        if (clips.jog != null) tree.AddChild(clips.jog, 1.7f);
        if (clips.sprint != null) tree.AddChild(clips.sprint, 3f);
        var ground = machine.AddState("Grounded");
        ground.motion = tree;
        machine.defaultState = ground;

        var jump = machine.AddState("Jump");
        jump.motion = clips.jump != null ? clips.jump : clips.fall != null ? clips.fall : clips.idle;
        var fall = machine.AddState("Fall");
        fall.motion = clips.fall != null ? clips.fall : clips.jump != null ? clips.jump : clips.idle;

        void To(AnimatorState from, AnimatorState to, float duration, params (AnimatorConditionMode mode, float value, string parameter)[] conditions)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.duration = duration;
            foreach (var c in conditions)
                t.AddCondition(c.mode, c.value, c.parameter);
        }
        To(ground, jump, 0.08f, (AnimatorConditionMode.IfNot, 0, "Grounded"), (AnimatorConditionMode.Greater, 0.2f, "VerticalSpeed"));
        To(ground, fall, 0.2f, (AnimatorConditionMode.IfNot, 0, "Grounded"), (AnimatorConditionMode.Less, -0.5f, "VerticalSpeed"));
        To(jump, fall, 0.25f, (AnimatorConditionMode.Less, 0f, "VerticalSpeed"));
        // Touching down: the landing, then back to standing, walking or running.
        var land = machine.AddState("Land");
        land.motion = clips.land != null ? clips.land : clips.idle;
        To(jump, land, 0.05f, (AnimatorConditionMode.If, 0, "Grounded"));
        To(fall, land, 0.05f, (AnimatorConditionMode.If, 0, "Grounded"));
        var settle = land.AddTransition(ground);
        settle.hasExitTime = true;
        settle.exitTime = 0.6f;
        settle.duration = 0.15f;
        To(land, ground, 0.15f, (AnimatorConditionMode.Greater, 0.5f, "Speed")); // landing on the run: keep going
        EditorUtility.SetDirty(controller);
        return controller;
    }

    // How far below the root the body's lowest point goes when it stands idle (meters): the animations' standing pose
    // puts the hips a little lower than this body's legs, which would sink its feet into the floor.
    static float IdleSink(GameObject body, AnimationClip idle)
    {
        if (idle == null)
            return 0f;
        var copy = Object.Instantiate(body, body.transform.parent, false);
        try
        {
            idle.SampleAnimation(copy, 0f);
            float lowest = float.MaxValue;
            var baked = new Mesh();
            foreach (var skin in copy.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skin.BakeMesh(baked, true);
                foreach (var v in baked.vertices)
                    lowest = Mathf.Min(lowest, copy.transform.parent.InverseTransformPoint(skin.transform.TransformPoint(v)).y);
            }
            Object.DestroyImmediate(baked);
            return lowest == float.MaxValue ? 0f : Mathf.Max(0f, -lowest);
        }
        finally
        {
            Object.DestroyImmediate(copy);
        }
    }

    static GameObject BuildPrefab(GameObject model, AnimatorController controller, AnimationClip idle, out float height)
    {
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(k_TemplatePath);
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try
        {
            var root = (GameObject)PrefabUtility.InstantiatePrefab(template, scene);
            PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.name = "Figure_Networked";
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
            if (root.TryGetComponent(out CapsidSites sites))
                Object.DestroyImmediate(sites);
            if (root.TryGetComponent(out AntibodyBinder binder))
                Object.DestroyImmediate(binder);
            Object.DestroyImmediate(root.GetComponent<MoleculeGravity>());
            root.transform.localScale = Vector3.one;

            // The person, standing on the root's origin, facing +Z.
            var body = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
            PrefabUtility.UnpackPrefabInstance(body, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            // Measured from the skin's own vertices: a skinned renderer's bounds are padded, which left the feet floating.
            var bounds = new Bounds(body.transform.position, Vector3.zero);
            bool first = true;
            var baked = new Mesh();
            foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skin.BakeMesh(baked, true);
                foreach (var v in baked.vertices)
                {
                    var p = skin.transform.TransformPoint(v);
                    if (first)
                        bounds = new Bounds(p, Vector3.zero);
                    else
                        bounds.Encapsulate(p);
                    first = false;
                }
            }
            Object.DestroyImmediate(baked);
            if (first)
                foreach (var r in body.GetComponentsInChildren<Renderer>())
                    bounds.Encapsulate(r.bounds);
            height = Mathf.Max(0.1f, bounds.size.y);
            body.transform.localPosition = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
            var animator = body.GetComponent<Animator>() ?? body.AddComponent<Animator>();
            animator.avatar = AssetDatabase.LoadAllAssetsAtPath(k_ModelPath).OfType<Avatar>().FirstOrDefault();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            float sink = IdleSink(body, idle);
            body.transform.localPosition += Vector3.up * sink;
            Debug.Log($"[Figure] standing idle the feet went {sink * 100f:F1} cm into the floor; the body is raised to match");
            foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skin.updateWhenOffscreen = false;
                skin.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                skin.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                skin.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }

            // What the hand grabs: a capsule round the body, which pushes nothing.
            var colliders = new GameObject("Colliders");
            colliders.transform.SetParent(root.transform, false);
            var capsule = colliders.AddComponent<CapsuleCollider>();
            float radius = Mathf.Max(bounds.size.x, bounds.size.z) * 0.25f;
            capsule.radius = Mathf.Clamp(radius, height * 0.1f, height * 0.25f);
            capsule.height = height;
            capsule.center = new Vector3(0f, height * 0.5f, 0f);
            capsule.excludeLayers = ~0;

            // Its own script moves it; it isn't pushed or thrown, and one hand picks it up and turns it, two scale it.
            var rigidbody = root.GetComponent<Rigidbody>();
            rigidbody.isKinematic = true;
            rigidbody.useGravity = false;
            rigidbody.interpolation = RigidbodyInterpolation.None;
            var grab = root.GetComponent<XRGrabInteractable>();
            grab.throwOnDetach = false;
            var grabSo = new SerializedObject(grab);
            grabSo.FindProperty("m_Colliders").ClearArray();
            grabSo.ApplyModifiedPropertiesWithoutUndo();
            if (root.TryGetComponent(out XRMultiplayer.NetworkPhysicsInteractable networkPhysics))
                networkPhysics.spawnLocked = false;
            var general = root.GetComponent<XRGeneralGrabTransformer>();
            if (general != null)
            {
                var generalSo = new SerializedObject(general);
                generalSo.FindProperty("m_AllowTwoHandedScaling").boolValue = true;
                generalSo.ApplyModifiedPropertiesWithoutUndo();
            }

            var figure = root.AddComponent<TunnelFigure>();
            figure.Configure(height);
            root.transform.localScale = Vector3.one * (k_StartHeight / height);

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, k_PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }
        finally
        {
            UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    // The TS Menu's New figure button (a copy of Clear all, under it) and its link to the figure.
    static string LinkMenu(TunnelFigure figure)
    {
        var root = PrefabUtility.LoadPrefabContents(k_SegmentPath);
        try
        {
            var menu = root.GetComponentInChildren<TunnelMenu>(true);
            var menuPanel = root.transform.Find("Control Panel/Menu Panel");
            var clearAll = menuPanel != null ? menuPanel.Find("Clear All Button") : null;
            if (menu == null || clearAll == null)
                return "TS Menu: FAILED (no TunnelMenu or Clear all button in the segment)";
            var old = menuPanel.Find("New Figure Button");
            if (old != null)
                Object.DestroyImmediate(old.gameObject);
            var button = Object.Instantiate(clearAll.gameObject, menuPanel, false).transform;
            button.name = "New Figure Button";
            button.SetSiblingIndex(clearAll.GetSiblingIndex() + 1);
            var ui = button.GetComponent<Button>();
            ui.onClick = new Button.ButtonClickedEvent();
            button.Find("Button Front/Text (TMP)").GetComponent<TMP_Text>().text = "New figure";
            TunnelMenuLayout.Arrange(menuPanel);

            var so = new SerializedObject(menu);
            so.FindProperty("m_NewFigureButton").objectReferenceValue = ui;
            so.FindProperty("m_FigurePrefab").objectReferenceValue = figure;
            so.ApplyModifiedPropertiesWithoutUndo();
            bool saved = PrefabUtility.SaveAsPrefabAsset(root, k_SegmentPath) != null;
            return saved ? "TS Menu: New figure button under Clear all" : "TS Menu: FAILED to save";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
