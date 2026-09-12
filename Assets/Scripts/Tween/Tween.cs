// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// A handle to a running tween. This is a 12-byte struct, not an object: copying it around,
/// storing it in a field or passing it to a method never allocates, and a handle to a tween that
/// has already died simply stops resolving instead of dangling.
/// </summary>
/// <remarks>
/// Every method is safe to call on a dead or default handle - it becomes a no-op.
/// </remarks>
public readonly partial struct Tween : IEquatable<Tween>
{
    internal readonly int StorageId;
    internal readonly int Slot;
    internal readonly int Version;

    /// <summary>An invalid handle. Every operation on it is a no-op.</summary>
    public static readonly Tween None = default;

    internal Tween(int storageId, int slot, int version)
    {
        StorageId = storageId;
        Slot = slot;
        Version = version;
    }

    /// <summary>
    /// Resolves the handle to a storage <i>and</i> a dense index in one go, so a fluent chain pays
    /// for one slot-table lookup per call instead of two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGet([NotNullWhen(true)] out ITweenStorage? storage, out int dense)
    {
        if (Version != 0)
        {
            ITweenStorage? s = TweenManager.Resolve(StorageId);
            if (s != null && s.TryResolve(Slot, Version, out dense))
            {
                storage = s;
                return true;
            }
        }

        storage = null;
        dense = -1;
        return false;
    }

    /// <summary>
    /// True while a dense index still refers to a living tween. Valid to call after user code has
    /// run: the index itself stays put until the next sweep, only the flags can have changed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StillAlive(ITweenStorage storage, int dense)
        => (storage.CoreAt(dense).Flags & TweenFlags.Dead) == 0;

    #region State

    /// <summary>True while the tween exists (it may still be paused).</summary>
    public bool IsActive => TryGet(out _, out _);

    /// <summary>True when the tween exists and is not paused.</summary>
    public bool IsPlaying
        => TryGet(out ITweenStorage? s, out int d) && (s.CoreAt(d).Flags & TweenFlags.Paused) == 0;

    /// <summary>True once the tween has reached the end of its last loop.</summary>
    public bool IsComplete
        => TryGet(out ITweenStorage? s, out int d) && (s.CoreAt(d).Flags & TweenFlags.Completed) != 0;

    /// <summary>Elapsed time including delay and completed loops.</summary>
    public float Elapsed
    {
        get
        {
            if (!TryGet(out ITweenStorage? s, out int d)) return 0f;
            ref TweenCore c = ref s.CoreAt(d);
            return c.DelayElapsed + c.Duration * c.CompletedLoops + c.Position;
        }
    }

    /// <summary>Length of a single loop cycle, excluding delay.</summary>
    public float Duration => TryGet(out ITweenStorage? s, out int d) ? s.CoreAt(d).Duration : 0f;

    /// <summary>Total length including delay and all loops. Infinite loops count as one.</summary>
    public float FullDuration => TryGet(out ITweenStorage? s, out int d) ? s.CoreAt(d).FullDuration : 0f;

    /// <summary>Number of loop cycles finished so far.</summary>
    public int CompletedLoops => TryGet(out ITweenStorage? s, out int d) ? s.CoreAt(d).CompletedLoops : 0;

    /// <summary>Per-tween speed multiplier. 2 plays twice as fast, 0 freezes it.</summary>
    public float TimeScale
    {
        get => TryGet(out ITweenStorage? s, out int d) ? s.CoreAt(d).TimeScale : 1f;
        set
        {
            if (TryGet(out ITweenStorage? s, out int d))
                s.CoreAt(d).TimeScale = value;
        }
    }

    #endregion

    #region Settings

    /// <summary>Sets the easing equation. <see cref="Ease.Unset"/> resolves to <see cref="DefaultEase"/>.</summary>
    public Tween SetEase(Ease ease)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            c.Ease = Resolve(ease);
            c.Flags &= ~TweenFlags.CustomEase;
        }
        return this;
    }

    /// <summary>Sets the easing equation with a Back overshoot / Elastic amplitude.</summary>
    public Tween SetEase(Ease ease, float overshootOrAmplitude)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            c.Ease = Resolve(ease);
            c.EaseOvershoot = overshootOrAmplitude;
            c.Flags &= ~TweenFlags.CustomEase;
        }
        return this;
    }

    /// <summary>Sets the easing equation with an Elastic amplitude and period.</summary>
    public Tween SetEase(Ease ease, float amplitude, float period)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            c.Ease = Resolve(ease);
            c.EaseOvershoot = amplitude;
            c.EasePeriod = period;
            c.Flags &= ~TweenFlags.CustomEase;
        }
        return this;
    }

    /// <summary>Sets a custom easing function. Cache the delegate to keep this allocation-free.</summary>
    public Tween SetEase(EaseFunction customEase)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            s.EventsAt(d).CustomEase = customEase;
            ref TweenCore c = ref s.CoreAt(d);
            c.Ease = Ease.Custom;
            c.Flags |= TweenFlags.CustomEase;
        }
        return this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Ease Resolve(Ease ease)
        => ease != Ease.Unset ? ease : (DefaultEase == Ease.Unset ? Ease.Linear : DefaultEase);

    /// <summary>Sets the loop count (-1 for infinite) and how each cycle restarts.</summary>
    public Tween SetLoops(int loops, LoopType loopType = LoopType.Restart)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            c.Loops = loops == 0 ? 1 : loops;
            c.LoopType = loopType;
        }
        return this;
    }

    /// <summary>Delays the start of the tween. Negative, NaN and infinite values mean "no delay".</summary>
    public Tween SetDelay(float delay)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            c.Delay = float.IsFinite(delay) && delay > 0f ? delay : 0f;
            c.DelayElapsed = 0f;
        }
        return this;
    }

    /// <summary>When true (the default) the tween is destroyed as soon as it completes.</summary>
    public Tween SetAutoKill(bool autoKill = true)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            if (autoKill) c.Flags |= TweenFlags.AutoKill;
            else c.Flags &= ~TweenFlags.AutoKill;
        }
        return this;
    }

    /// <summary>Chooses which manager tick drives the tween, and whether it ignores time scale.</summary>
    public Tween SetUpdate(UpdateType updateType, bool isIndependentUpdate = false)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            s.SetUpdateTypeAt(d, updateType);
            ref TweenCore c = ref s.CoreAt(d);
            if (isIndependentUpdate) c.Flags |= TweenFlags.Independent;
            else c.Flags &= ~TweenFlags.Independent;
            TweenManager.NotifyUpdateType(updateType);
        }
        return this;
    }

    /// <summary>Makes the tween ignore <see cref="TweenManager.TimeScale"/>.</summary>
    public Tween SetUpdate(bool isIndependentUpdate)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            if (isIndependentUpdate) c.Flags |= TweenFlags.Independent;
            else c.Flags &= ~TweenFlags.Independent;
        }
        return this;
    }

    /// <summary>Tags the tween so <see cref="KillAll(object, bool)"/> and friends can find it later.</summary>
    public Tween SetId(object id)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            s.TagsAt(d).Id = id;
            s.CoreAt(d).Flags |= TweenFlags.HasTags;
        }
        return this;
    }

    /// <summary>Associates the tween with an object, for target-based kills.</summary>
    public Tween SetTarget(object target)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            s.TagsAt(d).Target = target;
            s.CoreAt(d).Flags |= TweenFlags.HasTags;
        }
        return this;
    }

    /// <summary>
    /// Ties the tween's life to <paramref name="owner"/>. Before every tick the library asks
    /// <paramref name="isAlive"/> whether the owner is still there, and kills the tween the first
    /// time the answer is no - so a tween can never write to an object that has been destroyed.
    /// </summary>
    /// <remarks>
    /// The check runs at the start of the tick, before any value is applied. Use a <c>static</c>
    /// lambda and pass the owner as <paramref name="owner"/> to keep this allocation-free:
    /// <c>SetLink(go, static g =&gt; g.IsValid())</c>.
    /// </remarks>
    public Tween SetLink<TOwner>(TOwner owner, Func<TOwner, bool> isAlive) where TOwner : class
    {
        ArgumentNullException.ThrowIfNull(isAlive);

        if (TryGet(out ITweenStorage? s, out int d))
        {
            // Same reinterpret as the callback overloads: a delegate over a reference type shares
            // its calling convention with one over object.
            Func<object?, bool> erased = Unsafe.As<Func<TOwner, bool>, Func<object?, bool>>(ref isAlive);
            ref TweenTags tags = ref s.TagsAt(d);
            tags.LinkOwner = owner;
            tags.LinkAlive = erased;
            s.MarkLinkedAt(d);
        }
        return this;
    }

    /// <summary>Per-tween speed multiplier (same as assigning <see cref="TimeScale"/>).</summary>
    public Tween SetTimeScale(float timeScale)
    {
        TimeScale = timeScale;
        return this;
    }

    /// <summary>
    /// Treats the end value as an offset from the start value: <c>end = start + end</c>.
    /// Call it before the tween has run.
    /// </summary>
    public Tween SetRelative(bool isRelative = true)
    {
        if (isRelative && TryGet(out ITweenStorage? s, out int d))
            s.MakeRelativeAt(d);
        return this;
    }

    /// <summary>
    /// Swaps start and end so the tween runs <i>from</i> the given value <i>to</i> the current one,
    /// and immediately applies the new start value.
    /// </summary>
    public Tween From()
    {
        if (TryGet(out ITweenStorage? s, out int d))
            s.MakeFromAt(d);
        return this;
    }

    /// <summary>Applies <see cref="SetRelative"/> and then <see cref="From()"/> in one call.</summary>
    public Tween From(bool isRelative)
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            if (isRelative)
                s.MakeRelativeAt(d);
            s.MakeFromAt(d);
        }
        return this;
    }

    #endregion

    #region Callbacks

    /// <summary>Fired the first time the tween runs, after any delay.</summary>
    public Tween OnStart(Action callback) => SetCallback(callback, CallbackKind.Start);

    /// <summary>Fired when the tween is played (or resumed).</summary>
    public Tween OnPlay(Action callback) => SetCallback(callback, CallbackKind.Play);

    /// <summary>Fired when the tween is paused.</summary>
    public Tween OnPause(Action callback) => SetCallback(callback, CallbackKind.Pause);

    /// <summary>Fired every time the tween's value is applied.</summary>
    public Tween OnUpdate(Action callback) => SetCallback(callback, CallbackKind.Update);

    /// <summary>
    /// Fired at the end of each loop cycle. Fires once per tick even if the tick was long enough
    /// to cross several cycles.
    /// </summary>
    public Tween OnStepComplete(Action callback) => SetCallback(callback, CallbackKind.StepComplete);

    /// <summary>Fired when the tween reaches the end of its final loop.</summary>
    public Tween OnComplete(Action callback) => SetCallback(callback, CallbackKind.Complete);

    /// <summary>Fired when the tween is destroyed, for any reason.</summary>
    public Tween OnKill(Action callback) => SetCallback(callback, CallbackKind.Kill);

    /// <summary>Fired when the tween is rewound.</summary>
    public Tween OnRewind(Action callback) => SetCallback(callback, CallbackKind.Rewind);

    /// <summary>
    /// Allocation-free callback: pass the object you need instead of capturing it, and use a
    /// <c>static</c> lambda. All <c>On*</c> state overloads on one tween share a single state object.
    /// </summary>
    public Tween OnStart<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Start);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnPlay<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Play);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnPause<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Pause);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnUpdate<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Update);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnStepComplete<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.StepComplete);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnComplete<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Complete);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnKill<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Kill);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnRewind<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Rewind);

    private enum CallbackKind : byte { Start, Play, Pause, Update, StepComplete, Complete, Kill, Rewind }

    private Tween SetCallback(Delegate? callback, CallbackKind kind)
    {
        if (!TryGet(out ITweenStorage? s, out int d))
            return this;

        Assign(ref s.EventsAt(d), callback, kind);
        s.CoreAt(d).Flags |= TweenFlags.HasCallbacks;
        return this;
    }

    private Tween SetCallback<TState>(TState state, Action<TState> callback, CallbackKind kind) where TState : class
    {
        if (!TryGet(out ITweenStorage? s, out int d))
            return this;

        // A delegate over a reference type has the same calling convention as one over object,
        // so the reinterpret is free and avoids wrapping the user's callback in a closure.
        Action<object?> erased = Unsafe.As<Action<TState>, Action<object?>>(ref callback);

        ref TweenEvents ev = ref s.EventsAt(d);

        // One state object per tween, shared by every On*(state, callback) overload - that is what
        // keeps the callback record small. Handing a second callback a *different* state silently
        // re-points the first one at it, so say something rather than let it pass.
        if (ev.State != null && !ReferenceEquals(ev.State, state))
        {
            TweenManager.Warn($"[Tween] On{kind}(state, ...) replaced the callback state of this tween. "
                            + "All On*(state, callback) overloads on one tween share a single state "
                            + "object - pass the same state to each, or use the capturing overloads.");
        }

        ev.State = state;
        Assign(ref ev, erased, kind);
        s.CoreAt(d).Flags |= TweenFlags.HasCallbacks;
        return this;
    }

    private static void Assign(ref TweenEvents ev, Delegate? callback, CallbackKind kind)
    {
        switch (kind)
        {
            case CallbackKind.Start: ev.OnStart = callback; break;
            case CallbackKind.Play: ev.OnPlay = callback; break;
            case CallbackKind.Pause: ev.OnPause = callback; break;
            case CallbackKind.Update: ev.OnUpdate = callback; break;
            case CallbackKind.StepComplete: ev.OnStepComplete = callback; break;
            case CallbackKind.Complete: ev.OnComplete = callback; break;
            case CallbackKind.Kill: ev.OnKill = callback; break;
            case CallbackKind.Rewind: ev.OnRewind = callback; break;
        }
    }

    #endregion

    #region Playback control

    /// <summary>Resumes a paused tween.</summary>
    public Tween Play()
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            if ((c.Flags & TweenFlags.Paused) != 0)
            {
                c.Flags &= ~TweenFlags.Paused;
                if ((c.Flags & TweenFlags.HasCallbacks) != 0)
                    Invoke(ref s.EventsAt(d), CallbackKind.Play);
            }
        }
        return this;
    }

    /// <summary>Pauses the tween where it is.</summary>
    public Tween Pause()
    {
        if (TryGet(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            if ((c.Flags & TweenFlags.Paused) == 0)
            {
                c.Flags |= TweenFlags.Paused;
                if ((c.Flags & TweenFlags.HasCallbacks) != 0)
                    Invoke(ref s.EventsAt(d), CallbackKind.Pause);
            }
        }
        return this;
    }

    /// <summary>Pauses if playing, plays if paused.</summary>
    public Tween TogglePause() => IsPlaying ? Pause() : Play();

    /// <summary>Rewinds to the start and plays from there.</summary>
    public Tween Restart(bool includeDelay = true)
    {
        if (!TryGet(out ITweenStorage? s, out int d))
            return this;

        ref TweenCore c = ref s.CoreAt(d);
        c.Position = 0f;
        c.CompletedLoops = 0;
        c.DelayElapsed = includeDelay ? 0f : c.Delay;
        c.Flags &= ~(TweenFlags.Paused | TweenFlags.Completed | TweenFlags.Started);
        s.ApplyCurrentAt(d);
        return this;
    }

    /// <summary>Rewinds to the start and pauses.</summary>
    public Tween Rewind(bool includeDelay = true)
    {
        if (!TryGet(out ITweenStorage? s, out int d))
            return this;

        ref TweenCore c = ref s.CoreAt(d);
        c.Position = 0f;
        c.CompletedLoops = 0;
        c.DelayElapsed = includeDelay ? 0f : c.Delay;
        c.Flags &= ~(TweenFlags.Completed | TweenFlags.Started);
        c.Flags |= TweenFlags.Paused;

        bool hasCallbacks = (c.Flags & TweenFlags.HasCallbacks) != 0;
        s.ApplyCurrentAt(d);

        if (hasCallbacks && StillAlive(s, d))
            Invoke(ref s.EventsAt(d), CallbackKind.Rewind);
        return this;
    }

    /// <summary>Jumps to the end. Also kills the tween when auto-kill is on.</summary>
    public Tween Complete(bool withCallbacks = false)
    {
        if (!TryGet(out ITweenStorage? s, out int d))
            return this;

        bool autoKill = (s.CoreAt(d).Flags & TweenFlags.AutoKill) != 0;
        s.GotoAt(d, s.CoreAt(d).FullDuration, withCallbacks);

        if (!StillAlive(s, d))
            return this;

        if (autoKill)
            s.KillAt(d, false);
        else
            s.CoreAt(d).Flags |= TweenFlags.Paused;

        return this;
    }

    /// <summary>Jumps to an absolute time (delay included), optionally resuming playback.</summary>
    public Tween Goto(float to, bool andPlay = false)
    {
        if (!TryGet(out ITweenStorage? s, out int d))
            return this;

        s.GotoAt(d, float.IsFinite(to) && to > 0f ? to : 0f, false);
        if (!StillAlive(s, d))
            return this;

        ref TweenCore c = ref s.CoreAt(d);
        if (andPlay) c.Flags &= ~TweenFlags.Paused;
        else c.Flags |= TweenFlags.Paused;
        return this;
    }

    /// <summary>
    /// Reverses the tween: start and end are swapped and the playhead is mirrored inside the
    /// current cycle, so it plays back the way it came.
    /// </summary>
    public Tween Flip()
    {
        if (TryGet(out ITweenStorage? s, out int d))
            s.FlipAt(d);
        return this;
    }

    /// <summary>Destroys the tween. Optionally jumps to the end value first.</summary>
    public void Kill(bool complete = false)
    {
        if (TryGet(out ITweenStorage? s, out int d))
            s.KillAt(d, complete);
    }

    internal Tween Apply(TweenAction action) => action switch
    {
        TweenAction.Play => Play(),
        TweenAction.Pause => Pause(),
        TweenAction.Restart => Restart(),
        TweenAction.Rewind => Rewind(),
        TweenAction.Complete => Complete(),
        TweenAction.Flip => Flip(),
        _ => this
    };

    private static void Invoke(ref TweenEvents ev, CallbackKind kind)
    {
        Delegate? d = kind switch
        {
            CallbackKind.Play => ev.OnPlay,
            CallbackKind.Pause => ev.OnPause,
            CallbackKind.Rewind => ev.OnRewind,
            _ => null
        };

        if (d is null)
            return;

        if (d is Action plain)
            plain();
        else
            Unsafe.As<Action<object?>>(d)(ev.State);
    }

    #endregion

    #region Equality

    public bool Equals(Tween other) => StorageId == other.StorageId && Slot == other.Slot && Version == other.Version;
    public override bool Equals(object? obj) => obj is Tween t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(StorageId, Slot, Version);
    public static bool operator ==(Tween a, Tween b) => a.Equals(b);
    public static bool operator !=(Tween a, Tween b) => !a.Equals(b);

    #endregion
}
