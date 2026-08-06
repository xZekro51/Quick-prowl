// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>The shape a <see cref="KinoConfiner"/> keeps the camera inside.</summary>
public enum KinoConfineShape : byte
{
    /// <summary>A box, turned with this GameObject.</summary>
    Box,

    /// <summary>A sphere.</summary>
    Sphere
}

/// <summary>
/// Keeps the camera inside a volume. Stops a follow camera from wandering out of a room, through a
/// backdrop, or past the edge of a level that was never built to be seen from there.
/// </summary>
/// <remarks>
/// The volume is placed relative to this GameObject, so moving it moves the boundary. Runs after the
/// shot is composed, so it moves the camera and lets the framing shift rather than fighting whatever
/// the body component wanted.
/// </remarks>
[AddComponentMenu("Kino/Extensions/Kino Confiner")]
[ComponentIcon("\uf247")] // ObjectGroup
public class KinoConfiner : KinoComponent
{
    /// <summary>Which shape to confine to.</summary>
    public KinoConfineShape Shape = KinoConfineShape.Box;

    /// <summary>Centre of the volume, relative to this GameObject.</summary>
    public Float3 Center = Float3.Zero;

    /// <summary>Full size of the box, in the volume's own axes.</summary>
    public Float3 Size = new(20f, 10f, 20f);

    /// <summary>Radius of the sphere.</summary>
    public float Radius = 10f;

    /// <summary>Seconds to ease back inside. Zero clamps hard, which never lets the camera out but can jolt.</summary>
    public float Damping = 0f;

    public override KinoStage Stage => KinoStage.Finalize;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        Float3 center = Transform.Position + Transform.Rotation * Center;
        Float3 confined;

        if (Shape == KinoConfineShape.Sphere)
        {
            Float3 offset = state.Position - center;
            float distance = Float3.Length(offset);
            float radius = Maths.Max(Radius, 0f);
            confined = distance <= radius || distance < KinoMath.Epsilon
                ? state.Position
                : center + offset * (radius / distance);
        }
        else
        {
            // Clamped in the box's own axes so a rotated volume confines to its own sides rather than
            // to a world-aligned box around it.
            Quaternion rotation = Transform.Rotation;
            Float3 local = Quaternion.Inverse(rotation) * (state.Position - center);
            Float3 half = Maths.Abs(Size) * 0.5f;

            local = new Float3(
                Maths.Clamp(local.X, -half.X, half.X),
                Maths.Clamp(local.Y, -half.Y, half.Y),
                Maths.Clamp(local.Z, -half.Z, half.Z));

            confined = center + rotation * local;
        }

        state.Position += Damping <= 0f
            ? confined - state.Position
            : KinoMath.Damp(confined - state.Position, Damping, ctx.DeltaTime);
    }

    public override void DrawComponentGizmos()
    {
        Float3 center = Transform.Position + Transform.Rotation * Center;

        if (Shape == KinoConfineShape.Sphere)
            Debug.DrawWireSphere(center, Maths.Max(Radius, 0f), Color.Magenta);
        else
            Debug.DrawWireCube(center, Maths.Abs(Size) * 0.5f, Color.Magenta);
    }
}
