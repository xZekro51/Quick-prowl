// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Copies the follow target's orientation. Whatever the target is facing, the camera faces - the aim
/// for a cockpit, a helmet cam, or anything mounted to something that already knows where it is
/// looking.
/// </summary>
[AddComponentMenu("Kino/Aim/Kino Same As Follow Target")]
[ComponentIcon("\uf0c1")] // Link
public class KinoSameAsFollowTarget : KinoComponent
{
    /// <summary>Turn relative to the target, in degrees. Pitch, yaw, roll.</summary>
    public Float3 Rotation = Float3.Zero;

    /// <summary>Seconds of lag before the camera comes round. Zero is a rigid mount.</summary>
    public float Damping = 0f;

    public override KinoStage Stage => KinoStage.Aim;

    public override bool IsUsable => HasFollow;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (!HasFollow)
            return;

        Quaternion wanted = FollowRotation * Quaternion.FromEuler(Rotation);

        state.Rotation = Damping <= 0f
            ? wanted
            : KinoMath.DampTowards(state.Rotation, wanted, Damping, ctx.DeltaTime);
    }
}
