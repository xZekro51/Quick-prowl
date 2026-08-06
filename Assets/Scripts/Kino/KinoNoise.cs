// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// The smooth random signal behind camera shake. Deterministic, allocation-free, and sampled by time
/// rather than accumulated per frame, so the same moment always looks the same however the framerate
/// wandered getting there.
/// </summary>
public static class KinoNoise
{
    /// <summary>
    /// A smooth random value in -1 to 1 that varies about once per unit of <paramref name="time"/>.
    /// Different <paramref name="channel"/> numbers give unrelated signals.
    /// </summary>
    public static float Sample(float time, int channel)
    {
        // Channels are separated by walking far along the same curve rather than by seeding a second
        // one, which keeps this to a couple of integer ops and no state.
        time += channel * 137.31f;

        float floor = Maths.Floor(time);
        int cell = (int)floor;
        float f = time - floor;
        float smooth = f * f * (3f - 2f * f);

        return KinoMath.LerpUnclamped(Hash(cell), Hash(cell + 1), smooth);
    }

    /// <summary>
    /// Three unrelated signals at once, each with its own frequency - the usual shape of a shake.
    /// </summary>
    /// <param name="time">Seconds. Multiply by frequency inside, so changing frequency does not jump the signal.</param>
    /// <param name="frequency">Cycles per second per axis.</param>
    /// <param name="channel">Base channel; the three axes use this and the two after it.</param>
    public static Float3 Sample3(float time, Float3 frequency, int channel) => new(
        Sample(time * frequency.X, channel),
        Sample(time * frequency.Y, channel + 1),
        Sample(time * frequency.Z, channel + 2));

    /// <summary>
    /// Two octaves of <see cref="Sample(float, int)"/>. The second one is faster and quieter, which is
    /// what stops a shake sounding like a sine wave.
    /// </summary>
    public static float SampleRough(float time, int channel)
        => Sample(time, channel) + Sample(time * 2.37f, channel + 64) * 0.4f;

    private static float Hash(int value)
    {
        // Integer bit-mixer: cheap, no lookup table, and stable across platforms and sessions.
        unchecked
        {
            value = (value << 13) ^ value;
            int mixed = value * (value * value * 15731 + 789221) + 1376312589;
            return 1f - ((mixed & 0x7fffffff) / 1073741824f);
        }
    }
}
