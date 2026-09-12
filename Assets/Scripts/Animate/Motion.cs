// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Animate;

/// <summary>
/// Base class for anything an <see cref="AnimatorState"/> can play: a single clip
/// (<see cref="ClipMotion"/>) or a parameter-driven blend of motions (<see cref="BlendTree"/>).
/// Stored polymorphically; Echo records the concrete type on save.
/// </summary>
public abstract class Motion
{
    public string Name = string.Empty;
}

/// <summary>A motion that plays one <see cref="AnimationClip"/>.</summary>
public sealed class ClipMotion : Motion
{
    public AssetRef<AnimationClip> Clip;

    public ClipMotion() { }

    public ClipMotion(AssetRef<AnimationClip> clip)
    {
        Clip = clip;
        Name = clip.Name;
    }
}

/// <summary>The interpolation strategy used to weight a <see cref="BlendTree"/>'s children.</summary>
public enum BlendTreeType
{
    /// <summary>One parameter; children placed on a line by <see cref="BlendTreeChild.Threshold"/>.</summary>
    Simple1D,

    /// <summary>Two parameters; children placed by direction. Good for locomotion (forward/strafe).</summary>
    SimpleDirectional2D,

    /// <summary>Two parameters; children placed freely on a plane, blended by gradient bands.</summary>
    FreeformCartesian2D,
}

/// <summary>One entry in a <see cref="BlendTree"/>: a child motion plus its placement.</summary>
public sealed class BlendTreeChild
{
    /// <summary>The motion to play (a clip, or a nested blend tree).</summary>
    public Motion? Motion;

    /// <summary>Position along the axis for <see cref="BlendTreeType.Simple1D"/>.</summary>
    public float Threshold;

    /// <summary>Position on the plane for 2D blend trees.</summary>
    public Float2 Position;

    /// <summary>Per-child playback speed multiplier (children stay time-synchronized).</summary>
    public float TimeScale = 1f;

    public BlendTreeChild() { }

    public BlendTreeChild(Motion motion, float threshold)
    {
        Motion = motion;
        Threshold = threshold;
    }

    public BlendTreeChild(Motion motion, Float2 position)
    {
        Motion = motion;
        Position = position;
    }
}

/// <summary>
/// A blend of several motions, weighted continuously by one or two parameters. Children are kept
/// time-synchronized (sampled at a shared normalized time) so a walk/run blend never foot-slides.
/// Trees may nest, letting you compose, say, a directional locomotion tree under a speed tree.
/// </summary>
public sealed class BlendTree : Motion
{
    public BlendTreeType Type = BlendTreeType.Simple1D;

    /// <summary>Parameter driving the X axis (and the only axis for 1D trees).</summary>
    public string BlendParameter = string.Empty;

    /// <summary>Parameter driving the Y axis (2D trees only).</summary>
    public string BlendParameterY = string.Empty;

    public List<BlendTreeChild> Children = new();

    public BlendTree() { }

    public BlendTree(string blendParameter)
    {
        Type = BlendTreeType.Simple1D;
        BlendParameter = blendParameter;
    }

    public BlendTree(string blendParameterX, string blendParameterY, BlendTreeType type = BlendTreeType.FreeformCartesian2D)
    {
        Type = type;
        BlendParameter = blendParameterX;
        BlendParameterY = blendParameterY;
    }

    /// <summary>True when this tree is driven by two parameters rather than one.</summary>
    public bool Is2D => Type != BlendTreeType.Simple1D;

    /// <summary>Add a 1D child at the given threshold. Returns this for chaining.</summary>
    public BlendTree AddChild(AnimationClip clip, float threshold)
    {
        Children.Add(new BlendTreeChild(new ClipMotion(clip), threshold));
        return this;
    }

    /// <summary>Add a 2D child at the given plane position. Returns this for chaining.</summary>
    public BlendTree AddChild(AnimationClip clip, Float2 position)
    {
        Children.Add(new BlendTreeChild(new ClipMotion(clip), position));
        return this;
    }

    /// <summary>Add an arbitrary child motion (e.g. a nested tree). Returns this for chaining.</summary>
    public BlendTree AddChild(BlendTreeChild child)
    {
        Children.Add(child);
        return this;
    }

    /// <summary>
    /// Sort 1D children into ascending threshold order. The 1D weighting walks the children as a
    /// sorted line, so an out-of-order list silently blends the wrong pair; the editor calls this
    /// after a threshold edit so authoring order never has to be maintained by hand.
    /// </summary>
    public void SortChildren()
    {
        if (Type != BlendTreeType.Simple1D) return;
        Children.Sort(static (a, b) => a.Threshold.CompareTo(b.Threshold));
    }
}
