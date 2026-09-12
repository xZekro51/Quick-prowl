// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// Degenerate inputs. The rule is that none of them may produce a tween that can never finish and
/// can never be reclaimed - a NaN position, for instance, never compares greater than its duration.
/// </summary>
public sealed class RobustnessTests : TweenTestBase
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-5f)]
    public void ANonsenseDurationBecomesAnInstantTween(float duration)
    {
        var box = new Box();
        Tween t = Tween.To(0f, 10f, duration).Bind(box, SetValue).SetEase(Ease.Linear);

        Assert.Equal(0f, t.Duration);

        Tween.Update(0.016f);

        Assert.False(t.IsActive);
        Assert.Equal(10f, box.Value, 3);
        Assert.Equal(0, Tween.TotalActive);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    public void ANonsenseDelayIsNoDelay(float delay)
    {
        var box = new Box();
        Tween t = LinearTween(box).SetDelay(delay);

        Assert.Equal(1f, t.FullDuration, 3);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void ANonFiniteDeltaIsIgnoredAndDoesNotPoisonTheTween()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Tween.Update(float.NaN);
        Tween.Update(float.PositiveInfinity);

        Assert.Equal(0f, box.Value, 3);
        Assert.True(t.IsActive);

        // Still perfectly usable afterwards.
        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void ANonFiniteTimeScaleIsIgnored()
    {
        var box = new Box();
        LinearTween(box);

        Tween.GlobalTimeScale = float.NaN;
        Assert.Equal(1f, Tween.GlobalTimeScale);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void AZeroLengthInfiniteTweenCannotRunAwayWithTheLoopCounter()
    {
        var box = new Box();
        Tween t = Tween.To(0f, 10f, 0f).Bind(box, SetValue).SetLoops(-1);

        Tween.Update(1f);

        // A cycle pinned to the floor duration would otherwise credit a million loops for one
        // frame and eventually overflow the counter. Clamped to the per-tick ceiling instead.
        Assert.Equal(4096, t.CompletedLoops);
        Assert.True(t.IsActive);

        Tween.Update(1f);
        Assert.Equal(8192, t.CompletedLoops);
        Assert.True(t.CompletedLoops > 0);
    }

    [Fact]
    public void AVeryLongTickStillCompletesAFiniteTween()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetLoops(3);

        Tween.Update(1000f);

        Assert.Equal(10f, box.Value, 3);
        Assert.False(t.IsActive);
    }

    [Fact]
    public void GotoClampsANonsensePosition()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetAutoKill(false);

        t.Goto(float.NaN);
        Assert.Equal(0f, box.Value, 3);

        t.Goto(-10f);
        Assert.Equal(0f, box.Value, 3);

        t.Goto(1000f);
        Assert.Equal(10f, box.Value, 3);
    }

    [Fact]
    public void IntAndLongTweensRoundInsteadOfDrifting()
    {
        var hits = new System.Collections.Generic.List<int>();
        Tween.To(0, 10, 1f).Bind(v => hits.Add(v)).SetEase(Ease.Linear);

        Advance(4, 0.25f);

        // MathF.Round is round-half-to-even, so 2.5 lands on 2 and 7.5 on 8.
        Assert.Equal(new[] { 2, 5, 8, 10 }, hits);
    }

    [Fact]
    public void ANullSetterIsFineWhenOnlyCallbacksMatter()
    {
        var fired = new Box();
        Tween.DelayedCall(0.5f, fired, static b => b.Count++);

        Tween.Update(0.25f);
        Assert.Equal(0, fired.Count);

        Tween.Update(0.25f);
        Assert.Equal(1, fired.Count);
        Assert.Equal(0, Tween.TotalActive);
    }

    [Fact]
    public void SetRelativeTurnsTheEndValueIntoAnOffset()
    {
        var box = new Box();
        Tween.To(5f, 10f, 1f).Bind(box, SetValue).SetEase(Ease.Linear).SetRelative();

        Advance(2, 0.5f);

        Assert.Equal(15f, box.Value, 3);
    }

    [Fact]
    public void FromSwapsTheEndpointsAndAppliesTheNewStart()
    {
        var box = new Box();
        Tween.To(0f, 10f, 1f).Bind(box, SetValue).SetEase(Ease.Linear).From();

        // From() applies immediately, so the value is already at the far end.
        Assert.Equal(10f, box.Value, 3);

        Advance(2, 0.5f);
        Assert.Equal(0f, box.Value, 3);
    }
}
