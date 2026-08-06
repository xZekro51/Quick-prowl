// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Holds the camera at a fixed offset from the follow target, easing into position rather than
/// snapping. The everyday follow camera, and the one to reach for first.
/// </summary>
/// <remarks>
/// <see cref="BindingMode"/> is the whole component: it decides what the offset is measured against,
/// and so whether the camera swings around behind the target when it turns, stays put, or simply
/// keeps whatever bearing it already had.
/// </remarks>
[AddComponentMenu("Kino/Body/Kino Transposer")]
[ComponentIcon("\uf0b2")] // ArrowsUpDownLeftRight
public class KinoTransposer : KinoComponent
{
    /// <summary>What the offset is measured against. See <see cref="KinoBindingMode"/>.</summary>
    public KinoBindingMode BindingMode = KinoBindingMode.LockToTargetWithWorldUp;

    /// <summary>Where to sit relative to the target. Negative Z is behind it.</summary>
    public Float3 FollowOffset = new(0f, 2f, -6f);

    /// <summary>
    /// Seconds to catch up on each axis of the binding frame - right, up, forward. Larger is looser;
    /// zero is a rigid mount.
    /// </summary>
    public Float3 Damping = new(0.5f, 0.5f, 0.5f);

    /// <summary>
    /// Seconds for the camera to come around when the target turns. Only used by the modes that
    /// follow the target's orientation; raising it lets a target spin without the camera whipping.
    /// </summary>
    public float RotationDamping = 0.5f;

    private Quaternion _frame = Quaternion.Identity;
    private bool _hasFrame;

    public override KinoStage Stage => KinoStage.Body;

    public override bool IsUsable => HasFollow;

    public override void ResetDamping() => _hasFrame = false;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (!HasFollow)
            return;

        Float3 targetPosition = FollowPosition;
        Quaternion frame = ResolveFrame(state.Position, targetPosition, ctx);
        Float3 desired = targetPosition + frame * FollowOffset;

        // Damping is applied along the binding frame's own axes, not the world's, so "loose on the
        // forward axis, tight sideways" keeps meaning that however the target is facing.
        Float3 gap = Quaternion.Inverse(frame) * (desired - state.Position);
        state.Position += frame * KinoMath.Damp(gap, Damping, ctx.DeltaTime);
    }

    private Quaternion ResolveFrame(Float3 cameraPosition, Float3 targetPosition, in KinoContext ctx)
    {
        switch (BindingMode)
        {
            case KinoBindingMode.WorldSpace:
                return Quaternion.Identity;

            case KinoBindingMode.SimpleFollowWithWorldUp:
            {
                // Derived from where the camera already is, so it must not be damped as well - the
                // frame would be chasing a position that is itself chasing the frame.
                Float3 back = KinoMath.ProjectOnPlane(cameraPosition - targetPosition, ctx.WorldUp);
                if (Float3.LengthSquared(back) < KinoMath.Epsilon)
                    return _hasFrame ? _frame : Quaternion.Identity;

                _frame = KinoMath.SafeLookRotation(-back, ctx.WorldUp, _frame);
                _hasFrame = true;
                return _frame;
            }

            default:
            {
                Quaternion wanted = BindingMode == KinoBindingMode.LockToTarget
                    ? FollowRotation
                    : KinoMath.FlattenToUp(FollowRotation, ctx.WorldUp);

                if (!_hasFrame || ctx.IsSnap)
                {
                    _frame = wanted;
                    _hasFrame = true;
                }
                else
                {
                    _frame = KinoMath.DampTowards(_frame, wanted, RotationDamping, ctx.DeltaTime);
                }

                return _frame;
            }
        }
    }

    public override void DrawComponentGizmos()
    {
        if (!HasFollow)
            return;

        Debug.DrawLine(Transform.Position, FollowPosition, Color.Green);
    }
}
