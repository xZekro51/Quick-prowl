// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>The shape of a blend over its duration.</summary>
public enum KinoBlendStyle : byte
{
    /// <summary>Use the blend the <see cref="KinoBrain"/> is configured with. Only meaningful on a camera.</summary>
    Inherit,

    /// <summary>No blend at all - the new camera takes over on the next frame.</summary>
    Cut,

    /// <summary>Constant speed from start to finish.</summary>
    Linear,

    /// <summary>Eases out of the old shot and into the new one. The default, and what most cuts want.</summary>
    EaseInOut,

    /// <summary>Starts gently, arrives at full speed.</summary>
    EaseIn,

    /// <summary>Starts at full speed, settles gently.</summary>
    EaseOut,

    /// <summary>Like <see cref="EaseIn"/> but slower to leave.</summary>
    HardIn,

    /// <summary>Like <see cref="EaseOut"/> but slower to settle.</summary>
    HardOut
}

/// <summary>
/// How long a camera change takes and what curve it follows.
/// </summary>
public struct KinoBlend
{
    /// <summary>The curve the blend follows.</summary>
    public KinoBlendStyle Style;

    /// <summary>Seconds the blend lasts. Ignored by <see cref="KinoBlendStyle.Cut"/>.</summary>
    public float Duration;

    public KinoBlend(KinoBlendStyle style, float duration)
    {
        Style = style;
        Duration = duration;
    }

    /// <summary>An instant camera change.</summary>
    public static KinoBlend Cut => new(KinoBlendStyle.Cut, 0f);

    /// <summary>Defers to whatever the brain's default blend is.</summary>
    public static KinoBlend Inherit => new(KinoBlendStyle.Inherit, 0f);

    /// <summary>True when this blend takes no time, either by style or by duration.</summary>
    public readonly bool IsCut => Style == KinoBlendStyle.Cut || Duration <= 0f;

    /// <summary>
    /// Maps linear progress to blend weight. Both are 0-to-1 and both ends are pinned, so a blend
    /// always starts exactly on the old shot and ends exactly on the new one.
    /// </summary>
    public readonly float Evaluate(float progress)
    {
        float t = Maths.Saturate(progress);
        return Style switch
        {
            KinoBlendStyle.Cut => 1f,
            KinoBlendStyle.Linear => t,
            KinoBlendStyle.EaseIn => t * t,
            KinoBlendStyle.EaseOut => 1f - (1f - t) * (1f - t),
            KinoBlendStyle.HardIn => t * t * t,
            KinoBlendStyle.HardOut => 1f - (1f - t) * (1f - t) * (1f - t),
            _ => t * t * (3f - 2f * t) // EaseInOut, and the fallback for Inherit
        };
    }
}
