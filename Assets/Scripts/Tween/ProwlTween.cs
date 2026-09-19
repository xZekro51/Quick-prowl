// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#nullable enable

using System;

using Prowl.Tweening;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// <see cref="Tween"/> creation for Prowl's own math types.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Tween"/> itself lives in <c>Prowl.Tweening</c>, which knows nothing about
/// <see cref="Float3"/> or <see cref="Color"/> - keeping the tweening library portable. That would
/// otherwise leave engine types stuck with the long form,
/// <c>Tween.To&lt;Float3, Float3Adapter&gt;(a, b, 1f)</c>; these overloads give them the same short
/// spelling the built-in types get:
/// </para>
/// <code>
/// ProwlTween.To(from, to, 1f).Bind(this, static (v, s) =&gt; s.Tint = v);
/// </code>
/// </remarks>
public static class ProwlTween
{
    /// <summary>Describes a <see cref="Float2"/> tween. Nothing is scheduled until you call <c>Bind</c>.</summary>
    public static TweenBuilder<Float2, Float2Adapter> To(Float2 from, Float2 to, float duration)
        => Tween.To<Float2, Float2Adapter>(in from, in to, duration);

    /// <inheritdoc cref="To(Float2, Float2, float)"/>
    public static TweenBuilder<Float3, Float3Adapter> To(Float3 from, Float3 to, float duration)
        => Tween.To<Float3, Float3Adapter>(in from, in to, duration);

    /// <inheritdoc cref="To(Float2, Float2, float)"/>
    public static TweenBuilder<Float4, Float4Adapter> To(Float4 from, Float4 to, float duration)
        => Tween.To<Float4, Float4Adapter>(in from, in to, duration);

    /// <summary>Describes a rotation tween, interpolated along the shortest arc.</summary>
    public static TweenBuilder<Quaternion, RotationAdapter> To(Quaternion from, Quaternion to, float duration)
        => Tween.To<Quaternion, RotationAdapter>(in from, in to, duration);

    /// <inheritdoc cref="To(Float2, Float2, float)"/>
    public static TweenBuilder<Color, ColorAdapter> To(Color from, Color to, float duration)
        => Tween.To<Color, ColorAdapter>(in from, in to, duration);

    /// <summary>
    /// Reads the start value from <paramref name="state"/> when the tween starts - after any delay,
    /// or when a sequence reaches it - and tweens it. Allocation-free as long as both lambdas are
    /// <c>static</c>: pass the object you are animating as <paramref name="state"/> rather than
    /// capturing it.
    /// </summary>
    public static Tween To<TState>(TState state, Func<TState, Float3> getter, Action<Float3, TState> setter, Float3 to, float duration)
        where TState : class
        => Tween.To<Float3, Float3Adapter, TState>(state, getter, setter, in to, duration);

    /// <inheritdoc cref="To{TState}(TState, Func{TState, Float3}, Action{Float3, TState}, Float3, float)"/>
    public static Tween To<TState>(TState state, Func<TState, Color> getter, Action<Color, TState> setter, Color to, float duration)
        where TState : class
        => Tween.To<Color, ColorAdapter, TState>(state, getter, setter, in to, duration);

    /// <inheritdoc cref="To{TState}(TState, Func{TState, Float3}, Action{Float3, TState}, Float3, float)"/>
    public static Tween To<TState>(TState state, Func<TState, float> getter, Action<float, TState> setter, float to, float duration)
        where TState : class
        => Tween.To<float, FloatAdapter, TState>(state, getter, setter, in to, duration);

    /// <summary>
    /// Pre-grows the storage behind the engine's common tween types, so a burst - spawning a wave,
    /// opening a menu full of animated widgets - doesn't reallocate part-way through a frame.
    /// </summary>
    public static void Reserve(int capacity)
    {
        Tween.Reserve<float, FloatAdapter>(capacity);
        Tween.Reserve<Float3, Float3Adapter>(capacity);
        Tween.Reserve<Quaternion, RotationAdapter>(capacity);
    }
}
