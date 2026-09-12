// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Animate;

/// <summary>Whether the Animator advances on scaled game time or wall-clock time.</summary>
public enum AnimatorUpdateMode
{
    /// <summary>Advance by <see cref="Time.DeltaTime"/> (affected by time scale).</summary>
    Normal,

    /// <summary>Advance by unscaled delta time - ignores time scale (good for UI / menus).</summary>
    UnscaledTime,
}

/// <summary>A lightweight snapshot of what a layer is currently playing. Returned by queries.</summary>
public readonly struct AnimatorStateInfo
{
    public readonly bool IsValid;
    public readonly string Name;
    public readonly string Tag;

    /// <summary>Play head in normalized time; the integer part is the loop count.</summary>
    public readonly float NormalizedTime;

    /// <summary>State length in seconds (weighted for blend trees).</summary>
    public readonly float Length;

    public readonly float Speed;
    public readonly bool Loop;

    public AnimatorStateInfo(string name, string tag, float normalizedTime, float length, float speed, bool loop)
    {
        IsValid = true;
        Name = name;
        Tag = tag;
        NormalizedTime = normalizedTime;
        Length = length;
        Speed = speed;
        Loop = loop;
    }
}

/// <summary>Payload delivered to <see cref="Animator.OnAnimationEvent"/> and registered handlers.</summary>
public readonly struct AnimationEvent
{
    public readonly Animator Animator;
    public readonly string FunctionName;
    public readonly string StringParameter;
    public readonly float FloatParameter;
    public readonly int IntParameter;

    public AnimationEvent(Animator animator, AnimationEventMarker marker)
    {
        Animator = animator;
        FunctionName = marker.FunctionName;
        StringParameter = marker.StringParameter;
        FloatParameter = marker.FloatParameter;
        IntParameter = marker.IntParameter;
    }
}

/// <summary>
/// Plays a layered, parameter-driven <see cref="AnimatorController"/> on a skeleton, blending the
/// result onto the bone <see cref="Transform"/>s every frame.
///
/// <para><b>What's improved over the classic Unity Animator</b></para>
/// <list type="bullet">
/// <item><b>Allocation-free, string-free hot path.</b> Bones are resolved once, pose buffers are
/// reused, and the controller is compiled at bind time into integer slots
/// (<see cref="CompiledController"/>), so steady-state playback does no per-frame GC work and never
/// hashes a parameter name or scans for a state by name.</item>
/// <item><b>Code-first &amp; data-driven.</b> Controllers can be built entirely in C# and parameters
/// set by cached hash with zero allocation - no asset, no <c>StringToHash</c> boilerplate required.</item>
/// <item><b>Typed events.</b> Animation events are delivered through real delegates
/// (<see cref="OnAnimationEvent"/> / <see cref="RegisterEvent"/>) instead of reflective SendMessage.</item>
/// <item><b>First-class state callbacks.</b> <see cref="OnStateEnter"/> / <see cref="OnStateExit"/>
/// fire without authoring StateMachineBehaviours.</item>
/// <item><b>Direct play mode.</b> <see cref="Play(AnimationClip, bool)"/> /
/// <see cref="CrossFade(AnimationClip, float, bool)"/> work with no controller at all, for the common
/// "just play this clip" case.</item>
/// <item><b>Live re-authoring.</b> Editing the controller while the game runs rebuilds the compiled
/// form on the next frame (see <see cref="AnimatorController.MarkStructureChanged"/>).</item>
/// </list>
/// </summary>
[AddComponentMenu("Animation/Animator")]
[ComponentIcon("")] // person-running
public sealed class Animator : MonoBehaviour
{
    /// <summary>The state machine asset to play. Optional - use direct <see cref="Play(AnimationClip, bool)"/> without one.</summary>
    public AssetRef<AnimatorController> Controller;

    /// <summary>Global playback speed multiplier applied on top of per-state speeds.</summary>
    public float Speed = 1f;

    /// <summary>Scaled vs. unscaled time source.</summary>
    public AnimatorUpdateMode UpdateMode = AnimatorUpdateMode.Normal;

    /// <summary>
    /// When set, the base layer's root bone motion is extracted and applied to this GameObject's
    /// transform instead of moving the bone in place (see <see cref="DeltaPosition"/>).
    /// </summary>
    public bool ApplyRootMotion;

    /// <summary>Bone path (relative to the animation root) treated as the root for root motion.</summary>
    public string RootBonePath = string.Empty;

    // ── Typed delegate events (no reflection) ───────────────────────────────

    /// <summary>Raised when playback crosses an <see cref="AnimationEventMarker"/>.</summary>
    public event Action<AnimationEvent>? OnAnimationEvent;

    /// <summary>Raised when a layer begins entering a state. Args: (layerIndex, stateInfo).</summary>
    public event Action<int, AnimatorStateInfo>? OnStateEnter;

    /// <summary>Raised when a layer finishes leaving a state. Args: (layerIndex, stateInfo).</summary>
    public event Action<int, AnimatorStateInfo>? OnStateExit;

    /// <summary>Root-motion translation produced this frame (parent space). Zero when root motion is off.</summary>
    [NonSerialized] public Float3 DeltaPosition;

    /// <summary>Root-motion rotation produced this frame. Identity when root motion is off.</summary>
    [NonSerialized] public Quaternion DeltaRotation = Quaternion.Identity;

    // ── Runtime state (not serialized) ──────────────────────────────────────
    [NonSerialized] private AnimatorController? _boundController;
    [NonSerialized] private CompiledController? _compiled;
    [NonSerialized] private readonly AnimatorSkeleton _skeleton = new();
    [NonSerialized] private readonly Dictionary<AnimationClip, ClipBinding> _clipBindings = new();
    [NonSerialized] private readonly AnimationPose _workingPose = new();
    [NonSerialized] private readonly AnimationPose _layerPose = new();
    [NonSerialized] private readonly BlendShapeAccumulator _blendShapes = new();
    [NonSerialized] private readonly List<SampleInstruction> _instructions = new();
    [NonSerialized] private LayerRuntime[] _layers = Array.Empty<LayerRuntime>();

    // Per-layer bone inclusion resolved from the layer's avatar mask; null = every bone.
    [NonSerialized] private bool[]?[] _layerMask = Array.Empty<bool[]?>();
    [NonSerialized] private int _maskResolvedBones = -1;

    // Parameter store - a flat float array addressed by the compiled slots.
    [NonSerialized] private float[] _paramValues = Array.Empty<float>();

    // Typed animation-event handlers registered by name.
    [NonSerialized] private readonly Dictionary<string, Action<AnimationEvent>> _eventHandlers = new(StringComparer.Ordinal);

    [NonSerialized] private bool _bound;
    [NonSerialized] private bool _binding;
    [NonSerialized] private bool _forcedLoadAttempt;

    /// <summary>Incremented once per evaluated frame; blend trees stamp their cached weights with it.</summary>
    [NonSerialized] private int _evalStamp;

    // Root-motion tracking
    [NonSerialized] private int _rootBoneIndex = -1;
    [NonSerialized] private bool _rootValid;
    [NonSerialized] private Float3 _prevRootPos;
    [NonSerialized] private Quaternion _prevRootRot = Quaternion.Identity;

    // Direct (controller-less) playback
    [NonSerialized] private SimplePlayback? _simple;

    private sealed class LayerRuntime
    {
        public int CurrentState = -1;
        public float CurrentNorm;     // growing play head (integer part = loop count)
        public float CurrentPrevNorm;
        public float CurrentLength = 1f;

        public bool InTransition;
        public int NextState = -1;
        public float NextNorm;
        public float NextPrevNorm;
        public float NextLength = 1f;
        public float TransitionElapsed;
        public float TransitionDuration;

        public float Weight;
    }

    private sealed class SimplePlayback
    {
        public AnimationClip? Current;
        public float CurrentNorm, CurrentLength = 1f;
        public bool Loop = true;

        public AnimationClip? Next;
        public float NextNorm, NextLength = 1f;
        public bool NextLoop = true;

        public bool InTransition;
        public float TransitionElapsed, TransitionDuration;
    }

    public override void OnEnable()
    {
        _bound = false;
        _forcedLoadAttempt = false;
    }

    public override void OnDisable() => Unbind();

    public override void Update()
    {
        float dt = (UpdateMode == AnimatorUpdateMode.UnscaledTime ? Time.UnscaledDeltaTime : Time.DeltaTime) * Speed;

        if (_simple != null)
        {
            EnsureSearchRootBound();
            UpdateSimple(dt);
            return;
        }

        var controller = ResolveController();
        if (controller == null) return;

        // Rebind when the asset instance changes, and recompile when the graph itself was edited -
        // that is what makes retuning a controller while the game is playing take effect.
        if (!_bound || _boundController != controller || _compiled!.StructureVersion != controller.StructureVersion)
            Bind(controller);

        if (!_bound) return;

        unchecked { _evalStamp++; }

        DeltaPosition = Float3.Zero;
        DeltaRotation = Quaternion.Identity;

        ResolveMasksIfSkeletonGrew();

        _workingPose.EnsureSize(_skeleton.Count);
        _workingPose.ClearWeights();
        _blendShapes.Clear();

        for (int li = 0; li < _layers.Length; li++)
            EvaluateLayer(li, dt);

        ApplyWorkingPoseToTransforms();
        if (!_blendShapes.IsEmpty) _blendShapes.Apply();
    }

    // ============================================================
    //  Binding
    // ============================================================

    private void Bind(AnimatorController controller)
    {
        // A rebind is not always a fresh start: it also happens when the graph is edited while the
        // game runs, and when the asset is reimported. Carrying the parameter values and each
        // layer's play head across means an edit retunes the machine instead of restarting it.
        var previousParameters = CaptureParameters();
        var previousLayers = CaptureLayerState();

        Unbind();
        _binding = true;
        _boundController = controller;

        // Load every referenced clip up front so the search-root resolution below sees real
        // bone paths. FindSearchRoot reads clips non-blockingly (CollectAnimatedBonePaths uses
        // ResWeak), so on a cold asset cache - a fresh build - it would otherwise find no paths,
        // fall back to the absolute hierarchy root, and fail to resolve bones whose paths are
        // relative to a nested animation root, leaving the skeleton unbound and playback frozen.
        foreach (var layer in controller.Layers)
            foreach (var state in layer.States)
                EnsureMotionClipsLoaded(state.Motion);

        // Resolve the search root the same way the renderers do, tolerating reparenting.
        Transform searchRoot = FindSearchRoot(controller);
        _skeleton.Reset(searchRoot);
        _clipBindings.Clear();

        // Compiling resolves every parameter name, state name and clip reference exactly once. It
        // also prewarms the bindings (and therefore the skeleton) via the callback below, so the pose
        // buffers are the right size on the very first frame instead of growing over the next few.
        _compiled = CompiledController.Compile(controller, GetClipBinding);

        _paramValues = new float[_compiled.ParameterDefaults.Length];
        Array.Copy(_compiled.ParameterDefaults, _paramValues, _paramValues.Length);
        RestoreParameters(previousParameters);

        _layers = new LayerRuntime[_compiled.Layers.Length];
        _layerMask = new bool[]?[_compiled.Layers.Length];
        for (int li = 0; li < _compiled.Layers.Length; li++)
        {
            var layer = _compiled.Layers[li];
            var rt = new LayerRuntime { Weight = li == 0 ? 1f : layer.DefaultWeight };
            EnterState(li, layer, rt, layer.DefaultState, 0f, fireEnter: false);
            _layers[li] = rt;
        }
        RestoreLayerState(previousLayers);
        _maskResolvedBones = -1;
        ResolveMasksIfSkeletonGrew();

        _rootBoneIndex = string.IsNullOrEmpty(RootBonePath) ? -1 : _skeleton.GetOrAdd(RootBonePath);
        _rootValid = false;

        _workingPose.EnsureSize(_skeleton.Count);
        _layerPose.EnsureSize(_skeleton.Count);
        _bound = true;
        _binding = false;
    }

    private void Unbind()
    {
        _bound = false;
        _boundController = null;
        _compiled = null;
        _clipBindings.Clear();
        _layers = Array.Empty<LayerRuntime>();
        _layerMask = Array.Empty<bool[]?>();
        _maskResolvedBones = -1;
        _rootValid = false;
    }

    /// <summary>Snapshot the live parameter values by name, so a recompile can put them back.</summary>
    private Dictionary<string, float>? CaptureParameters()
    {
        if (_compiled == null || _paramValues.Length == 0) return null;

        var parameters = _compiled.Source.Parameters;
        var snapshot = new Dictionary<string, float>(parameters.Count, StringComparer.Ordinal);
        for (int i = 0; i < parameters.Count && i < _paramValues.Length; i++)
            snapshot[parameters[i].Name] = _paramValues[i];
        return snapshot;
    }

    private void RestoreParameters(Dictionary<string, float>? snapshot)
    {
        if (snapshot == null || _compiled == null) return;

        var parameters = _compiled.Source.Parameters;
        for (int i = 0; i < parameters.Count && i < _paramValues.Length; i++)
            if (snapshot.TryGetValue(parameters[i].Name, out float value))
                _paramValues[i] = value;
    }

    /// <summary>What each layer was playing, keyed by layer name so a reordered graph still matches.</summary>
    private Dictionary<string, (string State, float Norm, float Weight)>? CaptureLayerState()
    {
        if (_compiled == null || _layers.Length == 0) return null;

        var snapshot = new Dictionary<string, (string, float, float)>(_layers.Length, StringComparer.Ordinal);
        for (int li = 0; li < _layers.Length && li < _compiled.Layers.Length; li++)
        {
            var rt = _layers[li];
            if (rt.CurrentState < 0) continue;

            var layer = _compiled.Layers[li];
            snapshot[layer.Source.Name] = (layer.States[rt.CurrentState].Name, rt.CurrentNorm, rt.Weight);
        }
        return snapshot;
    }

    private void RestoreLayerState(Dictionary<string, (string State, float Norm, float Weight)>? snapshot)
    {
        if (snapshot == null || _compiled == null) return;

        for (int li = 0; li < _layers.Length; li++)
        {
            var layer = _compiled.Layers[li];
            if (!snapshot.TryGetValue(layer.Source.Name, out var previous)) continue;

            int index = layer.Source.FindStateIndex(previous.State);
            if (index < 0) continue; // the state was deleted - the layer falls back to its default

            var rt = _layers[li];
            rt.CurrentState = index;
            rt.CurrentNorm = previous.Norm;
            rt.CurrentPrevNorm = previous.Norm;
            rt.CurrentLength = MotionLength(layer.States[index]);
            rt.Weight = li == 0 ? 1f : previous.Weight;
        }
    }

    // Force every clip referenced by a motion to load (blocking). Used before search-root
    // resolution so the bone paths are available even on a cold asset cache.
    private static void EnsureMotionClipsLoaded(Motion? motion)
    {
        switch (motion)
        {
            case ClipMotion cm:
                cm.Clip.EnsureLoaded();
                break;

            case BlendTree bt:
                foreach (var child in bt.Children) EnsureMotionClipsLoaded(child.Motion);
                break;
        }
    }

    private ClipBinding? GetClipBinding(AnimationClip? clip)
    {
        if (clip == null) return null;
        if (_clipBindings.TryGetValue(clip, out var binding))
            return binding;

        // Reimporting a clip (every save from the animation window does) replaces the instance, so
        // the old one is dead but is still a key in here - and a dictionary key keeps the disposed
        // EngineObject alive. Only a controller rebind used to clear this, so an editing session
        // grew one leaked entry per save. A miss is the right moment to sweep: it is rare, and it is
        // exactly when an instance has just been replaced.
        SweepDeadClipBindings();

        binding = ClipBinding.Build(clip, _skeleton);
        _clipBindings[clip] = binding;

        // Skeleton may have grown - grow pose buffers to match.
        _workingPose.EnsureSize(_skeleton.Count);
        _layerPose.EnsureSize(_skeleton.Count);
        return binding;
    }

    private void SweepDeadClipBindings()
    {
        if (_clipBindings.Count == 0) return;

        List<AnimationClip>? dead = null;
        foreach (var kv in _clipBindings)
            if (kv.Key.IsNotValid())
                (dead ??= new List<AnimationClip>()).Add(kv.Key);

        if (dead == null) return;
        foreach (var clip in dead) _clipBindings.Remove(clip);
    }

    /// <summary>
    /// Resolve avatar masks against the skeleton. The skeleton can still grow after binding (a clip
    /// that streams in late adds bones), and a bone that appears later must not silently fall outside
    /// every mask, so the masks are rebuilt whenever the bone count moves.
    /// </summary>
    private void ResolveMasksIfSkeletonGrew()
    {
        int count = _skeleton.Count;
        if (_maskResolvedBones == count || _compiled == null) return;
        _maskResolvedBones = count;

        for (int li = 0; li < _compiled.Layers.Length; li++)
        {
            var mask = _compiled.Layers[li].Mask;
            if (mask == null || mask.IncludeEverything) { _layerMask[li] = null; continue; }

            var included = new bool[count];
            for (int b = 0; b < count; b++)
                included[b] = mask.Includes(_skeleton.GetPath(b));
            _layerMask[li] = included;
        }
    }

    private Transform FindSearchRoot(AnimatorController controller)
    {
        Transform absoluteRoot = Transform;
        while (absoluteRoot.Parent != null) absoluteRoot = absoluteRoot.Parent;

        // Find a sample bone path to validate candidate roots against.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        controller.CollectAnimatedBonePaths(paths);
        string? sample = null;
        foreach (var p in paths) { sample = p; break; }
        if (string.IsNullOrEmpty(sample)) return absoluteRoot;

        var ancestors = new List<Transform>();
        Transform? current = Transform;
        while (current != null) { ancestors.Add(current); current = current.Parent; }
        for (int i = ancestors.Count - 1; i >= 0; i--)
            if (ancestors[i].Find(sample) != null)
                return ancestors[i];

        return absoluteRoot;
    }

    private void EnsureSearchRootBound()
    {
        if (_skeleton.SearchRoot != null) return;
        Transform absoluteRoot = Transform;
        while (absoluteRoot.Parent != null) absoluteRoot = absoluteRoot.Parent;
        _skeleton.Reset(absoluteRoot);
    }

    // ============================================================
    //  Per-layer evaluation
    // ============================================================

    private void EvaluateLayer(int layerIndex, float dt)
    {
        var layer = _compiled!.Layers[layerIndex];
        var rt = _layers[layerIndex];
        if (rt.CurrentState < 0) return;

        // 1) Advance the current (and transitioning) state clocks.
        var curState = layer.States[rt.CurrentState];
        rt.CurrentLength = MotionLength(curState);
        AdvanceState(rt, isNext: false, dt, StateSpeed(curState), curState.Loop);
        FireEvents(layerIndex, curState, rt.CurrentPrevNorm, rt.CurrentNorm);

        if (rt.InTransition)
        {
            var nextState = layer.States[rt.NextState];
            rt.NextLength = MotionLength(nextState);
            AdvanceState(rt, isNext: true, dt, StateSpeed(nextState), nextState.Loop);
            FireEvents(layerIndex, nextState, rt.NextPrevNorm, rt.NextNorm);

            rt.TransitionElapsed += dt;
            if (rt.TransitionElapsed >= rt.TransitionDuration)
                CompleteTransition(layerIndex, layer, rt);
        }

        // 2) Resolve transitions (any-state first, then the active state's own).
        if (!rt.InTransition)
            CheckTransitions(layerIndex, layer, rt);

        // 3) Build sampling instructions and fold them into the layer pose.
        _layerPose.EnsureSize(_skeleton.Count);
        _layerPose.ClearWeights();
        _instructions.Clear();

        float blend = rt.InTransition
            ? Maths.Saturate(rt.TransitionElapsed / MathF.Max(rt.TransitionDuration, 1e-5f))
            : 0f;

        ExpandState(layer.States[rt.CurrentState], rt.CurrentNorm, 1f - blend);
        if (rt.InTransition)
            ExpandState(layer.States[rt.NextState], rt.NextNorm, blend);

        for (int i = 0; i < _instructions.Count; i++)
        {
            var ins = _instructions[i];
            ClipSampler.Sample(ins.Clip, ins.Binding, _skeleton, ins.Time, ins.Weight, _layerPose, _blendShapes);
        }

        // 4) Merge the layer pose into the working pose (mask + blend mode + layer weight).
        float lw = layerIndex == 0 ? 1f : Maths.Saturate(rt.Weight);
        if (lw > 0f)
            MergeLayer(layerIndex, layer.BlendMode, lw);

        // 5) Root motion (base layer only).
        if (layerIndex == 0 && ApplyRootMotion && _rootBoneIndex >= 0)
            ExtractRootMotion();
    }

    private float MotionLength(CompiledState state)
        => state.Motion?.ComputeLength(_paramValues, _evalStamp) ?? 1f;

    private void ExpandState(CompiledState state, float normalizedTime, float weight)
    {
        if (weight <= 0f) return;
        state.Motion?.Expand(_paramValues, _evalStamp, normalizedTime, state.Loop, weight, _instructions);
    }

    private float StateSpeed(CompiledState state)
    {
        float s = state.Speed;
        if (state.SpeedParameter >= 0) s *= _paramValues[state.SpeedParameter];
        return s;
    }

    private static void AdvanceState(LayerRuntime rt, bool isNext, float dt, float speed, bool loop)
    {
        ref float norm = ref (isNext ? ref rt.NextNorm : ref rt.CurrentNorm);
        ref float prev = ref (isNext ? ref rt.NextPrevNorm : ref rt.CurrentPrevNorm);
        float length = isNext ? rt.NextLength : rt.CurrentLength;

        prev = norm;
        if (length > 1e-6f)
            norm += dt * speed / length;
        if (!loop && norm > 1f) norm = 1f;
    }

    // ── Transition resolution ───────────────────────────────────────────────

    private void CheckTransitions(int layerIndex, CompiledLayer layer, LayerRuntime rt)
    {
        // Any-state transitions take precedence and may interrupt the current state.
        var any = layer.AnyTransitions;
        for (int i = 0; i < any.Length; i++)
        {
            var t = any[i];
            if (!CanTake(t, rt)) continue;
            if (TransitionReady(t, rt.CurrentPrevNorm, rt.CurrentNorm, layer.States[rt.CurrentState].Loop))
            {
                StartTransition(layerIndex, layer, rt, t);
                return;
            }
        }

        var own = layer.States[rt.CurrentState].Transitions;
        bool loop = layer.States[rt.CurrentState].Loop;
        for (int i = 0; i < own.Length; i++)
        {
            var t = own[i];
            if (!CanTake(t, rt)) continue;
            if (TransitionReady(t, rt.CurrentPrevNorm, rt.CurrentNorm, loop))
            {
                StartTransition(layerIndex, layer, rt, t);
                return;
            }
        }
    }

    private static bool CanTake(CompiledTransition t, LayerRuntime rt)
    {
        if (t.Muted || t.Destination < 0) return false;
        if (t.Destination == rt.CurrentState && !t.CanTransitionToSelf) return false;
        return true;
    }

    private bool TransitionReady(CompiledTransition t, float prevNorm, float curNorm, bool loop)
    {
        var conditions = t.Conditions;
        for (int i = 0; i < conditions.Length; i++)
            if (!conditions[i].IsMet(_paramValues)) return false;

        if (t.HasExitTime && !ExitTimeReached(t.ExitTime, prevNorm, curNorm, loop))
            return false;

        return true;
    }

    /// <summary>
    /// Whether a state has played far enough for an exit-time transition to be allowed to start.
    ///
    /// <para>An exit time below 1 opens a window - the tail of every loop past that phase - which
    /// gives the transition's conditions a few frames to come true. An exit time of exactly 1 is the
    /// end of the loop, which is an <i>instant</i>, not a window: testing whether the play head is
    /// sitting on it (as this did, against a 0.999999 epsilon) meant a 1-second loop at 60fps
    /// advancing 0.0167 per frame stepped over it every single time, and the default exit time is 1.
    /// It is a crossing test instead.</para>
    /// </summary>
    private static bool ExitTimeReached(float exitTime, float prevNorm, float curNorm, bool loop)
    {
        if (!loop) return curNorm >= exitTime;
        if (exitTime > 1f) return curNorm >= exitTime;  // fire after N full loops
        if (exitTime < 1f) return AnimMath.Frac(curNorm) >= exitTime;

        return MathF.Floor(curNorm) > MathF.Floor(prevNorm);
    }

    private void StartTransition(int layerIndex, CompiledLayer layer, LayerRuntime rt, CompiledTransition t)
    {
        // Consume any triggers this transition tested.
        var conditions = t.Conditions;
        for (int i = 0; i < conditions.Length; i++)
            if (conditions[i].IsTrigger) _paramValues[conditions[i].Parameter] = 0f;

        var destState = layer.States[t.Destination];
        rt.InTransition = true;
        rt.NextState = t.Destination;
        rt.NextNorm = t.Offset + destState.CycleOffset;
        rt.NextPrevNorm = rt.NextNorm;
        rt.NextLength = MotionLength(destState);
        rt.TransitionElapsed = 0f;
        rt.TransitionDuration = t.FixedDuration
            ? MathF.Max(t.Duration, 0f)
            : MathF.Max(t.Duration * rt.CurrentLength, 0f);

        OnStateEnter?.Invoke(layerIndex, MakeStateInfo(destState, rt.NextNorm, rt.NextLength));

        if (rt.TransitionDuration <= 0f)
            CompleteTransition(layerIndex, layer, rt);
    }

    private void CompleteTransition(int layerIndex, CompiledLayer layer, LayerRuntime rt)
    {
        var oldState = layer.States[rt.CurrentState];
        OnStateExit?.Invoke(layerIndex, MakeStateInfo(oldState, rt.CurrentNorm, rt.CurrentLength));

        rt.CurrentState = rt.NextState;
        rt.CurrentNorm = rt.NextNorm;
        rt.CurrentPrevNorm = rt.NextNorm;
        rt.CurrentLength = rt.NextLength;
        rt.InTransition = false;
        rt.NextState = -1;
    }

    private void EnterState(int layerIndex, CompiledLayer layer, LayerRuntime rt, int stateIndex,
        float normalizedTime, bool fireEnter)
    {
        rt.InTransition = false;
        rt.NextState = -1;
        rt.CurrentState = stateIndex;
        if (stateIndex < 0) return;

        var state = layer.States[stateIndex];
        rt.CurrentNorm = normalizedTime + state.CycleOffset;
        rt.CurrentPrevNorm = rt.CurrentNorm;
        rt.CurrentLength = MotionLength(state);
        if (fireEnter)
            OnStateEnter?.Invoke(layerIndex, MakeStateInfo(state, rt.CurrentNorm, rt.CurrentLength));
    }

    // ============================================================
    //  Pose merge + apply
    // ============================================================

    private void MergeLayer(int layerIndex, AnimatorLayerBlendMode mode, float layerWeight)
    {
        bool[]? mask = layerIndex < _layerMask.Length ? _layerMask[layerIndex] : null;
        int count = _skeleton.Count;

        for (int idx = 0; idx < count; idx++)
        {
            if (_layerPose.Weight[idx] <= 0f) continue;
            if (mask != null && (idx >= mask.Length || !mask[idx])) continue;

            float w = layerWeight;

            // Ensure the working pose has a base value for this bone (reference pose).
            if (_workingPose.Weight[idx] <= 0f)
            {
                _workingPose.Position[idx] = _skeleton.RefPosition(idx);
                _workingPose.Rotation[idx] = _skeleton.RefRotation(idx);
                _workingPose.Scale[idx] = _skeleton.RefScale(idx);
                _workingPose.Weight[idx] = 1f;
            }

            if (mode == AnimatorLayerBlendMode.Override)
            {
                _workingPose.Position[idx] = AnimMath.Lerp(_workingPose.Position[idx], _layerPose.Position[idx], w);
                _workingPose.Rotation[idx] = AnimMath.Slerp(_workingPose.Rotation[idx], _layerPose.Rotation[idx], w);
                _workingPose.Scale[idx] = AnimMath.Lerp(_workingPose.Scale[idx], _layerPose.Scale[idx], w);
            }
            else // Additive - relative to the reference pose
            {
                Float3 refPos = _skeleton.RefPosition(idx);
                Quaternion refRot = _skeleton.RefRotation(idx);
                Float3 refScl = _skeleton.RefScale(idx);

                _workingPose.Position[idx] += (_layerPose.Position[idx] - refPos) * w;
                Quaternion delta = _layerPose.Rotation[idx] * Quaternion.Inverse(refRot);
                Quaternion added = delta * _workingPose.Rotation[idx];
                _workingPose.Rotation[idx] = AnimMath.Slerp(_workingPose.Rotation[idx], added, w);
                _workingPose.Scale[idx] += (_layerPose.Scale[idx] - refScl) * w;
            }
        }
    }

    private void ApplyWorkingPoseToTransforms()
    {
        int count = _skeleton.Count;
        for (int idx = 0; idx < count; idx++)
        {
            if (_workingPose.Weight[idx] <= 0f) continue;
            Transform? bone = _skeleton.GetBone(idx);
            if (bone == null) continue;

            // One write instead of three: each individual local setter marks the transform (and its
            // whole subtree) dirty, so writing position, rotation and scale separately invalidated
            // every bone's world matrix three times per frame.
            bone.SetLocalTransform(
                _workingPose.Position[idx],
                Quaternion.NormalizeSafe(_workingPose.Rotation[idx]),
                _workingPose.Scale[idx]);
        }
    }

    // ============================================================
    //  Root motion
    // ============================================================

    private void ExtractRootMotion()
    {
        int idx = _rootBoneIndex;
        if (idx < 0 || idx >= _skeleton.Count || _workingPose.Weight[idx] <= 0f) return;

        Float3 curPos = _workingPose.Position[idx];
        Quaternion curRot = _workingPose.Rotation[idx];

        if (_rootValid)
        {
            Float3 deltaPos = curPos - _prevRootPos;
            Quaternion deltaRot = curRot * Quaternion.Inverse(_prevRootRot);

            // Guard against the discontinuity when a looped clip wraps back to the start.
            if (deltaPos.X * deltaPos.X + deltaPos.Y * deltaPos.Y + deltaPos.Z * deltaPos.Z < 100f)
            {
                DeltaPosition = Transform.LocalRotation * deltaPos;
                DeltaRotation = deltaRot;
                Transform.LocalPosition += DeltaPosition;
                Transform.LocalRotation = Quaternion.NormalizeSafe(Transform.LocalRotation * deltaRot);
            }
        }

        _prevRootPos = curPos;
        _prevRootRot = curRot;
        _rootValid = true;

        // Pin the root bone to its reference so its motion isn't double-applied.
        _workingPose.Position[idx] = _skeleton.RefPosition(idx);
        _workingPose.Rotation[idx] = _skeleton.RefRotation(idx);
    }

    // ============================================================
    //  Animation events
    // ============================================================

    private void FireEvents(int layerIndex, CompiledState state, float prevNorm, float curNorm)
    {
        var events = state.Events;
        if (events.Length == 0) return;
        if (OnAnimationEvent == null && _eventHandlers.Count == 0) return;

        bool loop = state.Loop;
        for (int i = 0; i < events.Length; i++)
        {
            var marker = events[i];
            if (CrossedPhase(prevNorm, curNorm, marker.NormalizedTime, loop))
            {
                var evt = new AnimationEvent(this, marker);
                OnAnimationEvent?.Invoke(evt);
                if (_eventHandlers.TryGetValue(marker.FunctionName, out var handler))
                    handler(evt);
            }
        }
    }

    private static bool CrossedPhase(float prev, float cur, float phase, bool loop)
    {
        if (cur <= prev) return false;
        if (!loop) return prev < phase && cur >= phase;
        return MathF.Floor(cur - phase) > MathF.Floor(prev - phase);
    }

    /// <summary>Register a delegate to receive a named animation event. Replaces any prior handler.</summary>
    public void RegisterEvent(string functionName, Action<AnimationEvent> handler)
        => _eventHandlers[functionName] = handler;

    /// <summary>Remove a previously registered animation-event handler.</summary>
    public void UnregisterEvent(string functionName) => _eventHandlers.Remove(functionName);

    // ============================================================
    //  Parameters
    // ============================================================

    /// <summary>FNV-1a hash matching how the Animator keys its parameters. Cache this for zero-alloc sets.</summary>
    public static int StringToHash(string name)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            for (int i = 0; i < name.Length; i++)
            {
                hash ^= name[i];
                hash *= prime;
            }
            return (int)hash;
        }
    }

    // Binding normally happens on the first Update. Parameter access forces it early so values set
    // before the first frame (a very common pattern) are preserved instead of being wiped by the
    // deferred bind's reset-to-defaults. The guard stops Bind's own parameter reads from re-entering.
    private void EnsureBound()
    {
        if (_bound || _binding) return;
        var c = ResolveController();
        if (c != null) Bind(c);
    }

    /// <summary>
    /// Get the controller, dealing with a cold asset cache. <see cref="AssetRef{T}.Res"/> is
    /// non-blocking and the background loader can't import an asset that has never been cached, so on
    /// the very first play after load it can return null for the entire session (the controller then
    /// appears "for free" on later plays only because the cache stayed warm). On the first miss we do
    /// one deterministic resolve: a prioritized <see cref="AssetRef{T}.EnsureLoaded"/> (reads the disk
    /// cache), falling back to a main-thread <see cref="AssetDatabase.Get"/> which imports on demand.
    /// After that single attempt we go back to the cheap non-blocking path.
    /// </summary>
    private AnimatorController? ResolveController()
    {
        var c = Controller.Res;
        if (c != null || _forcedLoadAttempt || Controller.AssetID == Guid.Empty)
            return c;

        _forcedLoadAttempt = true;
        Controller.EnsureLoaded();
        c = Controller.Res;
        if (c == null)
        {
            c = AssetDatabase.Get(Controller.AssetID) as AnimatorController;
            if (c != null) Controller.Res = c;
        }
        return c;
    }

    private int IndexOfHash(int hash)
    {
        EnsureBound();
        return _compiled != null && _compiled.HashToIndex.TryGetValue(hash, out int i) ? i : -1;
    }

    private int IndexOfName(string name)
    {
        EnsureBound();
        return _compiled?.IndexOfName(name) ?? -1;
    }

    public void SetFloat(string name, float value) => SetValue(IndexOfName(name), value);
    public void SetFloat(int hash, float value) => SetValue(IndexOfHash(hash), value);
    public void SetInt(string name, int value) => SetValue(IndexOfName(name), value);
    public void SetInt(int hash, int value) => SetValue(IndexOfHash(hash), value);
    public void SetBool(string name, bool value) => SetValue(IndexOfName(name), value ? 1f : 0f);
    public void SetBool(int hash, bool value) => SetValue(IndexOfHash(hash), value ? 1f : 0f);
    public void SetTrigger(string name) => SetValue(IndexOfName(name), 1f);
    public void SetTrigger(int hash) => SetValue(IndexOfHash(hash), 1f);
    public void ResetTrigger(string name) => SetValue(IndexOfName(name), 0f);
    public void ResetTrigger(int hash) => SetValue(IndexOfHash(hash), 0f);

    public float GetFloat(string name) => GetValue(IndexOfName(name));
    public float GetFloat(int hash) => GetValue(IndexOfHash(hash));
    public int GetInt(string name) => (int)MathF.Round(GetValue(IndexOfName(name)));
    public int GetInt(int hash) => (int)MathF.Round(GetValue(IndexOfHash(hash)));
    public bool GetBool(string name) => GetValue(IndexOfName(name)) != 0f;
    public bool GetBool(int hash) => GetValue(IndexOfHash(hash)) != 0f;

    private void SetValue(int index, float value)
    {
        if (index >= 0) _paramValues[index] = value;
    }

    private float GetValue(int index) => index >= 0 ? _paramValues[index] : 0f;

    // ============================================================
    //  Playback control
    // ============================================================

    /// <summary>Immediately play a state by name on a layer.</summary>
    public void Play(string stateName, int layer = 0, float normalizedTime = 0f)
    {
        EnsureBound();
        if (!_bound || layer < 0 || layer >= _layers.Length) return;

        var cl = _compiled!.Layers[layer];
        int idx = cl.Source.FindStateIndex(stateName);
        if (idx < 0) { Debug.LogWarning($"[Animator] State '{stateName}' not found on layer {layer}."); return; }

        EnterState(layer, cl, _layers[layer], idx, normalizedTime, fireEnter: true);
    }

    /// <summary>Cross-fade to a state by name over <paramref name="duration"/> seconds.</summary>
    public void CrossFade(string stateName, float duration, int layer = 0, float normalizedTime = 0f)
    {
        EnsureBound();
        if (!_bound || layer < 0 || layer >= _layers.Length) return;

        var cl = _compiled!.Layers[layer];
        int dest = cl.Source.FindStateIndex(stateName);
        if (dest < 0) { Debug.LogWarning($"[Animator] State '{stateName}' not found on layer {layer}."); return; }

        var rt = _layers[layer];
        var destState = cl.States[dest];
        rt.InTransition = true;
        rt.NextState = dest;
        rt.NextNorm = normalizedTime + destState.CycleOffset;
        rt.NextPrevNorm = rt.NextNorm;
        rt.NextLength = MotionLength(destState);
        rt.TransitionElapsed = 0f;
        rt.TransitionDuration = MathF.Max(duration, 0f);

        OnStateEnter?.Invoke(layer, MakeStateInfo(destState, rt.NextNorm, rt.NextLength));
        if (rt.TransitionDuration <= 0f)
            CompleteTransition(layer, cl, rt);
    }

    /// <summary>Set a non-base layer's blend weight (0..1).</summary>
    public void SetLayerWeight(int layer, float weight)
    {
        if (layer > 0 && layer < _layers.Length) _layers[layer].Weight = Maths.Saturate(weight);
    }

    /// <summary>Get a layer's blend weight.</summary>
    public float GetLayerWeight(int layer)
    {
        if (layer < 0 || layer >= _layers.Length) return 0f;
        return layer == 0 ? 1f : _layers[layer].Weight;
    }

    /// <summary>Snapshot of what a layer is currently playing (the source state during a transition).</summary>
    public AnimatorStateInfo GetCurrentStateInfo(int layer = 0)
    {
        if (!_bound || layer < 0 || layer >= _layers.Length) return default;
        var rt = _layers[layer];
        if (rt.CurrentState < 0) return default;
        return MakeStateInfo(_compiled!.Layers[layer].States[rt.CurrentState], rt.CurrentNorm, rt.CurrentLength);
    }

    /// <summary>Snapshot of the state a layer is blending toward, or an invalid info when not transitioning.</summary>
    public AnimatorStateInfo GetNextStateInfo(int layer = 0)
    {
        if (!_bound || layer < 0 || layer >= _layers.Length) return default;
        var rt = _layers[layer];
        if (!rt.InTransition || rt.NextState < 0) return default;
        return MakeStateInfo(_compiled!.Layers[layer].States[rt.NextState], rt.NextNorm, rt.NextLength);
    }

    /// <summary>True if a layer is currently blending between two states.</summary>
    public bool IsInTransition(int layer = 0)
        => _bound && layer >= 0 && layer < _layers.Length && _layers[layer].InTransition;

    /// <summary>How far through the current cross-fade a layer is, 0..1 (0 when not transitioning).</summary>
    public float GetTransitionProgress(int layer = 0)
    {
        if (!IsInTransition(layer)) return 0f;
        var rt = _layers[layer];
        return Maths.Saturate(rt.TransitionElapsed / MathF.Max(rt.TransitionDuration, 1e-5f));
    }

    private static AnimatorStateInfo MakeStateInfo(CompiledState state, float norm, float length)
        => new(state.Name, state.Tag, norm, length, state.Speed, state.Loop);

    // ============================================================
    //  Introspection (used by the graph editor's live view)
    // ============================================================

    /// <summary>The controller instance currently compiled and playing, or null.</summary>
    public AnimatorController? BoundController => _bound ? _boundController : null;

    /// <summary>Number of layers currently bound.</summary>
    public int LayerCount => _layers.Length;

    /// <summary>Name of the state a layer is playing, or null. Allocation-free.</summary>
    public string? GetCurrentStateName(int layer)
    {
        if (!_bound || layer < 0 || layer >= _layers.Length) return null;
        int idx = _layers[layer].CurrentState;
        return idx >= 0 ? _compiled!.Layers[layer].States[idx].Name : null;
    }

    /// <summary>Name of the state a layer is blending toward, or null.</summary>
    public string? GetNextStateName(int layer)
    {
        if (!_bound || layer < 0 || layer >= _layers.Length) return null;
        var rt = _layers[layer];
        return rt.InTransition && rt.NextState >= 0 ? _compiled!.Layers[layer].States[rt.NextState].Name : null;
    }

    /// <summary>Live parameter values, in declaration order. For inspectors and debug overlays.</summary>
    public IEnumerable<(string Name, AnimatorControllerParameterType Type, float Value)> EnumerateParameters()
    {
        if (!_bound || _compiled == null) yield break;
        var parameters = _compiled.Source.Parameters;
        for (int i = 0; i < parameters.Count && i < _paramValues.Length; i++)
            yield return (parameters[i].Name, parameters[i].Type, _paramValues[i]);
    }

    // ============================================================
    //  Direct (controller-less) playback
    // ============================================================

    /// <summary>Play a single clip directly, no controller required. Replaces any current direct playback.</summary>
    public void Play(AnimationClip clip, bool loop = true)
    {
        _simple ??= new SimplePlayback();
        EnsureSearchRootBound();
        _simple.Current = clip;
        _simple.CurrentNorm = 0f;
        _simple.CurrentLength = clip.Duration > 0f ? clip.Duration : 1f;
        _simple.Loop = loop;
        _simple.InTransition = false;
        _simple.Next = null;
    }

    /// <summary>Cross-fade to a clip directly, no controller required.</summary>
    public void CrossFade(AnimationClip clip, float duration, bool loop = true)
    {
        if (_simple == null || _simple.Current == null) { Play(clip, loop); return; }
        EnsureSearchRootBound();
        _simple.Next = clip;
        _simple.NextNorm = 0f;
        _simple.NextLength = clip.Duration > 0f ? clip.Duration : 1f;
        _simple.NextLoop = loop;
        _simple.InTransition = true;
        _simple.TransitionElapsed = 0f;
        _simple.TransitionDuration = MathF.Max(duration, 0f);
    }

    /// <summary>Stop direct playback and hand control back to the controller (if one is assigned).</summary>
    public void StopDirectPlayback()
    {
        _simple = null;
        _bound = false;
    }

    private void UpdateSimple(float dt)
    {
        var s = _simple!;
        if (s.Current == null) return;

        _workingPose.EnsureSize(_skeleton.Count);
        _workingPose.ClearWeights();
        _blendShapes.Clear();
        _layerPose.EnsureSize(_skeleton.Count);
        _layerPose.ClearWeights();
        _instructions.Clear();

        AdvanceSimple(ref s.CurrentNorm, s.CurrentLength, dt, s.Loop);

        float blend = 0f;
        if (s.InTransition && s.Next != null)
        {
            AdvanceSimple(ref s.NextNorm, s.NextLength, dt, s.NextLoop);
            s.TransitionElapsed += dt;
            blend = Maths.Saturate(s.TransitionElapsed / MathF.Max(s.TransitionDuration, 1e-5f));
            if (blend >= 1f)
            {
                s.Current = s.Next;
                s.CurrentNorm = s.NextNorm;
                s.CurrentLength = s.NextLength;
                s.Loop = s.NextLoop;
                s.Next = null;
                s.InTransition = false;
                blend = 0f;
            }
        }

        AddSimpleInstruction(s.Current, s.CurrentNorm, s.CurrentLength, s.Loop, 1f - blend);
        if (s.InTransition && s.Next != null)
            AddSimpleInstruction(s.Next, s.NextNorm, s.NextLength, s.NextLoop, blend);

        for (int i = 0; i < _instructions.Count; i++)
        {
            var ins = _instructions[i];
            ClipSampler.Sample(ins.Clip, ins.Binding, _skeleton, ins.Time, ins.Weight, _layerPose, _blendShapes);
        }

        // Direct mode is a single override layer at full weight.
        int count = _skeleton.Count;
        for (int idx = 0; idx < count; idx++)
        {
            if (_layerPose.Weight[idx] <= 0f) continue;
            _workingPose.Position[idx] = _layerPose.Position[idx];
            _workingPose.Rotation[idx] = _layerPose.Rotation[idx];
            _workingPose.Scale[idx] = _layerPose.Scale[idx];
            _workingPose.Weight[idx] = 1f;
        }

        ApplyWorkingPoseToTransforms();
        if (!_blendShapes.IsEmpty) _blendShapes.Apply();
    }

    private void AddSimpleInstruction(AnimationClip? clip, float norm, float length, bool loop, float weight)
    {
        if (clip == null || weight <= 0f) return;
        var binding = GetClipBinding(clip);
        if (binding == null) return;

        _workingPose.EnsureSize(_skeleton.Count);
        _layerPose.EnsureSize(_skeleton.Count);
        float phase = loop ? AnimMath.Frac(norm) : Maths.Saturate(norm);
        _instructions.Add(new SampleInstruction
        {
            Clip = clip,
            Binding = binding,
            Time = clip.StartTime + phase * length,
            Weight = weight,
        });
    }

    private static void AdvanceSimple(ref float norm, float length, float dt, bool loop)
    {
        if (length > 1e-6f) norm += dt / length;
        if (!loop && norm > 1f) norm = 1f;
    }
}
