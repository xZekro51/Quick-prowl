// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.IO;

using Prowl.Echo;
using Prowl.Editor;
using Prowl.Editor.Projects;
using Prowl.Runtime;

namespace Prowl.Animate.Editor;

/// <summary>Writing <see cref="AnimationClip"/> assets back to <c>.anim</c> files.</summary>
internal static class AnimationClipIO
{
    /// <summary>
    /// The clip's own writable <c>.anim</c> file (project-relative), or null when it does not have one.
    ///
    /// <para>Clips that came in as sub-assets of a model - an .fbx's embedded takes - live inside that
    /// model file and cannot be written back. The editor asks this <i>before</i> offering to save, so
    /// the answer becomes an Extract button rather than a failed save and a line in the console.</para>
    /// </summary>
    public static string? WritablePath(AnimationClip clip)
    {
        if (clip.IsNotValid() || EditorAssetBackend.Instance == null || Project.Current == null) return null;

        string? relative = EditorAssetBackend.Instance.GuidToPath(clip.AssetID);
        return !string.IsNullOrEmpty(relative) && relative!.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
            ? relative
            : null;
    }

    /// <summary>Save a clip over its own asset file and reimport it.</summary>
    public static bool Save(AnimationClip clip)
    {
        if (clip.IsNotValid()) return false;

        var backend = EditorAssetBackend.Instance;
        if (backend == null || Project.Current == null)
        {
            Debug.LogWarning("[Animate] Cannot save: no project is open.");
            return false;
        }

        string? relative = WritablePath(clip);
        if (relative == null)
        {
            Debug.LogWarning($"[Animate] '{clip.Name}' has no .anim file of its own (it is probably a model sub-asset). Extract it to a standalone clip first.");
            return false;
        }

        string absolute = Path.Combine(Project.Current.AssetsPath, relative);
        try
        {
            WriteTo(clip, absolute);
            backend.Reimport(clip.AssetID);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Animate] Failed to save '{relative}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Write a copy of a clip as a standalone <c>.anim</c> beside the file it came from, and return the
    /// new asset's id (<see cref="Guid.Empty"/> on failure). This is how a take embedded in a model
    /// becomes something you can actually edit and save.
    /// </summary>
    public static Guid Extract(AnimationClip clip)
    {
        var backend = EditorAssetBackend.Instance;
        if (clip.IsNotValid() || backend == null || Project.Current == null)
        {
            Debug.LogWarning("[Animate] Cannot extract: no project is open.");
            return Guid.Empty;
        }

        // Sub-assets report the container's path, which is exactly the folder we want to land in.
        string? source = backend.GuidToPathIncludingSubAssets(clip.AssetID);
        string folder = !string.IsNullOrEmpty(source)
            ? Path.GetDirectoryName(Path.Combine(Project.Current.AssetsPath, source!)) ?? Project.Current.AssetsPath
            : Project.Current.AssetsPath;

        string baseName = string.IsNullOrWhiteSpace(clip.Name) ? "Animation" : clip.Name;
        string fileName = AssetCreateMenu.FindUniqueName(folder, baseName, ".anim");
        string absolute = Path.Combine(folder, fileName);

        if (!SaveAs(clip, absolute)) return Guid.Empty;

        Guid guid = backend.PathToGuid(backend.ToRelativePath(absolute));
        Debug.Log($"[Animate] Extracted '{clip.Name}' to {fileName}");
        return guid;
    }

    /// <summary>Write a clip to a new <c>.anim</c> file under the project and import it.</summary>
    public static bool SaveAs(AnimationClip clip, string absolutePath)
    {
        try
        {
            WriteTo(clip, absolutePath);

            // ImportFile keys its tables by the project-relative path; handing it an absolute one
            // registers the asset under a path nothing will ever look up again.
            var backend = EditorAssetBackend.Instance;
            backend?.ImportFile(backend.ToRelativePath(absolutePath));
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Animate] Failed to write '{absolutePath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Serialize to an absolute path. The asset id is blanked across the write so the file does not
    /// bake in the guid the meta file owns.
    /// </summary>
    public static void WriteTo(AnimationClip clip, string absolutePath)
    {
        Guid saved = clip.AssetID;
        clip.AssetID = Guid.Empty;
        try
        {
            EchoObject echo = Serializer.Serialize(typeof(object), clip);
            File.WriteAllText(absolutePath, echo.WriteToString());
        }
        finally
        {
            clip.AssetID = saved;
        }
    }
}
