// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Animate;

/// <summary>One clip to sample this frame, already resolved to a binding, a time and a weight.</summary>
internal struct SampleInstruction
{
    public AnimationClip Clip;
    public ClipBinding Binding;
    public float Time;   // seconds into the clip
    public float Weight;
}

/// <summary>
/// The authored <see cref="AnimatorController"/> reduced to what the per-frame loop actually needs:
/// integer parameter slots, integer state destinations, priority-sorted transition arrays and
/// pre-resolved clip bindings.
///
/// <para><b>Why this exists.</b> The authored graph addresses everything by name - a condition names
/// a parameter, a transition names its destination state, a blend tree names its axes. Evaluating
/// that directly means a dictionary probe per condition, a linear state-name scan per transition and
/// a second name probe per blend axis, <i>every frame, per layer, per animator</i>. Compiling once at
/// bind time turns all of it into array indexing, and the hot path stops touching strings entirely.</para>
///
/// <para>The compiled form is per-<see cref="Animator"/> because clip bindings are skeleton-specific.
/// It is rebuilt when the controller instance changes or its
/// <see cref="AnimatorController.StructureVersion"/> moves.</para>
/// </summary>
internal sealed class CompiledController
{
    public AnimatorController Source = null!;
    public int StructureVersion;
    public CompiledLayer[] Layers = Array.Empty<CompiledLayer>();

    public float[] ParameterDefaults = Array.Empty<float>();
    public AnimatorControllerParameterType[] ParameterTypes = Array.Empty<AnimatorControllerParameterType>();
    public readonly Dictionary<string, int> NameToIndex = new(StringComparer.Ordinal);
    public readonly Dictionary<int, int> HashToIndex = new();

    /// <summary>Build the compiled form. <paramref name="bindClip"/> resolves (and caches) clip bindings.</summary>
    public static CompiledController Compile(AnimatorController controller, Func<AnimationClip, ClipBinding?> bindClip)
    {
        var c = new CompiledController
        {
            Source = controller,
            StructureVersion = controller.StructureVersion,
        };

        int pc = controller.Parameters.Count;
        c.ParameterDefaults = new float[pc];
        c.ParameterTypes = new AnimatorControllerParameterType[pc];
        for (int i = 0; i < pc; i++)
        {
            var p = controller.Parameters[i];
            c.ParameterTypes[i] = p.Type;
            c.ParameterDefaults[i] = p.DefaultValue;

            // First declaration wins, matching how the runtime resolved duplicates before; the
            // validator flags the duplicate rather than silently changing which one is reachable.
            c.NameToIndex.TryAdd(p.Name, i);
            c.HashToIndex.TryAdd(Animator.StringToHash(p.Name), i);
        }

        c.Layers = new CompiledLayer[controller.Layers.Count];
        for (int li = 0; li < controller.Layers.Count; li++)
            c.Layers[li] = CompileLayer(controller.Layers[li], c, bindClip);

        return c;
    }

    public int IndexOfName(string name)
        => !string.IsNullOrEmpty(name) && NameToIndex.TryGetValue(name, out int i) ? i : -1;

    private static CompiledLayer CompileLayer(AnimatorLayer layer, CompiledController c,
        Func<AnimationClip, ClipBinding?> bindClip)
    {
        var cl = new CompiledLayer
        {
            Source = layer,
            BlendMode = layer.BlendMode,
            DefaultWeight = layer.DefaultWeight,
            Mask = layer.Mask,
            States = new CompiledState[layer.States.Count],
        };

        for (int i = 0; i < layer.States.Count; i++)
        {
            var s = layer.States[i];
            cl.States[i] = new CompiledState
            {
                Source = s,
                Name = s.Name,
                Tag = s.Tag,
                Speed = s.Speed,
                SpeedParameter = c.IndexOfName(s.SpeedParameter),
                Loop = s.Loop,
                CycleOffset = s.CycleOffset,
                Motion = CompileMotion(s.Motion, c, bindClip),
                Events = s.Events.Count > 0 ? s.Events.ToArray() : Array.Empty<AnimationEventMarker>(),
            };
        }

        // Destinations are resolved against the authored layer, so they must be compiled after all
        // states exist.
        for (int i = 0; i < layer.States.Count; i++)
            cl.States[i].Transitions = CompileTransitions(layer.States[i].Transitions, layer, c);

        cl.AnyTransitions = CompileTransitions(layer.AnyStateTransitions, layer, c);
        cl.DefaultState = layer.FindStateIndex(layer.DefaultState);
        return cl;
    }

    private static CompiledTransition[] CompileTransitions(List<AnimatorStateTransition> list,
        AnimatorLayer layer, CompiledController c)
    {
        if (list.Count == 0) return Array.Empty<CompiledTransition>();

        var result = new CompiledTransition[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            var conditions = new CompiledCondition[t.Conditions.Count];
            for (int k = 0; k < t.Conditions.Count; k++)
            {
                var cond = t.Conditions[k];
                int pi = c.IndexOfName(cond.Parameter);
                conditions[k] = new CompiledCondition(
                    pi, cond.Mode, cond.Threshold,
                    pi >= 0 && c.ParameterTypes[pi] == AnimatorControllerParameterType.Trigger);
            }

            result[i] = new CompiledTransition
            {
                Source = t,
                Destination = layer.FindStateIndex(t.DestinationState),
                Conditions = conditions,
                Muted = t.Muted,
                HasExitTime = t.HasExitTime,
                ExitTime = t.ExitTime,
                Duration = t.Duration,
                FixedDuration = t.FixedDuration,
                Offset = t.Offset,
                CanTransitionToSelf = t.CanTransitionToSelf,
                Priority = t.Priority,
            };
        }

        // Sorted once here so the frame loop can walk the array in order. A stable sort keeps
        // equal-priority transitions in authored order, which is the tie-break users expect.
        StableSortByPriority(result);
        return result;
    }

    private static void StableSortByPriority(CompiledTransition[] items)
    {
        // Insertion sort: stable, allocation-free, and transition counts are tiny.
        for (int i = 1; i < items.Length; i++)
        {
            var item = items[i];
            int j = i - 1;
            while (j >= 0 && items[j].Priority > item.Priority)
            {
                items[j + 1] = items[j];
                j--;
            }
            items[j + 1] = item;
        }
    }

    private static CompiledMotion? CompileMotion(Motion? motion, CompiledController c,
        Func<AnimationClip, ClipBinding?> bindClip)
    {
        switch (motion)
        {
            case ClipMotion cm:
                return new CompiledClip(cm.Clip, bindClip);

            case BlendTree bt:
            {
                int n = bt.Children.Count;
                var tree = new CompiledTree
                {
                    Type = bt.Type,
                    ParameterX = c.IndexOfName(bt.BlendParameter),
                    ParameterY = c.IndexOfName(bt.BlendParameterY),
                    Children = new CompiledMotion?[n],
                    Thresholds = new float[n],
                    Positions = new Float2[n],
                    TimeScales = new float[n],
                    Weights = new float[n],
                };

                // 1D weighting walks the children as a sorted line, so compile them in threshold order
                // rather than authoring order. Doing it here means the editor never has to re-sort the
                // authored list while you are typing into it, and a controller built in code cannot
                // silently blend the wrong pair because its children were added out of order.
                var order = new int[n];
                for (int i = 0; i < n; i++) order[i] = i;
                if (bt.Type == BlendTreeType.Simple1D)
                {
                    var thresholds = bt.Children;
                    Array.Sort(order, (a, b) => thresholds[a].Threshold.CompareTo(thresholds[b].Threshold));
                }

                for (int i = 0; i < n; i++)
                {
                    var child = bt.Children[order[i]];
                    tree.Children[i] = CompileMotion(child.Motion, c, bindClip);
                    tree.Thresholds[i] = child.Threshold;
                    tree.Positions[i] = child.Position;
                    tree.TimeScales[i] = child.TimeScale;
                }
                return tree;
            }

            default:
                return null;
        }
    }
}

/// <summary>A layer reduced to arrays. <see cref="Mask"/> stays authored - the Animator resolves it
/// against the skeleton, which can still grow as clips stream in.</summary>
internal sealed class CompiledLayer
{
    public AnimatorLayer Source = null!;
    public CompiledState[] States = Array.Empty<CompiledState>();
    public CompiledTransition[] AnyTransitions = Array.Empty<CompiledTransition>();
    public int DefaultState = -1;
    public AnimatorLayerBlendMode BlendMode;
    public float DefaultWeight = 1f;
    public AvatarMask? Mask;
}

/// <summary>A state reduced to values and indices. <see cref="Source"/> is kept only for queries.</summary>
internal sealed class CompiledState
{
    public AnimatorState Source = null!;
    public string Name = string.Empty;
    public string Tag = string.Empty;
    public float Speed = 1f;

    /// <summary>Parameter slot scaling <see cref="Speed"/>, or -1.</summary>
    public int SpeedParameter = -1;

    public bool Loop = true;
    public float CycleOffset;
    public CompiledMotion? Motion;
    public CompiledTransition[] Transitions = Array.Empty<CompiledTransition>();
    public AnimationEventMarker[] Events = Array.Empty<AnimationEventMarker>();
}

/// <summary>A transition with its destination and parameter slots already resolved.</summary>
internal sealed class CompiledTransition
{
    public AnimatorStateTransition Source = null!;

    /// <summary>Index into the layer's state array, or -1 when the destination does not exist.</summary>
    public int Destination = -1;

    public CompiledCondition[] Conditions = Array.Empty<CompiledCondition>();
    public bool Muted;
    public bool HasExitTime;
    public float ExitTime = 1f;
    public float Duration;
    public bool FixedDuration = true;
    public float Offset;
    public bool CanTransitionToSelf;
    public int Priority;
}

/// <summary>A condition against an integer parameter slot.</summary>
internal readonly struct CompiledCondition
{
    /// <summary>Parameter slot, or -1 when the named parameter does not exist (never passes).</summary>
    public readonly int Parameter;

    public readonly AnimatorConditionMode Mode;
    public readonly float Threshold;
    public readonly bool IsTrigger;

    public CompiledCondition(int parameter, AnimatorConditionMode mode, float threshold, bool isTrigger)
    {
        Parameter = parameter;
        Mode = mode;
        Threshold = threshold;
        IsTrigger = isTrigger;
    }

    public bool IsMet(float[] values)
    {
        if (Parameter < 0) return false;
        float v = values[Parameter];
        return Mode switch
        {
            AnimatorConditionMode.If => v != 0f,
            AnimatorConditionMode.IfNot => v == 0f,
            AnimatorConditionMode.Greater => v > Threshold,
            AnimatorConditionMode.Less => v < Threshold,
            AnimatorConditionMode.Equals => (int)MathF.Round(v) == (int)Threshold,
            AnimatorConditionMode.NotEqual => (int)MathF.Round(v) != (int)Threshold,
            _ => false,
        };
    }
}

/// <summary>
/// A motion reduced to what sampling needs. Both operations take the parameter array directly and a
/// per-frame <c>stamp</c>; blend weights computed while measuring the motion's length are reused when
/// it is expanded into sample instructions later in the same frame, so a tree is weighted once per
/// frame rather than twice.
/// </summary>
internal abstract class CompiledMotion
{
    /// <summary>Weighted length in seconds; 1 when nothing is resolvable (so time still advances).</summary>
    public abstract float ComputeLength(float[] values, int stamp);

    /// <summary>Append the clips this motion plays at <paramref name="normalizedTime"/>.</summary>
    public abstract void Expand(float[] values, int stamp, float normalizedTime, bool loop, float weight,
        List<SampleInstruction> into);
}

/// <summary>A single clip. The asset reference is resolved lazily and then cached.</summary>
internal sealed class CompiledClip : CompiledMotion
{
    private AssetRef<AnimationClip> _ref;
    private readonly Func<AnimationClip, ClipBinding?> _bindClip;

    private AnimationClip? _clip;
    private ClipBinding? _binding;

    public CompiledClip(AssetRef<AnimationClip> clipRef, Func<AnimationClip, ClipBinding?> bindClip)
    {
        _ref = clipRef;
        _bindClip = bindClip;
    }

    /// <summary>
    /// The resolved clip, or null while it is still streaming in.
    ///
    /// <para>The cached instance is validity-checked rather than trusted outright. A clip can be
    /// disposed underneath a binding - a reimport from the editor replaces the instance, and the
    /// asset cache idle-sweeps assets nothing reports as in use - and a stale reference would throw
    /// on the next sample. <see cref="EngineObjectExtensions.IsValid"/> is a null plus disposed-flag
    /// test, so the common path stays a field read and the asset-system lookup only happens when the
    /// instance really has gone away.</para>
    /// </summary>
    public AnimationClip? Clip => _clip.IsValid() ? _clip : Resolve();

    /// <summary>Re-resolve through the asset ref and rebuild the binding for the new instance.</summary>
    private AnimationClip? Resolve()
    {
        var clip = _ref.Res;
        if (clip.IsNotValid()) { _clip = null; _binding = null; return null; }

        _clip = clip;
        _binding = _bindClip(clip!);
        return clip;
    }

    public override float ComputeLength(float[] values, int stamp)
    {
        var clip = Clip;
        return clip != null && clip.Duration > 0f ? clip.Duration : 1f;
    }

    public override void Expand(float[] values, int stamp, float normalizedTime, bool loop, float weight,
        List<SampleInstruction> into)
    {
        if (weight <= 0f) return;

        var clip = Clip;
        if (clip == null || _binding == null) return;

        float len = clip.Duration;
        float phase = loop ? AnimMath.Frac(normalizedTime) : Maths.Saturate(normalizedTime);

        // Offset by StartTime: a clip authored on a shared timeline (a take cut out of a longer
        // animation) has its first key at StartTime, not at zero, and sampling it from zero holds
        // its first pose for the whole leading gap.
        into.Add(new SampleInstruction
        {
            Clip = clip,
            Binding = _binding,
            Time = clip.StartTime + (len > 0f ? phase * len : 0f),
            Weight = weight,
        });
    }
}

/// <summary>A blend of child motions, weighted by one or two parameter slots.</summary>
internal sealed class CompiledTree : CompiledMotion
{
    public BlendTreeType Type;
    public int ParameterX = -1;
    public int ParameterY = -1;
    public CompiledMotion?[] Children = Array.Empty<CompiledMotion?>();
    public float[] Thresholds = Array.Empty<float>();
    public Float2[] Positions = Array.Empty<Float2>();
    public float[] TimeScales = Array.Empty<float>();

    /// <summary>Scratch weights, valid for <see cref="_weightStamp"/>.</summary>
    public float[] Weights = Array.Empty<float>();

    private int _weightStamp = -1;

    /// <summary>
    /// The tree's length, weighted across the children that are contributing.
    ///
    /// <para>A child's <see cref="BlendTreeChild.TimeScale"/> divides into its length here, and that
    /// is the whole of what a time scale does. Children are sampled at a shared normalized phase to
    /// keep them in step, so scaling a child's own clock would desynchronise the very thing the tree
    /// exists to synchronise; shortening its contribution to the blended length makes the state's
    /// play head advance faster instead, which is the same result and stays in sync. The field was
    /// compiled into <see cref="TimeScales"/> and then never read at all, so authoring one did
    /// nothing.</para>
    /// </summary>
    public override float ComputeLength(float[] values, int stamp)
    {
        int n = Children.Length;
        if (n == 0) return 1f;

        EnsureWeights(values, stamp);

        float len = 0f, total = 0f;
        for (int i = 0; i < n; i++)
        {
            float w = Weights[i];
            if (w <= 1e-5f) continue;

            var child = Children[i];
            if (child == null) continue;

            float scale = i < TimeScales.Length ? TimeScales[i] : 1f;
            if (scale <= 1e-5f) scale = 1f;

            len += w * (child.ComputeLength(values, stamp) / scale);
            total += w;
        }
        return total > 1e-5f ? len / total : 1f;
    }

    public override void Expand(float[] values, int stamp, float normalizedTime, bool loop, float weight,
        List<SampleInstruction> into)
    {
        if (weight <= 0f) return;

        int n = Children.Length;
        if (n == 0) return;

        EnsureWeights(values, stamp);

        for (int i = 0; i < n; i++)
        {
            float w = Weights[i];
            if (w <= 1e-5f) continue;
            Children[i]?.Expand(values, stamp, normalizedTime, loop, weight * w, into);
        }
    }

    private void EnsureWeights(float[] values, int stamp)
    {
        if (_weightStamp == stamp) return;
        _weightStamp = stamp;
        ComputeWeights(values);
    }

    private void ComputeWeights(float[] values)
    {
        int n = Children.Length;
        var w = Weights;
        Array.Clear(w, 0, n);

        if (n == 1) { w[0] = 1f; return; }

        if (Type == BlendTreeType.Simple1D) Compute1D(values, w);
        else Compute2D(values, w);
    }

    private float Value(float[] values, int slot) => slot >= 0 ? values[slot] : 0f;

    private void Compute1D(float[] values, float[] w)
    {
        float p = Value(values, ParameterX);
        int n = Thresholds.Length;

        // Children are expected in ascending threshold order; clamp outside the range to the ends.
        if (p <= Thresholds[0]) { w[0] = 1f; return; }
        if (p >= Thresholds[n - 1]) { w[n - 1] = 1f; return; }

        for (int i = 0; i < n - 1; i++)
        {
            float a = Thresholds[i];
            float b = Thresholds[i + 1];
            if (p >= a && p <= b)
            {
                float t = b > a ? (p - a) / (b - a) : 0f;
                w[i] = 1f - t;
                w[i + 1] = t;
                return;
            }
        }
    }

    // Gradient-band ("freeform cartesian") interpolation. Robust for any 2D layout and used for
    // directional layouts too. O(n^2) but child counts are tiny.
    private void Compute2D(float[] values, float[] w)
    {
        int n = Positions.Length;
        var sample = new Float2(Value(values, ParameterX), Value(values, ParameterY));

        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            Float2 pi = Positions[i];
            float weight = 1f;
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                Float2 pipj = Positions[j] - pi;
                float denom = pipj.X * pipj.X + pipj.Y * pipj.Y;
                if (denom <= 1e-8f) continue;

                Float2 pip = sample - pi;
                float t = 1f - (pip.X * pipj.X + pip.Y * pipj.Y) / denom;
                float clamped = Maths.Saturate(t);
                if (clamped < weight) weight = clamped;
            }
            w[i] = weight;
            total += weight;
        }

        if (total > 1e-5f)
            for (int i = 0; i < n; i++) w[i] /= total;
        else
            w[0] = 1f;
    }
}
