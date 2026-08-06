// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Hands the aim to the player. Two axes, mouse or stick, with limits and optional recentering -
/// first-person look, or a free look bolted onto a follow camera.
/// </summary>
/// <remarks>
/// Needs no look-at target: it points the camera where the player is pointing it. Combine with
/// <see cref="KinoHardLockToTarget"/> for first person, or with a transposer for an over-the-shoulder
/// camera the player aims themselves.
/// </remarks>
[AddComponentMenu("Kino/Aim/Kino POV")]
[ComponentIcon("\uf06e")] // Eye
public class KinoPOV : KinoComponent
{
    /// <summary>Left and right, in degrees. Wraps by default, so it can be spun forever.</summary>
    public KinoAxis Horizontal = new()
    {
        Min = -180f,
        Max = 180f,
        Wrap = true,
        Input = KinoInputSource.MouseX,
        Gain = 0.15f,
        MaxSpeed = 180f
    };

    /// <summary>
    /// Up and down, in degrees, positive looking up. Inverted by default because screen coordinates
    /// grow downwards - untick it for inverted-look players.
    /// </summary>
    public KinoAxis Vertical = new()
    {
        Min = -70f,
        Max = 70f,
        Wrap = false,
        Input = KinoInputSource.MouseY,
        Gain = 0.15f,
        MaxSpeed = 120f,
        Invert = true
    };

    /// <summary>
    /// Measure the angles from the follow target's facing instead of from the world, so turning the
    /// character turns the view with it.
    /// </summary>
    public bool RelativeToFollowTarget = false;

    public override KinoStage Stage => KinoStage.Aim;

    public override void ResetDamping()
    {
        Horizontal.Reset();
        Vertical.Reset();
    }

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        Horizontal.Update(ctx.DeltaTime);
        Vertical.Update(ctx.DeltaTime);

        Quaternion frame = KinoMath.HeadingFrame(state.ReferenceUp);
        if (RelativeToFollowTarget && HasFollow)
            frame = KinoMath.FlattenToUp(FollowRotation, state.ReferenceUp);

        state.Rotation = Quaternion.AxisAngle(state.ReferenceUp, Horizontal.Value * Maths.Deg2Rad)
                         * frame
                         * Quaternion.RotateX(-Vertical.Value * Maths.Deg2Rad);
    }
}
