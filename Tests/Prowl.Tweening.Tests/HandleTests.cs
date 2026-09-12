// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// The handle contract: a <see cref="Tween"/> is a versioned slot, every operation on a dead or
/// default one is a no-op, and a recycled slot never answers to the handle that used to own it.
/// </summary>
public sealed class HandleTests : TweenTestBase
{
    [Fact]
    public void DefaultHandleIsInertAndEveryCallIsSafe()
    {
        Tween t = Tween.None;

        // None of this should throw, and none of it should do anything.
        t.SetEase(Ease.Linear)
         .SetLoops(3, LoopType.Yoyo)
         .SetDelay(1f)
         .SetAutoKill(false)
         .SetId("id")
         .SetTarget(this)
         .SetTimeScale(2f)
         .SetRelative()
         .From()
         .OnComplete(() => Assert.Fail("a dead handle must not accept callbacks"))
         .Play()
         .Pause()
         .Restart()
         .Rewind()
         .Complete()
         .Goto(5f)
         .Flip()
         .Kill();

        Assert.False(t.IsActive);
        Assert.False(t.IsPlaying);
        Assert.False(t.IsComplete);
        Assert.Equal(0f, t.Duration);
        Assert.Equal(0f, t.FullDuration);
        Assert.Equal(0f, t.Elapsed);
        Assert.Equal(0, t.CompletedLoops);
        Assert.Equal(1f, t.TimeScale);
    }

    [Fact]
    public void KillingATweenInvalidatesItsHandleImmediately()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Assert.True(t.IsActive);
        t.Kill();
        Assert.False(t.IsActive);
    }

    [Fact]
    public void ARecycledSlotDoesNotAnswerToTheOldHandle()
    {
        var box = new Box();

        Tween first = LinearTween(box);
        int slot = first.Slot;
        first.Kill();

        // Sweep, so the slot goes back on the free list.
        Tween.Update(0.016f);

        Tween second = LinearTween(box);

        // Same slot, new version: only the new handle resolves.
        Assert.Equal(slot, second.Slot);
        Assert.True(second.IsActive);
        Assert.False(first.IsActive);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AutoKillCompletesAndReleasesTheTween()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
        Assert.True(t.IsActive);

        Tween.Update(0.5f);
        Assert.Equal(10f, box.Value, 3);
        Assert.False(t.IsActive);
        Assert.Equal(0, Tween.TotalActive);
    }

    [Fact]
    public void AutoKillOffLeavesTheTweenCompleteAndPaused()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetAutoKill(false);

        Advance(2, 0.5f);

        Assert.True(t.IsActive);
        Assert.True(t.IsComplete);
        Assert.False(t.IsPlaying);
        Assert.Equal(10f, box.Value, 3);
    }

    [Fact]
    public void EqualityComparesTheWholeHandle()
    {
        var box = new Box();
        Tween a = LinearTween(box);
        Tween b = LinearTween(box);

        Tween copy = a;

        Assert.Equal(a, copy);
        Assert.NotEqual(a, b);
        Assert.True(a == copy);
        Assert.True(a != b);
        Assert.Equal(a.GetHashCode(), copy.GetHashCode());
    }

    [Fact]
    public void DelayHoldsTheTweenThenItRunsWithTheOverflow()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetDelay(0.5f);

        Tween.Update(0.25f);
        Assert.Equal(0f, box.Value, 3);
        Assert.False(t.IsComplete);

        // 0.25 finishes the delay; the remaining 0.5 is spent on the tween itself.
        Tween.Update(0.75f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void GotoJumpsAndPausesUnlessAskedToPlay()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetAutoKill(false);

        t.Goto(0.25f);
        Assert.Equal(2.5f, box.Value, 3);
        Assert.False(t.IsPlaying);

        t.Goto(0.75f, andPlay: true);
        Assert.Equal(7.5f, box.Value, 3);
        Assert.True(t.IsPlaying);
    }

    [Fact]
    public void FlipReversesTheRemainingTravel()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetAutoKill(false);

        Tween.Update(0.25f);
        Assert.Equal(2.5f, box.Value, 3);

        t.Flip();

        // The flip itself applies nothing - it swaps the endpoints and mirrors the playhead, which
        // together describe the same point travelling the other way.
        Assert.Equal(2.5f, box.Value, 3);

        // A quarter second later it has arrived back where it set off from.
        Tween.Update(0.25f);
        Assert.Equal(0f, box.Value, 3);
        Assert.True(t.IsComplete);
    }
}
