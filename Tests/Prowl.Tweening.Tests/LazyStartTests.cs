// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// Tweens with a getter read their start value when they start - after their delay, or when a
/// sequence reaches them - not when they are created. That is what lets consecutive steps of a
/// sequence each continue from where the previous one ended.
/// </summary>
public sealed class LazyStartTests : TweenTestBase
{
    /// <summary>Counts every read in <see cref="Box.Count"/>, so a test can tell when the start was taken.</summary>
    private static readonly Func<Box, float> CountedRead = static b => { b.Count++; return b.Value; };

    private static Tween Towards(Box box, float to, float duration = 1f)
        => Tween.To<float, FloatAdapter, Box>(box, CountedRead, SetValue, to, duration).SetEase(Ease.Linear);

    private static void AssertSeries(float[] expected, List<float> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(MathF.Abs(expected[i] - actual[i]) < 1e-3f, $"step {i}: expected {expected[i]}, got {actual[i]}");
    }

    [Fact]
    public void TheStartIsReadWhenTheTweenStartsNotWhenItIsCreated()
    {
        var box = new Box();
        Towards(box, 10f);
        Assert.Equal(0, box.Count);

        box.Value = 4f;
        Tween.Update(0.5f);

        Assert.Equal(1, box.Count);
        Assert.Equal(7f, box.Value, 3);         // half way from 4, not from 0

        Tween.Update(0.25f);
        Assert.Equal(1, box.Count);             // read once, not every tick
        Assert.Equal(8.5f, box.Value, 3);
    }

    [Fact]
    public void ADelayedTweenReadsItsStartOnceTheDelayIsOver()
    {
        var box = new Box();
        Towards(box, 10f).SetDelay(1f);

        Tween.Update(0.5f);
        Assert.Equal(0, box.Count);

        box.Value = 6f;
        Tween.Update(1f);                       // 0.5 ends the delay, 0.5 runs the tween

        Assert.Equal(1, box.Count);
        Assert.Equal(8f, box.Value, 3);
    }

    [Fact]
    public void ExplicitValuesAreUsedExactlyAsGiven()
    {
        var box = new Box();
        LinearTween(box);

        box.Value = 4f;
        Tween.Update(0.5f);

        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void TheCapturingGetterFormReadsLateToo()
    {
        var box = new Box();
        Tween.To(() => box.Value, v => box.Value = v, 10f, 1f).SetEase(Ease.Linear);

        box.Value = 4f;
        Tween.Update(0.5f);

        Assert.Equal(7f, box.Value, 3);
    }

    [Fact]
    public void EachSequenceStepStartsWhereThePreviousOneEnded()
    {
        var box = new Box();

        // The reported case, in miniature: steps that read their start from the value they animate.
        Sequence sequence = Tween.Sequence();
        sequence.Append(Towards(box, 10f));
        sequence.Append(Towards(box, 20f));
        sequence.Append(Towards(box, 5f));

        var seen = new List<float>();
        for (int i = 0; i < 6; i++)
        {
            Tween.Update(0.5f);
            seen.Add(box.Value);
        }

        AssertSeries(new[] { 5f, 10f, 15f, 20f, 12.5f, 5f }, seen);
        Assert.Equal(3, box.Count);
    }

    [Fact]
    public void StepsAheadOfThePlayheadAreLeftAlone()
    {
        var box = new Box();
        var started = new List<string>();

        Sequence sequence = Tween.Sequence();
        sequence.Append(Towards(box, 10f).OnStart(started, static s => s.Add("first")));
        sequence.Append(Towards(box, 20f).OnStart(started, static s => s.Add("second")));

        Tween.Update(0.5f);

        // Only the first step has begun: the second hasn't read its start or announced itself.
        Assert.Equal(1, box.Count);
        Assert.Equal(new[] { "first" }, started);

        Tween.Update(1f);

        Assert.Equal(2, box.Count);
        Assert.Equal(new[] { "first", "second" }, started);
    }

    [Fact]
    public void JumpingIntoASequenceStillChainsEveryStep()
    {
        var box = new Box();

        Sequence sequence = Tween.Sequence().SetAutoKill(false);
        sequence.Append(Towards(box, 10f));
        sequence.Append(Towards(box, 20f));
        sequence.Append(Towards(box, 40f));

        // Straight into the middle of the third step: the first two still start in order, each from
        // where the one before it ended.
        sequence.Goto(2.5f);

        Assert.Equal(30f, box.Value, 3);
        Assert.Equal(3, box.Count);
    }

    [Fact]
    public void ANestedSequenceDoesNotStartItsStepsBeforeItsTurn()
    {
        var box = new Box();
        var other = new Box();

        Sequence inner = Tween.Sequence();
        inner.Append(Towards(other, 10f));

        Sequence outer = Tween.Sequence();
        outer.Append(Towards(box, 10f));
        outer.Append(inner.Tween);

        Tween.Update(0.5f);
        Assert.Equal(0, other.Count);

        other.Value = 2f;
        Tween.Update(1f);                       // half way through the inner sequence

        Assert.Equal(1, other.Count);
        Assert.Equal(6f, other.Value, 3);
    }

    [Fact]
    public void SetRelativeIsRelativeToTheValueAtStart()
    {
        var box = new Box();
        Towards(box, 2f).SetRelative();

        box.Value = 5f;
        Tween.Update(1f);

        Assert.Equal(7f, box.Value, 3);
    }

    [Fact]
    public void FromReadsTheCurrentValueRightAway()
    {
        var box = new Box { Value = 3f };

        // From() swaps immediately, so it needs the current value now: it jumps to 0 and heads back to 3.
        Towards(box, 0f).From();

        Assert.Equal(1, box.Count);
        Assert.Equal(0f, box.Value, 3);

        Tween.Update(0.5f);
        Assert.Equal(1.5f, box.Value, 3);
        Assert.Equal(1, box.Count);
    }

    [Fact]
    public void RestartingKeepsTheStartReadTheFirstTime()
    {
        var box = new Box();
        Tween t = Towards(box, 10f).SetAutoKill(false);

        box.Value = 2f;
        Tween.Update(1f);
        Assert.Equal(10f, box.Value, 3);

        box.Value = 50f;
        t.Restart();

        Assert.Equal(2f, box.Value, 3);
        Assert.Equal(1, box.Count);
    }

    [Fact]
    public void RestartingATweenThatHasNotStartedWritesNothing()
    {
        var box = new Box { Value = 7f };
        Tween t = Towards(box, 10f).SetDelay(1f);

        t.Restart();
        t.Rewind();

        Assert.Equal(0, box.Count);
        Assert.Equal(7f, box.Value);
    }

    [Fact]
    public void AKilledTweenNeverReadsItsStart()
    {
        var box = new Box();
        Towards(box, 10f).Kill();

        Tween.Update(0.5f);

        Assert.Equal(0, box.Count);
    }

    [Fact]
    public void CompletingATweenThatHasNotStartedReadsItsStartFirst()
    {
        var box = new Box { Value = 4f };
        Towards(box, 2f).SetRelative().Complete();

        Assert.Equal(1, box.Count);
        Assert.Equal(6f, box.Value, 3);
    }

    [Fact]
    public void TheStartRecordsAreReleasedOnceEveryTweenHasStarted()
    {
        var box = new Box();
        Towards(box, 10f, 100f);
        Assert.True(FloatStorage.HasLazyArray);

        Tween.Update(0.1f);
        Tween.Trim();

        Assert.False(FloatStorage.HasLazyArray);
        Assert.Equal(1, Tween.TotalActive);
    }
}
