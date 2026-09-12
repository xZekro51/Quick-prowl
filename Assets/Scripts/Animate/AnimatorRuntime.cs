// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Animate;

/// <summary>
/// A working set of local bone transforms keyed by a stable skeleton index. The Animator keeps a
/// couple of these around and reuses them every frame so steady-state playback allocates nothing.
/// <see cref="Weight"/> doubles as a coverage/"was-written" accumulator used by the incremental
/// weighted blend.
/// </summary>
internal sealed class AnimationPose
{
    public Float3[] Position = Array.Empty<Float3>();
    public Quaternion[] Rotation = Array.Empty<Quaternion>();
    public Float3[] Scale = Array.Empty<Float3>();
    public float[] Weight = Array.Empty<float>();
    public int Count;

    public void EnsureSize(int count)
    {
        Count = count;
        if (Position.Length >= count) return;

        int cap = Math.Max(count, Math.Max(8, Position.Length * 2));
        Array.Resize(ref Position, cap);
        Array.Resize(ref Rotation, cap);
        Array.Resize(ref Scale, cap);
        Array.Resize(ref Weight, cap);
    }

    /// <summary>Reset the coverage weights (values are left as-is - the blend overwrites them).</summary>
    public void ClearWeights()
    {
        if (Count > 0) Array.Clear(Weight, 0, Count);
    }

    /// <summary>
    /// Fold one sampled bone transform into the pose at <paramref name="index"/> using an
    /// order-independent incremental weighted blend. The first contribution wins outright; each
    /// subsequent one nudges the running value toward itself by its share of the total weight.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Accumulate(int index, in Float3 pos, in Quaternion rot, in Float3 scl, float weight)
    {
        if (weight <= 0f) return;

        float w0 = Weight[index];
        Weight[index] = w0 + weight;

        // First contribution: nothing to blend against, so take it whole. This is the overwhelmingly
        // common case (one clip per bone per layer) and skips a Lerp/Slerp triple entirely.
        if (w0 <= 0f)
        {
            Position[index] = pos;
            Rotation[index] = rot;
            Scale[index] = scl;
            return;
        }

        float t = weight / (w0 + weight);
        Position[index] = AnimMath.Lerp(Position[index], pos, t);
        Rotation[index] = AnimMath.Slerp(Rotation[index], rot, t);
        Scale[index] = AnimMath.Lerp(Scale[index], scl, t);
    }
}

/// <summary>
/// Resolves the bones a controller's clips animate to live <see cref="Transform"/>s exactly once,
/// and records their bind-pose local transforms (used as the reference for additive layers and as
/// the base for partially-weighted overrides). Bones are addressed by a dense integer index so the
/// per-frame hot path never touches a string.
/// </summary>
internal sealed class AnimatorSkeleton
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<Transform?> _bones = new();
    private readonly List<string> _paths = new();

    private readonly List<Float3> _refPos = new();
    private readonly List<Quaternion> _refRot = new();
    private readonly List<Float3> _refScl = new();

    public Transform? SearchRoot { get; private set; }

    public int Count => _bones.Count;
    public Transform? GetBone(int i) => _bones[i];
    public string GetPath(int i) => _paths[i];

    public Float3 RefPosition(int i) => _refPos[i];
    public Quaternion RefRotation(int i) => _refRot[i];
    public Float3 RefScale(int i) => _refScl[i];

    /// <summary>Discard all bindings. Call when the component re-enables or the controller changes.</summary>
    public void Reset(Transform? searchRoot)
    {
        SearchRoot = searchRoot;
        _index.Clear();
        _bones.Clear();
        _paths.Clear();
        _refPos.Clear();
        _refRot.Clear();
        _refScl.Clear();
    }

    /// <summary>
    /// Return the dense index for a bone path, resolving and caching the transform (and capturing
    /// its bind pose) on first request. Unresolved paths still get an index so we don't re-search
    /// them every frame; their transform is simply null and gets skipped.
    /// </summary>
    public int GetOrAdd(string path)
    {
        if (_index.TryGetValue(path, out int existing))
            return existing;

        Transform? bone = SearchRoot != null ? SearchRoot.Find(path) : null;

        int idx = _bones.Count;
        _index[path] = idx;
        _paths.Add(path);
        _bones.Add(bone);

        if (bone != null)
        {
            _refPos.Add(bone.LocalPosition);
            _refRot.Add(bone.LocalRotation);
            _refScl.Add(bone.LocalScale);
        }
        else
        {
            _refPos.Add(Float3.Zero);
            _refRot.Add(Quaternion.Identity);
            _refScl.Add(Float3.One);
        }
        return idx;
    }

    /// <summary>True if a path is in this skeleton's bound set.</summary>
    public bool TryGetIndex(string path, out int index) => _index.TryGetValue(path, out index);
}

/// <summary>
/// Per-clip cached resolution: which skeleton index each of the clip's bones maps to, and which
/// renderer/shape each blend-shape track drives. Built once, reused every frame.
/// </summary>
internal sealed class ClipBinding
{
    public int[] BoneIndex = Array.Empty<int>();

    // Blend-shape track resolution (parallel to clip.BlendShapes).
    public SkinnedMeshRenderer?[] BsRenderer = Array.Empty<SkinnedMeshRenderer?>();
    public int[] BsShapeIndex = Array.Empty<int>();

    /// <summary>True when the clip drives no blend shapes at all - lets the sampler skip that pass.</summary>
    public bool HasBlendShapes;

    public static ClipBinding Build(AnimationClip clip, AnimatorSkeleton skeleton)
    {
        var binding = new ClipBinding();

        int boneCount = clip.Bones.Count;
        binding.BoneIndex = new int[boneCount];
        for (int i = 0; i < boneCount; i++)
            binding.BoneIndex[i] = skeleton.GetOrAdd(clip.Bones[i].BoneName);

        int bsCount = clip.BlendShapes.Count;
        binding.BsRenderer = new SkinnedMeshRenderer?[bsCount];
        binding.BsShapeIndex = new int[bsCount];
        for (int i = 0; i < bsCount; i++)
        {
            var track = clip.BlendShapes[i];
            Transform? t = string.IsNullOrEmpty(track.Path)
                ? skeleton.SearchRoot
                : skeleton.SearchRoot?.Find(track.Path);
            var smr = t != null ? t.GameObject.GetComponent<SkinnedMeshRenderer>() : null;
            if (smr.IsNotValid()) smr = null;

            int shapeIndex = smr != null ? smr.GetBlendShapeIndex(track.ShapeName) : -1;
            binding.BsRenderer[i] = smr;
            binding.BsShapeIndex[i] = shapeIndex;
            if (shapeIndex >= 0) binding.HasBlendShapes = true;
        }

        return binding;
    }
}

/// <summary>Accumulates weighted blend-shape contributions across all active samples in a frame.</summary>
internal sealed class BlendShapeAccumulator
{
    private readonly struct Key : IEquatable<Key>
    {
        public readonly SkinnedMeshRenderer Renderer;
        public readonly int Shape;

        public Key(SkinnedMeshRenderer r, int s) { Renderer = r; Shape = s; }

        public bool Equals(Key o) => ReferenceEquals(Renderer, o.Renderer) && Shape == o.Shape;
        public override bool Equals(object? o) => o is Key k && Equals(k);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(Renderer) * 397 ^ Shape;
    }

    private readonly Dictionary<Key, Float2> _values = new(); // (weighted sum, total weight)

    public bool IsEmpty => _values.Count == 0;

    public void Clear() => _values.Clear();

    public void Add(SkinnedMeshRenderer renderer, int shapeIndex, float value, float weight)
    {
        if (renderer == null || shapeIndex < 0 || weight <= 0f) return;

        // One hash instead of the TryGetValue + indexer pair: this runs per shape per clip per frame.
        ref Float2 slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_values, new Key(renderer, shapeIndex), out _);
        slot = new Float2(slot.X + value * weight, slot.Y + weight);
    }

    public void Apply()
    {
        foreach (var kv in _values)
        {
            float total = kv.Value.Y;
            if (total <= 0f) continue;
            kv.Key.Renderer.SetBlendShapeWeight(kv.Key.Shape, kv.Value.X / total);
        }
    }
}

/// <summary>Samples clips into a pose. Stateless - all per-clip resolution lives in the binding.</summary>
internal static class ClipSampler
{
    /// <summary>
    /// Sample <paramref name="clip"/> at <paramref name="time"/> seconds and fold every animated
    /// bone (and blend shape) into <paramref name="target"/> with the given blend weight.
    ///
    /// <para>A channel the clip does not animate contributes the bone's reference (bind) pose rather
    /// than an identity value. Returning <c>Float3.Zero</c> for the position of a rotation-only clip -
    /// which is what an identity default means - collapses the whole skeleton onto its root the
    /// moment such a clip plays, and rotation-only clips are the common case for a retargeted rig.</para>
    /// </summary>
    public static void Sample(
        AnimationClip clip, ClipBinding binding, AnimatorSkeleton skeleton, float time, float weight,
        AnimationPose target, BlendShapeAccumulator? blendShapes)
    {
        var bones = clip.Bones;
        int[] boneIndex = binding.BoneIndex;

        // The binding is built from the clip, so the arrays agree in length; clamp defensively in
        // case a clip was swapped underneath a cached binding.
        int count = Math.Min(bones.Count, boneIndex.Length);

        for (int i = 0; i < count; i++)
        {
            int idx = boneIndex[i];
            if (idx < 0) continue;

            AnimationClip.AnimBone bone = bones[i];

            // Evaluation is the engine's: one binary search per channel, with a rotation that slerps
            // on linear segments and renormalises on cubic ones. Reimplementing it here (as the old
            // per-axis sampler did) meant component-wise lerping a quaternion and taking the shortest
            // path only by accident.
            Float3 pos = bone.Position is { Count: > 0 }
                ? bone.Position.EvaluateFloat3(time)
                : skeleton.RefPosition(idx);

            // The dimension test is not redundant: EvaluateQuaternion throws on a curve that is not
            // four-component, and a malformed rotation track out of an importer would throw on every
            // bone of every frame rather than simply looking wrong.
            Quaternion rot = bone.Rotation is { Count: > 0, Dimension: 4 }
                ? bone.Rotation.EvaluateQuaternion(time)
                : skeleton.RefRotation(idx);

            Float3 scl = bone.Scale is { Count: > 0 }
                ? bone.Scale.EvaluateFloat3(time)
                : skeleton.RefScale(idx);

            target.Accumulate(idx, pos, rot, scl, weight);
        }

        if (blendShapes != null && binding.HasBlendShapes)
        {
            var tracks = clip.BlendShapes;
            int bsCount = Math.Min(tracks.Count, binding.BsShapeIndex.Length);
            for (int i = 0; i < bsCount; i++)
            {
                var smr = binding.BsRenderer[i];
                int shapeIdx = binding.BsShapeIndex[i];
                if (smr == null || shapeIdx < 0) continue;

                blendShapes.Add(smr, shapeIdx, tracks[i].EvaluateAt(time), weight);
            }
        }
    }
}

/// <summary>Small interpolation helpers used by the blend pipeline.</summary>
internal static class AnimMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Float3 Lerp(in Float3 a, in Float3 b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion Slerp(in Quaternion a, in Quaternion b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        return Quaternion.Slerp(a, b, t);
    }

    /// <summary>Fractional part, always in [0,1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Frac(float v) => v - MathF.Floor(v);
}
