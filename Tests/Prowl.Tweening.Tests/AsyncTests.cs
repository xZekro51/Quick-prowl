// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// Awaiting tweens. Continuations are resumed by the tick, inline, so every assertion here can be
/// made synchronously right after the tick that should have resumed them.
/// </summary>
public sealed class AsyncTests : TweenTestBase
{
    [Fact]
    public async Task WaitingForCompletionResumesOnTheTickThatCompletesIt()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Task wait = t.AsyncWaitForCompletion().AsTask();
        Assert.False(wait.IsCompleted);

        Tween.Update(0.5f);
        Assert.False(wait.IsCompleted);

        Tween.Update(0.5f);
        Assert.True(wait.IsCompleted);

        await wait;
        Assert.Equal(10f, box.Value, 3);
    }

    [Fact]
    public async Task WaitingForATweenThatGetsKilledResumesRatherThanHanging()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Task wait = t.AsyncWaitForCompletion().AsTask();
        t.Kill();

        Tween.Update(0.016f);

        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task WaitingForKillOnlyEndsWhenTheTweenDies()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetAutoKill(false);

        Task wait = t.AsyncWaitForKill().AsTask();

        Advance(2, 0.5f);
        Assert.True(t.IsComplete);
        Assert.False(wait.IsCompleted);      // complete, but still alive

        t.Kill();
        Tween.Update(0.016f);
        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task WaitingForStartClearsTheDelay()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetDelay(0.5f);

        Task wait = t.AsyncWaitForStart().AsTask();

        Tween.Update(0.25f);
        Assert.False(wait.IsCompleted);

        Tween.Update(0.5f);
        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task WaitingForAPositionResumesOnceThePlayheadPassesIt()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetAutoKill(false);

        Task wait = t.AsyncWaitForPosition(0.75f).AsTask();

        Advance(2, 0.25f);
        Assert.False(wait.IsCompleted);

        Tween.Update(0.25f);
        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task WaitingForElapsedLoopsCountsCycles()
    {
        var box = new Box();
        Tween t = LinearTween(box).SetLoops(4);

        Task wait = t.AsyncWaitForElapsedLoops(2).AsTask();

        Advance(2, 0.5f);                    // one cycle
        Assert.False(wait.IsCompleted);

        Advance(2, 0.5f);                    // two
        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task CancellingAWaitThrowsAndLeavesTheTweenAlone()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        using var cts = new CancellationTokenSource();
        Task wait = t.AsyncWaitForCompletion(cts.Token).AsTask();

        cts.Cancel();
        Tween.Update(0.016f);

        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() => wait);

        // Cancelling the wait must not touch the tween itself.
        Assert.True(t.IsActive);
    }

    [Fact]
    public void AwaitingAnAlreadyDeadHandleCostsNothing()
    {
        Assert.True(Tween.None.AsyncWaitForCompletion().GetAwaiter().IsCompleted);
        Assert.True(Tween.None.AsyncWaitForKill().GetAwaiter().IsCompleted);
        Assert.Equal(0, Tween.PendingAwaits);
    }

    [Fact]
    public async Task WaitForSecondsRunsOnUnscaledTime()
    {
        Tween.GlobalTimeScale = 0f;

        Task wait = Tween.AsyncWaitForSeconds(0.5f).AsTask();

        Tween.Update(0.25f);
        Assert.False(wait.IsCompleted);

        Tween.Update(0.25f);
        Assert.True(wait.IsCompleted);
        await wait;

        Tween.GlobalTimeScale = 1f;
    }

    [Fact]
    public async Task SequencesCanBeAwaitedToo()
    {
        var box = new Box();

        Sequence seq = Tween.Sequence().SetEase(Ease.Linear);
        seq.Append(Tween.To(0f, 10f, 1f).Bind(box, SetValue).SetEase(Ease.Linear));

        Task wait = seq.AsyncWaitForCompletion().AsTask();

        Tween.Update(0.5f);
        Assert.False(wait.IsCompleted);

        Tween.Update(0.5f);
        Assert.True(wait.IsCompleted);
        await wait;

        Assert.Equal(10f, box.Value, 3);
    }

    [Fact]
    public void PendingWaitsAreTrackedAndReleased()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Assert.Equal(0, Tween.PendingAwaits);

        Task wait = t.AsyncWaitForCompletion().AsTask();
        Assert.Equal(1, Tween.PendingAwaits);

        Advance(2, 0.5f);
        Assert.True(wait.IsCompleted);
        Assert.Equal(0, Tween.PendingAwaits);
    }

    [Fact]
    public void ResetDropsPendingWaitsWithoutResumingThem()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Task wait = t.AsyncWaitForCompletion().AsTask();
        Assert.Equal(1, Tween.PendingAwaits);

        TweenManager.Reset();

        Assert.Equal(0, Tween.PendingAwaits);
        Assert.False(wait.IsCompleted);
    }
}
