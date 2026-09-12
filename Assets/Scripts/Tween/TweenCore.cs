// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#nullable enable

using System;

namespace Prowl.Tweening;

/// <summary>
/// Type-agnostic per-tween state. Lives inline inside the typed storage's dense array so the
/// update loop walks one contiguous block of memory.
/// </summary>
internal struct TweenCore
{
    /// <summary>Elapsed time inside the current loop cycle.</summary>
    public float Position;
    public float Duration;
    public float Delay;
    public float DelayElapsed;
    /// <summary>Per-tween speed multiplier.</summary>
    public float TimeScale;
    public float EaseOvershoot;
    public float EasePeriod;
    public int Loops;
    public int CompletedLoops;
    public TweenFlags Flags;
    public Ease Ease;
    public LoopType LoopType;
    public UpdateType UpdateType;

    /// <summary>Total playable length including delay, treating infinite loops as a single cycle.</summary>
    public readonly float FullDuration => Delay + Duration * (Loops < 0 ? 1 : Loops);
}

/// <summary>
/// Cold per-tween identity: the id and target used by the bulk operations, plus the optional
/// lifetime link. Kept apart from <see cref="TweenEvents"/> on purpose - tagging a tween with
/// <c>SetTarget</c> must not drag the much larger callback record into the hot loop.
/// </summary>
internal struct TweenTags
{
    public object? Id;
    public object? Target;

    /// <summary>The object this tween's life is tied to, or null when unlinked.</summary>
    public object? LinkOwner;

    /// <summary>
    /// Returns false once <see cref="LinkOwner"/> should be considered gone. Stored type-erased;
    /// see <see cref="Tween.SetLink{TOwner}"/>.
    /// </summary>
    public Func<object?, bool>? LinkAlive;
}

/// <summary>
/// Cold per-tween callbacks and custom easing. The backing array is only allocated the first time
/// a tween in a storage actually assigns one of these, and the update loop only reads it when
/// <see cref="TweenFlags.HasCallbacks"/> is set.
/// </summary>
internal struct TweenEvents
{
    /// <summary>
    /// Shared state passed to the allocation-free <c>On*(state, callback)</c> overloads.
    /// One state object per tween - assigning a second typed callback with a different state
    /// replaces the first one's state.
    /// </summary>
    public object? State;

    public Delegate? OnStart;
    public Delegate? OnPlay;
    public Delegate? OnPause;
    public Delegate? OnUpdate;
    public Delegate? OnStepComplete;
    public Delegate? OnComplete;
    public Delegate? OnKill;
    public Delegate? OnRewind;

    public EaseFunction? CustomEase;
}

/// <summary>
/// Type-erased view over a typed <c>TweenStorage&lt;T, TAdapter&gt;</c>.
/// <para>
/// Everything is addressed by <i>dense index</i> rather than by handle: callers resolve a handle
/// once through <see cref="TryResolve"/> and then operate on the index, so a fluent chain pays for
/// one slot-table lookup instead of one per call.
/// </para>
/// </summary>
internal interface ITweenStorage
{
    int Id { get; set; }
    int ActiveCount { get; }

    /// <summary>
    /// Bumped every time <see cref="PruneAndSweep"/> moves an entry. A cached dense index is only
    /// valid while the stamp it was read with still matches.
    /// </summary>
    int CompactionStamp { get; }

    /// <summary>Turns a handle into a dense index. False for a stale, dead or default handle.</summary>
    bool TryResolve(int slot, int version, out int dense);

    ref TweenCore CoreAt(int dense);

    /// <summary>Events record for a live entry, allocating the cold array on first use.</summary>
    ref TweenEvents EventsAt(int dense);

    /// <summary>Tags record for a live entry, allocating the cold array on first use.</summary>
    ref TweenTags TagsAt(int dense);

    object? StateAt(int dense);
    int SlotAt(int dense);
    int VersionOfSlot(int slot);

    /// <summary>Moves an entry between update-type buckets, keeping the per-type counters honest.</summary>
    void SetUpdateTypeAt(int dense, UpdateType type);

    /// <summary>Registers that an entry now carries a lifetime link.</summary>
    void MarkLinkedAt(int dense);

    void KillAt(int dense, bool complete);
    void GotoAt(int dense, float position, bool withCallbacks);
    void ApplyCurrentAt(int dense);
    void MakeFromAt(int dense);
    void MakeRelativeAt(int dense);
    void FlipAt(int dense);

    void Update(UpdateType type, float deltaTime, float unscaledDeltaTime);

    /// <summary>
    /// Kills tweens whose link has gone, then compacts dead entries out of the dense array.
    /// Runs at the start of every tick, so a tween never writes to a dead target.
    /// </summary>
    void PruneAndSweep();

    /// <summary>Releases capacity above what the live tweens need.</summary>
    void Trim();

    /// <summary>Grows the arrays up front so a burst of creations doesn't reallocate mid-frame.</summary>
    void Reserve(int capacity);

    int KillWhere(object? idOrTarget, bool complete);
    int ControlWhere(object? idOrTarget, TweenAction action);
}
