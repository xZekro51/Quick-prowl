// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>Ready-made shake settings. Pick one and the six amplitude and frequency values are filled in for you.</summary>
public enum KinoShakePreset : byte
{
    /// <summary>Use the amplitude and frequency values on the component.</summary>
    Custom,

    /// <summary>Someone holding the camera. The default, and the one to reach for.</summary>
    Handheld,

    /// <summary>The same, barely there. For shots that should feel steady but not locked off.</summary>
    HandheldMild,

    /// <summary>Slow, wide drift. Dream sequences, drunkenness, underwater.</summary>
    Wobble,

    /// <summary>Fast and tight. Engines, machinery, a building coming down.</summary>
    Rumble
}

/// <summary>
/// Continuous procedural shake. Everything a real camera operator's hands do, without an animation to
/// author or a clip to loop.
/// </summary>
/// <remarks>
/// <para>
/// The signal is sampled from the clock rather than accumulated frame by frame, so a shake looks the
/// same whatever the framerate did, and two cameras with different <see cref="Seed"/>s never shake in
/// step with each other.
/// </para>
/// <para>
/// For one-off shakes - explosions, impacts - use <see cref="KinoImpulse"/> and a
/// <see cref="KinoImpulseListener"/> instead, or call <see cref="Pulse"/> on this component.
/// </para>
/// </remarks>
[AddComponentMenu("Kino/Noise/Kino Shake")]
[ComponentIcon("\uf83e")] // WaveSquare
public class KinoShake : KinoComponent
{
    /// <summary>Which shake to run. Anything but <see cref="KinoShakePreset.Custom"/> ignores the values below.</summary>
    public KinoShakePreset Preset = KinoShakePreset.Handheld;

    /// <summary>Scales the whole shake. Zero switches it off; animate it to fade shake in and out.</summary>
    public float AmplitudeGain = 1f;

    /// <summary>Scales how fast the shake runs.</summary>
    public float FrequencyGain = 1f;

    /// <summary>Separates this camera's shake from every other one. Any two different numbers will do.</summary>
    public int Seed = 0;

    /// <summary>How far the camera moves, in metres, on each of its own axes.</summary>
    [ShowIf(nameof(IsCustom))]
    public Float3 PositionAmplitude = new(0.05f, 0.05f, 0.02f);

    /// <summary>How fast it moves, in cycles per second, on each axis.</summary>
    [ShowIf(nameof(IsCustom))]
    public Float3 PositionFrequency = new(1.2f, 1.0f, 0.8f);

    /// <summary>How far the camera tips, in degrees, about each of its own axes.</summary>
    [ShowIf(nameof(IsCustom))]
    public Float3 RotationAmplitude = new(0.8f, 0.8f, 0.4f);

    /// <summary>How fast it tips, in cycles per second, about each axis.</summary>
    [ShowIf(nameof(IsCustom))]
    public Float3 RotationFrequency = new(1.0f, 0.9f, 0.7f);

    /// <summary>True when the preset is <see cref="KinoShakePreset.Custom"/>. Drives which fields the inspector shows.</summary>
    public bool IsCustom => Preset == KinoShakePreset.Custom;

    private float _time;
    private float _pulseStrength;
    private float _pulseRemaining;
    private float _pulseDuration;

    public override KinoStage Stage => KinoStage.Noise;

    public override void ResetDamping()
    {
        _pulseRemaining = 0f;
        _pulseStrength = 0f;
    }

    /// <summary>
    /// Adds a burst of extra shake that fades out on its own - a hit, a stumble, a heavy landing.
    /// </summary>
    /// <param name="strength">Multiplier on top of <see cref="AmplitudeGain"/> at the moment it lands.</param>
    /// <param name="duration">Seconds to fade away over.</param>
    public void Pulse(float strength = 2f, float duration = 0.35f)
    {
        if (strength <= 0f || duration <= 0f)
            return;

        // A stronger pulse always wins outright rather than adding, so hits landing in quick
        // succession stay readable instead of piling up into a blur.
        float current = _pulseDuration > 0f ? _pulseStrength * (_pulseRemaining / _pulseDuration) : 0f;
        if (strength < current)
            return;

        _pulseStrength = strength;
        _pulseDuration = duration;
        _pulseRemaining = duration;
    }

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        _time += Maths.Abs(ctx.DeltaTime);

        float amplitude = AmplitudeGain;
        if (_pulseRemaining > 0f)
        {
            _pulseRemaining = Maths.Max(_pulseRemaining - Maths.Abs(ctx.DeltaTime), 0f);
            float fade = _pulseDuration > 0f ? _pulseRemaining / _pulseDuration : 0f;
            amplitude += _pulseStrength * fade * fade;
        }

        if (amplitude <= KinoMath.Epsilon || FrequencyGain <= 0f)
            return;

        Resolve(out Float3 positionAmplitude, out Float3 positionFrequency, out Float3 rotationAmplitude, out Float3 rotationFrequency);

        int channel = Seed * 16;
        Float3 offset = KinoNoise.Sample3(_time, positionFrequency * FrequencyGain, channel) * positionAmplitude * amplitude;
        Float3 tilt = KinoNoise.Sample3(_time, rotationFrequency * FrequencyGain, channel + 3) * rotationAmplitude * amplitude;

        // Applied in the camera's own axes: shake means "the operator's hands moved", not "the camera
        // slid north", so it has to travel with wherever the shot is pointing.
        state.PositionShake += state.Rotation * offset;
        state.RotationShake *= Quaternion.FromEuler(tilt);
    }

    private void Resolve(out Float3 positionAmplitude, out Float3 positionFrequency, out Float3 rotationAmplitude, out Float3 rotationFrequency)
    {
        switch (Preset)
        {
            case KinoShakePreset.Handheld:
                positionAmplitude = new Float3(0.05f, 0.05f, 0.02f);
                positionFrequency = new Float3(1.2f, 1.0f, 0.8f);
                rotationAmplitude = new Float3(0.8f, 0.8f, 0.4f);
                rotationFrequency = new Float3(1.0f, 0.9f, 0.7f);
                break;

            case KinoShakePreset.HandheldMild:
                positionAmplitude = new Float3(0.02f, 0.02f, 0.01f);
                positionFrequency = new Float3(0.9f, 0.8f, 0.6f);
                rotationAmplitude = new Float3(0.3f, 0.3f, 0.15f);
                rotationFrequency = new Float3(0.8f, 0.7f, 0.5f);
                break;

            case KinoShakePreset.Wobble:
                positionAmplitude = new Float3(0.12f, 0.10f, 0.06f);
                positionFrequency = new Float3(0.3f, 0.25f, 0.2f);
                rotationAmplitude = new Float3(1.5f, 1.5f, 1.0f);
                rotationFrequency = new Float3(0.3f, 0.25f, 0.15f);
                break;

            case KinoShakePreset.Rumble:
                positionAmplitude = new Float3(0.02f, 0.02f, 0.015f);
                positionFrequency = new Float3(12f, 11f, 10f);
                rotationAmplitude = new Float3(0.25f, 0.25f, 0.15f);
                rotationFrequency = new Float3(10f, 9f, 8f);
                break;

            default:
                positionAmplitude = PositionAmplitude;
                positionFrequency = PositionFrequency;
                rotationAmplitude = RotationAmplitude;
                rotationFrequency = RotationFrequency;
                break;
        }
    }
}
