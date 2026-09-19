// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Prowl.Tweening;

/// <summary>
/// Timeline of child tweens and callbacks. A sequence is itself a tween - it can be eased,
/// looped, killed, nested inside another sequence and given callbacks like any other.
/// </summary>
/// <remarks>
/// <para>
/// A sequence with nothing in it yet cannot complete, so it is safe to create one and populate it
/// later - even on a later frame. Once it holds something it plays like any other tween.
/// </para>
/// <para>
/// A tween in a sequence starts when the playhead reaches it, not before: that is when it reads its
/// start value (for tweens that have a getter, such as the transform shortcuts) and fires its
/// <c>OnStart</c>. So consecutive moves each continue from where the previous one ended.
/// </para>
/// </remarks>
public readonly struct Sequence : IEquatable<Sequence>
{
    /// <summary>The underlying tween handle. Everything on <see cref="Tween"/> works on a sequence too.</summary>
    public readonly Tween Tween;

    internal Sequence(Tween tween) => Tween = tween;

    public static implicit operator Tween(Sequence sequence) => sequence.Tween;

    /// <summary>
    /// Resolves the owner handle to its storage, dense index and timeline in one lookup, instead of
    /// once per property access.
    /// </summary>
    private bool TryGet(
        [NotNullWhen(true)] out SequenceData? data,
        [NotNullWhen(true)] out ITweenStorage? storage,
        out int dense)
    {
        ITweenStorage? s = Tween.Version == 0 ? null : TweenManager.Resolve(Tween.StorageId);
        if (s != null && s.TryResolve(Tween.Slot, Tween.Version, out dense) && s.StateAt(dense) is SequenceData d)
        {
            data = d;
            storage = s;
            return true;
        }

        data = null;
        storage = null;
        dense = -1;
        return false;
    }

    private SequenceData? Data => TryGet(out SequenceData? data, out _, out _) ? data : null;

    internal static Sequence Create()
    {
        SequenceData data = SequenceData.Rent();

        // Created in the Building state: an empty sequence must not be allowed to complete and
        // recycle its timeline before whoever made it has finished populating it.
        Tween tween = TweenManager.Of<float, FloatAdapter>.Storage
            .Create(0f, 1f, SequenceData.MinDuration, SequenceData.Driver, data,
                    TweenFlags.IsSequence | TweenFlags.Building);

        tween.SetEase(Ease.Linear);
        data.Owner = tween;
        return new Sequence(tween);
    }

    #region Composition

    /// <summary>Adds a tween at the end of the sequence.</summary>
    public Sequence Append(Tween tween)
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();
        if (!tween.IsActive)
            return this;

        float full = SequenceData.Adopt(tween);
        data.LastAppendPoint = data.Duration;
        data.Add(tween, data.Duration, full);
        data.Duration += full;
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Adds a tween in parallel with the last appended element.</summary>
    public Sequence Join(Tween tween)
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();
        if (!tween.IsActive)
            return this;

        float full = SequenceData.Adopt(tween);
        data.Add(tween, data.LastAppendPoint, full);
        data.Duration = MathF.Max(data.Duration, data.LastAppendPoint + full);
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Adds a tween at an exact position on the timeline.</summary>
    public Sequence Insert(float atPosition, Tween tween)
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();
        if (!tween.IsActive)
            return this;

        atPosition = Sanitize(atPosition);
        float full = SequenceData.Adopt(tween);
        data.Add(tween, atPosition, full);
        data.Duration = MathF.Max(data.Duration, atPosition + full);
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Adds a tween at the start, pushing everything else back.</summary>
    public Sequence Prepend(Tween tween)
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();
        if (!tween.IsActive)
            return this;

        float full = SequenceData.Adopt(tween);
        data.Shift(full);
        data.Add(tween, 0f, full);
        data.Duration += full;
        data.LastAppendPoint += full;
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Adds empty time at the end of the sequence.</summary>
    public Sequence AppendInterval(float interval)
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();

        data.LastAppendPoint = data.Duration;
        data.Duration += Sanitize(interval);
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Adds empty time at the start of the sequence.</summary>
    public Sequence PrependInterval(float interval)
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();

        interval = Sanitize(interval);
        data.Shift(interval);
        data.Duration += interval;
        data.LastAppendPoint += interval;
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Fires a callback when the playhead reaches the end of the sequence so far.</summary>
    public Sequence AppendCallback(Action callback)
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();

        data.AddCallback(callback, data.Duration);
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Fires a callback at an exact position on the timeline.</summary>
    public Sequence InsertCallback(float atPosition, Action callback)
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();

        atPosition = Sanitize(atPosition);
        data.AddCallback(callback, atPosition);
        data.Duration = MathF.Max(data.Duration, atPosition);
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Allocation-free variant of <see cref="AppendCallback(Action)"/>.</summary>
    public Sequence AppendCallback<TState>(TState state, Action<TState> callback) where TState : class
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();

        data.AddCallback(Unsafe.As<Action<TState>, Action<object?>>(ref callback), data.Duration, state);
        Commit(storage, dense, data);
        return this;
    }

    /// <summary>Allocation-free variant of <see cref="InsertCallback(float, Action)"/>.</summary>
    public Sequence InsertCallback<TState>(float atPosition, TState state, Action<TState> callback) where TState : class
    {
        if (!TryGet(out SequenceData? data, out ITweenStorage? storage, out int dense))
            return WarnDead();

        atPosition = Sanitize(atPosition);
        data.AddCallback(Unsafe.As<Action<TState>, Action<object?>>(ref callback), atPosition, state);
        data.Duration = MathF.Max(data.Duration, atPosition);
        Commit(storage, dense, data);
        return this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Sanitize(float seconds) => float.IsFinite(seconds) && seconds > 0f ? seconds : 0f;

    /// <summary>Pushes the timeline length onto the owner tween and leaves the Building state.</summary>
    private static void Commit(ITweenStorage storage, int dense, SequenceData data)
    {
        ref TweenCore c = ref storage.CoreAt(dense);
        c.Duration = MathF.Max(data.Duration, SequenceData.MinDuration);
        c.Flags &= ~TweenFlags.Building;
    }

    /// <summary>
    /// A composition call on a sequence that has already been killed does nothing, which is very
    /// hard to spot. Say so through the diagnostics hook if the host installed one.
    /// </summary>
    private Sequence WarnDead()
    {
        TweenManager.Warn("[Tween] Composition call ignored: this Sequence has already been killed. "
                        + "A sequence is killed when it completes with auto-kill on, or by Kill/KillAll.");
        return this;
    }

    #endregion

    #region Tween passthrough

    public Sequence SetEase(Ease ease) { Tween.SetEase(ease); return this; }
    public Sequence SetEase(Ease ease, float overshootOrAmplitude) { Tween.SetEase(ease, overshootOrAmplitude); return this; }
    public Sequence SetEase(EaseFunction customEase) { Tween.SetEase(customEase); return this; }
    public Sequence SetLoops(int loops, LoopType loopType = LoopType.Restart) { Tween.SetLoops(loops, loopType); return this; }
    public Sequence SetDelay(float delay) { Tween.SetDelay(delay); return this; }
    public Sequence SetAutoKill(bool autoKill = true) { Tween.SetAutoKill(autoKill); return this; }
    public Sequence SetUpdate(UpdateType updateType, bool isIndependentUpdate = false) { Tween.SetUpdate(updateType, isIndependentUpdate); return this; }
    public Sequence SetUpdate(bool isIndependentUpdate) { Tween.SetUpdate(isIndependentUpdate); return this; }
    public Sequence SetId(object id) { Tween.SetId(id); return this; }
    public Sequence SetTarget(object target) { Tween.SetTarget(target); return this; }
    public Sequence SetTimeScale(float timeScale) { Tween.SetTimeScale(timeScale); return this; }

    /// <inheritdoc cref="Tween.SetLink{TOwner}(TOwner, Func{TOwner, bool})"/>
    public Sequence SetLink<TOwner>(TOwner owner, Func<TOwner, bool> isAlive) where TOwner : class
    { Tween.SetLink(owner, isAlive); return this; }

    public Sequence OnStart(Action callback) { Tween.OnStart(callback); return this; }
    public Sequence OnPlay(Action callback) { Tween.OnPlay(callback); return this; }
    public Sequence OnPause(Action callback) { Tween.OnPause(callback); return this; }
    public Sequence OnUpdate(Action callback) { Tween.OnUpdate(callback); return this; }
    public Sequence OnStepComplete(Action callback) { Tween.OnStepComplete(callback); return this; }
    public Sequence OnComplete(Action callback) { Tween.OnComplete(callback); return this; }
    public Sequence OnKill(Action callback) { Tween.OnKill(callback); return this; }
    public Sequence OnRewind(Action callback) { Tween.OnRewind(callback); return this; }

    /// <inheritdoc cref="Tween.OnStart{TState}(TState, Action{TState})"/>
    public Sequence OnComplete<TState>(TState state, Action<TState> callback) where TState : class
    { Tween.OnComplete(state, callback); return this; }

    /// <inheritdoc cref="Tween.OnStart{TState}(TState, Action{TState})"/>
    public Sequence OnKill<TState>(TState state, Action<TState> callback) where TState : class
    { Tween.OnKill(state, callback); return this; }

    /// <inheritdoc cref="Tween.OnStart{TState}(TState, Action{TState})"/>
    public Sequence OnStepComplete<TState>(TState state, Action<TState> callback) where TState : class
    { Tween.OnStepComplete(state, callback); return this; }

    public Sequence Play() { Tween.Play(); return this; }
    public Sequence Pause() { Tween.Pause(); return this; }
    public Sequence TogglePause() { Tween.TogglePause(); return this; }
    public Sequence Restart(bool includeDelay = true) { Tween.Restart(includeDelay); return this; }
    public Sequence Rewind(bool includeDelay = true) { Tween.Rewind(includeDelay); return this; }
    public Sequence Complete(bool withCallbacks = false) { Tween.Complete(withCallbacks); return this; }
    public Sequence Goto(float to, bool andPlay = false) { Tween.Goto(to, andPlay); return this; }
    public Sequence Flip() { Tween.Flip(); return this; }
    public void Kill(bool complete = false) => Tween.Kill(complete);

    public bool IsActive => Tween.IsActive;
    public bool IsPlaying => Tween.IsPlaying;
    public bool IsComplete => Tween.IsComplete;
    public float Elapsed => Tween.Elapsed;

    /// <summary>Length of the timeline. 0 for a sequence that is still empty.</summary>
    public float Duration => Data?.Duration ?? 0f;

    /// <summary>How many elements - tweens and callbacks - the timeline holds.</summary>
    public int Count => Data?.Count ?? 0;

    #endregion

    #region Awaiting

    /// <inheritdoc cref="Tween.AsyncWaitForCompletion(CancellationToken)"/>
    public TweenAwaitable AsyncWaitForCompletion(CancellationToken cancellationToken = default) => Tween.AsyncWaitForCompletion(cancellationToken);

    /// <inheritdoc cref="Tween.AsyncWaitForKill(CancellationToken)"/>
    public TweenAwaitable AsyncWaitForKill(CancellationToken cancellationToken = default) => Tween.AsyncWaitForKill(cancellationToken);

    /// <inheritdoc cref="Tween.AsyncWaitForStart(CancellationToken)"/>
    public TweenAwaitable AsyncWaitForStart(CancellationToken cancellationToken = default) => Tween.AsyncWaitForStart(cancellationToken);

    /// <inheritdoc cref="Tween.AsyncWaitForElapsedLoops(int, CancellationToken)"/>
    public TweenAwaitable AsyncWaitForElapsedLoops(int loops, CancellationToken cancellationToken = default) => Tween.AsyncWaitForElapsedLoops(loops, cancellationToken);

    /// <inheritdoc cref="Tween.AsyncWaitForPosition(float, CancellationToken)"/>
    public TweenAwaitable AsyncWaitForPosition(float position, CancellationToken cancellationToken = default) => Tween.AsyncWaitForPosition(position, cancellationToken);

    #endregion

    public bool Equals(Sequence other) => Tween.Equals(other.Tween);
    public override bool Equals(object? obj) => obj is Sequence s && Equals(s);
    public override int GetHashCode() => Tween.GetHashCode();
    public static bool operator ==(Sequence a, Sequence b) => a.Equals(b);
    public static bool operator !=(Sequence a, Sequence b) => !a.Equals(b);
}

/// <summary>
/// Timeline backing a <see cref="Sequence"/>. Instances are pooled, so building and destroying
/// sequences repeatedly settles at zero allocations.
/// </summary>
internal sealed class SequenceData
{
    internal const float MinDuration = 1e-6f;

    /// <summary>Pooled instances, and the largest element array worth keeping with one.</summary>
    private const int PoolLimit = 64;
    private const int PooledElementLimit = 64;

    private struct Element
    {
        public Tween Tween;

        /// <summary>Resolved child location, valid while <see cref="Stamp"/> matches the storage.</summary>
        public ITweenStorage? Storage;
        public int Dense;
        public int Stamp;

        public float Start;
        public float Duration;
        public float LastLocal;
        public Delegate? Callback;
        public object? CallbackState;
        public bool IsCallback;
    }

    /// <summary>Cached driver: the sequence tween's "value" is its normalized playhead.</summary>
    internal static readonly Action<float, object?> Driver =
        static (progress, state) => ((SequenceData)state!).Evaluate(progress);

    private static readonly Stack<SequenceData> s_pool = new();

    private Element[] _elements = new Element[8];
    private int _count;
    private float _lastPosition = -1f;
    private int _lastLoop;

    internal float Duration;
    internal float LastAppendPoint;
    internal Tween Owner;

    internal int Count => _count;

    internal static SequenceData Rent()
    {
        if (s_pool.Count > 0)
        {
            SequenceData data = s_pool.Pop();
            data.Reset();
            return data;
        }

        return new SequenceData();
    }

    private void Reset()
    {
        Array.Clear(_elements, 0, _count);
        _count = 0;
        Duration = 0f;
        LastAppendPoint = 0f;
        _lastPosition = -1f;
        _lastLoop = 0;
        Owner = Tween.None;
    }

    /// <summary>Detaches a tween from the normal update loop and hands it to the sequence.</summary>
    internal static float Adopt(Tween tween)
    {
        ITweenStorage? storage = TweenManager.Resolve(tween.StorageId);
        if (storage == null || !storage.TryResolve(tween.Slot, tween.Version, out int dense))
            return 0f;

        ref TweenCore core = ref storage.CoreAt(dense);
        core.Flags |= TweenFlags.Sequenced;
        core.Flags &= ~(TweenFlags.AutoKill | TweenFlags.Paused);
        return core.FullDuration;
    }

    internal void Add(Tween tween, float start, float duration)
    {
        EnsureCapacity();
        ref Element e = ref _elements[_count++];
        e.Tween = tween;
        e.Storage = null;
        e.Dense = -1;
        e.Stamp = 0;
        e.Start = start;
        e.Duration = duration;
        e.LastLocal = float.NaN;
        e.Callback = null;
        e.CallbackState = null;
        e.IsCallback = false;
    }

    internal void AddCallback(Delegate callback, float position, object? state = null)
    {
        EnsureCapacity();
        ref Element e = ref _elements[_count++];
        e.Tween = Tween.None;
        e.Storage = null;
        e.Dense = -1;
        e.Stamp = 0;
        e.Start = position;
        e.Duration = 0f;
        e.LastLocal = float.NaN;
        e.Callback = callback;
        e.CallbackState = state;
        e.IsCallback = true;
    }

    internal void Shift(float amount)
    {
        for (int i = 0; i < _count; i++)
            _elements[i].Start += amount;
    }

    private void EnsureCapacity()
    {
        if (_count == _elements.Length)
            Array.Resize(ref _elements, _elements.Length * 2);
    }

    /// <summary>
    /// Resolves a child's dense index, reusing the cached one until the storage compacts.
    /// This is what keeps a sequence's per-frame cost to one lookup per child instead of three.
    /// </summary>
    private static bool ResolveChild(ref Element e, [NotNullWhen(true)] out ITweenStorage? storage, out int dense)
    {
        ITweenStorage? s = e.Storage;
        if (s != null && e.Stamp == s.CompactionStamp)
        {
            storage = s;
            dense = e.Dense;
            return true;
        }

        s = e.Tween.Version == 0 ? null : TweenManager.Resolve(e.Tween.StorageId);
        if (s != null && s.TryResolve(e.Tween.Slot, e.Tween.Version, out dense))
        {
            e.Storage = s;
            e.Dense = dense;
            e.Stamp = s.CompactionStamp;
            storage = s;
            return true;
        }

        e.Storage = null;
        e.Dense = -1;
        storage = null;
        dense = -1;
        return false;
    }

    /// <summary>Drives every child from the sequence's normalized playhead.</summary>
    internal void Evaluate(float progress)
    {
        float position = progress * Duration;

        ReadOwner(out int loops, out bool ownerComplete);
        if (loops != _lastLoop)
        {
            _lastLoop = loops;

            // A new cycle starts at whichever end the playhead is nearest - 0 after a Restart
            // wrap, the far end after a Yoyo turnaround. Parking the previous position just
            // outside that end lets a callback sitting exactly on the boundary still fire.
            //
            // The final cycle is not a boundary, even though the counter changed there too: the
            // playhead is still travelling the way it was, and resetting here would drop any
            // callback between the last tick and the end.
            if (!ownerComplete)
                _lastPosition = position <= Duration * 0.5f ? -1f : Duration + 1f;
        }

        bool forward = position >= _lastPosition;
        float grownTo = Duration;

        // Index-based on purpose: a child's callback may append to this sequence and resize the array.
        for (int i = 0; i < _count; i++)
        {
            if (_elements[i].IsCallback)
            {
                float at = _elements[i].Start;

                // Crossed in either direction, so a Yoyo cycle fires callbacks on the way back too.
                bool crossed = forward
                    ? _lastPosition < at && position >= at
                    : _lastPosition > at && position <= at;

                if (crossed)
                {
                    Delegate? callback = _elements[i].Callback;
                    object? state = _elements[i].CallbackState;
                    if (callback is Action plain) plain();
                    else if (callback != null) Unsafe.As<Action<object?>>(callback)(state);
                }
                continue;
            }

            if (!ResolveChild(ref _elements[i], out ITweenStorage? storage, out int dense))
                continue;

            // Re-read the child's length every tick: changing a child's duration, delay or loop
            // count after it was added stays consistent with the timeline instead of desyncing.
            float childFull = storage.CoreAt(dense).FullDuration;
            _elements[i].Duration = childFull;
            if (_elements[i].Start + childFull > grownTo)
                grownTo = _elements[i].Start + childFull;

            float raw = position - _elements[i].Start;

            // A child starts when the playhead reaches it, not before. Driving one that hasn't
            // started to its local time 0 would start it early: it would read its start value from
            // wherever the children before it left things this frame, and fire OnStart ahead of
            // its turn.
            if (raw < 0f && (storage.CoreAt(dense).Flags & TweenFlags.Started) == 0)
                continue;

            float local = raw;
            if (local < 0f) local = 0f;
            else if (local > childFull) local = childFull;

            if (local == _elements[i].LastLocal)
                continue;

            _elements[i].LastLocal = local;
            storage.GotoAt(dense, local, true);
        }

        // A child that outgrew the timeline extends it, so it still gets played in full. It takes
        // effect from the next tick, since this one's playhead was already scaled.
        if (grownTo > Duration)
        {
            Duration = grownTo;
            SyncOwnerDuration();
        }

        _lastPosition = position;
    }

    private ITweenStorage? _ownerStorage;
    private int _ownerDense = -1;
    private int _ownerStamp;

    private bool TryResolveOwner([NotNullWhen(true)] out ITweenStorage? storage, out int dense)
    {
        ITweenStorage? s = _ownerStorage;
        if (s != null && _ownerStamp == s.CompactionStamp)
        {
            storage = s;
            dense = _ownerDense;
            return true;
        }

        s = Owner.Version == 0 ? null : TweenManager.Resolve(Owner.StorageId);
        if (s != null && s.TryResolve(Owner.Slot, Owner.Version, out dense))
        {
            _ownerStorage = s;
            _ownerDense = dense;
            _ownerStamp = s.CompactionStamp;
            storage = s;
            return true;
        }

        _ownerStorage = null;
        _ownerDense = -1;
        storage = null;
        dense = -1;
        return false;
    }

    private void ReadOwner(out int completedLoops, out bool complete)
    {
        if (TryResolveOwner(out ITweenStorage? s, out int d))
        {
            ref TweenCore c = ref s.CoreAt(d);
            completedLoops = c.CompletedLoops;
            complete = (c.Flags & TweenFlags.Completed) != 0;
            return;
        }

        completedLoops = 0;
        complete = false;
    }

    private void SyncOwnerDuration()
    {
        if (TryResolveOwner(out ITweenStorage? s, out int d))
            s.CoreAt(d).Duration = MathF.Max(Duration, MinDuration);
    }

    /// <summary>Kills every child, then recycles this instance.</summary>
    internal void OnOwnerKilled()
    {
        for (int i = 0; i < _count; i++)
        {
            Tween child = _elements[i].Tween;
            if (child.IsActive)
                child.Kill();
        }

        bool keepArray = _elements.Length <= PooledElementLimit;

        Reset();
        _ownerStorage = null;
        _ownerDense = -1;
        _ownerStamp = 0;

        // Don't park a huge element array in the pool just to keep one instance around.
        if (!keepArray)
            _elements = new Element[8];

        if (s_pool.Count < PoolLimit)
            s_pool.Push(this);
    }
}
