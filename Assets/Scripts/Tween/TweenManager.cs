// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// Owns every typed storage and drives them. Tweening is single-threaded by design: create,
/// configure and tick tweens from the same thread (normally the main/game thread).
/// </summary>
public static class TweenManager
{
    private static ITweenStorage[] s_storages = new ITweenStorage[8];
    private static int s_storageCount;
    private static readonly object s_registerLock = new();

    /// <summary>
    /// Set the first time a tween asks for a given update type, and never cleared. A stale
    /// <c>true</c> only costs one wasted pass - and each storage then skips its own traversal when
    /// no live tween of its type wants that tick - so the common "nobody uses FixedUpdate" case
    /// costs nothing.
    /// </summary>
    private static readonly bool[] s_usesUpdateType = [true, false, false, false];

    /// <summary>Global speed multiplier applied to the scaled delta of every tween.</summary>
    public static float TimeScale = 1f;

    /// <summary>
    /// Managed id of the thread that last ticked the library, or 0 before the first tick. Used to
    /// keep <c>await</c> off the storage arrays from any other thread.
    /// </summary>
    internal static int TickThreadId;

    /// <summary>Total number of live tweens across every value type.</summary>
    public static int ActiveTweenCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < s_storageCount; i++)
                total += s_storages[i].ActiveCount;
            return total;
        }
    }

    /// <summary>
    /// Per-(value type, adapter) singleton storage: a static field read plus a check that the
    /// storage is still the registered one.
    /// </summary>
    /// <remarks>
    /// That check is what makes <see cref="Reset"/> work. This static lives inside a generic type,
    /// so the engine's hot reload resets it rather than migrating it - which is what we want, since
    /// the delegates an old storage holds belong to the unloaded assembly. Either way - a reload
    /// that cleared this field, or an in-process <see cref="Reset"/> that did not - the next access
    /// notices the storage is no longer registered and creates a fresh one.
    /// </remarks>
#pragma warning disable EMBA001
    internal static class Of<T, TAdapter>
        where T : unmanaged
        where TAdapter : unmanaged, ITweenAdapter<T>
    {
        private static TweenStorage<T, TAdapter>? s_storage;

        internal static TweenStorage<T, TAdapter> Storage
        {
            get
            {
                TweenStorage<T, TAdapter>? s = s_storage;
                return s != null && IsRegistered(s) ? s : s_storage = Register(new TweenStorage<T, TAdapter>());
            }
        }
    }
#pragma warning restore EMBA001

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRegistered(ITweenStorage storage)
        => (uint)storage.Id < (uint)s_storageCount && ReferenceEquals(s_storages[storage.Id], storage);

    private static TweenStorage<T, TAdapter> Register<T, TAdapter>(TweenStorage<T, TAdapter> storage)
        where T : unmanaged
        where TAdapter : unmanaged, ITweenAdapter<T>
    {
        lock (s_registerLock)
        {
            if (s_storageCount == s_storages.Length)
                Array.Resize(ref s_storages, s_storageCount * 2);

            storage.Id = s_storageCount;
            s_storages[s_storageCount++] = storage;
            return storage;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ITweenStorage? Resolve(int storageId)
        => (uint)storageId < (uint)s_storageCount ? s_storages[storageId] : null;

    internal static void NotifyUpdateType(UpdateType type) => s_usesUpdateType[(int)type] = true;

    #region Runtime bootstrap

    /// <summary>
    /// Invoked once, just before the first tween is created, so the host engine can bring up
    /// whatever drives the ticks below. Prowl points this at <c>TweenMaster.Ensure</c>, which spawns
    /// the ticking component on demand; leaving it null simply means nothing is auto-created and
    /// you call <see cref="Update"/> and friends yourself.
    /// </summary>
    public static Action? RuntimeBootstrap;

    private static bool s_runtimeReady;

    /// <summary>
    /// Runs <see cref="RuntimeBootstrap"/> the first time it is needed. Latched, so creating a
    /// thousand tweens asks the host once - the host's own check may be far from free.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EnsureRuntime()
    {
        if (s_runtimeReady)
            return;

        // Set before invoking: a bootstrap that creates a tween of its own must not recurse.
        s_runtimeReady = true;
        RuntimeBootstrap?.Invoke();
    }

    /// <summary>
    /// Tells the library that whatever drives the ticks has gone, so the next tween created asks
    /// <see cref="RuntimeBootstrap"/> for a new one. Hosts call this when their driver is destroyed.
    /// </summary>
    public static void InvalidateRuntime() => s_runtimeReady = false;

    #endregion

    #region Diagnostics

    /// <summary>
    /// Where the library reports misuse that would otherwise be silent - composing a sequence that
    /// has already been killed, for instance. Point it at your engine's logger; leave it null and
    /// the warnings cost nothing. Prowl wires it to <c>Debug.LogWarning</c>.
    /// </summary>
    public static Action<string>? Diagnostics;

    internal static void Warn(string message) => Diagnostics?.Invoke(message);

    #endregion

    #region Lifetime

    /// <summary>
    /// Drops every storage and every tween in it without running callbacks, and resets the global
    /// defaults and the bootstrap latch.
    /// <para>
    /// This is the hot-reload / teardown seam: storages registered before a reload hold delegates
    /// into the assembly that has just been unloaded, so they have to be discarded rather than kept
    /// ticking. It is equally usable in-process - to isolate tests, or to tear a play session down -
    /// because the next tween of a given type simply registers a fresh storage.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Every handle taken out before the reset should be treated as invalid afterwards. They
    /// almost all stop resolving, but storage ids are handed out again from zero, so a handle held
    /// across a reset is not guaranteed to.
    /// </remarks>
    public static void Reset()
    {
        lock (s_registerLock)
        {
            Array.Clear(s_storages, 0, s_storageCount);
            s_storageCount = 0;
        }

        s_usesUpdateType[0] = true;
        s_usesUpdateType[1] = false;
        s_usesUpdateType[2] = false;
        s_usesUpdateType[3] = false;

        s_runtimeReady = false;
        TickThreadId = 0;
        TimeScale = 1f;
        Tween.ResetDefaults();
        TweenAwaiterRegistry.Clear();
    }

    /// <summary>
    /// Hands back the capacity every storage grew to but no longer needs, and releases the cold
    /// callback and tag arrays when nothing uses them. Worth calling after a burst - a loading
    /// screen, a cutscene - since the arrays otherwise stay at their high-water mark.
    /// </summary>
    public static void Trim()
    {
        for (int i = 0; i < s_storageCount; i++)
            s_storages[i].Trim();
    }

    #endregion

    #region Ticking

    /// <summary>
    /// Advances <see cref="UpdateType.Normal"/> tweens and reclaims killed ones.
    /// Call once per frame from your engine's update.
    /// </summary>
    /// <param name="deltaTime">Scaled frame delta.</param>
    /// <param name="unscaledDeltaTime">
    /// Unscaled frame delta, used by tweens marked with <c>SetUpdate(type, true)</c>.
    /// Defaults to <paramref name="deltaTime"/> when negative.
    /// </param>
    public static void Update(float deltaTime, float unscaledDeltaTime = -1f)
        => Tick(UpdateType.Normal, deltaTime, unscaledDeltaTime);

    /// <summary>Advances <see cref="UpdateType.Late"/> tweens. Call after your engine's late update.</summary>
    public static void LateUpdate(float deltaTime, float unscaledDeltaTime = -1f)
        => Tick(UpdateType.Late, deltaTime, unscaledDeltaTime);

    /// <summary>Advances <see cref="UpdateType.Fixed"/> tweens. Call from your fixed-step loop.</summary>
    public static void FixedUpdate(float fixedDeltaTime, float unscaledFixedDeltaTime = -1f)
        => Tick(UpdateType.Fixed, fixedDeltaTime, unscaledFixedDeltaTime);

    /// <summary>Advances <see cref="UpdateType.Manual"/> tweens. Call it yourself, whenever.</summary>
    public static void ManualUpdate(float deltaTime, float unscaledDeltaTime = -1f)
        => Tick(UpdateType.Manual, deltaTime, unscaledDeltaTime);

    private static void Tick(UpdateType type, float deltaTime, float unscaledDeltaTime)
    {
        if (!float.IsFinite(deltaTime))
            deltaTime = 0f;
        if (unscaledDeltaTime < 0f || !float.IsFinite(unscaledDeltaTime))
            unscaledDeltaTime = deltaTime;

        TickThreadId = Environment.CurrentManagedThreadId;

        // Before stepping, not after: pruning a tween whose owner has been destroyed has to happen
        // while it still cannot write a value, and every tick sweeps so a host that only drives the
        // fixed step still reclaims its dead entries.
        for (int i = 0; i < s_storageCount; i++)
            s_storages[i].PruneAndSweep();

        if (s_usesUpdateType[(int)type])
        {
            float scaled = deltaTime * TimeScale;
            for (int i = 0; i < s_storageCount; i++)
                s_storages[i].Update(type, scaled, unscaledDeltaTime);
        }

        TweenAwaiterRegistry.Pump();
    }

    #endregion

    #region Bulk control

    /// <summary>
    /// Kills every tween whose id or target equals <paramref name="idOrTarget"/>, or every live
    /// tween when it is null. Returns how many were killed.
    /// </summary>
    public static int Kill(object? idOrTarget, bool complete = false)
    {
        int killed = 0;
        for (int i = 0; i < s_storageCount; i++)
            killed += s_storages[i].KillWhere(idOrTarget, complete);
        return killed;
    }

    internal static int Control(object? idOrTarget, TweenAction action)
    {
        int affected = 0;
        for (int i = 0; i < s_storageCount; i++)
            affected += s_storages[i].ControlWhere(idOrTarget, action);
        return affected;
    }

    #endregion
}
