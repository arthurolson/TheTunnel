using System;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

/// <summary>
/// Runs a builder with a new, empty scene as the active one and closes it afterwards without saving, so the objects a
/// builder makes along the way (new GameObjects go to the active scene) never leave the open scenes with unsaved
/// changes. Used by the container, splat and movie builders.
/// </summary>
static class BuilderScratchScene
{
    static int s_Depth;

    public static T Run<T>(Func<T> build)
    {
        // Already in one (a builder that runs another): no second scratch scene.
        if (s_Depth > 0)
            return build();
        var previous = SceneManager.GetActiveScene();
        var scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        s_Depth++;
        try
        {
            SceneManager.SetActiveScene(scratch);
            return build();
        }
        finally
        {
            s_Depth--;
            if (previous.IsValid() && previous.isLoaded)
                SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scratch, true);
        }
    }
}
