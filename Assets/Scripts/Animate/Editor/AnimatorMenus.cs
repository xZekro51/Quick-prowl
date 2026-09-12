// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.IO;

using Prowl.Editor;
using Prowl.Editor.Core;
using Prowl.Editor.Projects;
using Prowl.Runtime;

namespace Prowl.Animate.Editor;

/// <summary>Menu entry points: opening the graph editor, and creating a controller asset.</summary>
public static class AnimatorMenus
{
    [MenuItem("Window/Tools/Animator Controller", priority: 210)]
    private static void OpenWindow() => AnimatorGraphWindow.OpenEmpty();

    [MenuItem("Window/Tools/Animation", priority: 211)]
    private static void OpenAnimationWindow() => AnimationClipEditorWindow.OpenEmpty();

    /// <summary>
    /// Create an empty <c>.anim</c> clip. Unity makes you go through the Animation window with a
    /// GameObject selected before it will create one; this just makes the asset.
    /// </summary>
    [MenuItem("Assets/Create/Animation/Animation Clip", priority: 41)]
    private static void CreateClip()
    {
        if (Project.Current == null)
        {
            Debug.LogWarning("[Animate] No project open.");
            return;
        }

        string folder = AssetCreateMenu.GetCurrentFolder();
        string absoluteFolder = AssetCreateMenu.GetAbsoluteFolder(folder);
        string fileName = AssetCreateMenu.FindUniqueName(absoluteFolder, "New Animation", ".anim");
        string absolutePath = Path.Combine(absoluteFolder, fileName);

        var clip = new AnimationClip
        {
            Name = Path.GetFileNameWithoutExtension(fileName),
            Duration = 1f,
            TicksPerSecond = 30f,
            Wrap = AnimationWrapMode.Loop,
        };

        AnimationClipIO.SaveAs(clip, absolutePath);
        Debug.Log($"[Animate] Created {fileName}");
    }

    /// <summary>
    /// Create a starter controller next to whatever is selected in the Project panel. It ships with a
    /// base layer and an Idle state so the asset is playable the moment it exists, rather than an
    /// empty graph that silently does nothing.
    /// </summary>
    [MenuItem("Assets/Create/Animation/Animator Controller", priority: 40)]
    private static void CreateController()
    {
        if (Project.Current == null)
        {
            Debug.LogWarning("[Animate] No project open.");
            return;
        }

        string folder = AssetCreateMenu.GetCurrentFolder();
        string absoluteFolder = AssetCreateMenu.GetAbsoluteFolder(folder);
        string fileName = AssetCreateMenu.FindUniqueName(absoluteFolder, "New Animator Controller", ".animator");
        string absolutePath = Path.Combine(absoluteFolder, fileName);

        var controller = new AnimatorController(Path.GetFileNameWithoutExtension(fileName));
        controller.AddLayer("Base Layer").AddState("Idle");

        AnimatorAssetIO.WriteTo(controller, absolutePath);

        // ImportFile keys its tables by the project-relative path.
        var backend = EditorAssetBackend.Instance;
        backend?.ImportFile(backend.ToRelativePath(absolutePath));

        Debug.Log($"[Animate] Created {fileName}");
    }
}
