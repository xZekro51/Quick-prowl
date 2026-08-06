// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

namespace Prowl.Kino;

/// <summary>
/// The slot a <see cref="KinoComponent"/> occupies in a <see cref="KinoCamera"/>'s pipeline.
/// Stages always run in declaration order, so an aim component always sees the position the body
/// component just chose.
/// </summary>
public enum KinoStage : byte
{
    /// <summary>Decides where the camera is. Only the first enabled Body component runs.</summary>
    Body,

    /// <summary>Decides where the camera looks. Only the first enabled Aim component runs.</summary>
    Aim,

    /// <summary>Adds shake on top of the chosen shot. Every enabled Noise component runs.</summary>
    Noise,

    /// <summary>Corrects the finished shot - collision, confining, impulses. Every enabled component runs.</summary>
    Finalize
}

/// <summary>How often a <see cref="KinoCamera"/> runs its pipeline while it is not live.</summary>
/// <remarks>
/// A camera that is live always updates every frame. This only controls the idle ones, and the
/// default costs nothing at all: idle cameras are skipped entirely and snap into place the moment
/// they take over. Raise it only when a camera has to arrive already settled - a long, heavily
/// damped follow shot that should not slide into position while the blend is running.
/// </remarks>
public enum KinoStandbyUpdate : byte
{
    /// <summary>Never updated while idle. Snaps to its ideal shot when it becomes live.</summary>
    Never,

    /// <summary>One idle camera updates per frame, cycling through them all.</summary>
    RoundRobin,

    /// <summary>Updated every frame, live or not. The expensive option.</summary>
    Always
}

/// <summary>Which engine tick a <see cref="KinoBrain"/> drives its camera from.</summary>
public enum KinoUpdateMethod : byte
{
    /// <summary>Drive in LateUpdate, after gameplay has moved everything. The right choice almost always.</summary>
    LateUpdate,

    /// <summary>Drive in FixedUpdate. Use when the follow target is a rigidbody moved by physics.</summary>
    FixedUpdate,

    /// <summary>Never driven automatically - call <see cref="KinoBrain.ManualUpdate"/> yourself.</summary>
    Manual
}

/// <summary>The frame a <see cref="KinoTransposer"/> offset is expressed in.</summary>
public enum KinoBindingMode : byte
{
    /// <summary>The offset is a world-space vector. The camera never rotates around the target.</summary>
    WorldSpace,

    /// <summary>The offset rotates with the target, all three axes. The camera rides the target rigidly.</summary>
    LockToTarget,

    /// <summary>The offset rotates with the target's heading only, so target pitch and roll do not tip the camera.</summary>
    LockToTargetWithWorldUp,

    /// <summary>
    /// The offset keeps its distance and height but the camera stays wherever it already is around
    /// the target - it follows without ever pushing itself behind the target's back.
    /// </summary>
    SimpleFollowWithWorldUp
}

/// <summary>Which direction is "up" for the shot.</summary>
public enum KinoUpMode : byte
{
    /// <summary>World up (+Y). The horizon stays level.</summary>
    World,

    /// <summary>The up vector the body component produced - a dolly path's roll, for instance.</summary>
    FromBody
}

/// <summary>A raw input channel a <see cref="KinoAxis"/> can read by itself.</summary>
/// <remarks>
/// Set <see cref="KinoInput.Provider"/> to route these through your own input system instead of
/// reading the engine's directly.
/// </remarks>
public enum KinoInputSource : byte
{
    /// <summary>No input. The axis only moves when you assign <see cref="KinoAxis.Value"/> yourself.</summary>
    None,
    MouseX,
    MouseY,
    MouseScroll,
    GamepadLeftStickX,
    GamepadLeftStickY,
    GamepadRightStickX,
    GamepadRightStickY
}
