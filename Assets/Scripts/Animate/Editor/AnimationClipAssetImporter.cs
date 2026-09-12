// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.IO;

using Prowl.Echo;
using Prowl.Editor.Importers;
using Prowl.Runtime;

namespace Prowl.Animate.Editor;

/// <summary>
/// Imports animation-only source files as standalone <see cref="AnimationClip"/> assets.
///
/// <list type="bullet">
/// <item><c>.anim</c> - an Echo-serialized clip written by hand or by tooling.</item>
/// <item><c>.vrm</c> - a model container; only its animation tracks are kept.</item>
/// </list>
///
/// <para>It deliberately does not claim <c>.fbx</c>/<c>.gltf</c>/<c>.glb</c>, so it cannot shadow the
/// engine's model importer as the default for those. To pull clips out of an animation-only file in
/// one of those formats, assign this importer to that specific asset.</para>
///
/// <para>Unlike the version this was ported from, the model path actually produces assets: the first
/// clip becomes the main asset and the rest are registered as sub-assets, rather than the import
/// silently succeeding with nothing in it.</para>
/// </summary>
[ImporterFor(".anim", ".vrm")]
public class AnimationClipAssetImporter : AssetImporter
{
    public override int Version => 1;

    public override bool Import(ImportContext ctx)
    {
        try
        {
            var settings = ReadSettings(ctx);

            return ctx.AbsolutePath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
                ? ImportEchoClip(ctx, settings)
                : ImportFromModel(ctx, settings);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Animate] Failed to import animation '{ctx.AbsolutePath}': {ex.Message}");
            return false;
        }
    }

    private static bool ImportEchoClip(ImportContext ctx, AnimationClipImporterSettings settings)
    {
        var serCtx = ImportHelper.CreateTrackingContext(out var dependencies);
        var echo = EchoObject.ReadFromString(File.ReadAllText(ctx.AbsolutePath));

        var clip = Serializer.Deserialize<AnimationClip>(echo, serCtx);
        if (clip == null)
        {
            Debug.LogWarning($"[Animate] '{ctx.AbsolutePath}' did not deserialize into an AnimationClip.");
            return false;
        }

        clip.Wrap = settings.Loop ? AnimationWrapMode.Loop : AnimationWrapMode.Once;
        if (settings.Speed > 0f) clip.TicksPerSecond *= settings.Speed;

        if (string.IsNullOrEmpty(clip.Name))
            clip.Name = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);

        ctx.SetMainAsset(clip);
        foreach (var dep in dependencies) ctx.AddDependency(dep);
        return true;
    }

    private static bool ImportFromModel(ImportContext ctx, AnimationClipImporterSettings settings)
    {
        var result = new AnimationClipImporter().Import(new FileInfo(ctx.AbsolutePath), settings);
        if (result.Animations.Count == 0)
        {
            Debug.LogWarning($"[Animate] '{Path.GetFileName(ctx.AbsolutePath)}' contains no animation tracks.");
            return false;
        }

        string baseName = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);

        for (int i = 0; i < result.Animations.Count; i++)
        {
            var clip = result.Animations[i];
            if (string.IsNullOrEmpty(clip.Name))
                clip.Name = result.Animations.Count == 1 ? baseName : $"{baseName}_{i}";

            if (i == 0) ctx.SetMainAsset(clip);
            else ctx.AddSubAsset(clip.Name, clip, default);
        }

        return true;
    }

    private static AnimationClipImporterSettings ReadSettings(ImportContext ctx)
    {
        var settings = new AnimationClipImporterSettings();
        var s = ctx.Settings;
        if (s == null) return settings;

        if (s.TryGet("unitScale", out var unitScale)) settings.UnitScale = unitScale.FloatValue;
        if (s.TryGet("speed", out var speed)) settings.Speed = speed.FloatValue;
        if (s.TryGet("loop", out var loop)) settings.Loop = loop.BoolValue;
        return settings;
    }

    public override EchoObject? DefaultSettings()
    {
        var s = EchoObject.NewCompound();
        s["unitScale"] = new EchoObject(1.0f);
        s["speed"] = new EchoObject(1.0f);
        s["loop"] = new EchoObject(false);
        return s;
    }
}
