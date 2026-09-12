// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.IO;

using Prowl.Echo;
using Prowl.Editor.Importers;
using Prowl.Runtime;

namespace Prowl.Animate.Editor;

/// <summary>
/// Imports <c>.animator</c> files - Echo-serialized <see cref="AnimatorController"/> assets. The
/// controller's polymorphic motion/state graph is restored automatically by Echo; the clips it
/// references are registered as dependencies so editing a clip reimports the controllers that use it.
/// </summary>
[ImporterFor(".animator")]
public class AnimatorControllerImporter : AssetImporter
{
    public override int Version => 1;

    public override bool Import(ImportContext ctx)
    {
        try
        {
            string text = File.ReadAllText(ctx.AbsolutePath);
            var echo = EchoObject.ReadFromString(text);

            var serCtx = ImportHelper.CreateTrackingContext(out var dependencies);
            var controller = Serializer.Deserialize<AnimatorController>(echo, serCtx);
            if (controller == null)
            {
                Debug.LogWarning($"[Animate] '{ctx.AbsolutePath}' did not deserialize into an AnimatorController.");
                return false;
            }

            controller.Name = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);
            ctx.SetMainAsset(controller);

            foreach (var dep in dependencies)
                ctx.AddDependency(dep);

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Animate] Failed to import animator controller '{ctx.AbsolutePath}': {ex.Message}");
            return false;
        }
    }
}
