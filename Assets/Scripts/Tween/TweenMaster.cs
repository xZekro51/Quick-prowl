using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Tweening;

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

    /// <summary>The instance currently driving every tween, or null before the first one exists.</summary>
    public static TweenMaster? Driver => IsUsable(s_driver) ? s_driver : null;

    /// <summary>
    /// Points <see cref="TweenManager.RuntimeBootstrap"/> at us. The runtime runs this before the
    /// first access to anything in this assembly, so the hook is always in place by the time game
    /// code can tween - including after a hot reload, which resets the field along with everything
    /// else in here.
    /// </summary>
#pragma warning disable CA2255 // This assembly IS the application, which is what a module initializer is for.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() => TweenManager.RuntimeBootstrap = static () => Ensure();

    /// <summary>
    /// Returns the live master, creating the GameObject, adding the component and preserving it
    /// across scene loads if there isn't one. A master already sitting in the scene is adopted
    /// rather than duplicated.
    /// </summary>
    public static TweenMaster? Ensure()
    {
        if (IsUsable(s_driver) || s_creating)
            return s_driver;

        foreach (TweenMaster? existing in Scene.Current.FindObjectsOfType<TweenMaster>())
        {
            if (!IsUsable(existing))
                continue;

            s_driver = existing;
            return existing;
        }

        s_creating = true;
        try
        {
            // DontSave: this object belongs to the run, not to whatever scene happens to be open
            // when the first tween fires, so it must never be serialized into one.
            GameObject go = new(ObjectName) { HideFlags = HideFlags.DontSave };
            TweenMaster master = go.AddComponent<TweenMaster>();

            // Claimed before the scene add, so anything that tweens from OnEnable finds this one.
            s_driver = master;

            Scene.Current.Add(go);
            Scene.DontDestroyOnLoad(go);
            return master;
        }
        finally
        {
            s_creating = false;
        }
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

        s_driver = this;
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
