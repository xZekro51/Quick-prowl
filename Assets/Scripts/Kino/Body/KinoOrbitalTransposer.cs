// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Orbits the follow target at a fixed distance, with the player steering. A third-person camera in
/// one component: heading, elevation and distance, each with its own limits and easing.
/// </summary>
/// <remarks>
/// <para>
/// Pair it with <see cref="KinoComposer"/> or <see cref="KinoHardLookAt"/> to keep the subject framed
/// while it orbits, and with <see cref="KinoCollider"/> so it ducks under scenery.
/// </para>
/// <para>
/// Give <see cref="Heading"/> a <see cref="KinoAxis.RecenterWait"/> and the camera drifts back behind
/// the target when the player stops steering, which is what most third-person games do.
/// </para>
/// </remarks>
[AddComponentMenu("Kino/Body/Kino Orbital Transposer")]
[ComponentIcon("\ue4bb")] // ArrowsSpin
public class KinoOrbitalTransposer : KinoComponent
{
    /// <summary>Where the orbit is centred, relative to the follow target. Raise Y to orbit the head rather than the feet.</summary>
    public Float3 TargetOffset = new(0f, 1.5f, 0f);

    /// <summary>How far out the camera orbits, in metres.</summary>
    public float Radius = 6f;

    /// <summary>Rotation around the up axis, in degrees. Wraps, so it can be spun forever.</summary>
    public KinoAxis Heading = new()
    {
        Min = -180f,
        Max = 180f,
        Wrap = true,
        Input = KinoInputSource.MouseX,
        Gain = 0.2f,
        MaxSpeed = 180f
    };

    /// <summary>Height around the target, in degrees. Positive looks down at it.</summary>
    public KinoAxis Elevation = new()
    {
        Min = -30f,
        Max = 70f,
        Wrap = false,
        Input = KinoInputSource.MouseY,
        Gain = 0.15f,
        MaxSpeed = 120f
    };

    /// <summary>Seconds to catch up on each axis of the orbit frame - sideways, vertical, distance.</summary>
    public Float3 Damping = new(0.2f, 0.2f, 0.2f);

    /// <summary>
    /// Line the camera up behind the target the first time it runs. Turn it off to start at whatever
    /// <see cref="KinoAxis.Value"/> the heading was authored with.
    /// </summary>
    public bool StartBehindTarget = true;

    private bool _initialised;

    public override KinoStage Stage => KinoStage.Body;

    public override bool IsUsable => HasFollow;

    public override void ResetDamping()
    {
        Heading.Reset();
        Elevation.Reset();
    }

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (!HasFollow)
            return;

        Float3 up = ctx.WorldUp;
        Float3 pivot = FollowPosition + TargetOffset;

        // Heading zero means "behind the target", so letting go of the stick returns the camera to
        // the shot the player started with rather than to some fixed world direction.
        Quaternion baseFrame = KinoMath.HeadingFrame(up);
        float targetHeading = KinoMath.SignedAngle(baseFrame * Float3.UnitZ, FollowRotation * Float3.UnitZ, up);

        if (!_initialised)
        {
            _initialised = true;
            if (StartBehindTarget)
                Heading.Set(targetHeading);
        }

        // Deliberately not re-centred when the camera goes live: taking over should not yank the view
        // out of wherever the player had steered it to.
        Heading.Update(ctx.DeltaTime, targetHeading);
        Elevation.Update(ctx.DeltaTime);

        Quaternion orbit = Quaternion.AxisAngle(up, Heading.Value * Maths.Deg2Rad)
                           * baseFrame
                           * Quaternion.RotateX(Elevation.Value * Maths.Deg2Rad);

        Float3 desired = pivot + orbit * new Float3(0f, 0f, -Maths.Max(Radius, 0f));

        Float3 gap = Quaternion.Inverse(orbit) * (desired - state.Position);
        state.Position += orbit * KinoMath.Damp(gap, Damping, ctx.DeltaTime);
    }

    public override void DrawComponentGizmos()
    {
        if (!HasFollow)
            return;

        Float3 pivot = FollowPosition + TargetOffset;
        Debug.DrawWireCircle(pivot, Float3.UnitY, Maths.Max(Radius, 0.01f), Color.Green);
        Debug.DrawLine(pivot, Transform.Position, Color.Green);
    }
}
