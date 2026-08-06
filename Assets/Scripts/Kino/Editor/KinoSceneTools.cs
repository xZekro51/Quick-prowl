// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Prowl.Editor.Core;
using Prowl.Editor.GUI;
using Prowl.Editor.GUI.SceneView;
using Prowl.Editor.Theming;
using Prowl.PaperUI;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino.Editor;

/// <summary>
/// Scene-view editing for a selected shot: the frame it takes, what it is pointed at, and a handle on
/// whatever its body component actually uses to place the camera.
/// </summary>
/// <remarks>
/// The handle edits the component's own value, not the camera's transform - dragging the follow offset
/// changes the offset, so the shot keeps working when the target moves. Dragging the transform of a
/// camera that has a body component would be undone on the next frame.
/// </remarks>
[ComponentSceneTool(typeof(KinoCamera))]
public class KinoCameraSceneTool : SceneTool
{
    public override string Name => "Kino Shot";
    public override string Icon => "\uf030"; // camera
    public override string? Tooltip => "Frame the selected shot and drag its offset";

    private const string OffsetHandle = "kino_offset";

    private static readonly Color32 s_frustum = new(120, 210, 255, 200);
    private static readonly Color32 s_target = new(250, 220, 90, 200);
    private static readonly Color32 s_live = new(120, 230, 150, 220);

    private KinoCamera? _vcam;

    public override void OnActivated(SceneToolContext ctx)
    {
        GameObject? go = ctx.ActiveObject;
        _vcam = go.IsValid() ? go.GetComponent<KinoCamera>() : null;
        TransformHandles.Forget(OffsetHandle);
    }

    public override void OnDeactivated()
    {
        _vcam = null;
        TransformHandles.Forget(OffsetHandle);
    }

    public override void OnSceneInput(SceneToolContext toolCtx)
    {
        GameObject? go = toolCtx.ActiveObject;
        _vcam = go.IsValid() ? go.GetComponent<KinoCamera>() : null;
        if (_vcam.IsNotValid())
            return;

        DrawShot(_vcam);
        DrawTargets(_vcam);
        DrawBodyHandle(toolCtx, _vcam);
    }

    public override void OnToolStripGUI(SceneToolContext ctx, Paper paper, string id)
    {
        if (_vcam.IsNotValid())
            return;

        bool solo = ReferenceEquals(KinoCore.Solo, _vcam);
        KinoEditorGUI.ToggleButton(paper, $"{id}_solo", "Solo", solo,
            () => KinoCore.Solo = solo ? null : _vcam);
    }

    /// <summary>The frame this shot takes. Evaluated here so it is right even with no brain in the scene.</summary>
    private void DrawShot(KinoCamera vcam)
    {
        KinoState state = vcam.Evaluate();
        Float3 position = state.FinalPosition;
        Quaternion rotation = state.FinalRotation;

        Color32 color = vcam.IsLive ? s_live : s_frustum;

        float near = Maths.Max(state.Lens.NearClip, 0.05f);
        float far = Maths.Min(Maths.Max(near + 0.5f, 4f), state.Lens.FarClip);

        DrawFrustumRing(position, rotation, state.Lens, near, color);
        DrawFrustumRing(position, rotation, state.Lens, far, color);

        Span<Float3> nearCorners = stackalloc Float3[4];
        Span<Float3> farCorners = stackalloc Float3[4];
        Corners(position, rotation, state.Lens, near, nearCorners);
        Corners(position, rotation, state.Lens, far, farCorners);

        for (int i = 0; i < 4; i++)
            DrawLine(nearCorners[i], farCorners[i], color);
    }

    private void DrawFrustumRing(Float3 position, Quaternion rotation, in KinoLens lens, float distance, Color32 color)
    {
        Span<Float3> corners = stackalloc Float3[4];
        Corners(position, rotation, lens, distance, corners);

        for (int i = 0; i < 4; i++)
            DrawLine(corners[i], corners[(i + 1) % 4], color);
    }

    private static void Corners(Float3 position, Quaternion rotation, in KinoLens lens, float distance, Span<Float3> corners)
    {
        // 16:9, because that is what the shot is being composed for even if the game view is not.
        float halfHeight = lens.HalfHeightAt(distance);
        float halfWidth = lens.HalfWidthAt(distance, 16f / 9f);

        Float3 forward = rotation * Float3.UnitZ * distance;
        Float3 right = rotation * Float3.UnitX * halfWidth;
        Float3 up = rotation * Float3.UnitY * halfHeight;

        corners[0] = position + forward - right - up;
        corners[1] = position + forward + right - up;
        corners[2] = position + forward + right + up;
        corners[3] = position + forward - right + up;
    }

    private void DrawTargets(KinoCamera vcam)
    {
        Float3 position = vcam.State.FinalPosition;

        if (vcam.HasLookAt)
        {
            Float3 point = vcam.LookAtPosition;
            DrawLine(position, point, s_target);
            DrawHandleDot(point, s_target, 7f, HandleCap.Circle);
            DrawWorldLabel(point, "Look At", s_target);
        }

        if (vcam.HasFollow)
        {
            Float3 point = vcam.FollowPosition;
            DrawHandleDot(point, new Color32(150, 250, 170, 200), 7f, HandleCap.Square);
            DrawWorldLabel(point, "Follow", new Color32(150, 250, 170, 200));
        }
    }

    /// <summary>
    /// A drag handle on whatever the body component uses to place the camera, so the shot can be framed
    /// by eye instead of by typing numbers into an offset.
    /// </summary>
    private void DrawBodyHandle(SceneToolContext toolCtx, KinoCamera vcam)
    {
        KinoComponent? body = KinoEditorUtil.StageWinner(vcam, KinoStage.Body);
        if (body.IsNotValid() || !vcam.HasFollow)
            return;

        HandleContext ctx = toolCtx.Handles;

        switch (body)
        {
            case KinoTransposer transposer:
            {
                // The offset lives in the binding frame; the handle works in world space, so the drag is
                // converted back through the same frame the component would have used.
                Quaternion frame = FrameOf(transposer, vcam);
                Float3 world = vcam.FollowPosition + frame * transposer.FollowOffset;
                Float3 before = world;

                if (TransformHandles.PositionHandle(ctx, OffsetHandle, ref world, out _))
                {
                    Undo.Snapshot(transposer);
                    transposer.FollowOffset = Quaternion.Inverse(frame) * (world - vcam.FollowPosition);
                    toolCtx.MarkDirty();
                }

                DrawLine(vcam.FollowPosition, before, new Color32(150, 250, 170, 160));
                DrawLengthLabel(vcam.FollowPosition, before, new Color32(150, 250, 170, 200));
                break;
            }

            case KinoOrbitalTransposer orbital:
            {
                Float3 pivot = vcam.FollowPosition + orbital.TargetOffset;
                DrawCircle(pivot, Float3.UnitY, Maths.Max(orbital.Radius, 0.01f), new Color32(150, 250, 170, 140));
                DrawLine(pivot, vcam.State.Position, new Color32(150, 250, 170, 160));

                Float3 handle = vcam.FollowPosition + orbital.TargetOffset;
                Float3 before = handle;

                if (TransformHandles.PositionHandle(ctx, OffsetHandle, ref handle, out _))
                {
                    Undo.Snapshot(orbital);
                    orbital.TargetOffset = handle - vcam.FollowPosition;
                    toolCtx.MarkDirty();
                }

                DrawWorldLabel(before, $"r {orbital.Radius:0.#}", new Color32(150, 250, 170, 200));
                break;
            }

            case KinoFramingTransposer framing:
            {
                Float3 subject = vcam.FollowPosition + framing.TrackedObjectOffset;
                Float3 before = subject;

                if (TransformHandles.PositionHandle(ctx, OffsetHandle, ref subject, out _))
                {
                    Undo.Snapshot(framing);
                    framing.TrackedObjectOffset = subject - vcam.FollowPosition;
                    toolCtx.MarkDirty();
                }

                DrawLine(vcam.State.FinalPosition, before, new Color32(150, 250, 170, 160));
                DrawWorldLabel(before, $"{framing.CameraDistance:0.#} m", new Color32(150, 250, 170, 200));
                break;
            }
        }
    }

    private static Quaternion FrameOf(KinoTransposer transposer, KinoCamera vcam) => transposer.BindingMode switch
    {
        KinoBindingMode.WorldSpace => Quaternion.Identity,
        KinoBindingMode.LockToTarget => vcam.FollowRotation,
        KinoBindingMode.LockToTargetWithWorldUp => KinoMath.FlattenToUp(vcam.FollowRotation, Float3.UnitY),
        _ => KinoMath.SafeLookRotation(
            KinoMath.ProjectOnPlane(vcam.FollowPosition - vcam.State.Position, Float3.UnitY),
            Float3.UnitY, Quaternion.Identity)
    };
}

/// <summary>
/// Scene-view editing for a dolly path: every waypoint is a handle, and the curve is drawn as the
/// camera will actually travel it rather than as straight lines between points.
/// </summary>
[ComponentSceneTool(typeof(KinoPath))]
public class KinoPathSceneTool : SceneTool
{
    public override string Name => "Kino Path";
    public override string Icon => "\uf4d7"; // route
    public override string? Tooltip => "Drag waypoints, and add or remove them";

    private const string WaypointHandle = "kino_waypoint";
    private const string WaypointControl = "kino_waypoint_pick";

    private static readonly Color32 s_curve = new(255, 170, 70, 220);
    private static readonly Color32 s_point = new(255, 220, 120, 255);
    private static readonly Color32 s_selected = new(140, 230, 255, 255);

    private KinoPath? _path;
    private int _selected = -1;

    public override void OnActivated(SceneToolContext ctx)
    {
        GameObject? go = ctx.ActiveObject;
        _path = go.IsValid() ? go.GetComponent<KinoPath>() : null;
        _selected = -1;
        TransformHandles.Forget(WaypointHandle);
    }

    public override void OnDeactivated()
    {
        _path = null;
        _selected = -1;
        TransformHandles.Forget(WaypointHandle);
    }

    public override bool SuppressTransformGizmo => _selected >= 0;

    public override void OnSceneInput(SceneToolContext toolCtx)
    {
        GameObject? go = toolCtx.ActiveObject;
        _path = go.IsValid() ? go.GetComponent<KinoPath>() : null;
        if (_path.IsNotValid())
            return;

        HandleContext ctx = toolCtx.Handles;
        List<KinoPath.Waypoint> waypoints = _path.Waypoints;

        if (_selected >= waypoints.Count)
            _selected = -1;

        DrawCurve(_path);

        // Every waypoint registers its own control, so they arbitrate against each other and against
        // everything else in the viewport rather than this tool claiming the cursor.
        for (int i = 0; i < waypoints.Count; i++)
        {
            KinoPath.Waypoint waypoint = waypoints[i];
            if (waypoint == null)
                continue;

            Float3 world = _path.Transform.TransformPoint(waypoint.Position);
            ControlID id = ctx.GetControlID(WaypointControl, i);
            ctx.AddControl(id, world, 9f);
            ctx.RequestCursor(id, PaperCursor.Grab);

            if (ctx.IsNearest(id) && ctx.TryBeginDrag(id))
                _selected = i;

            bool selected = i == _selected;
            DrawHandleDot(world, selected ? s_selected : s_point, selected ? 9f : 7f, HandleCap.Circle);
            DrawWorldLabel(world, i.ToString(), selected ? s_selected : s_point);
        }

        if (_selected < 0 || _selected >= waypoints.Count)
            return;

        KinoPath.Waypoint chosen = waypoints[_selected];
        Float3 position = _path.Transform.TransformPoint(chosen.Position);

        if (TransformHandles.PositionHandle(ctx, WaypointHandle, ref position, out _))
        {
            Undo.Snapshot(_path);
            chosen.Position = _path.Transform.InverseTransformPoint(position);
            toolCtx.MarkDirty();
        }

        if (ctx.GetKeyDown(KeyCode.Delete) || ctx.GetKeyDown(KeyCode.Backspace))
        {
            Undo.Snapshot(_path);
            waypoints.RemoveAt(_selected);
            _selected = -1;
            toolCtx.MarkDirty();
        }
    }

    public override void OnToolStripGUI(SceneToolContext ctx, Paper paper, string id)
    {
        if (_path.IsNotValid())
            return;

        KinoEditorGUI.Button(paper, $"{id}_add", "+ Waypoint", () =>
        {
            Undo.Snapshot(_path);

            // Placed in front of the scene camera, which is where the user is looking and therefore
            // where they mean.
            Float3 world = ctx.Camera.IsValid()
                ? ctx.Camera.Transform.Position + ctx.Camera.Transform.Forward * 8f
                : _path!.Transform.Position;

            _path!.AddWaypoint(world);
            _selected = _path.Waypoints.Count - 1;
            ctx.MarkDirty();
        }, EditorTheme.Purple400, grow: false);

        if (_selected >= 0)
            KinoEditorGUI.Button(paper, $"{id}_del", "Remove", () =>
            {
                if (_path.IsNotValid() || _selected < 0 || _selected >= _path.Waypoints.Count)
                    return;

                Undo.Snapshot(_path);
                _path.Waypoints.RemoveAt(_selected);
                _selected = -1;
                ctx.MarkDirty();
            }, EditorTheme.Red400, grow: false);
    }

    private void DrawCurve(KinoPath path)
    {
        if (!path.IsUsable)
            return;

        int steps = Maths.Clamp(path.SegmentCount * 12, 8, 512);
        float max = path.MaxPosition;

        Float3 previous = path.EvaluatePosition(0f);
        for (int i = 1; i <= steps; i++)
        {
            Float3 current = path.EvaluatePosition(max * i / steps);
            DrawLine(previous, current, s_curve);
            previous = current;
        }
    }
}
