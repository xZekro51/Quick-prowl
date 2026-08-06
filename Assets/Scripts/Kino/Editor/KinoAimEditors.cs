// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Editor.Core;
using Prowl.Editor.GUI;
using Prowl.PaperUI;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino.Editor;

/// <summary>
/// Composer: the same composition block as the framing transposer, because a dead zone means the same
/// thing whether the camera turns to hold the subject or slides to.
/// </summary>
[CustomEditor(typeof(KinoComposer))]
public class KinoComposerEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var aim = (KinoComposer)target;
        if (aim.IsNotValid()) return;

        Undo.Snapshot(aim);
        KinoEditorGUI.PipelineStatus(paper, id, aim);

        KinoEditorGUI.Section(paper, $"{id}_framing", "Composition", () =>
        {
            var composition = new KinoComposition(aim.ScreenX, aim.ScreenY,
                aim.DeadZoneWidth, aim.DeadZoneHeight, aim.SoftZoneWidth, aim.SoftZoneHeight, aim);

            KinoCompositionGuide.Section(paper, $"{id}_comp", composition, hasPreview: false);
            KinoEditorGUI.Float3Row(paper, $"{id}_toff", "Tracked Object Offset", aim.TrackedObjectOffset,
                v => aim.TrackedObjectOffset = v);
        });

        KinoEditorGUI.Section(paper, $"{id}_damping", "Damping", () =>
        {
            KinoEditorGUI.DampingRow(paper, $"{id}_dh", "Horizontal", aim.HorizontalDamping, v => aim.HorizontalDamping = v);
            KinoEditorGUI.DampingRow(paper, $"{id}_dv", "Vertical", aim.VerticalDamping, v => aim.VerticalDamping = v);
        });

        if (aim.DeadZoneWidth <= 0f && aim.DeadZoneHeight <= 0f)
            KinoEditorGUI.Note(paper, $"{id}_nodead",
                "With no dead zone this is a damped Hard Look At. Widen it and the shot starts to feel operated rather than bolted on.");

        KinoCamera? vcam = aim.VirtualCamera;
        if (vcam.IsValid() && vcam.Lens.Orthographic)
            KinoEditorGUI.Note(paper, $"{id}_ortho",
                "Composing by rotation does very little through an orthographic lens. Use a Framing Transposer instead - it moves the camera.",
                KinoNoteKind.Warning);
    }
}

/// <summary>Hard look at: two fields, plus the reason you would reach for the composer instead.</summary>
[CustomEditor(typeof(KinoHardLookAt))]
public class KinoHardLookAtEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var aim = (KinoHardLookAt)target;
        if (aim.IsNotValid()) return;

        Undo.Snapshot(aim);
        KinoEditorGUI.PipelineStatus(paper, id, aim);

        KinoEditorGUI.Float3Row(paper, $"{id}_offset", "Tracked Object Offset", aim.TrackedObjectOffset,
            v => aim.TrackedObjectOffset = v);
        KinoEditorGUI.Note(paper, $"{id}_offnote", "A world-space offset from the target - raise Y to aim at the head rather than the feet.");
        KinoEditorGUI.DampingRow(paper, $"{id}_damp", "Damping", aim.Damping, v => aim.Damping = v);
    }
}

/// <summary>POV: two axes, and nothing else worth showing.</summary>
[CustomEditor(typeof(KinoPOV))]
public class KinoPOVEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var aim = (KinoPOV)target;
        if (aim.IsNotValid()) return;

        Undo.Snapshot(aim);
        KinoEditorGUI.PipelineStatus(paper, id, aim);

        KinoEditorGUI.ToggleRow(paper, $"{id}_rel", "Relative To Follow Target", aim.RelativeToFollowTarget,
            v => aim.RelativeToFollowTarget = v);

        if (aim.RelativeToFollowTarget && !HasFollow(aim))
            KinoEditorGUI.Note(paper, $"{id}_nofollow",
                "Nothing to be relative to: the camera has no Follow target, so the angles fall back to world space.",
                KinoNoteKind.Warning);

        KinoEditorGUI.Section(paper, $"{id}_h", "Horizontal", () => KinoAxisEditor.Draw(paper, $"{id}_hx", aim.Horizontal));
        KinoEditorGUI.Section(paper, $"{id}_v", "Vertical", () => KinoAxisEditor.Draw(paper, $"{id}_vy", aim.Vertical));
    }

    private static bool HasFollow(KinoPOV aim)
    {
        KinoCamera? vcam = aim.VirtualCamera;
        return vcam.IsValid() && vcam.HasFollow;
    }
}

/// <summary>Same as follow target: a rotation offset in degrees, which is worth labelling as such.</summary>
[CustomEditor(typeof(KinoSameAsFollowTarget))]
public class KinoSameAsFollowTargetEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var aim = (KinoSameAsFollowTarget)target;
        if (aim.IsNotValid()) return;

        Undo.Snapshot(aim);
        KinoEditorGUI.PipelineStatus(paper, id, aim);

        KinoEditorGUI.Float3Row(paper, $"{id}_rot", "Rotation", aim.Rotation, v => aim.Rotation = v);
        KinoEditorGUI.Note(paper, $"{id}_rotnote", "Degrees, applied on top of the target's own orientation. Pitch, yaw, roll.");
        KinoEditorGUI.DampingRow(paper, $"{id}_damp", "Damping", aim.Damping, v => aim.Damping = v);
    }
}
