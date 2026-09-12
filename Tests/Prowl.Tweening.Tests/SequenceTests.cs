// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// Timeline behaviour: layout, callbacks in both directions, and the two traps the sequence API
/// used to have - completing before it was populated, and freezing its children's durations.
/// </summary>
public sealed class SequenceTests : TweenTestBase
{
    private static Tween Child(Box box, float from, float to, float duration)
        => Tween.To(from, to, duration).Bind(box, SetValue).SetEase(Ease.Linear);

    private static Sequence NewSequence() => Tween.Sequence().SetEase(Ease.Linear);

    [Fact]
    public void AnEmptySequenceWaitsToBeBuiltInsteadOfCompleting()
    {
        Sequence seq = NewSequence();

        // Two frames go by before anything is appended. The old behaviour completed the zero-length
        // sequence, auto-killed it and recycled its timeline, after which every Append silently
        // did nothing.
        Tween.Update(0.5f);
        Tween.Update(0.5f);

        Assert.True(seq.IsActive);
        Assert.Equal(0, seq.Count);

        var box = new Box();
        seq.Append(Child(box, 0f, 10f, 1f));

        Assert.Equal(1, seq.Count);
        Assert.Equal(1f, seq.Duration, 3);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void AppendPlaysChildrenOneAfterAnother()
    {
        var first = new Box();
        var second = new Box();

        Sequence seq = NewSequence();
        seq.Append(Child(first, 0f, 10f, 1f));
        seq.Append(Child(second, 0f, 10f, 1f));

        Assert.Equal(2f, seq.Duration, 3);

        Tween.Update(0.5f);
        Assert.Equal(5f, first.Value, 3);
        Assert.Equal(0f, second.Value, 3);

        Tween.Update(1f);                  // t = 1.5
        Assert.Equal(10f, first.Value, 3); // clamped at its end
        Assert.Equal(5f, second.Value, 3);
    }

    [Fact]
    public void JoinPlaysInParallelWithTheLastAppend()
    {
        var a = new Box();
        var b = new Box();

        Sequence seq = NewSequence();
        seq.Append(Child(a, 0f, 10f, 1f));
        seq.Join(Child(b, 0f, 20f, 1f));

        Assert.Equal(1f, seq.Duration, 3);

        Tween.Update(0.5f);
        Assert.Equal(5f, a.Value, 3);
        Assert.Equal(10f, b.Value, 3);
    }

    [Fact]
    public void InsertPlacesAChildAtAnExactPosition()
    {
        var box = new Box();

        Sequence seq = NewSequence();
        seq.AppendInterval(1f);
        seq.Insert(1f, Child(box, 0f, 10f, 1f));

        Assert.Equal(2f, seq.Duration, 3);

        Tween.Update(1f);
        Assert.Equal(0f, box.Value, 3);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void PrependPushesEverythingBack()
    {
        var late = new Box();
        var early = new Box();

        Sequence seq = NewSequence();
        seq.Append(Child(late, 0f, 10f, 1f));
        seq.Prepend(Child(early, 0f, 10f, 1f));

        Assert.Equal(2f, seq.Duration, 3);

        Tween.Update(0.5f);
        Assert.Equal(5f, early.Value, 3);
        Assert.Equal(0f, late.Value, 3);
    }

    [Fact]
    public void CallbacksFireAsThePlayheadPassesThem()
    {
        var log = new List<string>();

        Sequence seq = NewSequence();
        seq.AppendInterval(1f);
        seq.InsertCallback(0.25f, () => log.Add("quarter"));
        seq.InsertCallback(0.75f, () => log.Add("three quarters"));

        Tween.Update(0.5f);
        Assert.Equal(new[] { "quarter" }, log);

        Tween.Update(0.5f);
        Assert.Equal(new[] { "quarter", "three quarters" }, log);
    }

    [Fact]
    public void YoyoFiresCallbacksOnTheWayBackToo()
    {
        var hits = new Box();

        Sequence seq = NewSequence().SetLoops(2, LoopType.Yoyo).SetAutoKill(false);
        seq.AppendInterval(1f);
        seq.InsertCallback(0.5f, () => hits.Count++);

        Advance(2, 0.25f);          // t = 0.5, crossed going out
        Assert.Equal(1, hits.Count);

        Advance(4, 0.25f);          // t = 1.5, crossed again coming back
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void AChildThatGrowsExtendsTheTimeline()
    {
        var box = new Box();

        Sequence seq = NewSequence();
        Tween child = Child(box, 0f, 10f, 1f);
        seq.Append(child);
        Assert.Equal(1f, seq.Duration, 3);

        // Reconfiguring a child after it was added used to desync the timeline silently.
        child.SetLoops(2);

        Tween.Update(0.5f);
        Assert.Equal(2f, seq.Duration, 3);
    }

    [Fact]
    public void KillingASequenceKillsItsChildren()
    {
        var box = new Box();

        Sequence seq = NewSequence();
        Tween a = Child(box, 0f, 10f, 1f);
        Tween b = Child(box, 0f, 10f, 1f);
        seq.Append(a);
        seq.Append(b);

        seq.Kill();

        Assert.False(seq.IsActive);
        Assert.False(a.IsActive);
        Assert.False(b.IsActive);
        Assert.Equal(0, Tween.TotalActive);
    }

    [Fact]
    public void ComposingAKilledSequenceWarnsInsteadOfDoingNothingQuietly()
    {
        var warnings = new List<string>();
        TweenManager.Diagnostics = warnings.Add;

        var box = new Box();
        Sequence seq = NewSequence();
        seq.Append(Child(box, 0f, 10f, 1f));
        seq.Kill();

        seq.Append(Child(box, 0f, 10f, 1f));

        Assert.Single(warnings);
        Assert.Contains("already been killed", warnings[0]);
    }

    [Fact]
    public void ASequenceNestsInsideAnotherSequence()
    {
        var box = new Box();

        Sequence inner = NewSequence();
        inner.Append(Child(box, 0f, 10f, 1f));

        Sequence outer = NewSequence();
        outer.Append(inner.Tween);

        Assert.Equal(1f, outer.Duration, 3);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void SequencedChildrenAreNotDrivenByTheUpdateLoopItself()
    {
        var box = new Box();

        Tween child = Child(box, 0f, 10f, 1f);
        Sequence seq = NewSequence().Pause();
        seq.Append(child);

        // The sequence is paused, so nothing should move - if the loop still drove the adopted
        // child directly it would advance on its own.
        Advance(4, 0.5f);
        Assert.Equal(0f, box.Value, 3);
    }

    [Fact]
    public void ASequenceCompletesAndAutoKillsWithItsChildren()
    {
        var box = new Box();
        var completed = new Box();

        Sequence seq = NewSequence().OnComplete(completed, static b => b.Count++);
        seq.Append(Child(box, 0f, 10f, 1f));

        Advance(2, 0.5f);

        Assert.Equal(1, completed.Count);
        Assert.Equal(10f, box.Value, 3);
        Assert.False(seq.IsActive);
        Assert.Equal(0, Tween.TotalActive);
    }

    [Fact]
    public void APooledTimelineComesBackEmpty()
    {
        var box = new Box();

        Sequence first = NewSequence();
        first.Append(Child(box, 0f, 10f, 1f));
        first.Append(Child(box, 0f, 10f, 1f));
        Assert.Equal(2, first.Count);
        first.Kill();
        Tween.Update(0.016f);

        Sequence second = NewSequence();
        Assert.Equal(0, second.Count);
        Assert.Equal(0f, second.Duration, 3);
    }

    [Fact]
    public void IntervalsAddEmptyTime()
    {
        var box = new Box();

        Sequence seq = NewSequence();
        seq.AppendInterval(0.5f);
        seq.Append(Child(box, 0f, 10f, 1f));
        seq.PrependInterval(0.5f);

        Assert.Equal(2f, seq.Duration, 3);

        Tween.Update(1f);                   // still inside the two intervals plus nothing
        Assert.Equal(0f, box.Value, 3);

        Tween.Update(0.5f);                 // t = 1.5, half way through the child
        Assert.Equal(5f, box.Value, 3);
    }
}
