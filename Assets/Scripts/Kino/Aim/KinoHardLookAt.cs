// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Points the camera straight at the look-at target, dead centre. The simplest aim there is, and the
/// right one whenever the subject should never drift off the middle of the screen.
/// </summary>
[AddComponentMenu("Kino/Aim/Kino Hard Look At")]
[ComponentIcon("\uf05b")] // Crosshairs
public class KinoHardLookAt : KinoComponent
{
    /// <summary>World-space offset from the target to the point actually aimed at - the head rather than the feet.</summary>
    public Float3 TrackedObjectOffset = Float3.Zero;

    /// <summary>Seconds of lag before the camera comes round. Zero is a rigid aim.</summary>
    public float Damping = 0f;

    public override KinoStage Stage => KinoStage.Aim;

    public override bool IsUsable => HasLookAt;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (!HasLookAt)
            return;

        Float3 point = LookAtPosition + TrackedObjectOffset;
        Quaternion wanted = KinoMath.SafeLookRotation(point - state.Position, state.ReferenceUp, state.Rotation);

        state.Rotation = Damping <= 0f
            ? wanted
            : KinoMath.DampTowards(state.Rotation, wanted, Damping, ctx.DeltaTime);

        // Recorded even when damping has the camera lagging behind, so a blend still knows what this
        // shot was about and keeps the subject pinned while it interpolates.
        state.LookAtPoint = point;
        state.HasLookAt = true;
    }
}
