// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// Loop arithmetic: cycle counting, the three loop modes, and where a looping tween lands when it
/// finishes. All easing is linear so the numbers are the loop maths and nothing else.
/// </summary>
public sealed class LoopTests : TweenTestBase
{
    [Fact]
    public void RestartLoopsCountCyclesAndStartOver()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetLoops(3);

        Advance(2, 0.5f);           // t = 1.0, first cycle done
        Assert.Equal(1, t.CompletedLoops);

        Tween.Update(0.5f);         // t = 1.5, half way through cycle two
        Assert.Equal(5f, box.Value, 3);
        Assert.Equal(1, t.CompletedLoops);

        Advance(3, 0.5f);           // t = 3.0, all three cycles done
        Assert.Equal(10f, box.Value, 3);
        Assert.False(t.IsActive);
    }

    [Fact]
    public void YoyoLoopsRunBackwardsOnOddCycles()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetLoops(2, LoopType.Yoyo).SetAutoKill(false);

        Advance(4, 0.25f);          // t = 1.0, end of the outbound pass
        Assert.Equal(10f, box.Value, 3);
        Assert.Equal(1, t.CompletedLoops);

        Tween.Update(0.25f);        // t = 1.25, a quarter of the way back
        Assert.Equal(7.5f, box.Value, 3);

        Advance(2, 0.25f);          // t = 1.75
        Assert.Equal(2.5f, box.Value, 3);

        Tween.Update(0.25f);        // t = 2.0, home again
        Assert.True(t.IsComplete);
        Assert.Equal(0f, box.Value, 3);
    }

    [Fact]
    public void IncrementalLoopsKeepAddingTheDelta()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetLoops(2, LoopType.Incremental).SetAutoKill(false);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);

        Advance(2, 0.5f);           // t = 1.5: one whole cycle plus half of the next
        Assert.Equal(15f, box.Value, 3);

        Tween.Update(0.5f);         // t = 2.0
        Assert.Equal(20f, box.Value, 3);
        Assert.True(t.IsComplete);
    }

    [Fact]
    public void StepCompleteFiresOncePerCycle()
    {
        var box = new Box();
        var steps = new Box();

        LinearTween(box)
            .SetLoops(3)
            .OnStepComplete(steps, static s => s.Count++);

        Advance(6, 0.5f);           // t = 3.0, three cycles

        Assert.Equal(3, steps.Count);
    }

    [Fact]
    public void StepCompleteFiresOncePerTickEvenIfTheTickCrossedSeveralCycles()
    {
        var box = new Box();
        var steps = new Box();

        LinearTween(box, 0.1f)
            .SetLoops(-1)
            .OnStepComplete(steps, static s => s.Count++);

        Tween.Update(1f);           // ten cycles in one go

        // Documented behaviour: one report per tick, not one per cycle crossed.
        Assert.Equal(1, steps.Count);
    }

    [Fact]
    public void InfiniteLoopsNeverComplete()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetLoops(-1);

        Advance(20, 0.25f);         // t = 5.0

        Assert.True(t.IsActive);
        Assert.False(t.IsComplete);
        Assert.Equal(5, t.CompletedLoops);
    }

    [Fact]
    public void FullDurationCountsAnInfiniteTweenAsOneCycle()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetDelay(0.5f).SetLoops(-1);

        Assert.Equal(1.5f, t.FullDuration, 3);
    }

    [Fact]
    public void ZeroLoopsIsTreatedAsOne()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetLoops(0);

        Advance(2, 0.5f);
        Assert.False(t.IsActive);
        Assert.Equal(10f, box.Value, 3);
    }

    [Fact]
    public void RestartRewindsAndKeepsPlaying()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Tween.Update(0.75f);
        Assert.Equal(7.5f, box.Value, 3);

        t.Restart();
        Assert.Equal(0f, box.Value, 3);
        Assert.True(t.IsPlaying);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void RewindRewindsAndPauses()
    {
        var box = new Box();
        var rewound = new Box();
        Tween t = LinearTween(box).OnRewind(rewound, static b => b.Count++);

        Tween.Update(0.75f);
        t.Rewind();

        Assert.Equal(0f, box.Value, 3);
        Assert.False(t.IsPlaying);
        Assert.Equal(1, rewound.Count);

        Tween.Update(0.5f);
        Assert.Equal(0f, box.Value, 3);
    }

    [Fact]
    public void GlobalTimeScaleScalesEveryDependentTween()
    {
        var scaled = new Box();
        var independent = new Box();

        LinearTween(scaled);
        LinearTween(independent).SetUpdate(isIndependentUpdate: true);

        Tween.GlobalTimeScale = 0.5f;
        Tween.Update(0.5f);

        Assert.Equal(2.5f, scaled.Value, 3);        // half speed
        Assert.Equal(5f, independent.Value, 3);     // unscaled

        Tween.GlobalTimeScale = 1f;
    }

    [Fact]
    public void PerTweenTimeScaleStacksOnTheGlobalOne()
    {
        var box = new Box();
        LinearTween(box).SetTimeScale(2f);

        Tween.GlobalTimeScale = 0.5f;
        Tween.Update(0.5f);

        Assert.Equal(5f, box.Value, 3);             // 0.5 * 0.5 * 2 = 0.5 of the way
        Tween.GlobalTimeScale = 1f;
    }
}
