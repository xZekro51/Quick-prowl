// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

// The library is a set of statics driven by an explicit tick, so tests cannot run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Prowl.Tweening.Tests;

/// <summary>
/// Puts the library back to a known state before every test. <see cref="TweenManager.Reset"/> is
/// the same seam the engine uses for a hot reload, so exercising it here keeps it honest.
/// </summary>
public abstract class TweenTestBase : IDisposable
{
    /// <summary>A mutable reference cell - the state object the allocation-free overloads want.</summary>
    protected sealed class Box
    {
        public float Value;
        public int Count;
    }

    /// <summary>Standard allocation-free float setter.</summary>
    protected static readonly Action<float, Box> SetValue = static (v, b) => b.Value = v;

    protected TweenTestBase()
    {
        TweenManager.Reset();
        TweenManager.RuntimeBootstrap = null;
        TweenManager.Diagnostics = null;
    }

    public void Dispose()
    {
        TweenManager.Reset();
        TweenManager.RuntimeBootstrap = null;
        TweenManager.Diagnostics = null;
    }

    /// <summary>The storage every <c>float</c> tween - and every sequence - lives in.</summary>
    internal static TweenStorage<float, FloatAdapter> FloatStorage
        => TweenManager.Of<float, FloatAdapter>.Storage;

    /// <summary>Reads a live tween's internal flags, for the tests that assert on the data layout.</summary>
    internal static TweenFlags FlagsOf(Tween tween)
    {
        ITweenStorage? storage = TweenManager.Resolve(tween.StorageId);
        Assert.NotNull(storage);
        Assert.True(storage.TryResolve(tween.Slot, tween.Version, out int dense), "tween is not alive");
        return storage.CoreAt(dense).Flags;
    }

    /// <summary>Runs <paramref name="ticks"/> Normal ticks of <paramref name="step"/> seconds each.</summary>
    protected static void Advance(int ticks, float step)
    {
        for (int i = 0; i < ticks; i++)
            Tween.Update(step);
    }

    /// <summary>Creates a linear 0..10 float tween over one second, bound to <paramref name="box"/>.</summary>
    protected static Tween LinearTween(Box box, float duration = 1f)
        => Tween.To(0f, 10f, duration).Bind(box, SetValue).SetEase(Ease.Linear);
}
