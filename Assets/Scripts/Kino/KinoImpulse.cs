// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// One-off shakes with a position in the world: an explosion, a landing, a hit. Fire one from
/// anywhere and every camera carrying a <see cref="KinoImpulseListener"/> feels it, harder if it is
/// close and not at all if it is far away.
/// </summary>
/// <remarks>
/// <code>
/// KinoImpulse.Shake(explosion.Transform.Position, force: 1.5f, radius: 30f);
/// KinoImpulse.Generate(hit.Point, hit.Normal * 0.4f, duration: 0.25f);
/// </code>
/// Impulses live in a fixed-size ring, so firing them costs nothing and never allocates. When more
/// than <see cref="Capacity"/> overlap the oldest is dropped, which nobody has ever noticed.
/// </remarks>
public static class KinoImpulse
{
    /// <summary>How many impulses can be running at once before the oldest is recycled.</summary>
    public const int Capacity = 16;

    private struct Impulse
    {
        public Float3 Position;
        public Float3 Direction;
        public float Strength;
        public float Radius;
        public float Frequency;
        public float Duration;
        public float Age;
        public bool Active;
    }

    private static readonly Impulse[] s_impulses = new Impulse[Capacity];
    private static int s_next;

    /// <summary>Scales every impulse fired from here on. Handy as a "screen shake" accessibility setting.</summary>
    public static float GlobalGain = 1f;

    /// <summary>How many impulses are currently running.</summary>
    public static int ActiveCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < Capacity; i++)
                if (s_impulses[i].Active) count++;
            return count;
        }
    }

    /// <summary>
    /// Fires a directional impulse - a kick along <paramref name="velocity"/> that rings down to
    /// nothing.
    /// </summary>
    /// <param name="position">Where it happened, in world space.</param>
    /// <param name="velocity">Direction of the kick; its length is how hard, in metres.</param>
    /// <param name="duration">Seconds until it has died away.</param>
    /// <param name="radius">Distance at which it can no longer be felt at all.</param>
    /// <param name="frequency">Ringing speed in cycles per second. Higher reads as sharper, lower as heavier.</param>
    public static void Generate(Float3 position, Float3 velocity, float duration = 0.4f, float radius = 25f, float frequency = 18f)
    {
        float strength = Float3.Length(velocity) * GlobalGain;
        if (strength < KinoMath.Epsilon || duration <= 0f)
            return;

        s_impulses[s_next] = new Impulse
        {
            Position = position,
            Direction = Float3.Normalize(velocity),
            Strength = strength,
            Radius = Maths.Max(radius, KinoMath.Epsilon),
            Frequency = Maths.Max(frequency, 0.01f),
            Duration = duration,
            Age = 0f,
            Active = true
        };

        s_next = (s_next + 1) % Capacity;
    }

    /// <summary>
    /// Fires an impulse with no particular direction - the everyday explosion or heavy footstep.
    /// </summary>
    /// <param name="position">Where it happened, in world space.</param>
    /// <param name="force">How hard, in metres of camera movement at point-blank range.</param>
    /// <param name="duration">Seconds until it has died away.</param>
    /// <param name="radius">Distance at which it can no longer be felt at all.</param>
    public static void Shake(Float3 position, float force = 0.3f, float duration = 0.4f, float radius = 25f)
        => Generate(position, Float3.UnitY * force, duration, radius);

    /// <summary>Stops every running impulse dead.</summary>
    public static void Clear()
    {
        for (int i = 0; i < Capacity; i++)
            s_impulses[i].Active = false;
    }

    /// <summary>Ages every impulse. Called once a frame by <see cref="KinoCore"/>.</summary>
    internal static void Advance(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        for (int i = 0; i < Capacity; i++)
        {
            if (!s_impulses[i].Active)
                continue;

            s_impulses[i].Age += deltaTime;
            if (s_impulses[i].Age >= s_impulses[i].Duration)
                s_impulses[i].Active = false;
        }
    }

    /// <summary>
    /// Adds up everything a listener at <paramref name="listenerPosition"/> can feel right now.
    /// </summary>
    /// <param name="positionOffset">Where to move the camera, in world space.</param>
    /// <param name="rotationEuler">How to tip the camera, in degrees.</param>
    /// <returns>False when nothing is happening, so the caller can skip the rest of its work.</returns>
    public static bool Sample(Float3 listenerPosition, out Float3 positionOffset, out Float3 rotationEuler)
    {
        positionOffset = Float3.Zero;
        rotationEuler = Float3.Zero;
        bool any = false;

        for (int i = 0; i < Capacity; i++)
        {
            ref Impulse impulse = ref s_impulses[i];
            if (!impulse.Active)
                continue;

            float distance = Float3.Distance(listenerPosition, impulse.Position);
            float falloff = 1f - Maths.Saturate(distance / impulse.Radius);
            if (falloff <= 0f)
                continue;

            // Squared falloff so an impulse fades out of earshot rather than stopping at a hard edge.
            falloff *= falloff;

            float t = Maths.Saturate(impulse.Age / impulse.Duration);
            float amplitude = impulse.Strength * falloff * Envelope(t);
            if (amplitude < KinoMath.Epsilon)
                continue;

            // A decaying ring along the impulse direction, roughened up so repeated hits from the same
            // spot do not look like the same animation played twice.
            float phase = impulse.Age * impulse.Frequency;
            float ring = Maths.Sin(phase * Maths.PI * 2f);
            float rough = KinoNoise.SampleRough(phase, i * 3);

            positionOffset += impulse.Direction * (amplitude * (ring * 0.7f + rough * 0.3f));
            rotationEuler += KinoNoise.Sample3(phase, Float3.One, i * 3 + 128) * (amplitude * 12f);
            any = true;
        }

        return any;
    }

    /// <summary>Fast attack, long decay - the shape of something hitting something else.</summary>
    private static float Envelope(float t)
    {
        const float attack = 0.12f;
        float level = t < attack ? t / attack : 1f - (t - attack) / (1f - attack);
        return level * level;
    }
}
