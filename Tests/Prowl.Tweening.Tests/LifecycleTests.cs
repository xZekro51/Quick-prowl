// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// Reclamation, lifetime links, and the ordering rules around callbacks that reach back into the
/// library while the update loop is still running.
/// </summary>
public sealed class LifecycleTests : TweenTestBase
{
    private sealed class Owner
    {
        public bool Alive = true;
    }

    private sealed class Holder
    {
        public Tween Tween;
        public readonly List<string> Log = [];
    }

    [Fact]
    public void EveryTickTypeSweeps()
    {
        var box = new Box();

        Tween first = LinearTween(box);
        int slot = first.Slot;
        first.Kill();

        // A host that only ever drives the fixed step still has to reclaim dead entries, otherwise
        // they pile up forever and keep their target objects alive with them.
        Tween.FixedUpdate(0.02f);

        Tween second = LinearTween(box);
        Assert.Equal(slot, second.Slot);
    }

    [Fact]
    public void LateAndManualTicksSweepToo()
    {
        var box = new Box();

        foreach (Action tick in new Action[] { () => Tween.LateUpdate(0.016f), () => Tween.ManualUpdate(0.016f) })
        {
            Tween first = LinearTween(box);
            int slot = first.Slot;
            first.Kill();

            tick();

            Tween second = LinearTween(box);
            Assert.Equal(slot, second.Slot);
            second.Kill();
            Tween.Update(0.016f);
        }
    }

    [Fact]
    public void ALinkedTweenDiesWithItsOwnerBeforeWritingAgain()
    {
        var owner = new Owner();
        var box = new Box();

        Tween t = LinearTween(box).SetLink(owner, static o => o.Alive);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
        Assert.True(t.IsActive);

        owner.Alive = false;

        // The link is checked at the start of the tick, so the setter must not run once more.
        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
        Assert.False(t.IsActive);
        Assert.Equal(0, Tween.TotalActive);
    }

    [Fact]
    public void AnUnlinkedTweenIsNotChargedForTheLinkCheck()
    {
        var box = new Box();
        LinearTween(box);

        Assert.Equal(0, FloatStorage.LinkedCount);

        var owner = new Owner();
        LinearTween(box).SetLink(owner, static o => o.Alive);
        Assert.Equal(1, FloatStorage.LinkedCount);
    }

    [Fact]
    public void KillingALinkedTweenReleasesItsLinkSlot()
    {
        var owner = new Owner();
        var box = new Box();

        Tween t = LinearTween(box).SetLink(owner, static o => o.Alive);
        Assert.Equal(1, FloatStorage.LinkedCount);

        t.Kill();
        Assert.Equal(0, FloatStorage.LinkedCount);
    }

    [Fact]
    public void CallbacksStopOnceTheSetterKillsTheTween()
    {
        var h = new Holder();
        h.Tween = Tween.To(0f, 10f, 1f)
            .Bind(h, static (v, s) => { s.Log.Add("set"); s.Tween.Kill(); })
            .SetEase(Ease.Linear)
            .OnUpdate(h, static s => s.Log.Add("update"))
            .OnStepComplete(h, static s => s.Log.Add("step"))
            .OnComplete(h, static s => s.Log.Add("complete"))
            .OnKill(h, static s => s.Log.Add("kill"));

        Tween.Update(0.5f);

        // The setter killed it, so nothing after the setter runs - except OnKill, which is the
        // kill itself reporting in.
        Assert.Equal(new[] { "set", "kill" }, h.Log);
    }

    [Fact]
    public void OnStartFiresBeforeTheFirstValue()
    {
        var h = new Holder();
        h.Tween = Tween.To(0f, 10f, 1f)
            .Bind(h, static (v, s) => s.Log.Add("set"))
            .SetEase(Ease.Linear)
            .OnStart(h, static s => s.Log.Add("start"));

        Tween.Update(0.5f);
        Tween.Update(0.5f);

        Assert.Equal(new[] { "start", "set", "set" }, h.Log);
    }

    [Fact]
    public void AFrozenTweenStillReportsThatItStarted()
    {
        var box = new Box();
        var started = new Box();

        Tween t = LinearTween(box)
            .SetTimeScale(0f)
            .OnStart(started, static s => s.Count++);

        Tween.Update(0.5f);
        Tween.Update(0.5f);

        // Frozen, not asleep: it says it started and shows its start value, exactly once.
        Assert.Equal(1, started.Count);
        Assert.Equal(0f, box.Value, 3);
        Assert.False(t.IsComplete);
        Assert.True(t.IsActive);
    }

    [Fact]
    public void ACallbackThatCreatesATweenDoesNotDisturbTheLoop()
    {
        var box = new Box();
        var spawned = new Box();

        Tween.To(0f, 10f, 0.5f)
            .Bind(box, SetValue)
            .SetEase(Ease.Linear)
            .OnComplete(spawned, static s =>
            {
                s.Count++;
                Tween.To(0f, 1f, 10f).Bind(s, static (v, b) => b.Value = v);
            });

        Tween.Update(0.5f);

        // Spawned from inside the loop: it exists, but it starts on the next tick.
        Assert.Equal(1, spawned.Count);
        Assert.Equal(1, Tween.TotalActive);

        Tween.Update(0.016f);
        Assert.Equal(1, Tween.TotalActive);
    }

    [Fact]
    public void ACallbackMayKillOtherTweens()
    {
        var box = new Box();

        Tween other = Tween.To(0f, 1f, 10f).Bind(box, SetValue);
        var holder = new Holder { Tween = other };

        Tween.To(0f, 1f, 0.5f)
            .Bind(box, SetValue)
            .OnComplete(holder, static s => s.Tween.Kill());

        Tween.Update(0.5f);

        Assert.False(other.IsActive);
        Assert.Equal(0, Tween.TotalActive);
    }

    private sealed class Counters
    {
        public int Paused;
        public int Played;
    }

    [Fact]
    public void PauseAndPlayFireTheirCallbacksOnce()
    {
        var box = new Box();
        var counters = new Counters();

        // One state object for both callbacks - see StateIsSharedByEveryTypedCallback below.
        Tween t = LinearTween(box)
            .OnPause(counters, static c => c.Paused++)
            .OnPlay(counters, static c => c.Played++);

        t.Pause();
        t.Pause();
        Assert.Equal(1, counters.Paused);

        t.Play();
        t.Play();
        Assert.Equal(1, counters.Played);
    }

    [Fact]
    public void StateIsSharedByEveryTypedCallbackAndSayingOtherwiseWarns()
    {
        var warnings = new List<string>();
        TweenManager.Diagnostics = warnings.Add;

        var box = new Box();
        var first = new Box();
        var second = new Box();

        // Deliberate misuse: one state slot per tween keeps the callback record small, so the
        // second state replaces the first. It used to do that in complete silence.
        LinearTween(box)
            .OnStart(first, static b => b.Count++)
            .OnComplete(second, static b => b.Count++);

        Assert.Single(warnings);
        Assert.Contains("share a single state object", warnings[0]);
    }

    [Fact]
    public void PassingTheSameStateTwiceIsFine()
    {
        var warnings = new List<string>();
        TweenManager.Diagnostics = warnings.Add;

        var box = new Box();
        LinearTween(box)
            .OnStart(box, static b => b.Count++)
            .OnComplete(box, static b => b.Count++);

        Assert.Empty(warnings);

        Advance(2, 0.5f);
        Assert.Equal(2, box.Count);
    }

    [Fact]
    public void KillAllByTargetOnlyTakesTheTaggedOnes()
    {
        var wanted = new Box();
        var other = new Box();

        LinearTween(wanted).SetTarget(wanted);
        LinearTween(wanted).SetTarget(wanted);
        LinearTween(other).SetTarget(other);
        LinearTween(other);

        Assert.Equal(2, Tween.KillAll(wanted));
        Assert.Equal(2, Tween.TotalActive);
    }

    [Fact]
    public void KillAllWithNoFilterTakesEverything()
    {
        var box = new Box();
        LinearTween(box);
        LinearTween(box).SetId("x");
        LinearTween(box).SetTarget(box);

        Assert.Equal(3, Tween.KillAll());
        Assert.Equal(0, Tween.TotalActive);
    }

    [Fact]
    public void KillWithCompleteJumpsToTheEndFirst()
    {
        var box = new Box();
        Tween t = LinearTween(box);

        Tween.Update(0.25f);
        Assert.Equal(2.5f, box.Value, 3);

        t.Kill(complete: true);

        Assert.Equal(10f, box.Value, 3);
        Assert.False(t.IsActive);
    }

    [Fact]
    public void ResetDropsEverythingAndLeavesTheLibraryUsable()
    {
        var box = new Box();
        LinearTween(box);
        LinearTween(box);
        Assert.Equal(2, Tween.TotalActive);

        TweenManager.Reset();
        Assert.Equal(0, Tween.TotalActive);

        // Still usable afterwards: the next tween registers a fresh storage.
        Tween t = LinearTween(box);
        Assert.True(t.IsActive);
        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void TheBootstrapIsAskedOncePerRuntime()
    {
        TweenManager.Reset();

        int asked = 0;
        TweenManager.RuntimeBootstrap = () => asked++;

        var box = new Box();
        for (int i = 0; i < 25; i++)
            LinearTween(box);

        // Latched: the host's check may be expensive, so it must not run per creation.
        Assert.Equal(1, asked);

        TweenManager.InvalidateRuntime();
        LinearTween(box);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void ResetReArmsTheBootstrap()
    {
        int asked = 0;
        TweenManager.RuntimeBootstrap = () => asked++;

        var box = new Box();
        LinearTween(box);
        Assert.Equal(1, asked);

        // What the Prowl driver does when its play session ends: the next tween has to ask the host
        // for a driver again, or nothing will ever tick it.
        TweenManager.Reset();
        LinearTween(box);
        Assert.Equal(2, asked);
    }
}
