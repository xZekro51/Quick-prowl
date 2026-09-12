// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading.Tasks;

using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Tweening;

using Xunit;

// The engine and the library are both static state, so tests cannot run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Prowl.Tweening.Integration.Tests;

/// <summary>
/// Drives the library through Prowl's play-mode lifecycle the way the editor does: scenes loaded and
/// swapped at the end of a frame, the Tween Master spawned by the bootstrap and preserved across
/// loads, and play mode left by clearing <see cref="Application.IsPlaying"/> <i>before</i> preserved
/// objects are destroyed.
/// <para>
/// That last ordering is what these tests exist for. Every gated component callback - OnDisable
/// included - is skipped by the teardown that follows it, which once left the library convinced a
/// destroyed master was still ticking: tweens worked in the first play session and never again.
/// </para>
/// </summary>
public sealed class PlayModeTests : IDisposable
{
    private sealed class Box
    {
        public float Value;
    }

    private static readonly Action<float, Box> SetValue = static (v, b) => b.Value = v;

    /// <summary>Every frame advances a quarter of a second, so a one-second linear tween moves 2.5 per frame.</summary>
    private readonly TimeData _time = new() { DeltaTime = 0.25f, UnscaledDeltaTime = 0.25f };

    public PlayModeTests()
    {
        Application.IsEditor = true;
        Application.IsPaused = false;
        Application.IsPlaying = false;
        Time.TimeStack.Push(_time);

        // Exactly what the module initializer installs when the game assembly loads.
        TweenMaster.Install();

        Scene.Load(new Scene { Name = "Editor" });
        EndFrame();
    }

    public void Dispose()
    {
        TweenMaster.Driver?.GameObject.Dispose();
        Application.IsPlaying = false;
        TweenManager.Reset();

        if (Time.TimeStack.Count > 0 && ReferenceEquals(Time.TimeStack.Peek(), _time))
            Time.TimeStack.Pop();
    }

    #region Editor stand-ins

    /// <summary>The end of a frame in the game loop: the destroy queue drains, then a queued scene load applies.</summary>
    private static void EndFrame()
    {
        EngineObject.ProcessDestroyed();
        Scene.ProcessPendingLoad();
    }

    /// <summary>One frame of gameplay: Start, Update and LateUpdate for the current scene, then the end of the frame.</summary>
    private static void Frame()
    {
        Scene.Current.Update();
        EndFrame();
    }

    /// <summary>
    /// Mirrors <c>EditorApplication.EnterPlayMode</c>: the flag goes up, preserved objects are
    /// destroyed, and a fresh play scene is loaded.
    /// </summary>
    private static void EnterPlayMode()
    {
        Application.IsPlaying = true;
        DestroyPreserved();
        Scene.Load(new Scene { Name = "Play" });
        EndFrame();
    }

    /// <summary>
    /// Mirrors <c>EditorApplication.ExitPlayMode</c>, in its order: the flag comes down first, then
    /// preserved objects are destroyed and the editor scene comes back.
    /// </summary>
    private static void ExitPlayMode()
    {
        Application.IsPlaying = false;
        DestroyPreserved();
        Scene.Load(new Scene { Name = "Editor" });
        EndFrame();
    }

    /// <summary>
    /// <c>Scene.DestroyPreserved()</c> is internal to the engine. For the one preserved object these
    /// tests create, its non-immediate path is exactly this: a queued destroy, drained at the end of
    /// the frame.
    /// </summary>
    private static void DestroyPreserved() => TweenMaster.Driver?.GameObject.Destroy();

    private static Tween LinearTween(Box box)
        => Tween.To(0f, 10f, 1f).Bind(box, SetValue).SetEase(Ease.Linear);

    #endregion

    [Fact]
    public void TweensStillRunInTheNextPlaySession()
    {
        EnterPlayMode();

        var first = new Box();
        LinearTween(first);
        Assert.NotNull(TweenMaster.Driver);

        Frame();
        Assert.Equal(2.5f, first.Value, 3);

        ExitPlayMode();
        Assert.Null(TweenMaster.Driver);

        EnterPlayMode();

        var second = new Box();
        LinearTween(second);

        // The regression: the library still believed the destroyed master was driving it, never
        // asked for a new one, and the tween sat at its start value forever.
        Assert.NotNull(TweenMaster.Driver);

        Frame();
        Assert.Equal(2.5f, second.Value, 3);
    }

    [Fact]
    public void EveryPlaySessionGetsItsOwnWorkingDriver()
    {
        for (int session = 0; session < 4; session++)
        {
            EnterPlayMode();

            var box = new Box();
            LinearTween(box);
            Frame();
            Frame();

            Assert.Equal(5f, box.Value, 3);
            ExitPlayMode();
        }
    }

    [Fact]
    public void LeavingPlayModeDropsWhatTheSessionLeftRunning()
    {
        EnterPlayMode();

        var leftover = new Box();
        Tween looping = LinearTween(leftover).SetLoops(-1);
        Task pending = looping.AsyncWaitForKill().AsTask();
        Tween.GlobalTimeScale = 0.5f;

        Frame();
        Assert.Equal(1, Tween.TotalActive);
        Assert.Equal(1, Tween.PendingAwaits);

        ExitPlayMode();

        // Nothing survives the session: not the infinite tween, not its await, not the global state.
        Assert.Equal(0, Tween.TotalActive);
        Assert.Equal(0, Tween.PendingAwaits);
        Assert.Equal(1f, Tween.GlobalTimeScale);
        Assert.False(looping.IsActive);

        EnterPlayMode();

        float frozenAt = leftover.Value;
        var fresh = new Box();
        LinearTween(fresh);
        Frame();

        // The next session's driver must not pick the old tween back up and write into an object
        // that belonged to the play scene that has already been thrown away.
        Assert.Equal(frozenAt, leftover.Value);
        Assert.Equal(2.5f, fresh.Value, 3);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public void DestroyingTheMasterDuringPlayStartsOverWithAFreshOne()
    {
        EnterPlayMode();

        var early = new Box();
        LinearTween(early);
        TweenMaster? original = TweenMaster.Driver;
        Assert.NotNull(original);

        // During play the gated OnDisable does run, and before OnDispose - the driver has to release
        // itself correctly on both paths.
        original.GameObject.Destroy();
        EndFrame();
        Assert.Null(TweenMaster.Driver);

        var late = new Box();
        LinearTween(late);
        Assert.NotNull(TweenMaster.Driver);
        Assert.NotSame(original, TweenMaster.Driver);

        // Deliberate: the driver's lifetime is the library's lifetime, so tweens created under the
        // destroyed one went with it.
        Assert.Equal(1, Tween.TotalActive);

        Frame();
        Assert.Equal(2.5f, late.Value, 3);
    }

    [Fact]
    public void AnIdleSecondMasterCanBeDestroyedWithoutDisturbingTheDriver()
    {
        EnterPlayMode();

        var box = new Box();
        LinearTween(box);
        TweenMaster? driver = TweenMaster.Driver;
        Assert.NotNull(driver);

        // A hand-placed second master stays idle rather than ticking everything twice.
        var extra = new GameObject("Second Tween Master");
        extra.AddComponent<TweenMaster>();
        Scene.Current.Add(extra);

        Frame();
        Assert.Equal(2.5f, box.Value, 3);

        extra.Destroy();
        EndFrame();

        Assert.Same(driver, TweenMaster.Driver);
        Assert.Equal(1, Tween.TotalActive);

        Frame();
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void SceneLoadsDuringPlayDoNotInterruptTweens()
    {
        EnterPlayMode();

        var box = new Box();
        LinearTween(box);
        Frame();

        // The master is preserved, so an ordinary level change inside one play session keeps it.
        Scene.Load(new Scene { Name = "Level 2" });
        EndFrame();

        Assert.NotNull(TweenMaster.Driver);
        Frame();
        Assert.Equal(5f, box.Value, 3);
    }
}
