// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;

using Prowl.Vector;

using Input = Prowl.Runtime.Input;

namespace Prowl.Kino;

/// <summary>
/// Where Kino reads input from. Point <see cref="Provider"/> at your own input system and every
/// <see cref="KinoAxis"/> in the game follows it instead of the raw devices.
/// </summary>
public static class KinoInput
{
    /// <summary>
    /// Replaces the built-in device reads. Return the value for the channel you are asked for -
    /// a mouse-style channel is a per-frame delta, a stick-style one is a steady -1 to 1.
    /// </summary>
    /// <example>
    /// <code>
    /// KinoInput.Provider = source => source switch
    /// {
    ///     KinoInputSource.MouseX =&gt; myBindings.Look.X,
    ///     KinoInputSource.MouseY =&gt; myBindings.Look.Y,
    ///     _ =&gt; 0f
    /// };
    /// </code>
    /// </example>
    public static Func<KinoInputSource, float>? Provider;

    /// <summary>Set false to freeze every input-driven camera at once - while a menu is open, say.</summary>
    public static bool Enabled = true;

    /// <summary>Reads a channel, through <see cref="Provider"/> when one is installed.</summary>
    public static float Read(KinoInputSource source)
    {
        if (!Enabled || source == KinoInputSource.None)
            return 0f;

        if (Provider != null)
            return Provider(source);

        return source switch
        {
            KinoInputSource.MouseX => Input.MouseDelta.X,
            KinoInputSource.MouseY => Input.MouseDelta.Y,
            KinoInputSource.MouseScroll => Input.MouseWheelDelta,
            KinoInputSource.GamepadLeftStickX => Input.GetGamepadLeftStick().X,
            KinoInputSource.GamepadLeftStickY => Input.GetGamepadLeftStick().Y,
            KinoInputSource.GamepadRightStickX => Input.GetGamepadRightStick().X,
            KinoInputSource.GamepadRightStickY => Input.GetGamepadRightStick().Y,
            _ => 0f
        };
    }

    /// <summary>
    /// True for channels that report movement since last frame (mice, wheels) rather than a held
    /// position (sticks). The two have to be integrated differently or a mouse ends up framerate
    /// dependent.
    /// </summary>
    public static bool IsDelta(KinoInputSource source)
        => source is KinoInputSource.MouseX or KinoInputSource.MouseY or KinoInputSource.MouseScroll;
}

/// <summary>
/// A single number a player can push around - a heading, a pitch, a zoom. Handles its own input,
/// limits, easing and recentering, so a camera component that wants a player-controlled value just
/// holds one of these.
/// </summary>
public class KinoAxis
{
    /// <summary>The current value. Assign it directly to drive the axis from your own code.</summary>
    public float Value;

    /// <summary>Lower limit.</summary>
    public float Min = -180f;

    /// <summary>Upper limit.</summary>
    public float Max = 180f;

    /// <summary>Wrap around the limits instead of stopping at them. What a heading wants; what a pitch does not.</summary>
    public bool Wrap = false;

    /// <summary>Which input channel drives this axis.</summary>
    public KinoInputSource Input = KinoInputSource.None;

    /// <summary>Units per unit of input, for mouse-style channels.</summary>
    public float Gain = 0.2f;

    /// <summary>Units per second at full deflection, for stick-style channels.</summary>
    public float MaxSpeed = 180f;

    /// <summary>Seconds to reach full speed on a stick-style channel.</summary>
    public float AccelTime = 0.1f;

    /// <summary>Seconds to come back to rest on a stick-style channel.</summary>
    public float DecelTime = 0.1f;

    /// <summary>Flips the direction the input pushes the value.</summary>
    public bool Invert = false;

    /// <summary>Seconds of no input before the axis drifts back to centre. Zero never recenters.</summary>
    public float RecenterWait = 0f;

    /// <summary>Seconds the drift back to centre takes. Ignored when <see cref="RecenterWait"/> is zero.</summary>
    public float RecenterTime = 1f;

    private float _speed;
    private float _idleTime;

    /// <summary>True while the player is actively pushing this axis.</summary>
    public bool IsMoving { get; private set; }

    /// <summary>
    /// Reads input and advances the value.
    /// </summary>
    /// <param name="deltaTime">Frame delta. Zero or less only clamps, without moving anything.</param>
    /// <param name="recenterTarget">
    /// Where the axis drifts back to when the player lets go, if <see cref="RecenterWait"/> is set.
    /// </param>
    public void Update(float deltaTime, float recenterTarget = 0f)
    {
        IsMoving = false;
        if (deltaTime <= 0f)
        {
            Clamp();
            return;
        }

        float raw = KinoInput.Read(Input);
        if (Invert)
            raw = -raw;

        if (KinoInput.IsDelta(Input))
        {
            // Already a per-frame movement - scaling it by delta time as well would make a fast
            // machine turn slower than a slow one for the same flick of the wrist.
            if (Maths.Abs(raw) > KinoMath.Epsilon)
            {
                Value += raw * Gain;
                IsMoving = true;
            }
        }
        else
        {
            float target = Maths.Clamp(raw, -1f, 1f) * MaxSpeed;
            float ramp = Maths.Abs(target) > Maths.Abs(_speed) ? AccelTime : DecelTime;
            _speed += KinoMath.Damp(target - _speed, ramp, deltaTime);

            if (Maths.Abs(_speed) > KinoMath.Epsilon)
            {
                Value += _speed * deltaTime;
                IsMoving = Maths.Abs(target) > KinoMath.Epsilon;
            }
        }

        _idleTime = IsMoving ? 0f : _idleTime + deltaTime;

        if (RecenterWait > 0f && _idleTime >= RecenterWait)
        {
            float gap = Wrap ? KinoMath.DeltaAngle(Value, recenterTarget) : recenterTarget - Value;
            Value += KinoMath.Damp(gap, RecenterTime, deltaTime);
        }

        Clamp();
    }

    /// <summary>Jumps the axis to a value and forgets any momentum or recentering countdown.</summary>
    public void Set(float value)
    {
        Value = value;
        _speed = 0f;
        _idleTime = 0f;
        Clamp();
    }

    /// <summary>Stops the axis where it is.</summary>
    public void Reset()
    {
        _speed = 0f;
        _idleTime = 0f;
    }

    private void Clamp()
    {
        if (Max <= Min)
            return;

        if (!Wrap)
        {
            Value = Maths.Clamp(Value, Min, Max);
            return;
        }

        float range = Max - Min;
        Value -= Maths.Floor((Value - Min) / range) * range;
    }
}
