// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// A complete shot: where the camera is, where it points, and what it is looking through. Every
/// <see cref="KinoCamera"/> produces one each frame and the <see cref="KinoBrain"/> blends them.
/// </summary>
/// <remarks>
/// Shake is kept apart from the shot itself rather than folded into it. That way a blend interpolates
/// the framing a camera worked out and the shake it added independently, so shake fades with its own
/// camera instead of dragging the framing around with it.
/// </remarks>
public struct KinoState
{
    /// <summary>Where the camera sits, before shake.</summary>
    public Float3 Position;

    /// <summary>Where the camera points, before shake and before <see cref="KinoLens.Dutch"/>.</summary>
    public Quaternion Rotation;

    /// <summary>The lens the shot is taken through.</summary>
    public KinoLens Lens;

    /// <summary>World-space shake offset, added to <see cref="Position"/> at the very end.</summary>
    public Float3 PositionShake;

    /// <summary>Camera-local shake rotation, applied on top of <see cref="Rotation"/> at the very end.</summary>
    public Quaternion RotationShake;

    /// <summary>Which way is up for this shot. Aim components level the horizon against it.</summary>
    public Float3 ReferenceUp;

    /// <summary>The point the camera is composing around, when there is one. Blending uses it to keep the subject framed.</summary>
    public Float3 LookAtPoint;

    /// <summary>Whether <see cref="LookAtPoint"/> holds anything.</summary>
    public bool HasLookAt;

    /// <summary>An identity shot at the origin with the default lens.</summary>
    public static KinoState Default => new()
    {
        Position = Float3.Zero,
        Rotation = Quaternion.Identity,
        Lens = KinoLens.Default,
        PositionShake = Float3.Zero,
        RotationShake = Quaternion.Identity,
        ReferenceUp = Float3.UnitY,
        LookAtPoint = Float3.Zero,
        HasLookAt = false
    };

    /// <summary>The position to actually put the camera at, shake included.</summary>
    public readonly Float3 FinalPosition => Position + PositionShake;

    /// <summary>The rotation to actually put the camera at, shake and dutch included.</summary>
    public readonly Quaternion FinalRotation
    {
        get
        {
            Quaternion rotation = Rotation * RotationShake;
            if (Maths.Abs(Lens.Dutch) > KinoMath.Epsilon)
                rotation *= Quaternion.RotateZ(Lens.Dutch * Maths.Deg2Rad); // positive rolls the image clockwise
            return rotation;
        }
    }

    /// <summary>Points the shot at <paramref name="worldPoint"/> and records it for blending.</summary>
    public void LookAt(Float3 worldPoint)
    {
        Rotation = KinoMath.SafeLookRotation(worldPoint - Position, ReferenceUp, Rotation);
        LookAtPoint = worldPoint;
        HasLookAt = true;
    }

    /// <summary>
    /// Interpolates between two shots. <paramref name="t"/> runs 0 (all <paramref name="a"/>) to
    /// 1 (all <paramref name="b"/>) and is expected to have been through a blend curve already.
    /// </summary>
    /// <remarks>
    /// When both shots are composing around a subject, the subject is what gets interpolated and the
    /// rotation is rebuilt from it. Slerping the two rotations directly would swing the camera off the
    /// subject and back on again mid-blend, which reads as the subject sliding across the screen; this
    /// way it stays put and only the angle it is seen from travels.
    /// </remarks>
    public static KinoState Lerp(in KinoState a, in KinoState b, float t)
    {
        KinoState result;
        result.Position = KinoMath.LerpUnclamped(a.Position, b.Position, t);
        result.Lens = KinoLens.Lerp(a.Lens, b.Lens, t);
        result.PositionShake = KinoMath.LerpUnclamped(a.PositionShake, b.PositionShake, t);
        result.RotationShake = Quaternion.Slerp(a.RotationShake, b.RotationShake, t);
        result.ReferenceUp = KinoMath.SafeNormalize(KinoMath.LerpUnclamped(a.ReferenceUp, b.ReferenceUp, t), Float3.UnitY);
        result.LookAtPoint = KinoMath.LerpUnclamped(a.LookAtPoint, b.LookAtPoint, t);
        result.HasLookAt = a.HasLookAt && b.HasLookAt;

        if (result.HasLookAt)
        {
            // Each shot's rotation, minus the rotation that would look straight down the barrel at its
            // own subject. What is left is the composition - the subject sitting off-centre, say - and
            // that is what blends, laid back over a rotation aimed at the interpolated subject.
            Quaternion aimA = KinoMath.SafeLookRotation(a.LookAtPoint - a.Position, a.ReferenceUp, a.Rotation);
            Quaternion aimB = KinoMath.SafeLookRotation(b.LookAtPoint - b.Position, b.ReferenceUp, b.Rotation);
            Quaternion offset = Quaternion.Slerp(
                Quaternion.Inverse(aimA) * a.Rotation,
                Quaternion.Inverse(aimB) * b.Rotation, t);

            Quaternion aim = KinoMath.SafeLookRotation(result.LookAtPoint - result.Position, result.ReferenceUp, aimA);
            result.Rotation = aim * offset;
        }
        else
        {
            result.Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
        }

        return result;
    }
}

/// <summary>
/// What the pipeline knows about the frame it is being run for. Passed to every
/// <see cref="KinoComponent"/> by reference, so adding to it later costs nothing.
/// </summary>
public readonly struct KinoContext
{
    /// <summary>
    /// Seconds since the last update. <b>Zero or less means snap</b> - the camera is being placed
    /// rather than moved, so every damper must reach its target this call. Components get that for
    /// free by routing all smoothing through <see cref="KinoMath"/>.
    /// </summary>
    public readonly float DeltaTime;

    /// <summary>Width over height of the viewport the shot will be rendered into.</summary>
    public readonly float Aspect;

    /// <summary>The world's up direction, for components that need to level themselves against it.</summary>
    public readonly Float3 WorldUp;

    public KinoContext(float deltaTime, float aspect, Float3 worldUp)
    {
        DeltaTime = deltaTime;
        Aspect = aspect;
        WorldUp = worldUp;
    }

    /// <summary>True when dampers must go straight to their target instead of easing towards it.</summary>
    public bool IsSnap => DeltaTime <= 0f;

    /// <summary>A copy of this context that forces every damper to snap.</summary>
    public KinoContext AsSnap() => new(0f, Aspect, WorldUp);
}
