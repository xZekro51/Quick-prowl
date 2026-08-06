// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// A shot. It holds no rendering of its own - it works out where a camera should be and what it
/// should be looking at, and the <see cref="KinoBrain"/> puts the real <see cref="Camera"/> there.
/// </summary>
/// <remarks>
/// <para>
/// On its own a Kino Camera is a locked-off shot: wherever you drag its GameObject is where the
/// camera goes. Add a <see cref="KinoComponent"/> - a transposer, a composer - and it starts moving.
/// </para>
/// <para>
/// Whichever enabled camera has the highest <see cref="Priority"/> is the one you see. Raise a
/// priority, enable a camera, disable one: the brain blends to whatever now wins, so switching shots
/// is a matter of turning cameras on and off rather than animating anything.
/// </para>
/// <para>
/// While a camera is not live it costs nothing at all - see <see cref="StandbyUpdate"/>.
/// </para>
/// </remarks>
[AddComponentMenu("Kino/Kino Camera")]
[ComponentIcon("\uf030")] // Camera
[ExecuteAlways] // registers with KinoCore in the editor too, so shots can be previewed while authoring
public class KinoCamera : MonoBehaviour
{
    /// <summary>The highest priority among enabled cameras is the one the brain shows. Ties go to whichever was enabled last.</summary>
    public int Priority = 10;

    /// <summary>The object the body components move the camera relative to. May be a <see cref="KinoTargetGroup"/>.</summary>
    public GameObject? Follow;

    /// <summary>The object the aim components point the camera at. May be a <see cref="KinoTargetGroup"/>.</summary>
    public GameObject? LookAt;

    /// <summary>The lens this shot is taken through.</summary>
    public KinoLens Lens = KinoLens.Default;

    /// <summary>
    /// How the brain should blend <i>to</i> this camera. Leave it on
    /// <see cref="KinoBlendStyle.Inherit"/> to use the brain's default blend.
    /// </summary>
    public KinoBlend BlendIn = KinoBlend.Inherit;

    /// <summary>How often this camera updates while something else is live. See <see cref="KinoStandbyUpdate"/>.</summary>
    public KinoStandbyUpdate StandbyUpdate = KinoStandbyUpdate.Never;

    private KinoState _state = KinoState.Default;
    private bool _snapNextUpdate = true;
    private long _lastUpdateFrame = -1;

    private KinoComponent? _body;
    private KinoComponent? _aim;
    private readonly List<KinoComponent> _noise = [];
    private readonly List<KinoComponent> _finalize = [];
    private bool _componentsResolved;

    private GameObject? _followCached;
    private KinoTargetGroup? _followGroup;
    private GameObject? _lookAtCached;
    private KinoTargetGroup? _lookAtGroup;

    /// <summary>Set by the brain to the frame this camera last contributed to what is on screen.</summary>
    internal long LiveFrame = -1;

    /// <summary>Registration order, the tie-break between cameras of equal priority.</summary>
    internal int Sequence;

    /// <summary>The shot this camera worked out, as of its last update.</summary>
    public KinoState State => _state;

    /// <summary>
    /// The body component this camera actually runs, out of however many are attached. Null when the
    /// camera's position is simply wherever its GameObject is.
    /// </summary>
    public KinoComponent? ActiveBody
    {
        get
        {
            ResolveComponents();
            return _body.IsValid() ? _body : null;
        }
    }

    /// <summary>
    /// The aim component this camera actually runs. Null when nothing is turning the camera, which is
    /// what lets a body component fill the rotation in for itself.
    /// </summary>
    public KinoComponent? ActiveAim
    {
        get
        {
            ResolveComponents();
            return _aim.IsValid() ? _aim : null;
        }
    }

    /// <summary>True while this camera is what you are looking at, blends included.</summary>
    public bool IsLive => LiveFrame >= Time.FrameCount - 1;

    #region Targets

    /// <summary>True when <see cref="Follow"/> points at something.</summary>
    public bool HasFollow => Follow.IsValid();

    /// <summary>True when <see cref="LookAt"/> points at something.</summary>
    public bool HasLookAt => LookAt.IsValid();

    /// <summary>The group behind <see cref="Follow"/>, if it is one.</summary>
    public KinoTargetGroup? FollowGroup
    {
        get
        {
            ResolveGroups();
            return _followGroup.IsValid() ? _followGroup : null;
        }
    }

    /// <summary>The group behind <see cref="LookAt"/>, if it is one.</summary>
    public KinoTargetGroup? LookAtGroup
    {
        get
        {
            ResolveGroups();
            return _lookAtGroup.IsValid() ? _lookAtGroup : null;
        }
    }

    /// <summary>Where the follow target is - a group's centre when it is a group. Origin when there is none.</summary>
    public Float3 FollowPosition
    {
        get
        {
            ResolveGroups();
            if (_followGroup.IsValid() && !_followGroup.IsEmpty)
                return _followGroup.Center;
            return Follow.IsValid() ? Follow.Transform.Position : Float3.Zero;
        }
    }

    /// <summary>Where the look-at target is - a group's centre when it is a group. Origin when there is none.</summary>
    public Float3 LookAtPosition
    {
        get
        {
            ResolveGroups();
            if (_lookAtGroup.IsValid() && !_lookAtGroup.IsEmpty)
                return _lookAtGroup.Center;
            return LookAt.IsValid() ? LookAt.Transform.Position : Float3.Zero;
        }
    }

    /// <summary>The follow target's orientation, or identity when there is none.</summary>
    public Quaternion FollowRotation => Follow.IsValid() ? Follow.Transform.Rotation : Quaternion.Identity;

    private void ResolveGroups()
    {
        // Target fields are public and can be reassigned at any time, so the lookup is keyed on the
        // object itself rather than done once - it re-runs only when the target actually changes.
        if (!ReferenceEquals(Follow, _followCached))
        {
            _followCached = Follow;
            _followGroup = Follow.IsValid() ? Follow.GetComponent<KinoTargetGroup>() : null;
        }

        if (!ReferenceEquals(LookAt, _lookAtCached))
        {
            _lookAtCached = LookAt;
            _lookAtGroup = LookAt.IsValid() ? LookAt.GetComponent<KinoTargetGroup>() : null;
        }
    }

    #endregion

    #region Lifecycle

    public override void OnEnable()
    {
        _componentsResolved = false;
        _snapNextUpdate = true;
        KinoCore.Register(this);
    }

    public override void OnDisable() => KinoCore.Unregister(this);

    #endregion

    #region Control

    /// <summary>
    /// Puts the camera straight onto its ideal shot on the next update, with no easing. Done for you
    /// when a camera goes live, so it arrives composed rather than sliding in from wherever it was.
    /// </summary>
    public void Snap()
    {
        _snapNextUpdate = true;

        if (_body.IsValid()) _body.ResetDamping();
        if (_aim.IsValid()) _aim.ResetDamping();
        for (int i = 0; i < _noise.Count; i++)
            if (_noise[i].IsValid()) _noise[i].ResetDamping();
        for (int i = 0; i < _finalize.Count; i++)
            if (_finalize[i].IsValid()) _finalize[i].ResetDamping();
    }

    /// <summary>
    /// Moves the camera by <paramref name="delta"/> without disturbing anything it has smoothed so
    /// far. Call it when the follow target teleports, so the shot travels with it instead of chasing
    /// it across the level.
    /// </summary>
    public void Warp(Float3 delta)
    {
        Transform.Position += delta;
        _state.Position += delta;
        _state.LookAtPoint += delta;
    }

    /// <summary>
    /// Wins the tie against every other camera of the same priority, without touching the priority
    /// itself. The usual way to re-activate a camera you are toggling between.
    /// </summary>
    public void MoveToTop() => KinoCore.MoveToTop(this);

    /// <summary>Re-reads which components make up this camera's pipeline. Done automatically when one is added or removed.</summary>
    public void InvalidateComponentCache() => _componentsResolved = false;

    /// <summary>
    /// Works out this camera's shot right now and returns it, without waiting to be asked by a brain.
    /// Answers "where would this camera be if it were live" - which is what the editor's shot preview
    /// is showing, and what a script wants before deciding whether to cut to it.
    /// </summary>
    /// <param name="aspect">Viewport shape to compose for. Screen positions and dead zones depend on it.</param>
    /// <remarks>
    /// A camera still only runs its pipeline once per frame, so calling this on the live camera returns
    /// what the brain already worked out rather than a second, conflicting answer.
    /// </remarks>
    public KinoState Evaluate(float aspect = 16f / 9f)
    {
        KinoBrain? brain = KinoCore.MainBrain;
        Float3 up = brain.IsValid() ? KinoMath.SafeNormalize(brain.WorldUp, Float3.UnitY) : Float3.UnitY;

        UpdatePipeline(new KinoContext(0f, aspect, up));
        return _state;
    }

    #endregion

    #region Pipeline

    /// <summary>
    /// Runs the pipeline for this frame. Repeat calls within the same frame are ignored, so a camera
    /// that two brains both want costs the same as a camera only one of them wants.
    /// </summary>
    internal void UpdatePipeline(in KinoContext ctx)
    {
        long frame = Time.FrameCount;
        if (_lastUpdateFrame == frame)
            return;
        _lastUpdateFrame = frame;

        KinoContext effective = _snapNextUpdate ? ctx.AsSnap() : ctx;
        _snapNextUpdate = false;

        Lens.Validate();
        ResolveComponents();

        // The shot starts as wherever this GameObject is. A camera with no body component is a locked
        // shot, and a camera with one picks up last frame's result - the transform is the history the
        // dampers work against.
        _state.Position = Transform.Position;
        _state.Rotation = Transform.Rotation;
        _state.Lens = Lens;
        _state.ReferenceUp = ctx.WorldUp;
        _state.PositionShake = Float3.Zero;
        _state.RotationShake = Quaternion.Identity;
        _state.LookAtPoint = Float3.Zero;
        _state.HasLookAt = false;

        if (_body.IsValid()) _body.Mutate(ref _state, effective);
        if (_aim.IsValid()) _aim.Mutate(ref _state, effective);

        // Written back before shake and correction so the GameObject tracks the shot the camera chose,
        // and so none of what follows can feed itself next frame.
        Transform.Position = _state.Position;
        Transform.Rotation = _state.Rotation;

        for (int i = 0; i < _noise.Count; i++)
            if (_noise[i].IsValid()) _noise[i].Mutate(ref _state, effective);
        for (int i = 0; i < _finalize.Count; i++)
            if (_finalize[i].IsValid()) _finalize[i].Mutate(ref _state, effective);
    }

    private void ResolveComponents()
    {
        if (_componentsResolved)
            return;
        _componentsResolved = true;

        _body = null;
        _aim = null;
        _noise.Clear();
        _finalize.Clear();

        foreach (KinoComponent component in GetComponents<KinoComponent>())
        {
            if (component.IsNotValid() || !component.EnabledInHierarchy || !component.IsUsable)
                continue;

            switch (component.Stage)
            {
                // Body and Aim each answer a question that has one answer, so the first one attached
                // wins and the rest are left alone rather than fighting over the same result.
                case KinoStage.Body:
                    if (_body.IsNotValid()) _body = component;
                    break;
                case KinoStage.Aim:
                    if (_aim.IsNotValid()) _aim = component;
                    break;
                case KinoStage.Noise:
                    _noise.Add(component);
                    break;
                case KinoStage.Finalize:
                    _finalize.Add(component);
                    break;
            }
        }
    }

    #endregion

    public override void DrawGizmos()
    {
        Float3 position = Transform.Position;
        Float3 forward = Transform.Forward;
        Float3 up = Transform.Up;
        Float3 right = Transform.Right;

        // A stub of a frustum: enough to read position and facing at a glance without drawing a
        // second camera's worth of lines over the one that is actually rendering.
        const float length = 0.6f;
        float half = Maths.Tan(Lens.FieldOfView * 0.5f * Maths.Deg2Rad) * length;
        Float3 tip = position + forward * length;
        Color color = IsLive ? Color.Lime : Color.Gray;

        Debug.DrawLine(position, tip + up * half + right * half, color);
        Debug.DrawLine(position, tip + up * half - right * half, color);
        Debug.DrawLine(position, tip - up * half + right * half, color);
        Debug.DrawLine(position, tip - up * half - right * half, color);
        Debug.DrawWireCircle(tip, forward, half, color);

        if (HasLookAt)
            Debug.DrawLine(position, LookAtPosition, Color.Yellow);

        foreach (KinoComponent component in GetComponents<KinoComponent>())
            if (component.IsValid() && component.EnabledInHierarchy)
                component.DrawComponentGizmos();
    }
}
