// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Editor.Core;
using Prowl.Editor.GUI;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Vector;

using SColor = System.Drawing.Color;

namespace Prowl.Kino.Editor;

/// <summary>
/// The brain's inspector is mostly a readout: which shot is on screen, what it is blending out of, and
/// how far through. The settings underneath are few and rarely touched.
/// </summary>
[CustomEditor(typeof(KinoBrain))]
public class KinoBrainEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var brain = (KinoBrain)target;
        if (brain.IsNotValid()) return;

        Undo.Snapshot(brain);

        DrawStatus(paper, $"{id}_status", brain);
        DrawBrainList(paper, $"{id}_brains", brain);

        KinoEditorGUI.Section(paper, $"{id}_blend", "Blending", () =>
        {
            KinoEditorGUI.EnumRow(paper, $"{id}_style", "Default Blend", brain.DefaultBlend.Style,
                v => brain.DefaultBlend.Style = v);

            if (brain.DefaultBlend.Style == KinoBlendStyle.Inherit)
            {
                KinoEditorGUI.Note(paper, $"{id}_inherit",
                    "There is nothing above the brain to inherit from - this behaves as a cut. Pick a real blend.",
                    KinoNoteKind.Warning);
            }
            else if (brain.DefaultBlend.Style != KinoBlendStyle.Cut)
            {
                KinoEditorGUI.FloatRow(paper, $"{id}_dur", "Duration", brain.DefaultBlend.Duration,
                    v => brain.DefaultBlend.Duration = v, "s", 0f);
            }

            if (brain.IsBlending)
                KinoEditorGUI.Button(paper, $"{id}_cut", "Finish Blend Now", () => brain.ForceCut(), EditorTheme.Blue400);
        });

        KinoEditorGUI.Section(paper, $"{id}_timing", "Timing", () =>
        {
            KinoEditorGUI.EnumRow(paper, $"{id}_method", "Update Method", brain.UpdateMethod,
                v => brain.UpdateMethod = v);

            KinoEditorGUI.Note(paper, $"{id}_methodnote", brain.UpdateMethod switch
            {
                KinoUpdateMethod.FixedUpdate => "Driven from the physics step. Use this when the follow target is a rigidbody.",
                KinoUpdateMethod.Manual => "Nothing drives the camera until your code calls ManualUpdate.",
                _ => "Driven after gameplay has finished moving everything this frame. The right choice almost always."
            });

            KinoEditorGUI.ToggleRow(paper, $"{id}_ts", "Ignore Time Scale", brain.IgnoreTimeScale,
                v => brain.IgnoreTimeScale = v);

            if (brain.IgnoreTimeScale)
                KinoEditorGUI.Note(paper, $"{id}_tsnote", "Blends and damping keep running at full speed while the game is slowed or paused.");
        });

        KinoEditorGUI.Section(paper, $"{id}_world", "World", () =>
        {
            KinoEditorGUI.Float3Row(paper, $"{id}_up", "World Up", brain.WorldUp, v => brain.WorldUp = v);

            if (Float3.LengthSquared(brain.WorldUp) < 0.0001f)
                KinoEditorGUI.Note(paper, $"{id}_upzero", "World Up is zero, so +Y is used instead.", KinoNoteKind.Warning);
        });

        DrawEditModeSection(paper, $"{id}_edit", brain);
    }

    private static void DrawStatus(Paper paper, string id, KinoBrain brain)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        var m = Origami.Current.Metrics;

        if (brain.OutputCamera.IsNotValid())
        {
            KinoEditorGUI.Note(paper, $"{id}_nocam",
                "No Camera on this GameObject, so there is nothing for the brain to drive.", KinoNoteKind.Problem);
        }

        KinoCamera? active = brain.ActiveCamera;
        if (active.IsNotValid())
        {
            KinoEditorGUI.Note(paper, $"{id}_novcam",
                KinoCore.Cameras.Count == 0
                    ? "No Kino Cameras in the scene yet. Add one and this brain will show it."
                    : "Nothing is live yet. The camera stays exactly where it is until a shot takes over.");
            return;
        }

        using (paper.Row($"{id}_live").Height(24)
            .Margin(m.PaddingLarge, m.PaddingLarge, m.Spacing, m.Spacing).RowBetween(m.SpacingMedium).Enter())
        {
            KinoEditorGUI.Badge(paper, $"{id}_b", ReferenceEquals(KinoCore.Solo, active) ? "SOLO" : "LIVE",
                ReferenceEquals(KinoCore.Solo, active) ? EditorTheme.Amber400 : EditorTheme.Green400);

            paper.Box($"{id}_name").Width(UnitValue.Stretch()).Height(24).IsNotInteractable()
                .Text(active!.GameObject.Name, font).TextColor(EditorTheme.Ink500)
                .FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft).TextTruncate();
        }

        if (!brain.IsBlending)
            return;

        KinoCamera? from = brain.OutgoingCamera;
        string fromName = from.IsValid() ? from.GameObject.Name : "the previous shot";

        EditorGUI.Row(paper, $"{id}_blendbar", "Blending",
            () => Origami.ProgressBar(paper, $"{id}_pb", brain.BlendWeight).Show());

        KinoEditorGUI.Note(paper, $"{id}_blendnote", $"From {fromName}, {brain.BlendWeight * 100f:0}% of the way across.");
    }

    /// <summary>
    /// Every enabled brain, and what each one is showing. Two brains quietly fighting over the same
    /// camera - a leftover on a prefab, a second one added to a rig - is otherwise invisible until the
    /// shot starts flickering, so the list is always there rather than only when something looks wrong.
    /// </summary>
    private static void DrawBrainList(Paper paper, string id, KinoBrain self)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        var m = Origami.Current.Metrics;
        int count = KinoCore.Brains.Count;

        KinoEditorGUI.Section(paper, id, count == 1 ? "Brain" : $"Brains ({count})", () =>
        {
            for (int i = 0; i < count; i++)
            {
                KinoBrain other = KinoCore.Brains[i];
                if (other.IsNotValid())
                    continue;

                bool isSelf = ReferenceEquals(other, self);
                bool driving = other.ActiveCamera.IsValid();
                string showing = driving ? other.ActiveCamera!.GameObject.Name : "nothing yet";
                if (other.IsBlending)
                    showing += $"  ({other.BlendWeight * 100f:0}%)";

                KinoBrain captured = other;

                using (paper.Row($"{id}_b{i}").Height(UnitValue.Auto).MinHeight(22)
                    .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.Spacing)
                    .Padding(m.SpacingLarge, m.SpacingLarge, m.SpacingSmall, m.SpacingSmall)
                    .RowBetween(m.SpacingMedium)
                    .Rounded(m.SmallRounding)
                    .BackgroundColor(isSelf ? SColor.FromArgb(36, EditorTheme.Purple400) : EditorTheme.Glass)
                    .BorderColor(isSelf ? SColor.FromArgb(110, EditorTheme.Purple400) : EditorTheme.BorderSoft)
                    .BorderWidth(1)
                    .OnClick(_ => Selection.Select(captured.GameObject))
                    .Enter())
                {
                    KinoEditorGUI.Badge(paper, $"{id}_b{i}_state",
                        driving ? "DRIVING" : "IDLE",
                        driving ? EditorTheme.Green400 : EditorTheme.Ink300);

                    paper.Box($"{id}_b{i}_n").Width(UnitValue.Stretch()).Height(20).IsNotInteractable()
                        .Text($"{captured.GameObject.Name}  →  {showing}", font)
                        .TextColor(isSelf ? EditorTheme.Ink500 : EditorTheme.Ink300)
                        .FontSize(m.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();

                    if (isSelf)
                        KinoEditorGUI.Badge(paper, $"{id}_b{i}_self", "THIS", EditorTheme.Purple400);
                    else if (!Application.IsPlaying && captured.PreviewInEditMode)
                        KinoEditorGUI.Badge(paper, $"{id}_b{i}_prev", "PREVIEW", EditorTheme.Blue400);
                }
            }

            if (count > 1)
                KinoEditorGUI.Note(paper, $"{id}_many",
                    "Each brain drives the Camera on its own GameObject - which is what split screen wants, and a mistake otherwise. Click a row to select it.",
                    KinoNoteKind.Warning);
        });
    }

    private static void DrawEditModeSection(Paper paper, string id, KinoBrain brain)
    {
        KinoEditorGUI.Section(paper, id, "Editor Preview", () =>
        {
            KinoEditorGUI.ToggleRow(paper, $"{id}_preview", "Preview In Edit Mode", brain.PreviewInEditMode,
                v =>
                {
                    brain.PreviewInEditMode = v;
                    if (!v)
                        brain.RestoreEditModePose();
                });

            KinoEditorGUI.Note(paper, $"{id}_note", brain.PreviewInEditMode
                ? "The Game view shows the winning shot without entering play mode, snapped rather than damped. The camera's own position is remembered and put back when this is turned off."
                : "The camera is yours again. Turn this on to see the winning shot in the Game view while you work.");

            if (brain.PreviewInEditMode)
                KinoEditorGUI.Button(paper, $"{id}_restore", "Release Camera", () =>
                {
                    brain.PreviewInEditMode = false;
                    brain.RestoreEditModePose();
                }, EditorTheme.Blue400);

            if (KinoCore.Solo.IsValid())
                KinoEditorGUI.Button(paper, $"{id}_unsolo", "Clear Solo", () => KinoCore.Solo = null, EditorTheme.Amber400);
        });
    }
}
