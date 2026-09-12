// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;

using Prowl.Runtime;
using Prowl.Runtime.AssetImporting;

namespace Prowl.Animate;

/// <summary>Knobs for <see cref="AnimationClipImporter"/>.</summary>
public struct AnimationClipImporterSettings
{
    /// <summary>Uniform scale applied to the animation's positional tracks. Matches the model importer.</summary>
    public float UnitScale = 1.0f;

    /// <summary>Wrap the resulting clips as looping.</summary>
    public bool Loop = false;

    /// <summary>Playback rate multiplier baked into the clip's ticks-per-second.</summary>
    public float Speed = 1f;

    public AnimationClipImporterSettings() { }
}

/// <summary>The clips pulled out of a source file.</summary>
public sealed class AnimationClipImportResult
{
    public List<AnimationClip> Animations = new();
}

/// <summary>
/// Loads the animation tracks out of a model/animation file (.gltf / .glb / .vrm / .fbx) as
/// <see cref="AnimationClip"/>s, without keeping any of the mesh/material/hierarchy data.
///
/// <para>This is the animation-only counterpart to <see cref="ModelImporter"/>: it reuses the exact
/// same Clay-backed extraction path the model importer uses for animations (so coordinate handling,
/// quaternion continuity, bone-path naming and blend-shape tracks are identical), then keeps only
/// the resulting clips. Use it for animation-only source files (a "Walk.fbx" with no mesh) when you
/// want clean standalone clip assets instead of clips buried as model sub-assets.</para>
/// </summary>
public sealed class AnimationClipImporter
{
    public AnimationClipImportResult Import(FileInfo assetPath, AnimationClipImporterSettings? settings = null)
    {
        var s = settings ?? new AnimationClipImporterSettings();
        ModelImportResult model = new ModelImporter().Import(assetPath, ToModelSettings(s));
        return Extract(model, s);
    }

    public AnimationClipImportResult Import(Stream stream, string virtualPath, AnimationClipImporterSettings? settings = null)
    {
        var s = settings ?? new AnimationClipImporterSettings();
        ModelImportResult model = new ModelImporter().Import(stream, virtualPath, ToModelSettings(s));
        return Extract(model, s);
    }

    /// <summary>
    /// The animation tracks don't depend on anything else the model importer produces, so everything
    /// that isn't animation is turned off - only <see cref="AnimationClipImporterSettings.UnitScale"/>
    /// influences the (positional) animation data.
    ///
    /// <para>Materials, cameras and lights are off as well as the mesh post-processing: importing
    /// materials resolves (and, on the default resolver, decodes and GPU-uploads) every texture the
    /// file references, which for an animation-only import is the most expensive thing in the run and
    /// none of it survives <see cref="Extract"/>. Blend shapes stay on - their weight tracks are
    /// animation.</para>
    /// </summary>
    private static ModelImporterSettings ToModelSettings(AnimationClipImporterSettings s) => new()
    {
        UnitScale = s.UnitScale,
        ImportAnimations = true,
        ImportBlendShapes = true,
        AnimationWrapMode = s.Loop ? AnimationWrapMode.Loop : AnimationWrapMode.Once,

        GenerateNormals = false,
        GenerateSmoothNormals = false,
        RecalculateNormals = false,
        CalculateTangentSpace = false,
        GenerateLightmapUVs = false,
        OptimizeMeshes = false,

        // OptimizeHierarchy folds pass-through nodes into their children, which renames bone paths.
        // A clip's tracks are keyed by those paths, so collapsing them silently breaks the binding.
        OptimizeHierarchy = false,

        ImportMaterials = false,
        ImportCameras = false,
        ImportLights = false,
    };

    private static AnimationClipImportResult Extract(ModelImportResult model, AnimationClipImporterSettings s)
    {
        var result = new AnimationClipImportResult { Animations = model.Animations ?? new List<AnimationClip>() };

        foreach (var clip in result.Animations)
        {
            clip.Wrap = s.Loop ? AnimationWrapMode.Loop : AnimationWrapMode.Once;
            if (s.Speed > 0f) clip.TicksPerSecond *= s.Speed;
        }

        return result;
    }
}
