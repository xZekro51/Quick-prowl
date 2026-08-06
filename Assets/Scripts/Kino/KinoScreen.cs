// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Turns world positions into screen positions and back. Everything that composes a shot - dead
/// zones, soft zones, group framing - is really working in these coordinates.
/// </summary>
/// <remarks>
/// Screen space here is normalized device coordinates: <c>(-1,-1)</c> bottom-left, <c>(0,0)</c> dead
/// centre, <c>(1,1)</c> top-right. The 0-to-1 screen positions on the components are converted with
/// <see cref="FromScreenPoint"/>, so a dead zone width of <c>0.2</c> (a fifth of the screen) is
/// <c>0.2</c> in NDC either side of centre - the factor of two cancels out.
/// </remarks>
public static class KinoScreen
{
    /// <summary>Converts a 0-to-1 screen point (origin bottom-left) to NDC.</summary>
    public static Float2 FromScreenPoint(float x, float y) => new(x * 2f - 1f, y * 2f - 1f);

    /// <summary>
    /// Where <paramref name="target"/> lands on screen. Returns false when it is behind the camera,
    /// in which case <paramref name="ndc"/> is meaningless and the caller should aim at it directly.
    /// </summary>
    public static bool TargetToNdc(Float3 camPos, Quaternion camRot, Float3 target,
        in KinoLens lens, float aspect, out Float2 ndc, out float depth)
    {
        Float3 local = Quaternion.Inverse(camRot) * (target - camPos);
        depth = local.Z;

        if (lens.Orthographic)
        {
            float half = Maths.Max(lens.OrthographicSize * 0.5f, KinoMath.Epsilon);
            ndc = new Float2(local.X / half, local.Y / half);
            return depth > 0f;
        }

        if (depth <= KinoMath.Epsilon)
        {
            ndc = Float2.Zero;
            return false;
        }

        float halfHeight = lens.HalfHeightAt(depth);
        float halfWidth = Maths.Max(halfHeight * aspect, KinoMath.Epsilon);
        ndc = new Float2(local.X / halfWidth, local.Y / Maths.Max(halfHeight, KinoMath.Epsilon));
        return true;
    }

    /// <summary>
    /// The world-space half extents of the view at <paramref name="depth"/> - what one unit of NDC is
    /// worth in metres, horizontally and vertically.
    /// </summary>
    public static Float2 HalfExtentsAt(float depth, in KinoLens lens, float aspect)
        => new(lens.HalfWidthAt(depth, aspect), lens.HalfHeightAt(depth));

    /// <summary>
    /// The rotation that puts <paramref name="target"/> at <paramref name="ndc"/> on screen, keeping
    /// the horizon level against <paramref name="up"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Solved outright rather than chased: a camera that is only allowed to turn and stay level has
    /// exactly two free angles - a heading about <paramref name="up"/> and a pitch about its own right
    /// axis - and asking for the subject at a given point on screen pins both of them down. Two
    /// candidate headings satisfy it; the one that keeps the camera nearest to where it was already
    /// pointing, and the right way up, is the one taken.
    /// </para>
    /// <para>
    /// Exactness matters because this is what places a camera the moment it goes live. An approximate
    /// answer would settle over the following frames, which is fine while a shot is running and very
    /// much not fine on the first frame of a cut.
    /// </para>
    /// </remarks>
    public static Quaternion RotateToPlace(Float3 camPos, Quaternion camRot, Float3 target, Float2 ndc,
        in KinoLens lens, float aspect, Float3 up)
    {
        Float3 toTarget = target - camPos;
        if (Float3.LengthSquared(toTarget) < KinoMath.Epsilon * KinoMath.Epsilon)
            return camRot;

        // Everything is solved in a frame where up is +Y, then handed back to world space, so an
        // unusual up direction costs one rotation rather than a special case.
        Quaternion frame = KinoMath.HeadingFrame(up);
        Float3 world = Quaternion.Inverse(frame) * Float3.Normalize(toTarget);

        // The direction the subject must lie along in the camera's own axes to land on that point.
        float halfHeight = Maths.Tan(lens.FieldOfView * 0.5f * Maths.Deg2Rad);
        Float3 wanted = Float3.Normalize(new Float3(ndc.X * halfHeight * aspect, ndc.Y * halfHeight, 1f));

        // Heading: pitching cannot change the subject's sideways position in the camera, so the
        // heading is fixed by that alone - a cosine equation with two roots.
        float a = world.X;
        float b = -world.Z;
        float magnitude = Maths.Sqrt(a * a + b * b);
        if (magnitude < KinoMath.Epsilon || Maths.Abs(wanted.X) > magnitude)
            return KinoMath.SafeLookRotation(toTarget, up, camRot); // straight up or down: nothing to compose

        float centre = Maths.Atan2(b, a);
        float spread = Maths.Acos(Maths.Clamp(wanted.X / magnitude, -1f, 1f));

        Float3 facing = camRot * Float3.UnitZ;
        Quaternion best = camRot;
        float bestScore = float.MinValue;

        for (int root = 0; root < 2; root++)
        {
            float heading = root == 0 ? centre + spread : centre - spread;
            float sin = Maths.Sin(heading);
            float cos = Maths.Cos(heading);

            Float3 turned = new(world.X * cos - world.Z * sin, world.Y, world.X * sin + world.Z * cos);
            float pitch = Maths.Atan2(wanted.Y, wanted.Z) - Maths.Atan2(turned.Y, turned.Z);

            Quaternion candidate = frame * Quaternion.RotateY(heading) * Quaternion.RotateX(pitch);
            float score = Float3.Dot(candidate * Float3.UnitZ, facing);

            // Both roots frame the subject, but one of them does it from upside down.
            if (Maths.Abs(KinoMath.WrapAngle(pitch * Maths.Rad2Deg)) > 90f)
                score -= 4f;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Shrinks <paramref name="error"/> by a dead zone and clamps it to a soft zone.
    /// </summary>
    /// <param name="error">How far the subject is from where it belongs, in NDC.</param>
    /// <param name="deadZone">Half-width of the region where the camera does not react at all.</param>
    /// <param name="softZone">Half-width of the region the subject may never leave.</param>
    /// <param name="hardLimit">
    /// How much of the error has to be corrected this frame no matter what damping says, because the
    /// subject is already outside the soft zone.
    /// </param>
    /// <returns>The part of the error the camera should be chasing.</returns>
    public static float ApplyZones(float error, float deadZone, float softZone, out float hardLimit)
    {
        float magnitude = Maths.Abs(error);
        float sign = error < 0f ? -1f : 1f;

        hardLimit = softZone > 0f && magnitude > softZone ? (magnitude - softZone) * sign : 0f;

        return magnitude <= deadZone ? 0f : (magnitude - deadZone) * sign;
    }

    /// <summary>
    /// Raises a damped correction to the hard limit <see cref="ApplyZones"/> asked for, so damping can
    /// soften a move but never let the subject slide out of the soft zone.
    /// </summary>
    public static float AtLeast(float applied, float hardLimit)
        => Maths.Abs(hardLimit) > Maths.Abs(applied) ? hardLimit : applied;
}
