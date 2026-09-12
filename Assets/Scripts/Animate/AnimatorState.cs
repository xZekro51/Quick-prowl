// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Animate;

/// <summary>
/// A typed animation event baked onto a state's timeline. When playback crosses
/// <see cref="NormalizedTime"/> the Animator raises <see cref="Animator.OnAnimationEvent"/> and
/// invokes any handler registered for <see cref="FunctionName"/>. Unlike Unity's reflective
/// <c>SendMessage</c> events, these are delivered through plain delegates - no string method
/// resolution, no silent failures.
/// </summary>
public sealed class AnimationEventMarker
{
    /// <summary>Normalized position in the state (0..1) at which the event fires.</summary>
    public float NormalizedTime;

    /// <summary>Identifier passed to listeners; used to look up a registered handler.</summary>
    public string FunctionName = string.Empty;

    public string StringParameter = string.Empty;
    public float FloatParameter;
    public int IntParameter;

    public AnimationEventMarker() { }

    public AnimationEventMarker(float normalizedTime, string functionName)
    {
        NormalizedTime = normalizedTime;
        FunctionName = functionName;
    }
}

/// <summary>If/when a state may hand control to another state.</summary>
public sealed class AnimatorStateTransition
{
    /// <summary>Name of the destination state within the same layer.</summary>
    public string DestinationState = string.Empty;

    /// <summary>All conditions must pass (AND) for the transition to be taken.</summary>
    public List<AnimatorCondition> Conditions = new();

    /// <summary>When true the transition can only start once normalized time reaches <see cref="ExitTime"/>.</summary>
    public bool HasExitTime;

    /// <summary>Normalized time (0..1, may exceed 1 for looped exit) the source must reach.</summary>
    public float ExitTime = 1f;

    /// <summary>Cross-fade length. Seconds when <see cref="FixedDuration"/>, else normalized to source length.</summary>
    public float Duration = 0.15f;

    /// <summary>Interpret <see cref="Duration"/> as seconds (true) or as a fraction of the source clip (false).</summary>
    public bool FixedDuration = true;

    /// <summary>Normalized time the destination starts playing from.</summary>
    public float Offset;

    /// <summary>Allow a state to transition back into itself (e.g. to restart on a trigger).</summary>
    public bool CanTransitionToSelf;

    /// <summary>Lower numbers are evaluated first when several transitions are ready on the same frame.</summary>
    public int Priority;

    /// <summary>
    /// Turns the transition off without deleting it. Useful while tuning a graph - the arrow stays on
    /// the canvas (drawn dashed) so you can see what you switched off and put it back.
    /// </summary>
    public bool Muted;

    public AnimatorStateTransition() { }

    public AnimatorStateTransition(string destinationState)
    {
        DestinationState = destinationState;
    }

    /// <summary>Add a condition. Returns this for chaining.</summary>
    public AnimatorStateTransition When(string parameter, AnimatorConditionMode mode, float threshold = 0f)
    {
        Conditions.Add(new AnimatorCondition(parameter, mode, threshold));
        return this;
    }

    /// <summary>Set exit-time gating. Returns this for chaining.</summary>
    public AnimatorStateTransition WithExitTime(float exitTime = 1f)
    {
        HasExitTime = true;
        ExitTime = exitTime;
        return this;
    }

    /// <summary>Set the cross-fade duration in seconds. Returns this for chaining.</summary>
    public AnimatorStateTransition WithDuration(float seconds)
    {
        FixedDuration = true;
        Duration = seconds;
        return this;
    }
}

/// <summary>
/// A node in a layer's state machine. Holds the motion to play plus playback settings and the
/// outgoing transitions. The motion may be a single clip or a blend tree.
/// </summary>
public sealed class AnimatorState
{
    public string Name = "New State";

    /// <summary>What this state plays - a clip or a blend tree.</summary>
    public Motion? Motion;

    /// <summary>Constant playback speed multiplier.</summary>
    public float Speed = 1f;

    /// <summary>Optional float parameter that further scales <see cref="Speed"/> at runtime.</summary>
    public string SpeedParameter = string.Empty;

    /// <summary>Normalized time offset applied when the state is (re)entered.</summary>
    public float CycleOffset;

    /// <summary>Loop the motion (otherwise it clamps at the end).</summary>
    public bool Loop = true;

    /// <summary>Free-form tag for gameplay queries (matches Unity's state tags).</summary>
    public string Tag = string.Empty;

    public List<AnimatorStateTransition> Transitions = new();

    public List<AnimationEventMarker> Events = new();

    /// <summary>Node position in the graph editor (purely cosmetic).</summary>
    public Float2 EditorPosition;

    public AnimatorState() { }

    public AnimatorState(string name, Motion? motion = null)
    {
        Name = name;
        Motion = motion;
    }

    /// <summary>Add and return a transition to another state.</summary>
    public AnimatorStateTransition AddTransition(string destinationState)
    {
        var t = new AnimatorStateTransition(destinationState);
        Transitions.Add(t);
        return t;
    }

    /// <summary>Add a typed animation event at a normalized time.</summary>
    public AnimationEventMarker AddEvent(float normalizedTime, string functionName)
    {
        var e = new AnimationEventMarker(normalizedTime, functionName);
        Events.Add(e);
        return e;
    }
}

/// <summary>How a layer's pose combines with the layers beneath it.</summary>
public enum AnimatorLayerBlendMode
{
    /// <summary>Replaces lower layers (weighted by the layer weight and mask).</summary>
    Override,

    /// <summary>Adds the layer's motion <i>relative to the reference pose</i> on top of lower layers.</summary>
    Additive,
}

/// <summary>
/// Restricts a layer to a subset of bones. Paths are relative to the animation root (the same
/// "Armature/Hips/Spine" convention <see cref="AnimationClip"/> uses). An empty mask affects
/// every bone.
///
/// <para>Improvement over the original port: a listed path masks in that bone <i>and everything
/// under it</i> by default (<see cref="IncludeChildren"/>). Masking an upper body is what a mask is
/// almost always for, and spelling out every finger bone by hand is how masks get abandoned.</para>
/// </summary>
public sealed class AvatarMask
{
    /// <summary>Bone paths this mask includes. Empty means "include everything".</summary>
    public List<string> IncludedPaths = new();

    /// <summary>When true (the default) each listed path also includes its descendant bones.</summary>
    public bool IncludeChildren = true;

    public bool IncludeEverything => IncludedPaths.Count == 0;

    public AvatarMask() { }

    public AvatarMask(params string[] paths) => IncludedPaths.AddRange(paths);

    /// <summary>True if <paramref name="bonePath"/> falls inside this mask.</summary>
    public bool Includes(string bonePath)
    {
        if (IncludeEverything) return true;

        for (int i = 0; i < IncludedPaths.Count; i++)
        {
            string p = IncludedPaths[i];
            if (p.Length == 0) continue;
            if (string.Equals(bonePath, p, StringComparison.Ordinal)) return true;

            // "Armature/Spine" covers "Armature/Spine/Chest" but must not cover "Armature/SpineIK".
            if (IncludeChildren
                && bonePath.Length > p.Length
                && bonePath[p.Length] == '/'
                && bonePath.StartsWith(p, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

/// <summary>
/// An independent state machine evaluated and blended in order. Layer 0 is the base pose; higher
/// layers refine it via <see cref="BlendMode"/> and an optional <see cref="Mask"/> (e.g. an upper-body
/// "aim/throw" layer over a full-body locomotion base).
/// </summary>
public sealed class AnimatorLayer
{
    public string Name = "Base Layer";

    public List<AnimatorState> States = new();

    /// <summary>Name of the state entered when the layer starts.</summary>
    public string DefaultState = string.Empty;

    /// <summary>Transitions evaluated from <i>any</i> state in this layer.</summary>
    public List<AnimatorStateTransition> AnyStateTransitions = new();

    /// <summary>Starting blend weight (layer 0 is implicitly forced to 1).</summary>
    public float DefaultWeight = 1f;

    public AnimatorLayerBlendMode BlendMode = AnimatorLayerBlendMode.Override;

    public AvatarMask? Mask;

    /// <summary>Position of the "Any State" node in the graph editor (purely cosmetic).</summary>
    public Float2 AnyStateEditorPosition = new(40f, 40f);

    /// <summary>Position of the "Entry" node in the graph editor (purely cosmetic).</summary>
    public Float2 EntryEditorPosition = new(40f, 170f);

    public AnimatorLayer() { }

    public AnimatorLayer(string name) => Name = name;

    /// <summary>Add a clip-backed state. The first state added becomes the default.</summary>
    public AnimatorState AddState(string name, AnimationClip clip)
        => AddState(name, new ClipMotion(clip));

    /// <summary>Add a state with an arbitrary motion. The first state added becomes the default.</summary>
    public AnimatorState AddState(string name, Motion? motion = null)
    {
        var state = new AnimatorState(name, motion);
        States.Add(state);
        if (string.IsNullOrEmpty(DefaultState))
            DefaultState = name;
        return state;
    }

    /// <summary>Find a state by name, or null.</summary>
    public AnimatorState? FindState(string name)
    {
        foreach (var s in States)
            if (s.Name == name) return s;
        return null;
    }

    /// <summary>Index of a state by name, or -1.</summary>
    public int FindStateIndex(string name)
    {
        for (int i = 0; i < States.Count; i++)
            if (States[i].Name == name) return i;
        return -1;
    }

    /// <summary>Add and return an "any state" transition to the given destination.</summary>
    public AnimatorStateTransition AddAnyStateTransition(string destinationState)
    {
        var t = new AnimatorStateTransition(destinationState);
        AnyStateTransitions.Add(t);
        return t;
    }

    /// <summary>Every transition in the layer, paired with its owning state (null for any-state).</summary>
    public IEnumerable<(AnimatorState? Owner, AnimatorStateTransition Transition)> AllTransitions()
    {
        foreach (var t in AnyStateTransitions)
            yield return (null, t);
        foreach (var s in States)
            foreach (var t in s.Transitions)
                yield return (s, t);
    }
}
