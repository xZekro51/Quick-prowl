// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// One step of a <see cref="KinoCamera"/>'s pipeline. Add it to the same GameObject as the camera and
/// it is picked up automatically; the <see cref="Stage"/> it declares decides when it runs.
/// </summary>
/// <remarks>
/// <para>
/// A component is handed the shot as it stands and changes the part it is responsible for. It is
/// never asked to produce a whole shot, so a camera with only an aim component still works - the
/// position simply stays where the camera's own transform is.
/// </para>
/// <para>
/// Write <see cref="Mutate"/> so that a <see cref="KinoContext.DeltaTime"/> of zero lands on the
/// final answer immediately. That is how the camera is placed when it goes live, and skipping it is
/// what makes a camera visibly slide in from wherever it was left.
/// </para>
/// </remarks>
[ExecuteAlways] // so adding or removing one is noticed while authoring, not only in play mode
public abstract class KinoComponent : MonoBehaviour
{
    private KinoCamera? _vcam;

    /// <summary>When this component runs.</summary>
    public abstract KinoStage Stage { get; }

    /// <summary>
    /// Whether the pipeline should run this component at all. Override to opt out when a required
    /// target or asset is missing - the camera then falls through to the next component in the stage.
    /// </summary>
    public virtual bool IsUsable => true;

    /// <summary>The camera this component belongs to, or null while it is sitting on its own.</summary>
    public KinoCamera? VirtualCamera
    {
        get
        {
            if (_vcam.IsNotValid())
                _vcam = GetComponent<KinoCamera>();
            return _vcam.IsValid() ? _vcam : null;
        }
    }

    /// <summary>Changes the part of the shot this component owns.</summary>
    /// <param name="state">The shot so far. Change only what this stage is responsible for.</param>
    /// <param name="ctx">Frame information; see <see cref="KinoContext.DeltaTime"/> for the snap rule.</param>
    public abstract void Mutate(ref KinoState state, in KinoContext ctx);

    /// <summary>
    /// Throws away any smoothing history, so the next update starts fresh. Called when the camera is
    /// about to go live and whenever a target is moved somewhere it could not have travelled to.
    /// </summary>
    public virtual void ResetDamping() { }

    /// <summary>Draws whatever this component wants to show for a camera that is selected in the editor.</summary>
    public virtual void DrawComponentGizmos() { }

    public override void OnEnable() => Invalidate();

    public override void OnDisable() => Invalidate();

    private void Invalidate()
    {
        _vcam = null;

        if (GameObject.IsNotValid())
            return;

        // The camera caches which components it runs; adding or removing one has to be noticed.
        KinoCamera? vcam = GetComponent<KinoCamera>();
        if (vcam.IsValid())
            vcam.InvalidateComponentCache();
    }

    #region Target helpers

    /// <summary>True when the camera has something to follow.</summary>
    protected bool HasFollow
    {
        get
        {
            KinoCamera? vcam = VirtualCamera;
            return vcam.IsValid() && vcam.HasFollow;
        }
    }

    /// <summary>True when the camera has something to look at.</summary>
    protected bool HasLookAt
    {
        get
        {
            KinoCamera? vcam = VirtualCamera;
            return vcam.IsValid() && vcam.HasLookAt;
        }
    }

    /// <summary>Where the follow target is, or the origin when there is none. Groups resolve to their centre.</summary>
    protected Float3 FollowPosition
    {
        get
        {
            KinoCamera? vcam = VirtualCamera;
            return vcam.IsValid() ? vcam.FollowPosition : Float3.Zero;
        }
    }

    /// <summary>Where the look-at target is, or the origin when there is none. Groups resolve to their centre.</summary>
    protected Float3 LookAtPosition
    {
        get
        {
            KinoCamera? vcam = VirtualCamera;
            return vcam.IsValid() ? vcam.LookAtPosition : Float3.Zero;
        }
    }

    /// <summary>The follow target's orientation, or identity when there is none.</summary>
    protected Quaternion FollowRotation
    {
        get
        {
            KinoCamera? vcam = VirtualCamera;
            return vcam.IsValid() ? vcam.FollowRotation : Quaternion.Identity;
        }
    }

    #endregion
}
