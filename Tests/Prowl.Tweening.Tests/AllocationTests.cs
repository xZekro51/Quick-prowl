// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// The library's central claim is that steady-state tweening allocates nothing. These lock it in.
/// <para>
/// Tiered compilation is off for this assembly (see the csproj), so the JIT cannot re-compile a
/// method part-way through a measured loop and charge its own allocation to the library.
/// </para>
/// </summary>
public sealed class AllocationTests : TweenTestBase
{
    // Hoisted out of the test bodies on purpose. Roslyn caches a static lambda per *occurrence*,
    // so writing the same lambda twice - once to warm up, once inside the measured region - makes
    // the first measured iteration allocate a delegate and charges it to the library.
    private static readonly Func<Box, float> ReadBox = static b => b.Value;
    private static readonly Action<float, Box> WriteBox = static (v, b) => b.Value = v;

    /// <summary>Allocated bytes on this thread across <paramref name="work"/>.</summary>
    private static long Measure(Action work)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void TheUpdateLoopAllocatesNothing()
    {
        var box = new Box();

        Tween.Reserve<float, FloatAdapter>(512);
        for (int i = 0; i < 200; i++)
            LinearTween(box, 10_000f);

        // Warm up: every path the measured loop takes must already be compiled.
        for (int i = 0; i < 200; i++)
            Tween.Update(0.016f);

        long allocated = Measure(static () =>
        {
            for (int i = 0; i < 500; i++)
                Tween.Update(0.016f);
        });

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void TheUpdateLoopAllocatesNothingWithCallbacksAndLinksInPlay()
    {
        var box = new Box();

        Tween.Reserve<float, FloatAdapter>(512);
        for (int i = 0; i < 100; i++)
        {
            LinearTween(box, 10_000f)
                .SetTarget(box)
                .SetLink(box, static b => true)
                .OnUpdate(box, static b => b.Count++)
                .OnStepComplete(box, static b => b.Count++);
        }

        for (int i = 0; i < 200; i++)
            Tween.Update(0.016f);

        long allocated = Measure(static () =>
        {
            for (int i = 0; i < 500; i++)
                Tween.Update(0.016f);
        });

        Assert.Equal(0L, allocated);
        Assert.True(box.Count > 0, "the callbacks should actually have run");
    }

    [Fact]
    public void CreatingATweenThroughBindAllocatesNothing()
    {
        var box = new Box();

        Tween.Reserve<float, FloatAdapter>(1024);

        // Warm up the creation path, then clear it out again.
        for (int i = 0; i < 100; i++)
            LinearTween(box, 10_000f);
        Tween.KillAll();
        Tween.Update(0.016f);

        long allocated = Measure(() =>
        {
            for (int i = 0; i < 200; i++)
                Tween.To(0f, 10f, 10_000f).Bind(box, SetValue).SetEase(Ease.Linear);
        });

        Assert.Equal(0L, allocated);
        Assert.Equal(200, Tween.TotalActive);
    }

    [Fact]
    public void TheStatefulGetterSetterOverloadAllocatesNothing()
    {
        var box = new Box();

        Tween.Reserve<float, FloatAdapter>(1024);

        for (int i = 0; i < 100; i++)
            Tween.To<float, FloatAdapter, Box>(box, ReadBox, WriteBox, 10f, 10_000f);
        Tween.KillAll();
        Tween.Update(0.016f);

        long allocated = Measure(() =>
        {
            for (int i = 0; i < 200; i++)
                Tween.To<float, FloatAdapter, Box>(box, ReadBox, WriteBox, 10f, 10_000f);
        });

        // This is the whole point of the overload: the convenient
        // To(getter, setter, ...) form costs two closures and two delegates per call.
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void TheConvenientGetterSetterOverloadIsTheOneThatCosts()
    {
        var box = new Box();
        Tween.Reserve<float, FloatAdapter>(1024);

        for (int i = 0; i < 100; i++)
            Tween.To(() => box.Value, v => box.Value = v, 10f, 10_000f);
        Tween.KillAll();
        Tween.Update(0.016f);

        long allocated = Measure(() =>
        {
            for (int i = 0; i < 200; i++)
                Tween.To(() => box.Value, v => box.Value = v, 10f, 10_000f);
        });

        // Not a bug - a documented trade-off. Asserted so the two paths cannot quietly converge
        // and leave the docs lying.
        Assert.True(allocated > 0, "the capturing overload is expected to allocate");
    }

    [Fact]
    public void BuildingAndDestroyingSequencesSettlesAtNoAllocation()
    {
        var box = new Box();

        Tween.Reserve<float, FloatAdapter>(1024);

        // Warm up, and let the SequenceData pool fill.
        for (int i = 0; i < 100; i++)
        {
            Sequence seq = Tween.Sequence().SetEase(Ease.Linear);
            seq.Append(Tween.To(0f, 10f, 1f).Bind(box, SetValue).SetEase(Ease.Linear));
            seq.Kill();
        }
        Tween.Update(0.016f);

        long allocated = Measure(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                Sequence seq = Tween.Sequence().SetEase(Ease.Linear);
                seq.Append(Tween.To(0f, 10f, 1f).Bind(box, SetValue).SetEase(Ease.Linear));
                seq.Kill();
            }
        });

        Assert.Equal(0L, allocated);
    }
}
