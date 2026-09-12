// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// Dense, cache-friendly storage for every tween of one (value type, adapter) pair.
/// <para>
/// Tweens live packed at the head of <c>_entries</c> with no holes, so the update loop is a
/// straight linear walk. Public handles point at a sparse slot table instead of the dense array,
/// which lets entries be swapped around during compaction without invalidating them.
/// </para>
/// <para>
/// The two cold arrays - <c>_tags</c> (id / target / lifetime link) and <c>_events</c> (callbacks
/// and custom easing) - are allocated only when something actually uses them, and are gated by
/// separate flags so tagging a tween never drags its callback record into the hot loop.
/// </para>
/// <para>
/// Nothing here allocates after the arrays have grown to their high-water mark: killing a tween
/// only flips a flag, and structural removal is batched into <see cref="PruneAndSweep"/>.
/// </para>
/// </summary>
internal sealed class TweenStorage<T, TAdapter> : ITweenStorage
    where T : unmanaged
    where TAdapter : unmanaged, ITweenAdapter<T>
{
    private const float MinDuration = 1e-6f;
    private const int InitialCapacity = 32;

    /// <summary>
    /// Ceiling on loop cycles credited in a single tick. A cycle clamped to <see cref="MinDuration"/>
    /// would otherwise let one long frame overflow <see cref="TweenCore.CompletedLoops"/>.
    /// </summary>
    private const int MaxStepsPerTick = 4096;

    private struct Entry
    {
        public TweenCore Core;
        public T Start;
        public T End;
        /// <summary>Back-reference into <c>_slots</c>, kept in sync when entries are swapped.</summary>
        public int Slot;
    }

    private struct Slot
    {
        public int Dense;     // index into _entries, -1 when free
        public int Version;   // bumped on kill so stale handles stop resolving
        public int NextFree;
    }

    private struct Binding
    {
        public Action<T, object?>? Setter;
        public object? State;
    }

    private Entry[] _entries = new Entry[InitialCapacity];
    private Binding[] _bindings = new Binding[InitialCapacity];
    private TweenTags[]? _tags;
    private TweenEvents[]? _events;
    private int _count;
    private int _deadCount;
    private int _linkCount;

    /// <summary>Live tweens per <see cref="UpdateType"/>, so an unused tick costs nothing.</summary>
    private readonly int[] _typeCounts = new int[4];

    private Slot[] _slots = new Slot[InitialCapacity];
    private int _slotCount;
    private int _freeSlot = -1;

    private int _compactionStamp;

    public int Id { get; set; }
    public int ActiveCount => _count - _deadCount;
    public int CompactionStamp => _compactionStamp;

    // Diagnostics: what the arrays currently cost, for Trim/Reserve callers and for the tests that
    // guard the cold-array split. Not part of ITweenStorage - nothing in the library reads them.
    internal int Capacity => _entries.Length;
    internal bool HasEventsArray => _events != null;
    internal bool HasTagsArray => _tags != null;
    internal int LinkedCount => _linkCount;

    #region Creation

    public Tween Create(in T from, in T to, float duration, Action<T, object?>? setter, object? state, TweenFlags extraFlags = TweenFlags.None)
    {
        // Every creation path funnels through here, so this is the one place the host engine has to
        // be asked for a ticker - a tween that nothing drives would just sit at its start value.
        TweenManager.EnsureRuntime();

        if (_count == _entries.Length)
            Grow(_entries.Length * 2);

        int dense = _count++;
        int slot = AllocateSlot(dense);

        ref Entry e = ref _entries[dense];
        e.Start = from;
        e.End = to;
        e.Slot = slot;

        ref TweenCore c = ref e.Core;
        c.Position = 0f;
        c.Duration = SanitizeTime(duration);
        c.Delay = 0f;
        c.DelayElapsed = 0f;
        c.TimeScale = 1f;
        c.Loops = 1;
        c.CompletedLoops = 0;
        c.Ease = Tween.DefaultEase == Ease.Unset ? Ease.Linear : Tween.DefaultEase;
        c.EaseOvershoot = Tween.DefaultEaseOvershootOrAmplitude;
        c.EasePeriod = Tween.DefaultEasePeriod;
        c.LoopType = LoopType.Restart;
        c.UpdateType = UpdateType.Normal;
        c.Flags = extraFlags | (Tween.DefaultAutoKill ? TweenFlags.AutoKill : TweenFlags.None);

        _bindings[dense].Setter = setter;
        _bindings[dense].State = state;
        if (_tags != null)
            _tags[dense] = default;
        if (_events != null)
            _events[dense] = default;

        _typeCounts[(int)UpdateType.Normal]++;

        return new Tween(Id, slot, _slots[slot].Version);
    }

    /// <summary>
    /// Rejects the values that would otherwise park a tween forever: a NaN position never compares
    /// greater than its duration, so the tween could never complete, auto-kill or be swept.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SanitizeTime(float seconds)
        => float.IsFinite(seconds) && seconds > 0f ? seconds : 0f;

    private void Grow(int capacity)
    {
        if (capacity <= _entries.Length)
            return;

        Array.Resize(ref _entries, capacity);
        Array.Resize(ref _bindings, capacity);
        if (_tags != null)
            Array.Resize(ref _tags, capacity);
        if (_events != null)
            Array.Resize(ref _events, capacity);
    }

    public void Reserve(int capacity)
    {
        if (capacity <= 0)
            return;

        int target = _entries.Length;
        while (target < capacity)
            target *= 2;

        Grow(target);

        if (_slots.Length < capacity)
            Array.Resize(ref _slots, target);
    }

    private int AllocateSlot(int dense)
    {
        int slot;
        if (_freeSlot >= 0)
        {
            slot = _freeSlot;
            _freeSlot = _slots[slot].NextFree;
        }
        else
        {
            if (_slotCount == _slots.Length)
                Array.Resize(ref _slots, _slotCount * 2);
            slot = _slotCount++;
            _slots[slot].Version = 1; // version 0 is reserved for the default (invalid) handle
        }

        _slots[slot].Dense = dense;
        return slot;
    }

    #endregion

    #region Handle resolution

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolve(int slot, int version, out int dense)
    {
        if ((uint)slot < (uint)_slotCount)
        {
            ref Slot s = ref _slots[slot];
            if (s.Version == version && s.Dense >= 0)
            {
                dense = s.Dense;
                return true;
            }
        }

        dense = -1;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TweenCore CoreAt(int dense) => ref _entries[dense].Core;

    public ref TweenEvents EventsAt(int dense)
    {
        _events ??= new TweenEvents[_entries.Length];
        return ref _events[dense];
    }

    public ref TweenTags TagsAt(int dense)
    {
        _tags ??= new TweenTags[_entries.Length];
        return ref _tags[dense];
    }

    public object? StateAt(int dense) => _bindings[dense].State;
    public int SlotAt(int dense) => _entries[dense].Slot;
    public int VersionOfSlot(int slot) => (uint)slot < (uint)_slotCount ? _slots[slot].Version : 0;

    public void SetUpdateTypeAt(int dense, UpdateType type)
    {
        ref TweenCore c = ref _entries[dense].Core;
        if (c.UpdateType == type)
            return;

        if ((c.Flags & TweenFlags.Dead) == 0)
        {
            _typeCounts[(int)c.UpdateType]--;
            _typeCounts[(int)type]++;
        }

        c.UpdateType = type;
    }

    public void MarkLinkedAt(int dense)
    {
        ref TweenCore c = ref _entries[dense].Core;
        if ((c.Flags & TweenFlags.HasLink) != 0)
            return;

        c.Flags |= TweenFlags.HasLink;
        _linkCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDead(int dense) => (_entries[dense].Core.Flags & TweenFlags.Dead) != 0;

    #endregion

    #region Value edits

    public void MakeFromAt(int dense)
    {
        ref Entry e = ref _entries[dense];
        (e.Start, e.End) = (e.End, e.Start);
        ApplyAt(dense, EasedProgress(dense, false));
    }

    public void MakeRelativeAt(int dense)
    {
        ref Entry e = ref _entries[dense];
        e.End = default(TAdapter).Add(in e.Start, in e.End);
    }

    public void FlipAt(int dense)
    {
        ref Entry e = ref _entries[dense];
        (e.Start, e.End) = (e.End, e.Start);
        e.Core.Position = Math.Max(0f, e.Core.Duration - e.Core.Position);
        e.Core.Flags &= ~TweenFlags.Completed;
    }

    public void ApplyCurrentAt(int dense) => ApplyAt(dense, EasedProgress(dense, false));

    #endregion

    #region Update

    public void Update(UpdateType type, float deltaTime, float unscaledDeltaTime)
    {
        // No live tween wants this tick: skip the traversal entirely. This is what keeps a single
        // SetUpdate(Fixed) call from making every storage walk its whole array on every fixed step.
        if (_typeCounts[(int)type] == 0)
            return;

        // Snapshot the count: tweens created from a callback start on the next tick, which is
        // also what keeps index-based iteration valid while user code runs.
        int n = _count;
        for (int i = 0; i < n; i++)
        {
            // Re-read the array every iteration - a callback may have grown (reallocated) it.
            ref TweenCore c = ref _entries[i].Core;
            if ((c.Flags & TweenFlags.NotSteppable) != 0)
                continue;
            if (c.UpdateType != type)
                continue;

            float delta = ((c.Flags & TweenFlags.Independent) != 0 ? unscaledDeltaTime : deltaTime) * c.TimeScale;
            if (!float.IsFinite(delta))
                continue;

            Step(i, delta);
        }
    }

    /// <summary>Advances one tween and applies the result. Never touches a <c>ref</c> after user code runs.</summary>
    private void Step(int dense, float delta)
    {
        ref Entry e = ref _entries[dense];
        ref TweenCore c = ref e.Core;

        if (c.DelayElapsed < c.Delay)
        {
            c.DelayElapsed += delta;
            if (c.DelayElapsed < c.Delay)
                return;

            delta = c.DelayElapsed - c.Delay;
            c.DelayElapsed = c.Delay;
        }

        bool started = (c.Flags & TweenFlags.Started) == 0;
        c.Flags |= TweenFlags.Started;

        // A frozen tween - TimeScale 0, or a paused game feeding a zero delta - still reports that
        // it started and shows its start value once. After that there is nothing left to do.
        if (delta == 0f && !started)
            return;

        float duration = c.Duration > MinDuration ? c.Duration : MinDuration;
        c.Position += delta;

        int steps = 0;
        bool complete = false;
        if (c.Position >= duration)
        {
            // Computed in double and clamped: a cycle pinned to MinDuration would otherwise let a
            // single long frame credit billions of loops and overflow the counter.
            double raw = c.Position / (double)duration;
            steps = raw >= MaxStepsPerTick ? MaxStepsPerTick : (int)raw;

            int done = c.CompletedLoops;
            c.CompletedLoops = done > int.MaxValue - steps ? int.MaxValue - 1 : done + steps;

            c.Position -= steps * duration;
            if (c.Position < 0f || c.Position >= duration)
                c.Position = 0f;

            if (c.Loops >= 0 && c.CompletedLoops >= c.Loops)
            {
                c.CompletedLoops = c.Loops;
                c.Position = 0f;
                complete = true;
            }
        }

        float eased = EasedProgress(dense, complete);
        T value = default(TAdapter).Evaluate(in e.Start, in e.End, eased);

        bool hasCallbacks = (c.Flags & TweenFlags.HasCallbacks) != 0;
        bool autoKill = false;
        if (complete)
        {
            c.Flags |= TweenFlags.Completed;
            if ((c.Flags & TweenFlags.AutoKill) != 0)
                autoKill = true;
            else
                c.Flags |= TweenFlags.Paused;
        }

        // ---- user code below: all refs into the arrays are considered invalid from here, and any
        // callback may have killed this tween, so every step re-checks before firing the next ----

        if (hasCallbacks && started)
        {
            Fire(_events![dense].OnStart, _events[dense].State);
            if (IsDead(dense)) return;
        }

        Action<T, object?>? setter = _bindings[dense].Setter;
        setter?.Invoke(value, _bindings[dense].State);
        if (IsDead(dense)) return;

        if (hasCallbacks)
        {
            object? state = _events![dense].State;
            Fire(_events[dense].OnUpdate, state);
            if (IsDead(dense)) return;

            // Fires once per tick even when several cycles elapsed inside it.
            if (steps > 0)
            {
                Fire(_events[dense].OnStepComplete, state);
                if (IsDead(dense)) return;
            }

            if (complete)
            {
                Fire(_events[dense].OnComplete, state);
                if (IsDead(dense)) return;
            }
        }

        if (autoKill)
            MarkDead(dense);
    }

    /// <summary>Cycle progress with loop mode and easing applied. Can exceed 0..1 for <see cref="LoopType.Incremental"/>.</summary>
    private float EasedProgress(int dense, bool complete)
    {
        ref TweenCore c = ref _entries[dense].Core;

        float p;
        int loopIndex;
        if (complete)
        {
            loopIndex = c.CompletedLoops > 0 ? c.CompletedLoops - 1 : 0;
            p = c.LoopType == LoopType.Yoyo && (loopIndex & 1) == 1 ? 0f : 1f;
        }
        else
        {
            loopIndex = c.CompletedLoops;
            p = c.Duration > MinDuration ? c.Position / c.Duration : 1f;
            if (c.LoopType == LoopType.Yoyo && (loopIndex & 1) == 1)
                p = 1f - p;
        }

        float eased;
        if ((c.Flags & TweenFlags.CustomEase) != 0 && _events != null && _events[dense].CustomEase != null)
            eased = _events[dense].CustomEase!(p, 1f, c.EaseOvershoot, c.EasePeriod);
        else
            eased = EaseUtility.Evaluate(c.Ease, p, c.EaseOvershoot, c.EasePeriod);

        if (c.LoopType == LoopType.Incremental)
            eased += loopIndex;

        return eased;
    }

    private void ApplyAt(int dense, float easedProgress)
    {
        T value = default(TAdapter).Evaluate(in _entries[dense].Start, in _entries[dense].End, easedProgress);
        Action<T, object?>? setter = _bindings[dense].Setter;
        setter?.Invoke(value, _bindings[dense].State);
    }

    /// <summary>Jumps to an absolute time (delay included) and applies the value there.</summary>
    public void GotoAt(int dense, float position, bool withCallbacks)
    {
        ref TweenCore c = ref _entries[dense].Core;

        if (position < c.Delay)
        {
            c.DelayElapsed = position < 0f ? 0f : position;
            c.Position = 0f;
            c.CompletedLoops = 0;
            c.Flags &= ~TweenFlags.Completed;
            return;
        }

        c.DelayElapsed = c.Delay;
        float local = position - c.Delay;
        float duration = c.Duration > MinDuration ? c.Duration : MinDuration;
        float full = c.Loops < 0 ? float.PositiveInfinity : duration * c.Loops;

        bool complete;
        if (local >= full)
        {
            c.CompletedLoops = c.Loops;
            c.Position = 0f;
            complete = true;
        }
        else
        {
            double raw = local / (double)duration;
            int done = raw >= int.MaxValue ? int.MaxValue - 1 : (int)raw;
            c.CompletedLoops = done;
            c.Position = local - done * duration;
            complete = false;
        }

        bool started = (c.Flags & TweenFlags.Started) == 0;
        c.Flags |= TweenFlags.Started;

        bool wasComplete = (c.Flags & TweenFlags.Completed) != 0;
        if (complete)
            c.Flags |= TweenFlags.Completed;
        else
            c.Flags &= ~TweenFlags.Completed;

        bool hasCallbacks = withCallbacks && (c.Flags & TweenFlags.HasCallbacks) != 0;
        float eased = EasedProgress(dense, complete);

        // ---- user code below ----

        if (hasCallbacks && started)
        {
            Fire(_events![dense].OnStart, _events[dense].State);
            if (IsDead(dense)) return;
        }

        ApplyAt(dense, eased);
        if (IsDead(dense)) return;

        if (hasCallbacks)
        {
            object? state = _events![dense].State;
            Fire(_events[dense].OnUpdate, state);
            if (IsDead(dense)) return;

            if (complete && !wasComplete)
            {
                Fire(_events[dense].OnStepComplete, state);
                if (IsDead(dense)) return;
                Fire(_events[dense].OnComplete, state);
            }
        }
    }

    #endregion

    #region Kill / prune / sweep

    public void KillAt(int dense, bool complete)
    {
        if (complete)
        {
            GotoAt(dense, _entries[dense].Core.FullDuration, true);
            // A callback fired by the completion pass may have killed it already.
            if (IsDead(dense))
                return;
        }

        MarkDead(dense);
    }

    /// <summary>
    /// Marks a tween dead: no array is restructured here, so this is safe to call from inside
    /// callbacks and from the update loop. <see cref="PruneAndSweep"/> reclaims the entry later.
    /// </summary>
    private void MarkDead(int dense)
    {
        ref TweenCore c = ref _entries[dense].Core;
        if ((c.Flags & TweenFlags.Dead) != 0)
            return;

        c.Flags |= TweenFlags.Dead;
        _deadCount++;
        _typeCounts[(int)c.UpdateType]--;
        if ((c.Flags & TweenFlags.HasLink) != 0)
            _linkCount--;

        int slot = _entries[dense].Slot;
        ref Slot s = ref _slots[slot];
        s.Version++;
        if (s.Version == 0)
            s.Version = 1;

        bool isSequence = (c.Flags & TweenFlags.IsSequence) != 0;
        bool hasCallbacks = (c.Flags & TweenFlags.HasCallbacks) != 0;

        // ---- user code below ----

        if (isSequence && _bindings[dense].State is SequenceData data)
            data.OnOwnerKilled();

        if (hasCallbacks)
            Fire(_events![dense].OnKill, _events[dense].State);
    }

    /// <summary>
    /// Kills tweens whose lifetime link has gone, then compacts dead entries out of the dense
    /// array. Called at the start of every tick, so a tween never gets a chance to write to an
    /// owner that has already been destroyed.
    /// </summary>
    public void PruneAndSweep()
    {
        if (_linkCount > 0)
            PruneLinks();

        if (_deadCount == 0)
            return;

        bool moved = false;

        for (int i = _count - 1; i >= 0; i--)
        {
            if ((_entries[i].Core.Flags & TweenFlags.Dead) == 0)
                continue;

            int slot = _entries[i].Slot;
            _slots[slot].Dense = -1;
            _slots[slot].NextFree = _freeSlot;
            _freeSlot = slot;

            int last = --_count;
            if (i != last)
            {
                _entries[i] = _entries[last];
                _bindings[i] = _bindings[last];
                if (_tags != null)
                    _tags[i] = _tags[last];
                if (_events != null)
                    _events[i] = _events[last];
                _slots[_entries[i].Slot].Dense = i;
                moved = true;
            }

            _entries[last] = default;
            _bindings[last] = default;
            if (_tags != null)
                _tags[last] = default;
            if (_events != null)
                _events[last] = default;
        }

        _deadCount = 0;
        if (moved)
            _compactionStamp++;
    }

    private void PruneLinks()
    {
        int n = _count;
        for (int i = 0; i < n; i++)
        {
            ref TweenCore c = ref _entries[i].Core;
            if ((c.Flags & (TweenFlags.Dead | TweenFlags.HasLink)) != TweenFlags.HasLink)
                continue;

            TweenTags tags = _tags![i];
            if (tags.LinkAlive == null)
                continue;

            // ---- user code below ----
            if (!tags.LinkAlive(tags.LinkOwner))
                MarkDead(i);
        }
    }

    public void Trim()
    {
        if (_deadCount != 0)
            PruneAndSweep();

        int target = InitialCapacity;
        while (target < _count)
            target *= 2;

        if (target < _entries.Length)
        {
            Array.Resize(ref _entries, target);
            Array.Resize(ref _bindings, target);
            if (_tags != null)
                Array.Resize(ref _tags, target);
            if (_events != null)
                Array.Resize(ref _events, target);
        }

        // Drop the cold arrays entirely once no live tween needs them.
        if (_events != null && !AnyFlag(TweenFlags.HasCallbacks | TweenFlags.CustomEase))
            _events = null;
        if (_tags != null && !AnyFlag(TweenFlags.HasTags | TweenFlags.HasLink))
            _tags = null;

        // _slots is deliberately left alone: the free list addresses it by index, so shrinking it
        // would mean remapping every live handle. At 12 bytes a slot that is not worth it.
    }

    private bool AnyFlag(TweenFlags mask)
    {
        for (int i = 0; i < _count; i++)
            if ((_entries[i].Core.Flags & mask) != 0)
                return true;
        return false;
    }

    #endregion

    #region Bulk operations

    public int KillWhere(object? idOrTarget, bool complete)
    {
        int killed = 0;
        int n = _count;
        for (int i = 0; i < n; i++)
        {
            if ((_entries[i].Core.Flags & (TweenFlags.Dead | TweenFlags.Sequenced)) != 0)
                continue;
            if (!Matches(i, idOrTarget))
                continue;

            KillAt(i, complete);
            killed++;
        }

        return killed;
    }

    public int ControlWhere(object? idOrTarget, TweenAction action)
    {
        int affected = 0;
        int n = _count;
        for (int i = 0; i < n; i++)
        {
            if ((_entries[i].Core.Flags & (TweenFlags.Dead | TweenFlags.Sequenced)) != 0)
                continue;
            if (!Matches(i, idOrTarget))
                continue;

            int slot = _entries[i].Slot;
            new Tween(Id, slot, _slots[slot].Version).Apply(action);
            affected++;
        }

        return affected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Matches(int dense, object? idOrTarget)
    {
        if (idOrTarget == null)
            return true;
        if (_tags == null || (_entries[dense].Core.Flags & TweenFlags.HasTags) == 0)
            return false;

        ref TweenTags tags = ref _tags[dense];
        return Equals(tags.Id, idOrTarget) || ReferenceEquals(tags.Target, idOrTarget);
    }

    #endregion

    private static void Fire(Delegate? callback, object? state)
    {
        if (callback is null)
            return;

        if (callback is Action plain)
            plain();
        else
            Unsafe.As<Action<object?>>(callback)(state);
    }
}
