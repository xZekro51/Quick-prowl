// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

namespace Prowl.Animate;

/// <summary>The value kind a controller parameter carries.</summary>
public enum AnimatorControllerParameterType
{
    Float,
    Int,
    Bool,

    /// <summary>A bool that auto-resets to false the moment a transition consumes it.</summary>
    Trigger,
}

/// <summary>
/// A named, typed input to an <see cref="AnimatorController"/>. Parameters drive transition
/// conditions, blend trees, and per-state speed. They are addressed at runtime by a stable
/// hash (see <see cref="Animator.StringToHash"/>) so gameplay code can set them allocation-free.
/// </summary>
public sealed class AnimatorControllerParameter
{
    public string Name = "New Parameter";
    public AnimatorControllerParameterType Type = AnimatorControllerParameterType.Float;

    // Defaults stored per-kind so a single class round-trips all parameter types.
    public float DefaultFloat;
    public int DefaultInt;
    public bool DefaultBool;

    public AnimatorControllerParameter() { }

    public AnimatorControllerParameter(string name, AnimatorControllerParameterType type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>The default packed into the single float slot the runtime parameter store uses.</summary>
    public float DefaultValue => Type switch
    {
        AnimatorControllerParameterType.Float => DefaultFloat,
        AnimatorControllerParameterType.Int => DefaultInt,
        AnimatorControllerParameterType.Bool => DefaultBool ? 1f : 0f,
        _ => 0f,
    };
}

/// <summary>How a single <see cref="AnimatorCondition"/> compares a parameter to its threshold.</summary>
public enum AnimatorConditionMode
{
    /// <summary>Bool/Trigger is true.</summary>
    If,

    /// <summary>Bool is false.</summary>
    IfNot,

    /// <summary>Float/Int is greater than the threshold.</summary>
    Greater,

    /// <summary>Float/Int is less than the threshold.</summary>
    Less,

    /// <summary>Int equals the threshold.</summary>
    Equals,

    /// <summary>Int does not equal the threshold.</summary>
    NotEqual,
}

/// <summary>
/// One predicate on a parameter. A transition fires only when <b>all</b> of its conditions are
/// satisfied (logical AND), matching the mental model most users expect from Unity.
/// </summary>
public sealed class AnimatorCondition
{
    public string Parameter = string.Empty;
    public AnimatorConditionMode Mode = AnimatorConditionMode.Greater;
    public float Threshold;

    public AnimatorCondition() { }

    public AnimatorCondition(string parameter, AnimatorConditionMode mode, float threshold = 0f)
    {
        Parameter = parameter;
        Mode = mode;
        Threshold = threshold;
    }

    /// <summary>True when the mode compares against <see cref="Threshold"/> rather than just truthiness.</summary>
    public bool UsesThreshold
        => Mode is not (AnimatorConditionMode.If or AnimatorConditionMode.IfNot);
}
