using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Checks the molecule library after it was sorted into category folders: every molecule is in its category's folder
/// and listed under the right heading in the TS Menu's Molecules list (in the order Viral capsids, Proteins, Ligands,
/// Chemicals, Nucleic acids, with no heading for an empty category); every toy loads, is a network prefab in both
/// network prefab lists and kept its network id through the move; loose files in Assets/Molecules are not molecules;
/// the builders find 2PLV, IgG and SARS in their new folders; EGFR and its ligand are made like the others (true
/// scale, colliders, grab, MoleculeGravity; EGFR's one pocket and the ligand's binder for Ligand binding), the free ligand being the inhibitor alone in its atoms' colours with a collider kept
/// grabbable while it is tiny; and both fit the spawn nozzle's triangle budget. In Play mode it also puts EGFR and the
/// ligand in a container and checks the container holds them and the ligand's collider follows magnification.
/// Runs from Tunnel > Molecules > Test Molecule Library (in Edit or Play mode); logs PASS or FAIL with the tag
/// [Molecules].
/// </summary>
static class MoleculeLibraryEditorTests
{
    const string k_RegistryPath = "Assets/Tunnel/TunnelPrefabs.asset";
    const string k_SegmentPath = "Assets/Tunnel/TG_TunnelSegment.prefab";
    const string k_SandboxListPath = "Assets/MRTabletopAssets/Prefabs/GameModes/Sandbox/NetworkedPrefabs/_SandboxPrefabList.asset";
    const string k_DefaultListPath = "Assets/DefaultNetworkPrefabs.asset";
    const float k_UnitsPerAngstrom = 0.2f / 167f; // as the molecule library

    // Where each molecule belongs, and the network ids the five older toys had before they were moved.
    static readonly (string name, string category, uint hash)[] k_Expected =
    {
        ("2PLV", "viral capsids", 1475192683u), ("SARS", "viral capsids", 914121012u), ("TBSV", "viral capsids", 2067459049u),
        ("Zika", "viral capsids", 2427790562u), ("E. coli 50S ribosome", "assemblies", 0u), ("Hi-res Ribosome", "assemblies", 0u), ("EGFR", "proteins", 0u), ("IGG", "proteins", 271599631u),
        ("EGFR ligand", "ligands", 0u),
    };

    [MenuItem("Tunnel/Molecules/Test Molecule Library")]
    public static string Test()
    {
        UseScratchScene();
        var report = new StringBuilder($"[Molecules] molecule library test ({(Application.isPlaying ? "Play mode" : "Edit mode")})\n");
        int failures = 0;
        void Check(bool ok, string what)
        {
            report.AppendLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok)
                failures++;
        }

        var registry = AssetDatabase.LoadAssetAtPath<TunnelPrefabRegistry>(k_RegistryPath);
        Check(registry != null, "TunnelPrefabs.asset loads");
        if (registry == null)
            return Finish(report, failures);
        var molecules = registry.molecules;

        // The folders: the six categories, with exactly the user's names.
        foreach (string category in TunnelPrefabRegistry.categories)
            Check(AssetDatabase.IsValidFolder($"{MoleculeLibraryBuilder.k_Root}/{category}"), $"folder Assets/Molecules/{category} exists");
        Check(TunnelPrefabRegistry.categories.SequenceEqual(new[] { "viral capsids", "assemblies", "proteins", "ligands", "chemicals", "nucleic acids" }),
            "categories in order: viral capsids, assemblies, proteins, ligands, chemicals, nucleic acids");
        Check(TunnelPrefabRegistry.CategoryHeading("viral capsids") == "Viral capsids" && TunnelPrefabRegistry.CategoryHeading("nucleic acids") == "Nucleic acids",
            "headings read Viral capsids ... Nucleic acids");

        // Every molecule where it belongs, its toy loaded and registered, its network id kept through the move.
        var sandbox = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_SandboxListPath);
        var defaults = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(k_DefaultListPath);
        Check(molecules.Count == k_Expected.Length, $"{k_Expected.Length} molecules in the library: {string.Join(", ", molecules.Select(m => m.name))}");
        foreach (var (name, category, hash) in k_Expected)
        {
            var entry = molecules.FirstOrDefault(m => m.name == name);
            if (entry == null || entry.prefab == null)
            {
                Check(false, $"{name} is in the library with a toy");
                continue;
            }
            string path = AssetDatabase.GetAssetPath(entry.prefab);
            Check(entry.category == category, $"{name} is listed under {TunnelPrefabRegistry.CategoryHeading(category)} ({entry.category})");
            Check(path.StartsWith($"{MoleculeLibraryBuilder.k_Root}/{category}/{name}/"), $"{name}'s toy is in Assets/Molecules/{category}/{name} ({path})");
            Check(AssetDatabase.LoadAssetAtPath<GameObject>(path) == entry.prefab, $"{name}'s toy loads from its path");
            var networkObject = entry.prefab.GetComponent<NetworkObject>();
            Check(networkObject != null, $"{name}'s toy is a network object");
            if (networkObject != null && hash != 0)
                Check(networkObject.PrefabIdHash == hash, $"{name} kept its network id {hash} ({networkObject.PrefabIdHash})");
            Check(sandbox != null && sandbox.PrefabList.Any(p => p.Prefab == entry.prefab), $"{name} is in _SandboxPrefabList");
            Check(defaults != null && defaults.PrefabList.Any(p => p.Prefab == entry.prefab), $"{name} is in DefaultNetworkPrefabs");
        }
        Check(sandbox != null && sandbox.PrefabList.All(p => p.Prefab != null), "_SandboxPrefabList has no missing prefabs");
        Check(defaults != null && defaults.PrefabList.All(p => p.Prefab != null), "DefaultNetworkPrefabs has no missing prefabs");
        Check(molecules.Select((m, i) => (m, i)).All(x => x.i == 0
                || TunnelPrefabRegistry.CategoryOrder(molecules[x.i - 1].category) <= TunnelPrefabRegistry.CategoryOrder(x.m.category)),
            "the library keeps each category together, in category order");
        Check(!molecules.Any(m => m.name.Contains("WRITTENBYMDANALYSIS") || m.name.StartsWith("EGF.") || m.name == "egfr"),
            "loose files in Assets/Molecules (WRITTENBYMDANALYSIS.glb, EGF.zip) are not molecules");
        Check(MoleculeLibraryBuilder.MoleculeFolders().Count == molecules.Count, "every molecule folder is in the library");

        // The tunnel segment still knows every molecule (its TS Menu reads the registry; the registry the toys).
        var segmentDependencies = AssetDatabase.GetDependencies(k_SegmentPath, true);
        foreach (var entry in molecules.Where(m => m.prefab != null))
            Check(segmentDependencies.Contains(AssetDatabase.GetAssetPath(entry.prefab)), $"TG_TunnelSegment still reaches {entry.name}'s toy");

        // The builders find the molecules they use in their new folders.
        Check(MoleculeLibraryBuilder.TemplatePath == $"{MoleculeLibraryBuilder.k_Root}/viral capsids/2PLV/Toy_2PLV_Networked.prefab", $"the builders' template is 2PLV's toy ({MoleculeLibraryBuilder.TemplatePath})");
        Check(MoleculeLibraryBuilder.ToyPath("IGG") == $"{MoleculeLibraryBuilder.k_Root}/proteins/IGG/Toy_IGG_Networked.prefab", "the movie builder finds IgG");
        Check(MoleculeLibraryBuilder.FolderOf("SARS") == $"{MoleculeLibraryBuilder.k_Root}/viral capsids/SARS", "the virion builder finds SARS");

        // EGFR and the ligand: made like the others.
        var template = AssetDatabase.LoadAssetAtPath<GameObject>(MoleculeLibraryBuilder.TemplatePath);
        var igg = molecules.FirstOrDefault(m => m.name == "IGG")?.prefab;
        foreach (string name in new[] { "EGFR", "EGFR ligand" })
        {
            var prefab = molecules.FirstOrDefault(m => m.name == name)?.prefab;
            if (prefab == null)
                continue;
            Check(template != null && prefab.transform.localScale == template.transform.localScale, $"{name} is at 2PLV's scale ({prefab.transform.localScale.x})");
            Check(prefab.GetComponent<MoleculeGravity>() != null && prefab.GetComponent<Rigidbody>() != null, $"{name} has MoleculeGravity and a body");
            Check(prefab.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>() != null, $"{name} can be grabbed");
            int colliders = prefab.GetComponentsInChildren<Collider>(true).Count(c => !c.isTrigger);
            Check(name == "EGFR" ? colliders >= 4 : colliders == 1,
                name == "EGFR" ? $"EGFR's body follows its surface ({colliders} convex pieces, its pocket left open)" : $"{name} has one fitted collider");
            if (name == "EGFR")
                Check(prefab.GetComponent<CapsidSites>() is { pocket: true } pocket && pocket.binds == "EGFR ligand" && prefab.GetComponent<AntibodyBinder>() == null,
                    "EGFR has one pocket for its ligand (Ligand binding) and binds nothing else");
            else
                Check(prefab.GetComponent<AntibodyBinder>() is { ligand: true } && prefab.GetComponent<CapsidSites>() == null,
                    "the ligand binds only into EGFR's pocket (no Fab arms, no sites of its own)");
            Check(prefab.GetComponent<MoleculeGravity>().volume > 0f, $"{name} is weighed by its volume");
            if (name == "EGFR")
                Check(prefab.transform.Find("Bound Cys775") is { } cys && !cys.gameObject.activeSelf && cys.GetComponentsInChildren<MeshRenderer>(true).Length > 0
                    && cys.GetComponentsInChildren<Collider>(true).Length == 0, "EGFR holds Cys775, hidden until a ligand is bound");
            int triangles = SpawnNozzle.Triangles(prefab);
            int copies = SpawnNozzle.CopiesFor(100, triangles, 3000000);
            Check(copies >= 50, $"{name}: {triangles:N0} triangles, the nozzle sprays up to {copies} per spray");
            report.AppendLine($"       {name} at ×1: {SizeCm(prefab)}");
        }

        var egfr = molecules.FirstOrDefault(m => m.name == "EGFR")?.prefab;
        if (egfr != null)
        {
            Check(igg != null && SpawnNozzle.Triangles(egfr) <= SpawnNozzle.Triangles(igg), $"EGFR is no heavier than IgG ({SpawnNozzle.Triangles(egfr):N0} vs {(igg != null ? SpawnNozzle.Triangles(igg) : 0):N0} triangles)");
            var size = MeshSize(egfr) / k_UnitsPerAngstrom;
            Check(Mathf.Abs(Mathf.Max(size.x, size.y, size.z) - 70f) < 4f, $"EGFR is its true size, about 70 Å across ({size.x:0} × {size.y:0} × {size.z:0} Å)");
            Check(egfr.GetComponentsInChildren<MeshRenderer>(true).Where(r => !r.transform.parent.name.StartsWith("Bound ")).All(r => r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("2PLV_")),
                "EGFR is coloured as the other surfaces (2PLV's chain colours)");
        }

        var ligand = molecules.FirstOrDefault(m => m.name == "EGFR ligand")?.prefab;
        if (ligand != null)
        {
            var colours = ligand.GetComponentsInChildren<MeshRenderer>(true).Select(r => (Color32)r.sharedMaterial.GetColor("_BaseColor")).ToList();
            bool Yellow(Color32 c) => c.r > 200 && c.g > 200 && c.b < 100;
            Check(colours.Count == 3 && !colours.Any(Yellow),
                $"the free ligand is the inhibitor alone, in its atoms' colours from Mol* (carbon, nitrogen, oxygen; no sulfur, so no Cys775): {string.Join(", ", colours.Select(c => ColorUtility.ToHtmlStringRGB(c)))}");
            var size = MeshSize(ligand) / k_UnitsPerAngstrom;
            Check(Mathf.Abs(Mathf.Max(size.x, size.y, size.z) - 17.9f) < 1f, $"the ligand is its true size, about 18 Å long ({size.x:0} × {size.y:0} × {size.z:0} Å)");
            var grab = ligand.GetComponent<MoleculeGrabSize>();
            Check(grab != null, "the tiny ligand keeps a minimum grab collider");
            var collider = ligand.GetComponentInChildren<Collider>(true);
            Check(collider != null && MinWorldSize(collider, ligand.transform.localScale.x) >= 0.0199f,
                $"at ×1 its collider is at least 2 cm across ({(collider != null ? MinWorldSize(collider, ligand.transform.localScale.x) * 100f : 0f):0.0} cm)");

            // Magnified ×20 (past 2 cm), the collider fits the molecule again.
            var copy = Object.Instantiate(ligand, new Vector3(-60f, 1.5f, -60f), Quaternion.identity);
            copy.transform.localScale = ligand.transform.localScale * 20f;
            copy.GetComponent<MoleculeGrabSize>().Fit();
            var copyCollider = copy.GetComponentInChildren<Collider>(true);
            Check(copyCollider.transform.localScale == Vector3.one, $"at ×20 the ligand's collider fits it again ({copyCollider.transform.localScale})");
            Object.DestroyImmediate(copy);
        }

        TestMenu(registry, Check, report);
        if (Application.isPlaying)
            TestInContainer(registry, Check);
        return Finish(report, failures);
    }

    /// <summary>The Molecules list, built in a copy of the tunnel segment: headings and rows in order.</summary>
    static void TestMenu(TunnelPrefabRegistry registry, System.Action<bool, string> Check, StringBuilder report)
    {
        var segment = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(k_SegmentPath));
        try
        {
            segment.transform.position = new Vector3(-80f, 0f, -80f);
            var menu = segment.GetComponentInChildren<TunnelMoleculeMenu>(true);
            var so = new SerializedObject(menu);
            var list = (GameObject)so.FindProperty("m_ListPanel").objectReferenceValue;
            var content = (RectTransform)so.FindProperty("m_ListContent").objectReferenceValue;
            list.SetActive(true);
            typeof(TunnelMoleculeMenu).GetMethod("BuildList", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(menu, null);
            var lines = content.Cast<Transform>().Where(t => t.gameObject.activeSelf)
                .OrderByDescending(t => ((RectTransform)t).anchoredPosition.y)
                .Select(t => (heading: !t.GetComponent<Button>().interactable, text: t.GetComponentInChildren<TMP_Text>(true).text.Replace("<b>", "").Replace("</b>", "")))
                .ToList();
            string shown = string.Join(" | ", lines.Select(l => l.heading ? $"[{l.text}]" : l.text));
            report.AppendLine($"       the list reads: {shown}");
            // Molecules from the asset repository (when its list has been read) come after the built-in ones of their
            // category; the built-in ones read as always.
            var repository = new HashSet<string>(TunnelAssetCatalog.Molecules().Select(TunnelAssetCatalog.MenuName));
            var builtInNames = new HashSet<string>(registry.molecules.Select(m => m.name));
            string builtInShown = string.Join(" | ", lines.Where(l => l.heading || builtInNames.Contains(l.text) || !repository.Contains(l.text))
                .Select(l => l.heading ? $"[{l.text}]" : l.text));
            Check(builtInShown ==
                "[Viral capsids] | 2PLV | SARS | TBSV | Zika | [Assemblies] | E. coli 50S ribosome | Hi-res Ribosome | [Proteins] | EGFR | IGG | [Ligands] | EGFR ligand",
                "the Molecules list shows each built-in molecule under its heading, in category order");
            Check(lines.Where(l => !l.heading && builtInNames.Contains(l.text)).Select(l => l.text).SequenceEqual(registry.molecules.Select(m => m.name)),
                "every built-in molecule has a row, with the name people already know");
            Check(lines.Where(l => !l.heading).GroupBy(l => l.text).All(g => g.Count() == 1),
                "no molecule is listed twice (a repository copy of a built-in one isn't listed again)");
            for (int i = 0; i + 1 < lines.Count; i++)
                if (lines[i].heading && lines[i + 1].heading)
                    Check(false, $"no empty heading ({lines[i].text})");
        }
        finally
        {
            Object.DestroyImmediate(segment);
        }
    }

    /// <summary>In Play mode: EGFR and the ligand in a box container are its contents, as any molecule.</summary>
    static void TestInContainer(TunnelPrefabRegistry registry, System.Action<bool, string> Check)
    {
        var at = new Vector3(-60f, 1.5f, -50f);
        var box = Object.Instantiate(registry.containerBox, at, Quaternion.identity);
        box.transform.localScale = Vector3.one;
        var made = new List<GameObject> { box };
        try
        {
            foreach (string name in new[] { "EGFR", "EGFR ligand" })
            {
                var prefab = registry.molecules.First(m => m.name == name).prefab;
                var go = Object.Instantiate(prefab, at, Quaternion.identity);
                go.transform.localScale = prefab.transform.localScale * 5f;
                made.Add(go);
            }
            Physics.SyncTransforms();
            var contents = box.GetComponent<TunnelContainer>().Contents(true);
            Check(made.Skip(1).All(contents.Contains), $"the container holds EGFR and the ligand ({contents.Count} molecule(s) inside)");
        }
        finally
        {
            foreach (var go in made)
                Object.Destroy(go);
        }
    }

    static UnityEngine.SceneManagement.Scene s_Scratch, s_WasActive;

    /// <summary>
    /// In Edit mode, makes a scratch scene the active one, so what the test makes goes there and the open scenes are
    /// left with no unsaved changes (it is closed, unsaved, at the end).
    /// </summary>
    static void UseScratchScene()
    {
        if (Application.isPlaying)
            return;
        s_WasActive = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        s_Scratch = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
        UnityEngine.SceneManagement.SceneManager.SetActiveScene(s_Scratch);
    }

    // The toy's drawn size (toy units, before its root's scale).
    static Vector3 MeshSize(GameObject prefab)
    {
        var bounds = new Bounds();
        bool first = true;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null))
        {
            var b = filter.sharedMesh.bounds;
            if (first)
                bounds = b;
            else
                bounds.Encapsulate(b);
            first = false;
        }
        return bounds.size;
    }

    static string SizeCm(GameObject prefab)
    {
        var s = MeshSize(prefab) * prefab.transform.localScale.x * 100f;
        return $"{s.x:0.00} × {s.y:0.00} × {s.z:0.00} cm";
    }

    // The collider's smallest extent along its own axes, in meters at this root scale.
    static float MinWorldSize(Collider collider, float rootScale)
    {
        Vector3 size = collider switch
        {
            BoxCollider box => box.size,
            MeshCollider mesh when mesh.sharedMesh != null => mesh.sharedMesh.bounds.size,
            SphereCollider sphere => Vector3.one * sphere.radius * 2f,
            _ => Vector3.zero,
        };
        size = Vector3.Scale(size, collider.transform.localScale) * rootScale;
        return Mathf.Min(size.x, Mathf.Min(size.y, size.z));
    }

    static string Finish(StringBuilder report, int failures)
    {
        if (s_Scratch.IsValid())
        {
            if (s_WasActive.IsValid())
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(s_WasActive);
            UnityEditor.SceneManagement.EditorSceneManager.CloseScene(s_Scratch, true);
        }
        s_Scratch = default;
        report.Insert(0, failures == 0 ? "PASS " : $"FAIL ({failures}) ");
        string text = report.ToString();
        if (failures == 0)
            Debug.Log(text);
        else
            Debug.LogError(text);
        return text;
    }
}
