// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Prowl.Runtime.Resources;
using Prowl.Tweening;

namespace Prowl.Runtime;

/// <summary>
/// Drives <see cref="Tween"/> from the engine loop. Nothing has to be placed in a scene by hand:
/// the first tween that gets created spawns one through <see cref="Ensure"/>, on a
/// <see cref="Scene.DontDestroyOnLoad"/> object so it keeps ticking across scene loads.
/// <para/>
/// Exactly one instance drives the tweens. A second one - a hand-placed component meeting the
/// auto-created object, say - stays idle instead of ticking everything twice.
/// </summary>
public class TweenMaster : MonoBehaviour
{
    /// <summary>Name given to the GameObject <see cref="Ensure"/> creates.</summary>
    public const string ObjectName = "Tween Master";

    private static TweenMaster? s_driver;

    /// <summary>Guards against a tween created from the driver's own OnEnable spawning a second one.</summary>
    private static bool s_creating;

    /// <summary>
    /// Set once this instance has driven the tweens, so its teardown knows whether the library's state
    /// is its to clear. An idle second master never sets it.
    /// </summary>
    private bool _hasDriven;

    /// <summary>The instance currently driving every tween, or null before the first one exists.</summary>
    public static TweenMaster? Driver => IsUsable(s_driver) ? s_driver : null;

    /// <summary>
    /// Wires the tweening library into the engine. The runtime runs this before the first access to
    /// anything in this assembly - including after a hot reload, which resets the statics in here.
    /// <para/>
    /// That makes it the reload seam: <see cref="TweenManager.Reset"/> discards the storages
    /// registered before the reload, whose tweens hold delegates into the assembly that has just
    /// been unloaded, instead of leaving them registered and still being ticked.
    /// </summary>
#pragma warning disable CA2255 // This assembly IS the application, which is what a module initializer is for.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install()
    {
        TweenManager.Reset();
        s_driver = null;
        s_creating = false;

        TweenManager.RuntimeBootstrap = static () => Ensure();
        TweenManager.Diagnostics = static message => Debug.LogWarning(message);
    }

    /// <summary>
    /// Returns the live master, creating the GameObject, adding the component and preserving it
    /// across scene loads if there isn't one. A master already sitting in the scene is adopted
    /// rather than duplicated.
    /// </summary>
    /// <remarks>
    /// The library latches this: it is asked once, not once per tween, so the scene scan below
    /// cannot turn into a per-creation cost. <see cref="TweenManager.InvalidateRuntime"/> re-arms it
    /// when the driver goes away.
    /// </remarks>
    public static TweenMaster? Ensure()
    {
        if (IsUsable(s_driver) || s_creating)
            return s_driver;

        foreach (TweenMaster? existing in Scene.Current.FindObjectsOfType<TweenMaster>())
        {
            if (!IsUsable(existing))
                continue;

            return Claim(existing);
        }

        s_creating = true;
        try
        {
            // DontSave: this object belongs to the run, not to whatever scene happens to be open
            // when the first tween fires, so it must never be serialized into one.
            GameObject go = new(ObjectName) { HideFlags = HideFlags.DontSave };
            TweenMaster master = go.AddComponent<TweenMaster>();

            // Claimed before the scene add, so anything that tweens from OnEnable finds this one.
            Claim(master);

            Scene.Current.Add(go);
            Scene.DontDestroyOnLoad(go);
            return master;
        }
        finally
        {
            s_creating = false;
        }
    }

    private static TweenMaster Claim(TweenMaster master)
    {
        s_driver = master;
        master._hasDriven = true;
        return master;
    }

    private static bool IsUsable([NotNullWhen(true)] TweenMaster? master)
        => master.IsValid() && master.GameObject.IsValid() && master.GameObject.Scene.IsValid();

    public override void OnEnable()
    {
        if (ReferenceEquals(s_driver, this))
            return;

        if (IsUsable(s_driver))
        {
            Debug.LogWarning($"[Tween] '{GameObject.Name}' is a second TweenMaster - '{s_driver.GameObject.Name}' keeps driving the tweens, this one stays idle.");
            return;
        }

        Claim(this);
    }

    public override void OnDisable() => Release();

    public override void OnRemovedFromScene() => Release();

    /// <summary>
    /// The one teardown hook the engine always delivers.
    /// <para>
    /// OnDisable and OnRemovedFromScene cannot be relied on for this: the engine skips gated
    /// callbacks whenever <see cref="Application.IsPlaying"/> is false, and leaving play mode clears
    /// that flag before it destroys the objects kept by <see cref="Scene.DontDestroyOnLoad"/>. Dispose
    /// runs regardless, so this is the only place the driver reliably learns its session is over.
    /// </para>
    /// </summary>
    protected override void OnDispose()
    {
        // Only the instance that drove the tweens owns their state, and only if no other master has
        // taken over since - an idle or superseded master going away must not touch anything.
        if (_hasDriven && (s_driver is null || ReferenceEquals(s_driver, this)))
        {
            s_driver = null;

            // The master is preserved across scene loads, so it only dies with the play session.
            // Nothing that session created may carry into the next one, and Reset also re-arms the
            // bootstrap, so the next tween spawns a fresh driver instead of waiting on this one.
            TweenManager.Reset();
        }

        base.OnDispose();
    }

    /// <summary>
    /// Gives up the driver role while the object still exists - disabled, or taken out of its scene -
    /// and tells the library to ask for a new one the next time a tween is created.
    /// </summary>
    private void Release()
    {
        if (!ReferenceEquals(s_driver, this))
            return;

        s_driver = null;
        TweenManager.InvalidateRuntime();
    }

    public override void FixedUpdate()
    {
        if (!ReferenceEquals(s_driver, this))
            return;

        Tween.FixedUpdate(Time.FixedDeltaTime);
    }

    public override void Update()
    {
        if (!ReferenceEquals(s_driver, this))
            return;

        float tweenDelta = Application.ShouldRunGameplay ? Time.DeltaTime : 0f;
        Tween.Update(tweenDelta, Time.UnscaledDeltaTime);
    }

    public override void LateUpdate()
    {
        if (!ReferenceEquals(s_driver, this))
            return;

        float tweenDelta = Application.ShouldRunGameplay ? Time.DeltaTime : 0f;
        Tween.LateUpdate(tweenDelta, Time.UnscaledDeltaTime);
    }
}
