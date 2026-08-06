// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;

using Prowl.Editor.Core;
using Prowl.Editor.GUI;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino.Editor;

/// <summary>
/// Transposer: the binding mode decides everything else in the component, so it is explained in words
/// rather than left as an enum, and the offset gets the four shots people actually set up.
/// </summary>
[CustomEditor(typeof(KinoTransposer))]
public class KinoTransposerEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var body = (KinoTransposer)target;
        if (body.IsNotValid()) return;

        Undo.Snapshot(body);
        KinoEditorGUI.PipelineStatus(paper, id, body);

        KinoEditorGUI.EnumRow(paper, $"{id}_mode", "Binding Mode", body.BindingMode, v => body.BindingMode = v);
        KinoEditorGUI.Note(paper, $"{id}_modenote", Explain(body.BindingMode));

        KinoEditorGUI.Float3Row(paper, $"{id}_offset", "Follow Offset", body.FollowOffset, v => body.FollowOffset = v);
        DrawOffsetPresets(paper, $"{id}_presets", body);

        KinoEditorGUI.Section(paper, $"{id}_damping", "Damping", () =>
        {
            (string x, string y, string z) = AxisNames(body.BindingMode);
            KinoEditorGUI.DampingRow3(paper, $"{id}_d", "Follow", body.Damping, v => body.Damping = v, x, y, z);

            if (body.BindingMode is KinoBindingMode.LockToTarget or KinoBindingMode.LockToTargetWithWorldUp)
                KinoEditorGUI.DampingRow(paper, $"{id}_rd", "Rotation", body.RotationDamping, v => body.RotationDamping = v);

            KinoEditorGUI.Note(paper, $"{id}_dnote", "Damping is the time to close the gap. 0 is a rigid mount.");
        });

        DrawLiveReadout(paper, $"{id}_live", body);
    }

    private static string Explain(KinoBindingMode mode) => mode switch
    {
        KinoBindingMode.WorldSpace =>
            "The offset is a world direction. The target can spin on the spot and the shot does not move.",
        KinoBindingMode.LockToTarget =>
            "The offset rides the target's full orientation, roll included. A camera bolted to a vehicle.",
        KinoBindingMode.LockToTargetWithWorldUp =>
            "The offset follows the target's heading only, so its pitch and roll do not tip the shot. The usual choice for characters.",
        _ =>
            "The camera keeps whatever bearing it already had and only corrects distance and height. It never pushes itself behind the target's back."
    };

    private static (string, string, string) AxisNames(KinoBindingMode mode) => mode switch
    {
        KinoBindingMode.WorldSpace => ("X", "Y", "Z"),
        _ => ("Side", "Up", "Fwd")
    };

    private static void DrawOffsetPresets(Paper paper, string id, KinoTransposer body)
    {
        var m = Origami.Current.Metrics;
        using (paper.Row(id).Height(UnitValue.Auto)
            .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.Spacing).RowBetween(m.Spacing).Enter())
        {
            float distance = Maths.Max(Float3.Length(body.FollowOffset), 1f);

            Preset(paper, $"{id}_behind", "Behind", new Float3(0f, distance * 0.35f, -distance));
            Preset(paper, $"{id}_side", "Side", new Float3(distance, distance * 0.2f, 0f));
            Preset(paper, $"{id}_above", "Above", new Float3(0f, distance, -0.01f));
            Preset(paper, $"{id}_front", "Front", new Float3(0f, distance * 0.25f, distance));
        }

        void Preset(Paper paper, string buttonId, string label, Float3 offset)
            => KinoEditorGUI.Button(paper, buttonId, label, () =>
            {
                Undo.Snapshot(body);
                body.FollowOffset = offset;
            }, EditorTheme.Blue400, grow: false);
    }

    private static void DrawLiveReadout(Paper paper, string id, KinoTransposer body)
    {
        KinoCamera? vcam = body.VirtualCamera;
        if (vcam.IsNotValid() || !vcam.HasFollow)
            return;

        float distance = Float3.Distance(vcam.State.Position, vcam.FollowPosition);
        KinoEditorGUI.Note(paper, id, $"Currently {distance:0.##} m from the target.");
    }
}

/// <summary>
/// Orbital transposer: two player-driven axes and a radius, so the inspector leads with the orbit
/// itself and keeps the axes together where they can be compared.
/// </summary>
[CustomEditor(typeof(KinoOrbitalTransposer))]
public class KinoOrbitalTransposerEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var body = (KinoOrbitalTransposer)target;
        if (body.IsNotValid()) return;

        Undo.Snapshot(body);
        KinoEditorGUI.PipelineStatus(paper, id, body);

        KinoEditorGUI.Section(paper, $"{id}_orbit", "Orbit", () =>
        {
            KinoEditorGUI.Float3Row(paper, $"{id}_pivot", "Target Offset", body.TargetOffset, v => body.TargetOffset = v);
            KinoEditorGUI.FloatRow(paper, $"{id}_radius", "Radius", body.Radius, v => body.Radius = v, "m", 0f);
            KinoEditorGUI.ToggleRow(paper, $"{id}_behind", "Start Behind Target", body.StartBehindTarget,
                v => body.StartBehindTarget = v);
            KinoEditorGUI.DampingRow3(paper, $"{id}_damp", "Damping", body.Damping, v => body.Damping = v, "Side", "Up", "Dist");
        });

        KinoEditorGUI.Section(paper, $"{id}_heading", "Heading", () =>
        {
            KinoAxisEditor.Draw(paper, $"{id}_h", body.Heading);
            if (body.Heading.RecenterWait <= 0f)
                KinoEditorGUI.Note(paper, $"{id}_hrecenter",
                    "Set Recenter Wait to have the camera drift back behind the target when the player stops steering.");
        });

        KinoEditorGUI.Section(paper, $"{id}_elevation", "Elevation", () =>
            KinoAxisEditor.Draw(paper, $"{id}_e", body.Elevation));
    }
}

/// <summary>
/// Framing transposer: the screen composition is the point of the component, so it is drawn before
/// anything else and the distance controls sit under it.
/// </summary>
[CustomEditor(typeof(KinoFramingTransposer))]
public class KinoFramingTransposerEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var body = (KinoFramingTransposer)target;
        if (body.IsNotValid()) return;

        Undo.Snapshot(body);
        KinoEditorGUI.PipelineStatus(paper, id, body);

        KinoCamera? vcam = body.VirtualCamera;

        KinoEditorGUI.Section(paper, $"{id}_framing", "Framing", () =>
        {
            var composition = new KinoComposition(body.ScreenX, body.ScreenY,
                body.DeadZoneWidth, body.DeadZoneHeight, body.SoftZoneWidth, body.SoftZoneHeight, body);

            KinoCompositionGuide.Section(paper, $"{id}_comp", composition, hasPreview: false);
            KinoEditorGUI.Float3Row(paper, $"{id}_toff", "Tracked Object Offset", body.TrackedObjectOffset,
                v => body.TrackedObjectOffset = v);
        });

        KinoEditorGUI.Section(paper, $"{id}_distance", "Distance", () =>
        {
            KinoEditorGUI.FloatRow(paper, $"{id}_dist", "Camera Distance", body.CameraDistance,
                v => body.CameraDistance = v, "m", 0f);
            KinoEditorGUI.FloatRow(paper, $"{id}_ddz", "Distance Dead Zone", body.DistanceDeadZone,
                v => body.DistanceDeadZone = v, "m", 0f);
        });

        KinoEditorGUI.Section(paper, $"{id}_damping", "Damping", () =>
        {
            KinoEditorGUI.DampingRow(paper, $"{id}_dh", "Horizontal", body.HorizontalDamping, v => body.HorizontalDamping = v);
            KinoEditorGUI.DampingRow(paper, $"{id}_dv", "Vertical", body.VerticalDamping, v => body.VerticalDamping = v);
            KinoEditorGUI.DampingRow(paper, $"{id}_dd", "Distance", body.DistanceDamping, v => body.DistanceDamping = v);
        });

        KinoEditorGUI.Section(paper, $"{id}_lookahead", "Lookahead", () =>
        {
            KinoEditorGUI.FloatRow(paper, $"{id}_lt", "Lookahead Time", body.LookaheadTime,
                v => body.LookaheadTime = v, "s", 0f);

            if (body.LookaheadTime > 0f)
                KinoEditorGUI.DampingRow(paper, $"{id}_ls", "Smoothing", body.LookaheadSmoothing,
                    v => body.LookaheadSmoothing = v);
            else
                KinoEditorGUI.Note(paper, $"{id}_lnote",
                    "Lead the subject by a fraction of a second and the camera looks where it is going rather than where it has been.");
        });

        KinoEditorGUI.Section(paper, $"{id}_group", "Group Framing", () =>
        {
            KinoEditorGUI.ToggleRow(paper, $"{id}_gf", "Group Framing", body.GroupFraming, v => body.GroupFraming = v);

            if (!body.GroupFraming)
                return;

            if (vcam.IsValid() && vcam.FollowGroup.IsNotValid())
            {
                KinoEditorGUI.Note(paper, $"{id}_gnote",
                    "Nothing to frame: this only does something when Follow points at a Kino Target Group.",
                    KinoNoteKind.Info);
                return;
            }

            KinoEditorGUI.SliderRow(paper, $"{id}_gs", "Framing Size", body.GroupFramingSize, 0.1f, 2f,
                v => body.GroupFramingSize = v);
            KinoEditorGUI.FloatRow(paper, $"{id}_gmin", "Minimum Distance", body.MinimumDistance,
                v => body.MinimumDistance = v, "m", 0f);
            KinoEditorGUI.FloatRow(paper, $"{id}_gmax", "Maximum Distance", body.MaximumDistance,
                v => body.MaximumDistance = v, "m", 0f);
        });
    }
}

/// <summary>
/// Tracked dolly: the path is the component's whole reason to exist, so a missing one is called out
/// and the position along it is a slider bounded by the path rather than a free number.
/// </summary>
[CustomEditor(typeof(KinoTrackedDolly))]
public class KinoTrackedDollyEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var body = (KinoTrackedDolly)target;
        if (body.IsNotValid()) return;

        Undo.Snapshot(body);
        KinoEditorGUI.PipelineStatus(paper, id, body);

        KinoEditorGUI.FieldRow(paper, $"{id}_path", "Path", typeof(KinoPath), body.Path,
            v => body.Path = v as KinoPath);

        bool usable = body.Path.IsValid() && body.Path.IsUsable;

        if (body.Path.IsValid() && !body.Path.IsUsable)
            KinoEditorGUI.Note(paper, $"{id}_short", "That path needs at least two waypoints before a camera can travel along it.",
                KinoNoteKind.Problem);

        KinoEditorGUI.Section(paper, $"{id}_travel", "Travel", () =>
        {
            KinoEditorGUI.ToggleRow(paper, $"{id}_auto", "Auto Dolly", body.AutoDolly, v => body.AutoDolly = v);

            if (body.AutoDolly)
            {
                KinoEditorGUI.Note(paper, $"{id}_autonote", "The camera slides along the path to stay level with the Follow target.");
                KinoEditorGUI.FieldRow(paper, $"{id}_res", "Search Resolution", typeof(int), body.AutoDollyResolution,
                    v => body.AutoDollyResolution = Maths.Max((int)v!, 2));
            }
            else if (usable)
            {
                KinoEditorGUI.SliderRow(paper, $"{id}_pos", "Path Position", body.PathPosition,
                    0f, body.Path!.MaxPosition, v => body.PathPosition = v);
            }
            else
            {
                KinoEditorGUI.FloatRow(paper, $"{id}_pos", "Path Position", body.PathPosition, v => body.PathPosition = v);
            }

            KinoEditorGUI.DampingRow(paper, $"{id}_pd", "Path Damping", body.PathDamping, v => body.PathDamping = v);
        });

        KinoEditorGUI.Section(paper, $"{id}_offset", "Offset", () =>
        {
            KinoEditorGUI.Float3Row(paper, $"{id}_po", "Path Offset", body.PathOffset, v => body.PathOffset = v);
            KinoEditorGUI.DampingRow3(paper, $"{id}_d", "Damping", body.Damping, v => body.Damping = v, "Side", "Up", "Fwd");
            KinoEditorGUI.EnumRow(paper, $"{id}_up", "Camera Up", body.CameraUp, v => body.CameraUp = v);

            if (body.CameraUp == KinoUpMode.FromBody)
                KinoEditorGUI.Note(paper, $"{id}_upnote", "The path's banking tilts the shot. Set roll on the waypoints to use it.");
        });
    }
}

/// <summary>Hard lock: three fields, but the offset frame is worth stating outright.</summary>
[CustomEditor(typeof(KinoHardLockToTarget))]
public class KinoHardLockToTargetEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var body = (KinoHardLockToTarget)target;
        if (body.IsNotValid()) return;

        Undo.Snapshot(body);
        KinoEditorGUI.PipelineStatus(paper, id, body);

        KinoEditorGUI.Float3Row(paper, $"{id}_offset", "Offset", body.Offset, v => body.Offset = v);
        KinoEditorGUI.Note(paper, $"{id}_note", "The offset is in the target's own axes: +Z is in front of it, +Y above it.");
        KinoEditorGUI.DampingRow(paper, $"{id}_damp", "Damping", body.Damping, v => body.Damping = v);
    }
}
