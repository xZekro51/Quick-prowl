// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;

using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// The small pile of math every camera component ends up needing: framerate-independent damping,
/// safe direction handling, and angle helpers that work in degrees.
/// </summary>
public static class KinoMath
{
    /// <summary>Below this, a value is treated as zero.</summary>
    public const float Epsilon = 0.0001f;

    /// <summary>
    /// Decay constant behind every damper. A damping time is the time to close 99% of the gap, so
    /// "0.5" reads as "half a second to get there" rather than as an opaque coefficient.
    /// </summary>
    private const float Decay = -4.6051702f; // ln(0.01)

    #region Damping

    /// <summary>
    /// The portion of <paramref name="gap"/> to consume this frame. Add the result to the current
    /// value; what remains carries into the next frame.
    /// </summary>
    /// <param name="gap">Distance still to travel (target - current).</param>
    /// <param name="dampTime">Seconds to close 99% of the gap. Zero means no damping.</param>
    /// <param name="deltaTime">Frame delta. Zero or less consumes the whole gap, which is how a snap is asked for.</param>
    /// <remarks>The curve is exponential, so the result is identical at any framerate.</remarks>
    public static float Damp(float gap, float dampTime, float deltaTime)
    {
        if (deltaTime <= 0f || dampTime < Epsilon || Maths.Abs(gap) < Epsilon)
            return gap;

        return gap * (1f - Maths.Exp(Decay * deltaTime / dampTime));
    }

    /// <inheritdoc cref="Damp(float, float, float)"/>
    public static Float3 Damp(Float3 gap, float dampTime, float deltaTime)
    {
        if (deltaTime <= 0f || dampTime < Epsilon)
            return gap;

        float t = 1f - Maths.Exp(Decay * deltaTime / dampTime);
        return gap * t;
    }

    /// <summary>Damps each axis of <paramref name="gap"/> with its own damping time.</summary>
    /// <inheritdoc cref="Damp(float, float, float)"/>
    public static Float3 Damp(Float3 gap, Float3 dampTime, float deltaTime) => new(
        Damp(gap.X, dampTime.X, deltaTime),
        Damp(gap.Y, dampTime.Y, deltaTime),
        Damp(gap.Z, dampTime.Z, deltaTime));

    /// <summary>
    /// Rotates <paramref name="from"/> part of the way towards <paramref name="to"/> and returns the
    /// result - the rotational counterpart of <see cref="Damp(float, float, float)"/>.
    /// </summary>
    public static Quaternion DampTowards(Quaternion from, Quaternion to, float dampTime, float deltaTime)
    {
        if (deltaTime <= 0f || dampTime < Epsilon)
            return to;

        return Quaternion.Slerp(from, to, 1f - Maths.Exp(Decay * deltaTime / dampTime));
    }

    /// <summary>Damped move of an angle in degrees, taking the shortest way round the circle.</summary>
    public static float DampAngle(float current, float target, float dampTime, float deltaTime)
        => current + Damp(DeltaAngle(current, target), dampTime, deltaTime);

    #endregion

    #region Angles

    /// <summary>Shortest signed distance from <paramref name="current"/> to <paramref name="target"/>, in degrees.</summary>
    public static float DeltaAngle(float current, float target)
    {
        float delta = (target - current) % 360f;
        if (delta > 180f) delta -= 360f;
        else if (delta < -180f) delta += 360f;
        return delta;
    }

    /// <summary>Wraps an angle into (-180, 180].</summary>
    public static float WrapAngle(float degrees) => DeltaAngle(0f, degrees);

    /// <summary>Signed angle in degrees from <paramref name="from"/> to <paramref name="to"/> about <paramref name="axis"/>.</summary>
    public static float SignedAngle(Float3 from, Float3 to, Float3 axis)
    {
        Float3 a = ProjectOnPlane(from, axis);
        Float3 b = ProjectOnPlane(to, axis);
        if (Float3.LengthSquared(a) < Epsilon || Float3.LengthSquared(b) < Epsilon)
            return 0f;

        a = Float3.Normalize(a);
        b = Float3.Normalize(b);
        float angle = Maths.Acos(Maths.Clamp(Float3.Dot(a, b), -1f, 1f)) * Maths.Rad2Deg;
        return Float3.Dot(Float3.Cross(a, b), axis) < 0f ? -angle : angle;
    }

    #endregion

    #region Vectors and rotations

    /// <summary>Drops the component of <paramref name="v"/> that runs along <paramref name="planeNormal"/>.</summary>
    public static Float3 ProjectOnPlane(Float3 v, Float3 planeNormal)
    {
        float lenSq = Float3.LengthSquared(planeNormal);
        if (lenSq < Epsilon)
            return v;

        return v - planeNormal * (Float3.Dot(v, planeNormal) / lenSq);
    }

    /// <summary>Normalizes <paramref name="v"/>, or returns <paramref name="fallback"/> if it is too short to have a direction.</summary>
    public static Float3 SafeNormalize(Float3 v, Float3 fallback)
        => Float3.LengthSquared(v) < Epsilon * Epsilon ? fallback : Float3.Normalize(v);

    /// <summary>
    /// Looks along <paramref name="direction"/>, falling back to <paramref name="fallback"/> when the
    /// direction is degenerate - which happens the instant a camera lands exactly on its target.
    /// </summary>
    public static Quaternion SafeLookRotation(Float3 direction, Float3 up, Quaternion fallback)
    {
        if (Float3.LengthSquared(direction) < Epsilon * Epsilon)
            return fallback;

        direction = Float3.Normalize(direction);

        // Cross(up, forward) collapses when the two are parallel - looking straight down at a target,
        // say - and the roll of the result would be arbitrary. Tilting up slightly off the forward
        // axis keeps it stable and continuous instead of snapping.
        if (Maths.Abs(Float3.Dot(direction, up)) > 0.9999f)
            up = ProjectOnPlane(Float3.UnitZ, direction);
        if (Float3.LengthSquared(up) < Epsilon)
            up = ProjectOnPlane(Float3.UnitX, direction);
        if (Float3.LengthSquared(up) < Epsilon)
            return fallback;

        return Quaternion.LookRotation(direction, Float3.Normalize(up));
    }

    /// <summary>The heading of <paramref name="rotation"/> about <paramref name="up"/>, with pitch and roll thrown away.</summary>
    public static Quaternion FlattenToUp(Quaternion rotation, Float3 up)
    {
        Float3 forward = ProjectOnPlane(rotation * Float3.UnitZ, up);
        if (Float3.LengthSquared(forward) < Epsilon)
            forward = ProjectOnPlane(rotation * Float3.UnitY, up); // looking straight up or down

        return SafeLookRotation(forward, up, rotation);
    }

    /// <summary>
    /// The orientation that heading and pitch angles are measured from. Built out of the up direction
    /// alone, so input-driven cameras keep working when up is not +Y.
    /// </summary>
    public static Quaternion HeadingFrame(Float3 up)
    {
        Float3 forward = ProjectOnPlane(Float3.UnitZ, up);
        if (Float3.LengthSquared(forward) < Epsilon)
            forward = ProjectOnPlane(Float3.UnitX, up);

        return SafeLookRotation(forward, up, Quaternion.Identity);
    }

    /// <summary>Unclamped linear interpolation. <see cref="Maths.Lerp(float, float, float)"/> saturates, which blending cannot use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;

    /// <inheritdoc cref="LerpUnclamped(float, float, float)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Float3 LerpUnclamped(Float3 a, Float3 b, float t) => a + (b - a) * t;

    #endregion
}
