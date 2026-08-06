// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Bolts the camera to the follow target. No easing unless you ask for it, no lag, no drift - a
/// first-person head, a cockpit, a camera mounted on a vehicle.
/// </summary>
[AddComponentMenu("Kino/Body/Kino Hard Lock To Target")]
[ComponentIcon("\uf023")] // Lock
public class KinoHardLockToTarget : KinoComponent
{
    /// <summary>Offset from the target, in the target's own axes. Y raises the mount, Z pushes it forward.</summary>
    public Float3 Offset = Float3.Zero;

    /// <summary>Seconds of lag. Zero is a rigid mount, which is what this component is usually for.</summary>
    public float Damping = 0f;

    public override KinoStage Stage => KinoStage.Body;

    public override bool IsUsable => HasFollow;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (!HasFollow)
            return;

        Float3 desired = FollowPosition + FollowRotation * Offset;
        state.Position += KinoMath.Damp(desired - state.Position, Damping, ctx.DeltaTime);
    }
}
