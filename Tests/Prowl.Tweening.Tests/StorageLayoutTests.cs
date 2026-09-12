// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// The data layout the update loop depends on: which flags a given call sets, which cold arrays
/// get allocated, and that capacity can be asked for and given back.
/// </summary>
public sealed class StorageLayoutTests : TweenTestBase
{
    [Fact]
    public void TaggingATweenDoesNotPutItOnTheCallbackPath()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetTarget(box).SetId("ui");

        TweenFlags flags = FlagsOf(t);

        Assert.True(flags.HasFlag(TweenFlags.HasTags));
        Assert.False(flags.HasFlag(TweenFlags.HasCallbacks));

        // Tags live in their own small array; the 80-byte callback record is never touched.
        Assert.True(FloatStorage.HasTagsArray);
        Assert.False(FloatStorage.HasEventsArray);

        // And tagging still does its job.
        Assert.Equal(1, Tween.KillAll(box));
    }

    [Fact]
    public void AssigningACallbackIsWhatOptsIntoTheCallbackPath()
    {
        var box = new Box();
        Tween t = LinearTween(box).OnComplete(box, static b => b.Count++);

        Assert.True(FlagsOf(t).HasFlag(TweenFlags.HasCallbacks));
        Assert.True(FloatStorage.HasEventsArray);
        Assert.False(FloatStorage.HasTagsArray);
    }

    [Fact]
    public void ACustomEaseDoesNotCountAsACallback()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetEase(static (time, duration, overshoot, period) => time);

        TweenFlags flags = FlagsOf(t);
        Assert.True(flags.HasFlag(TweenFlags.CustomEase));
        Assert.False(flags.HasFlag(TweenFlags.HasCallbacks));
    }

    [Fact]
    public void ALinkSetsOnlyTheLinkFlag()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetLink(box, static b => true);

        TweenFlags flags = FlagsOf(t);
        Assert.True(flags.HasFlag(TweenFlags.HasLink));
        Assert.False(flags.HasFlag(TweenFlags.HasCallbacks));
        Assert.False(flags.HasFlag(TweenFlags.HasTags));
    }

    [Fact]
    public void ReservePreGrowsSoABurstDoesNotReallocate()
    {
        Tween.Reserve<float, FloatAdapter>(200);
        Assert.True(FloatStorage.Capacity >= 200, $"capacity was {FloatStorage.Capacity}");

        var box = new Box();
        int capacity = FloatStorage.Capacity;

        for (int i = 0; i < 200; i++)
            LinearTween(box, 100f);

        Assert.Equal(capacity, FloatStorage.Capacity);
        Assert.Equal(200, Tween.TotalActive);
    }

    [Fact]
    public void TrimHandsBackTheCapacityAndTheColdArrays()
    {
        var box = new Box();

        Tween.Reserve<float, FloatAdapter>(256);
        for (int i = 0; i < 100; i++)
            LinearTween(box, 100f).SetTarget(box).OnComplete(box, static b => b.Count++);

        Assert.True(FloatStorage.Capacity >= 256);
        Assert.True(FloatStorage.HasTagsArray);
        Assert.True(FloatStorage.HasEventsArray);

        Tween.KillAll();
        Tween.Update(0.016f);
        Tween.Trim();

        Assert.Equal(0, Tween.TotalActive);
        Assert.True(FloatStorage.Capacity < 256, $"capacity was still {FloatStorage.Capacity}");
        Assert.False(FloatStorage.HasTagsArray);
        Assert.False(FloatStorage.HasEventsArray);
    }

    [Fact]
    public void TrimKeepsWhatIsStillInUse()
    {
        var box = new Box();

        for (int i = 0; i < 40; i++)
            LinearTween(box, 100f).OnComplete(box, static b => b.Count++);

        Tween.Trim();

        Assert.Equal(40, Tween.TotalActive);
        Assert.True(FloatStorage.Capacity >= 40);
        Assert.True(FloatStorage.HasEventsArray);
    }

    [Fact]
    public void AnUnusedTickTypeCostsNothingAndDoesNotStepTheWrongTweens()
    {
        var normal = new Box();
        var fixedStep = new Box();

        Tween.To(5f, 10f, 1f).Bind(normal, SetValue).SetEase(Ease.Linear);
        Tween.To(5f, 10f, 1f).Bind(fixedStep, SetValue).SetEase(Ease.Linear).SetUpdate(UpdateType.Fixed);

        Tween.Update(0.5f);
        Assert.Equal(7.5f, normal.Value, 3);
        Assert.Equal(0f, fixedStep.Value, 3);       // untouched: wrong tick

        Tween.FixedUpdate(0.5f);
        Assert.Equal(7.5f, normal.Value, 3);        // untouched: wrong tick
        Assert.Equal(7.5f, fixedStep.Value, 3);
    }

    [Fact]
    public void MovingATweenBetweenTickTypesKeepsTheCountersStraight()
    {
        var box = new Box();
        Tween t = Tween.To(5f, 10f, 1f).Bind(box, SetValue).SetEase(Ease.Linear);

        t.SetUpdate(UpdateType.Fixed);
        Tween.Update(0.5f);
        Assert.Equal(0f, box.Value, 3);

        t.SetUpdate(UpdateType.Normal);
        Tween.Update(0.5f);
        Assert.Equal(7.5f, box.Value, 3);
    }

    [Fact]
    public void SeparateValueTypesGetSeparateStorages()
    {
        var box = new Box();
        LinearTween(box);
        Tween.To(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One, 1f)
            .Bind(box, static (v, b) => b.Value = v.X);

        Assert.Equal(2, Tween.TotalActive);
        Assert.Equal(1, FloatStorage.ActiveCount);
    }
}
