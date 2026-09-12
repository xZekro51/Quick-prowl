// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.IO;

using Prowl.Echo;
using Prowl.Editor;
using Prowl.Editor.Projects;
using Prowl.Runtime;

namespace Prowl.Animate.Editor;

/// <summary>
/// Reading and writing <c>.animator</c> files. Kept in one place so the graph window, the inspector
/// and the create-asset menu all round-trip a controller exactly the same way.
/// </summary>
internal static class AnimatorAssetIO
{
    /// <summary>
    /// Serialize a controller back over its own asset file and reimport it. Returns false (and logs)
    /// when the controller is not backed by a file - a runtime-built controller, for instance.
    /// </summary>
    public static bool Save(AnimatorController controller)
    {
        if (controller.IsNotValid()) return false;

        var backend = EditorAssetBackend.Instance;
        if (backend == null || Project.Current == null)
        {
            Debug.LogWarning("[Animate] Cannot save: no project is open.");
            return false;
        }

        string? relative = backend.GuidToPath(controller.AssetID);
        if (string.IsNullOrEmpty(relative))
        {
            Debug.LogWarning("[Animate] Cannot save: this controller has no asset path.");
            return false;
        }

        string absolute = Path.Combine(Project.Current.AssetsPath, relative);

        try
        {
            WriteTo(controller, absolute);
            backend.Reimport(controller.AssetID);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Animate] Failed to save '{relative}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write a controller to an absolute path. The asset id is blanked for the duration of the write
    /// so the file does not bake in the guid that the meta file owns - otherwise a duplicated asset
    /// would claim the original's identity on import.
    /// </summary>
    public static void WriteTo(AnimatorController controller, string absolutePath)
    {
        Guid saved = controller.AssetID;
        controller.AssetID = Guid.Empty;
        try
        {
            EchoObject echo = Serializer.Serialize(typeof(object), controller);
            File.WriteAllText(absolutePath, echo.WriteToString());
        }
        finally
        {
            controller.AssetID = saved;
        }
    }
}
