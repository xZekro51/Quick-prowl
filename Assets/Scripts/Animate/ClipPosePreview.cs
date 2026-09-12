// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Animate;

/// <summary>
/// Poses a rig from a single <see cref="AnimationClip"/> at an arbitrary time, and can put the rig
/// back the way it found it.
///
/// <para>This is what the clip editor scrubs with, but it is deliberately public and engine-facing:
/// posing a character to an exact frame is also what you want for cutscene authoring, photo modes,
/// ragdoll hand-off and baking. It reuses the same binding and sampling path the
/// <see cref="Animator"/> uses, so a previewed frame is the frame that will actually play.</para>
///
/// <para><b>Restoring matters.</b> Scrubbing a clip writes to real scene transforms. Bind captures
/// each affected bone's local transform (and blend-shape weight) up front, so <see cref="Restore"/>
/// can undo the preview instead of leaving the scene silently modified - the single most common
/// complaint about previewing animation in-editor.</para>
/// </summary>
public sealed class ClipPosePreview
{
    private readonly AnimatorSkeleton _skeleton = new();
    private readonly AnimationPose _pose = new();
    private readonly BlendShapeAccumulator _blendShapes = new();

    private AnimationClip? _clip;
    private Transform? _root;
    private ClipBinding? _binding;

    private readonly List<(SkinnedMeshRenderer Renderer, int Shape, float Weight)> _savedShapes = new();

    /// <summary>The clip currently bound, or null.</summary>
    public AnimationClip? Clip => _clip;

    /// <summary>The rig root currently bound, or null.</summary>
    public Transform? Root => _root;

    /// <summary>
    /// The transform the clip's bone paths are actually resolved against - <see cref="Root"/>, or the
    /// closest ancestor that contains the clip's first animated bone. Tools that author new tracks
    /// need this: a path is only meaningful relative to the root it was resolved from, so building one
    /// against <see cref="Root"/> when binding fell back to an ancestor produces a track that resolves
    /// to nothing.
    /// </summary>
    public Transform? SearchRoot => _skeleton.SearchRoot;

    /// <summary>Number of bones the bound clip resolved against the rig.</summary>
    public int BoneCount => _skeleton.Count;

    /// <summary>Number of bones the bound clip animates that could <i>not</i> be found on the rig.</summary>
    public int UnresolvedBoneCount
    {
        get
        {
            int missing = 0;
            for (int i = 0; i < _skeleton.Count; i++)
                if (_skeleton.GetBone(i) == null) missing++;
            return missing;
        }
    }

    /// <summary>True when a clip and rig are bound and the rig can be posed.</summary>
    public bool IsBound => _clip.IsValid() && _root != null && _binding != null;

    /// <summary>
    /// Bind a clip to a rig. Re-binding the same pair is a no-op; binding anything else restores the
    /// previous rig first so a preview never leaks onto two skeletons at once.
    /// </summary>
    public void Bind(AnimationClip? clip, Transform? root)
    {
        if (ReferenceEquals(_clip, clip) && ReferenceEquals(_root, root)) return;

        Restore();

        _clip = clip.IsValid() ? clip : null;
        _root = root;
        _binding = null;
        _savedShapes.Clear();

        if (_clip == null || _root == null) return;

        // Resolve against the rig root, or the closest ancestor that actually contains the first
        // animated bone - the same tolerance the Animator applies, so a clip authored against a
        // nested armature still previews when you select the character's root.
        _skeleton.Reset(ResolveSearchRoot(_clip, _root));
        _binding = ClipBinding.Build(_clip, _skeleton);
        _pose.EnsureSize(_skeleton.Count);

        for (int i = 0; i < _binding.BsRenderer.Length; i++)
        {
            var renderer = _binding.BsRenderer[i];
            int shape = _binding.BsShapeIndex[i];
            if (renderer.IsValid() && shape >= 0)
                _savedShapes.Add((renderer!, shape, renderer!.GetBlendShapeWeight(shape)));
        }
    }

    private static Transform ResolveSearchRoot(AnimationClip clip, Transform root)
    {
        if (clip.Bones.Count == 0) return root;
        string sample = clip.Bones[0].BoneName;
        if (string.IsNullOrEmpty(sample)) return root;

        for (Transform? candidate = root; candidate != null; candidate = candidate.Parent)
            if (candidate.Find(sample) != null)
                return candidate;

        return root;
    }

    /// <summary>
    /// Pose the rig at <paramref name="timeSeconds"/>, in the clip's own time - the same axis its key
    /// times are on, which starts at <see cref="AnimationClip.StartTime"/> rather than necessarily at
    /// zero.
    /// </summary>
    public void Apply(float timeSeconds)
    {
        if (!IsBound) return;

        _pose.EnsureSize(_skeleton.Count);
        _pose.ClearWeights();
        _blendShapes.Clear();

        ClipSampler.Sample(_clip!, _binding!, _skeleton, timeSeconds, 1f, _pose, _blendShapes);

        for (int i = 0; i < _skeleton.Count; i++)
        {
            if (_pose.Weight[i] <= 0f) continue;
            Transform? bone = _skeleton.GetBone(i);
            if (bone == null) continue;

            bone.SetLocalTransform(_pose.Position[i], Quaternion.NormalizeSafe(_pose.Rotation[i]), _pose.Scale[i]);
        }

        _blendShapes.Apply();
    }

    /// <summary>Put every bone and blend shape the preview touched back where it was when bound.</summary>
    public void Restore()
    {
        if (_root == null) return;

        for (int i = 0; i < _skeleton.Count; i++)
        {
            Transform? bone = _skeleton.GetBone(i);
            if (bone == null) continue;
            bone.SetLocalTransform(_skeleton.RefPosition(i), _skeleton.RefRotation(i), _skeleton.RefScale(i));
        }

        foreach (var (renderer, shape, weight) in _savedShapes)
            if (renderer.IsValid())
                renderer.SetBlendShapeWeight(shape, weight);
    }

    /// <summary>
    /// Re-resolve the binding against the same clip and rig. The binding is a snapshot of the clip's
    /// track list taken at <see cref="Bind"/> time, so a track added afterwards is simply not sampled
    /// (the sampler clamps to the binding's length) - an editor that adds tracks has to say so.
    /// </summary>
    public void Rebuild()
    {
        AnimationClip? clip = _clip;
        Transform? root = _root;
        if (clip.IsNotValid() || root == null) return;

        Release();
        Bind(clip, root);
    }

    /// <summary>Restore the rig and drop the binding.</summary>
    public void Release()
    {
        Restore();
        _clip = null;
        _root = null;
        _binding = null;
        _savedShapes.Clear();
        _skeleton.Reset(null);
    }

    /// <summary>
    /// The bone paths this clip animates, paired with whether they resolved against the bound rig.
    /// The clip editor uses it to warn that a track is animating a bone this character does not have.
    /// </summary>
    public IEnumerable<(string Path, bool Resolved)> EnumerateBones()
    {
        for (int i = 0; i < _skeleton.Count; i++)
            yield return (_skeleton.GetPath(i), _skeleton.GetBone(i) != null);
    }
}
