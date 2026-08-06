// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>Which way the camera faces while it frames - and so which side of the subject it sits on.</summary>
public enum KinoFacing : byte
{
    /// <summary>
    /// Whatever the camera is already pointing. The angle comes from rotating the camera's own
    /// GameObject, or from an aim component when there is one. Right for 2D and top-down, where the
    /// angle is fixed and the camera only ever slides.
    /// </summary>
    CameraRotation,

    /// <summary>A direction stated outright, so the camera always sits on the same side of the subject.</summary>
    FixedDirection,

    /// <summary>The follow target's own heading, so the camera stays behind it as it turns.</summary>
    BehindTarget
}

/// <summary>
/// Keeps the subject at a chosen spot on the screen by moving the camera, not by turning it. The
/// component for side-on and top-down games, and for any shot where the framing matters more than
/// where the camera happens to be.
/// </summary>
/// <remarks>
/// <para>
/// Two things decide where the camera ends up: <see cref="Facing"/> - which way it looks, and so which
/// side of the subject it sits on - and <see cref="CameraDistance"/>, how far back along that. Screen
/// position and zones then slide it within the plane facing the subject.
/// </para>
/// <para>
/// The dead zone is the part of the screen where the subject can wander without the camera reacting
/// at all; the soft zone is the part it is allowed to reach while the camera catches up. Widen the
/// dead zone for a camera that ignores small movements, narrow it for one that is always centred.
/// </para>
/// <para>
/// Pointed at a <see cref="KinoTargetGroup"/> it can also pull back far enough to keep the whole
/// group in shot - see <see cref="GroupFraming"/>.
/// </para>
/// </remarks>
[AddComponentMenu("Kino/Body/Kino Framing Transposer")]
[ComponentIcon("\uf125")] // Crop
public class KinoFramingTransposer : KinoComponent
{
    /// <summary>
    /// Which way the camera faces while framing, and so which side of the subject it sits on. See
    /// <see cref="KinoFacing"/>.
    /// </summary>
    public KinoFacing Facing = KinoFacing.CameraRotation;

    /// <summary>
    /// The direction the camera looks along, in degrees: pitch, yaw, roll. Positive pitch looks down at
    /// the subject. Used by <see cref="KinoFacing.FixedDirection"/>, and added on top of the target's
    /// heading by <see cref="KinoFacing.BehindTarget"/>.
    /// </summary>
    public Float3 FacingAngles = new(20f, 0f, 0f);

    /// <summary>
    /// Seconds for the camera to come around when the direction it faces changes. Only used by
    /// <see cref="KinoFacing.BehindTarget"/>, where it stops a turning target whipping the camera.
    /// </summary>
    public float FacingDamping = 0.5f;

    /// <summary>
    /// World-space offset from the follow target to the point actually being framed. The camera frames
    /// that point, so moving this moves the camera with it - to aim off-centre without moving the
    /// camera, use the aim component's own offset instead.
    /// </summary>
    public Float3 TrackedObjectOffset = Float3.Zero;

    /// <summary>How far back from the subject the camera sits, in metres.</summary>
    public float CameraDistance = 10f;

    /// <summary>Horizontal screen position for the subject. 0 is the left edge, 1 the right.</summary>
    public float ScreenX = 0.5f;

    /// <summary>Vertical screen position for the subject. 0 is the bottom edge, 1 the top.</summary>
    public float ScreenY = 0.5f;

    /// <summary>Half-width of the region the subject may move in before the camera reacts, as a fraction of the screen.</summary>
    public float DeadZoneWidth = 0f;

    /// <summary>Half-height of the region the subject may move in before the camera reacts, as a fraction of the screen.</summary>
    public float DeadZoneHeight = 0f;

    /// <summary>Half-width of the region the subject may never leave, as a fraction of the screen.</summary>
    public float SoftZoneWidth = 0.8f;

    /// <summary>Half-height of the region the subject may never leave, as a fraction of the screen.</summary>
    public float SoftZoneHeight = 0.8f;

    /// <summary>Metres of slack in the distance before the camera bothers correcting it.</summary>
    public float DistanceDeadZone = 0f;

    /// <summary>Seconds to catch up sideways.</summary>
    public float HorizontalDamping = 1f;

    /// <summary>Seconds to catch up vertically.</summary>
    public float VerticalDamping = 1f;

    /// <summary>Seconds to correct the distance.</summary>
    public float DistanceDamping = 1f;

    /// <summary>
    /// Seconds of movement to lead the subject by, so the camera looks where it is going. Zero is off.
    /// Small values (0.1 - 0.4) read as anticipation; large ones as the camera running ahead.
    /// </summary>
    public float LookaheadTime = 0f;

    /// <summary>Seconds of smoothing on the lead, so a subject changing direction does not snap the camera about.</summary>
    public float LookaheadSmoothing = 0.3f;

    /// <summary>Pull back far enough to keep a whole <see cref="KinoTargetGroup"/> in shot.</summary>
    public bool GroupFraming = true;

    /// <summary>How much of the screen the group should fill, as a fraction of the half-height.</summary>
    public float GroupFramingSize = 0.8f;

    /// <summary>Closest the group framing may push the camera.</summary>
    public float MinimumDistance = 2f;

    /// <summary>Furthest the group framing may pull the camera.</summary>
    public float MaximumDistance = 100f;

    private Float3 _lastSubject;
    private Float3 _lookaheadVelocity;
    private float _distance;
    private bool _initialised;
    private Quaternion _facing = Quaternion.Identity;
    private bool _hasFacing;

    public override KinoStage Stage => KinoStage.Body;

    public override bool IsUsable => HasFollow;

    public override void ResetDamping()
    {
        _initialised = false;
        _hasFacing = false;
        _lookaheadVelocity = Float3.Zero;
    }

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (!HasFollow)
            return;

        Float3 subject = FollowPosition;

        if (!_initialised)
        {
            _initialised = true;
            _lastSubject = subject;
            _distance = CameraDistance;
            _lookaheadVelocity = Float3.Zero;
        }

        subject += Lookahead(subject, ctx.DeltaTime) + TrackedObjectOffset;

        float distance = ResolveDistance(state.Lens, ctx);
        Quaternion facing = ResolveFacing(state, ctx);

        // The position that frames the subject exactly where it was asked for. Everything after this is
        // about how quickly the camera is allowed to get there.
        Float2 want = KinoScreen.FromScreenPoint(ScreenX, ScreenY);
        Float2 halfExtents = KinoScreen.HalfExtentsAt(distance, state.Lens, ctx.Aspect);
        Float3 framed = new(want.X * halfExtents.X, want.Y * halfExtents.Y, distance);
        Float3 desired = subject - facing * framed;

        // Worked in the facing frame's axes, where sideways, vertical and distance are separate ideas
        // with separate zones and separate damping.
        Float3 gap = Quaternion.Inverse(facing) * (desired - state.Position);

        // A soft zone inside the dead zone would have the camera correcting what it just decided to
        // ignore, so it is never allowed to be the smaller of the two.
        float moveX = Correct(gap.X, DeadZoneWidth * halfExtents.X, Maths.Max(SoftZoneWidth, DeadZoneWidth) * halfExtents.X, HorizontalDamping, ctx.DeltaTime);
        float moveY = Correct(gap.Y, DeadZoneHeight * halfExtents.Y, Maths.Max(SoftZoneHeight, DeadZoneHeight) * halfExtents.Y, VerticalDamping, ctx.DeltaTime);
        float moveZ = Correct(gap.Z, DistanceDeadZone, 0f, DistanceDamping, ctx.DeltaTime);

        state.Position += facing * new Float3(moveX, moveY, moveZ);

        // Only when nothing else will. An aim component damps from the rotation it is handed, so
        // overwriting that every frame would restart its smoothing every frame and leave the shot
        // shivering in place instead of settling.
        if (Facing != KinoFacing.CameraRotation && !AimOwnsRotation)
            state.Rotation = facing;
    }

    /// <summary>
    /// True when an aim component is going to decide the rotation after this runs. The rotation belongs
    /// to exactly one stage, and when that stage is filled this component keeps its hands off it - both
    /// writing to it and reading from it.
    /// </summary>
    private bool AimOwnsRotation
    {
        get
        {
            KinoCamera? vcam = VirtualCamera;
            return vcam.IsValid() && vcam.ActiveAim.IsValid();
        }
    }

    /// <summary>
    /// The frame the camera slides in. This is the direction the whole component is built around: with
    /// no answer to "which way is it facing" a distance alone cannot say where a camera goes.
    /// </summary>
    private Quaternion ResolveFacing(in KinoState state, in KinoContext ctx)
    {
        if (Facing == KinoFacing.CameraRotation)
        {
            // With nothing aiming the camera its rotation is authored and cannot change underneath us,
            // so it is read live and rotating the GameObject in the scene view simply works.
            if (!AimOwnsRotation)
                return state.Rotation;

            // With an aim component that rotation is the aim's own output from last frame. Placing the
            // camera from it would feed this component's result back through the aim and into itself -
            // two controllers on one shot, which rings rather than settles. The direction is taken once
            // and held instead; Fixed Direction and Behind Target are the ways to state it outright.
            if (!_hasFacing)
            {
                _facing = state.Rotation;
                _hasFacing = true;
            }

            return _facing;
        }

        Quaternion offset = Quaternion.FromEuler(FacingAngles);
        Quaternion wanted = Facing == KinoFacing.BehindTarget
            ? KinoMath.FlattenToUp(FollowRotation, ctx.WorldUp) * offset
            : KinoMath.HeadingFrame(ctx.WorldUp) * offset;

        if (!_hasFacing || ctx.IsSnap)
        {
            _facing = wanted;
            _hasFacing = true;
        }
        else
        {
            _facing = KinoMath.DampTowards(_facing, wanted, FacingDamping, ctx.DeltaTime);
        }

        return _facing;
    }

    private static float Correct(float gap, float deadZone, float softZone, float damping, float deltaTime)
    {
        float wanted = KinoScreen.ApplyZones(gap, deadZone, softZone, out float hardLimit);
        return KinoScreen.AtLeast(KinoMath.Damp(wanted, damping, deltaTime), hardLimit);
    }

    private Float3 Lookahead(Float3 subject, float deltaTime)
    {
        if (LookaheadTime <= 0f || deltaTime <= 0f)
        {
            _lastSubject = subject;
            return Float3.Zero;
        }

        Float3 velocity = (subject - _lastSubject) / deltaTime;
        _lastSubject = subject;

        // Smoothing the velocity rather than the resulting offset keeps the lead pointing where the
        // subject is actually going, instead of trailing a frame or two behind every turn.
        _lookaheadVelocity += KinoMath.Damp(velocity - _lookaheadVelocity, LookaheadSmoothing, deltaTime);
        return _lookaheadVelocity * LookaheadTime;
    }

    private float ResolveDistance(in KinoLens lens, in KinoContext ctx)
    {
        float wanted = CameraDistance;

        KinoTargetGroup? group = VirtualCamera.IsValid() ? VirtualCamera.FollowGroup : null;
        if (GroupFraming && group.IsValid() && !group.IsEmpty && group.Radius > KinoMath.Epsilon && !lens.Orthographic)
        {
            // Distance at which a sphere of the group's radius fills the requested slice of the view.
            float halfFov = Maths.Tan(lens.FieldOfView * 0.5f * Maths.Deg2Rad);
            float framing = Maths.Max(GroupFramingSize, 0.01f);
            wanted = group.Radius / (framing * Maths.Max(halfFov, KinoMath.Epsilon));
            wanted = Maths.Clamp(wanted, MinimumDistance, Maths.Max(MinimumDistance, MaximumDistance));
        }

        _distance += KinoMath.Damp(wanted - _distance, DistanceDamping, ctx.DeltaTime);
        return _distance;
    }

    public override void DrawComponentGizmos()
    {
        if (!HasFollow)
            return;

        Debug.DrawLine(Transform.Position, FollowPosition + TrackedObjectOffset, Color.Green);
    }
}
