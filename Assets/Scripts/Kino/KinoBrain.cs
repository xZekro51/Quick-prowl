// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Drives a real <see cref="Camera"/> from whichever <see cref="KinoCamera"/> currently wins, and
/// blends between them when that changes. Put one on your camera and everything else is optional.
/// </summary>
/// <remarks>
/// <para>
/// With no Kino Camera anywhere in the scene the brain does nothing at all - the camera stays exactly
/// where you or your own script put it. It only takes over once there is a shot to show.
/// </para>
/// <para>
/// Only the cameras on screen are ever evaluated: the live one, plus the one being blended away from
/// while a blend is running. A scene with fifty shots in it costs the same as a scene with two.
/// </para>
/// </remarks>
[AddComponentMenu("Kino/Kino Brain")]
[ComponentIcon("\uf03d")] // Video
[RequireComponent(typeof(Camera))]
[ExecutionOrder(10000)] // after gameplay has finished moving everything this frame
[ExecuteAlways] // so the shot can be previewed without entering play mode
public class KinoBrain : MonoBehaviour
{
    /// <summary>The blend used whenever the camera taking over does not ask for one of its own.</summary>
    public KinoBlend DefaultBlend = new(KinoBlendStyle.EaseInOut, 1f);

    /// <summary>Which engine tick the camera is driven from. See <see cref="KinoUpdateMethod"/>.</summary>
    public KinoUpdateMethod UpdateMethod = KinoUpdateMethod.LateUpdate;

    /// <summary>Keep running at full speed while the game is slowed down or paused.</summary>
    public bool IgnoreTimeScale = false;

    /// <summary>Which way is up for every shot this brain shows. Change it for wall-walking or zero-g.</summary>
    public Float3 WorldUp = Float3.UnitY;

    /// <summary>
    /// Show the winning shot in the editor without entering play mode. The camera is put exactly where
    /// the shot says, with no easing and no blending, so what you see is what the game will open on.
    /// </summary>
    /// <remarks>
    /// Non-destructive: the camera's own position, rotation and lens are remembered the first time a
    /// shot takes over and put back the moment previewing stops - untick this, delete the last camera,
    /// or disable the brain, and the camera is exactly where you left it.
    /// </remarks>
    public bool PreviewInEditMode = true;

    /// <summary>Raised when a different camera takes over, blend or cut.</summary>
    public event Action<KinoCamera>? CameraActivated;

    /// <summary>Raised when the change was a cut, so effects that hide a discontinuity can fire.</summary>
    public event Action? CameraCut;

    private Camera? _camera;

    private KinoCamera? _activeCamera;
    private KinoCamera? _fromCamera;
    private KinoState _fromState = KinoState.Default;
    private KinoState _state = KinoState.Default;
    private KinoBlend _blend;
    private float _blendElapsed;
    private bool _blending;
    private bool _hasState;

    private bool _hasSavedPose;
    private Float3 _savedPosition;
    private Quaternion _savedRotation;
    private KinoLens _savedLens;

    /// <summary>The camera being shown, or null when nothing has taken over yet.</summary>
    public KinoCamera? ActiveCamera => _activeCamera.IsValid() ? _activeCamera : null;

    /// <summary>The camera being blended away from, if any.</summary>
    public KinoCamera? OutgoingCamera => _fromCamera.IsValid() ? _fromCamera : null;

    /// <summary>True while a blend is running.</summary>
    public bool IsBlending => _blending;

    /// <summary>How far the running blend has got, 0 to 1. One when nothing is blending.</summary>
    public float BlendWeight => _blending && _blend.Duration > 0f ? Maths.Saturate(_blendElapsed / _blend.Duration) : 1f;

    /// <summary>The shot currently on screen - the blended one while a blend is running.</summary>
    public KinoState CurrentState => _state;

    /// <summary>The camera this brain drives.</summary>
    public Camera? OutputCamera => _camera.IsValid() ? _camera : null;

    #region Lifecycle

    public override void OnEnable()
    {
        _camera = GetComponent<Camera>();
        KinoCore.RegisterBrain(this);
    }

    public override void OnDisable()
    {
        RestoreEditModePose();
        KinoCore.UnregisterBrain(this);
    }

    public override void LateUpdate()
    {
        // Outside play mode the preview runs from here whatever the update method says: the fixed and
        // manual clocks belong to a running game and do not tick in the editor.
        if (!Application.IsPlaying)
        {
            EditModePreview();
            return;
        }

        if (UpdateMethod == KinoUpdateMethod.LateUpdate)
            Process(IgnoreTimeScale ? Time.UnscaledDeltaTime : Time.DeltaTime);
    }

    public override void FixedUpdate()
    {
        if (Application.IsPlaying && UpdateMethod == KinoUpdateMethod.FixedUpdate)
            Process(Time.FixedDeltaTime);
    }

    private void EditModePreview()
    {
        if (!PreviewInEditMode || KinoCore.TopCamera().IsNotValid())
        {
            RestoreEditModePose();
            return;
        }

        if (!_hasSavedPose)
        {
            _hasSavedPose = true;
            _savedPosition = Transform.Position;
            _savedRotation = Transform.Rotation;
            if (_camera.IsValid())
                _savedLens = KinoLens.FromCamera(_camera);
        }

        // Snapped, never blended: authoring wants to see the shot itself, not a camera easing its way
        // towards one while the scene is being edited.
        Process(0f, snap: true);
    }

    /// <summary>
    /// Puts the camera back where it was before the editor preview took it over. Does nothing if the
    /// preview never ran.
    /// </summary>
    public void RestoreEditModePose()
    {
        if (!_hasSavedPose)
            return;

        _hasSavedPose = false;
        Transform.Position = _savedPosition;
        Transform.Rotation = _savedRotation;

        if (_camera.IsValid())
            _savedLens.ApplyTo(_camera);

        // The next preview starts fresh rather than blending out of a shot the camera is no longer on.
        _activeCamera = null;
        _fromCamera = null;
        _blending = false;
        _hasState = false;
    }

    #endregion

    #region Control

    /// <summary>
    /// Drives the camera by hand. Only does anything with <see cref="KinoUpdateMethod.Manual"/>, which
    /// is there for cutscene systems and replays that own their own clock.
    /// </summary>
    public void ManualUpdate(float deltaTime)
    {
        if (UpdateMethod == KinoUpdateMethod.Manual)
            Process(deltaTime);
    }

    /// <summary>Finishes the running blend immediately, landing on the incoming camera.</summary>
    public void ForceCut()
    {
        _blending = false;
        _fromCamera = null;
        _blendElapsed = 0f;
    }

    #endregion

    private KinoContext BuildContext(float deltaTime)
    {
        float aspect = 16f / 9f;
        if (_camera.IsValid() && _camera.Aspect > 0f)
            aspect = _camera.Aspect;

        Float3 up = KinoMath.SafeNormalize(WorldUp, Float3.UnitY);
        return new KinoContext(deltaTime, aspect, up);
    }

    private void Process(float deltaTime, bool snap = false)
    {
        KinoContext ctx = BuildContext(snap ? 0f : deltaTime);

        KinoCamera? top = KinoCore.TopCamera();
        if (top.IsNotValid())
            return; // every camera is switched off - leave the camera wherever it was left

        if (!ReferenceEquals(top, _activeCamera))
            Activate(top);

        if (snap)
        {
            _blending = false;
            _fromCamera = null;
        }

        // Claimed before the housekeeping pass so that the cameras being shown are not also treated as
        // idle ones, and stamped every frame so IsLive stays true for the whole blend.
        long frame = Time.FrameCount;
        if (_activeCamera.IsValid()) _activeCamera.LiveFrame = frame;
        if (_blending && _fromCamera.IsValid()) _fromCamera.LiveFrame = frame;

        KinoCore.Tick(ctx);

        if (_activeCamera.IsNotValid())
            return; // nothing has taken over - leave the camera exactly as we found it

        _activeCamera.UpdatePipeline(ctx);
        KinoState target = _activeCamera.State;

        if (_blending)
        {
            _blendElapsed += Maths.Abs(deltaTime);
            float progress = _blend.Duration > 0f ? _blendElapsed / _blend.Duration : 1f;

            if (progress >= 1f)
            {
                _blending = false;
                _fromCamera = null;
                _state = target;
            }
            else
            {
                if (_fromCamera.IsValid() && _fromCamera.EnabledInHierarchy)
                {
                    _fromCamera.UpdatePipeline(ctx);

                    // Kept up to date every frame so that a camera switched off or destroyed part way
                    // through a blend leaves the picture where it had got to, rather than snapping back
                    // to wherever it was when the blend started.
                    _fromState = _fromCamera.State;
                }

                _state = KinoState.Lerp(_fromState, target, _blend.Evaluate(progress));
            }
        }
        else
        {
            _state = target;
        }

        _hasState = true;
        Apply(_state);
    }

    private void Activate(KinoCamera camera)
    {
        KinoCamera? previous = _activeCamera;

        KinoBlend blend = camera.BlendIn.Style == KinoBlendStyle.Inherit ? DefaultBlend : camera.BlendIn;
        bool cut = blend.IsCut || previous.IsNotValid() || !_hasState;

        if (cut)
        {
            _blending = false;
            _fromCamera = null;
        }
        else
        {
            // Interrupting a blend freezes what is on screen and blends out of that, rather than
            // handing the outgoing camera back to a shot it was already halfway out of. The picture
            // carries on from exactly where it was, whichever camera it happened to belong to.
            _fromState = _state;

            // A camera that is still running carries on being evaluated while it is blended away from,
            // so it keeps following its target on the way out. One that is mid-blend, switched off or
            // gone is blended out of as a frozen picture instead.
            _fromCamera = !_blending && previous.IsValid() && previous.EnabledInHierarchy ? previous : null;

            _blend = blend;
            _blendElapsed = 0f;
            _blending = true;
        }

        _activeCamera = camera;

        // An idle camera has no idea where it should be, so it is placed rather than moved - otherwise
        // it would spend the blend sliding in from wherever it was last left.
        if (camera.StandbyUpdate == KinoStandbyUpdate.Never)
            camera.Snap();

        CameraActivated?.Invoke(camera);
        if (cut)
            CameraCut?.Invoke();
    }

    private void Apply(in KinoState state)
    {
        Transform.Position = state.FinalPosition;
        Transform.Rotation = state.FinalRotation;

        if (_camera.IsValid())
            state.Lens.ApplyTo(_camera);
    }
}
