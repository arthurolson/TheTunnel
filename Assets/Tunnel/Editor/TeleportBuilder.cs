using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

/// <summary>
/// Tunnel > Teleport > Add Teleport Button: puts a "Teleport" check button under the Laser button over the left
/// palm, in the open scene (SampleScene), and wires it to a <see cref="TunnelTeleport"/> that aims with every hand and
/// controller. Run it again to bring the button and its wiring up to date; it saves the scene.
/// </summary>
public static class TeleportBuilder
{
    const string k_MaterialPath = "Assets/Tunnel/Materials/TeleportMarker.mat";

    [MenuItem("Tunnel/Teleport/Add Teleport Button")]
    public static void Build()
    {
        var laser = Object.FindAnyObjectByType<HandLaserToggle>(FindObjectsInactive.Include);
        if (laser == null)
        {
            Debug.LogError("[Teleport] no Hand Laser Toggle in the open scene (open SampleScene)");
            return;
        }
        var so = new SerializedObject(laser);
        var canvasRoot = (Transform)so.FindProperty("m_PalmButtonRoot").objectReferenceValue;
        var laserButton = (Button)so.FindProperty("m_Button").objectReferenceValue;
        if (canvasRoot == null || laserButton == null)
        {
            Debug.LogError("[Teleport] the Laser toggle has no palm button to sit under");
            return;
        }

        // The button: a copy of the Laser button, just under it.
        var existing = canvasRoot.Find("Teleport Button");
        if (existing != null)
            Object.DestroyImmediate(existing.gameObject);
        var copy = Object.Instantiate(laserButton.gameObject, laserButton.transform.parent);
        copy.name = "Teleport Button";
        var rect = (RectTransform)copy.transform;
        var laserRect = (RectTransform)laserButton.transform;
        float height = laserRect.rect.height * laserRect.localScale.y;
        rect.anchoredPosition = laserRect.anchoredPosition - new Vector2(0f, height + 12f);
        var button = copy.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        var label = copy.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            label.text = "[  ] Teleport";

        // The palm canvas grows downward to hold both buttons (the Laser button stays where it was).
        var canvasRect = (RectTransform)canvasRoot;
        float bottom = rect.anchoredPosition.y - height * 0.5f - 6f;
        float needed = Mathf.Max(canvasRect.sizeDelta.y, 2f * Mathf.Abs(bottom));
        canvasRect.sizeDelta = new Vector2(canvasRect.sizeDelta.x, needed);

        // The marker's material: plain, bright cyan, unlit.
        var material = AssetDatabase.LoadAssetAtPath<Material>(k_MaterialPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(material, k_MaterialPath);
        }
        material.SetColor("_BaseColor", new Color(0.25f, 0.85f, 1f, 1f));
        EditorUtility.SetDirty(material);

        // The teleport itself, beside the Laser toggle.
        var teleport = laser.GetComponent<TunnelTeleport>() ?? laser.gameObject.AddComponent<TunnelTeleport>();
        var interactors = Object.FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(i => i.gameObject.scene == laser.gameObject.scene).OrderBy(i => i.transform.parent.name).ToArray();
        var provider = Object.FindAnyObjectByType<TeleportationProvider>(FindObjectsInactive.Include);
        var t = new SerializedObject(teleport);
        var list = t.FindProperty("m_Interactors");
        list.arraySize = interactors.Length;
        for (int i = 0; i < interactors.Length; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = interactors[i];
        t.FindProperty("m_Provider").objectReferenceValue = provider;
        t.FindProperty("m_Button").objectReferenceValue = button;
        t.FindProperty("m_Label").objectReferenceValue = label;
        t.FindProperty("m_MarkerMaterial").objectReferenceValue = material;
        t.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(laser.gameObject.scene);
        EditorSceneManager.SaveScene(laser.gameObject.scene);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Teleport] Teleport button added under the Laser button; aims with {interactors.Length} hands and controllers " +
            $"({string.Join(", ", interactors.Select(i => i.transform.parent.name))}), provider {(provider != null ? provider.name : "missing")}");
    }
}
