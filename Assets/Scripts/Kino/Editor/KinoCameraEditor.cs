// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Prowl.Editor.Core;
using Prowl.Editor.GUI;
using Prowl.Editor.GUI.SceneView;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Vector;

using SColor = System.Drawing.Color;

namespace Prowl.Kino.Editor;

/// <summary>
/// The inspector for a shot. Answers the three questions the default grid cannot: is this the camera
/// I am looking at, what does it actually see, and what is running inside it.
/// </summary>
[CustomEditor(typeof(KinoCamera))]
public class KinoCameraEditor : CustomEditor
{
    private readonly KinoShotPreview _preview = new();
    private bool _showPreview = true;

    /// <summary>Every stage, and what to offer adding when it is empty.</summary>
    private static readonly (KinoStage Stage, string Label, Type[] Options)[] s_stages =
    [
        (KinoStage.Body, "Body", [typeof(KinoTransposer), typeof(KinoOrbitalTransposer), typeof(KinoFramingTransposer), typeof(KinoHardLockToTarget), typeof(KinoTrackedDolly)]),
        (KinoStage.Aim, "Aim", [typeof(KinoComposer), typeof(KinoHardLookAt), typeof(KinoPOV), typeof(KinoSameAsFollowTarget)]),
        (KinoStage.Noise, "Noise", [typeof(KinoShake)]),
        (KinoStage.Finalize, "Finalize", [typeof(KinoCollider), typeof(KinoConfiner), typeof(KinoImpulseListener)])
    ];

    public override void OnGUI(Paper paper, string id, object target)
    {
        var vcam = (KinoCamera)target;
        if (vcam.IsNotValid())
            return;

        Undo.Snapshot(vcam);

        DrawStatusBar(paper, $"{id}_status", vcam);
        DrawPreview(paper, $"{id}_preview", vcam);

        KinoEditorGUI.Section(paper, $"{id}_shot", "Shot", () =>
        {
            KinoEditorGUI.FieldRow(paper, $"{id}_prio", "Priority", typeof(int), vcam.Priority,
                v => vcam.Priority = (int)v!);

            KinoEditorGUI.FieldRow(paper, $"{id}_follow", "Follow", typeof(GameObject), vcam.Follow,
                v => vcam.Follow = v as GameObject);

            KinoEditorGUI.FieldRow(paper, $"{id}_lookat", "Look At", typeof(GameObject), vcam.LookAt,
                v => vcam.LookAt = v as GameObject);

            DrawTargetNotes(paper, $"{id}_tnotes", vcam);
        });

        KinoEditorGUI.Section(paper, $"{id}_lens", "Lens", () =>
        {
            KinoEditorGUI.ToggleRow(paper, $"{id}_ortho", "Orthographic", vcam.Lens.Orthographic,
                v => vcam.Lens.Orthographic = v);

            if (vcam.Lens.Orthographic)
                KinoEditorGUI.FloatRow(paper, $"{id}_osize", "Size", vcam.Lens.OrthographicSize,
                    v => vcam.Lens.OrthographicSize = v, "m", 0.01f);
            else
                KinoEditorGUI.SliderRow(paper, $"{id}_fov", "Field Of View", vcam.Lens.FieldOfView, 1f, 179f,
                    v => vcam.Lens.FieldOfView = v, "F0");

            KinoEditorGUI.FloatRow(paper, $"{id}_near", "Near Clip", vcam.Lens.NearClip,
                v => vcam.Lens.NearClip = v, "m", 0.001f);
            KinoEditorGUI.FloatRow(paper, $"{id}_far", "Far Clip", vcam.Lens.FarClip,
                v => vcam.Lens.FarClip = v, "m", 0.01f);
            KinoEditorGUI.SliderRow(paper, $"{id}_dutch", "Dutch", vcam.Lens.Dutch, -45f, 45f,
                v => vcam.Lens.Dutch = v, "F1");
        });

        KinoEditorGUI.Section(paper, $"{id}_blend", "Taking Over", () =>
        {
            KinoEditorGUI.EnumRow(paper, $"{id}_bstyle", "Blend In", vcam.BlendIn.Style,
                v => vcam.BlendIn.Style = v);

            if (vcam.BlendIn.Style is not KinoBlendStyle.Inherit and not KinoBlendStyle.Cut)
                KinoEditorGUI.FloatRow(paper, $"{id}_bdur", "Duration", vcam.BlendIn.Duration,
                    v => vcam.BlendIn.Duration = v, "s", 0f);
            else if (vcam.BlendIn.Style == KinoBlendStyle.Inherit)
                KinoEditorGUI.Note(paper, $"{id}_binherit", "Uses the brain's default blend.");

            KinoEditorGUI.EnumRow(paper, $"{id}_standby", "Standby Update", vcam.StandbyUpdate,
                v => vcam.StandbyUpdate = v);

            if (vcam.StandbyUpdate != KinoStandbyUpdate.Never)
                KinoEditorGUI.Note(paper, $"{id}_standbynote",
                    "This camera keeps running while another one is live. Only worth it for a heavily damped shot that should arrive already settled.");
        });

        DrawPipeline(paper, $"{id}_pipeline", vcam);
    }

    #region Status

    private void DrawStatusBar(Paper paper, string id, KinoCamera vcam)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        var m = Origami.Current.Metrics;
        bool solo = ReferenceEquals(KinoCore.Solo, vcam);
        bool live = vcam.IsLive;

        using (paper.Row(id).Height(28)
            .Margin(m.PaddingLarge, m.PaddingLarge, m.Spacing, m.Spacing)
            .RowBetween(m.SpacingMedium).Enter())
        {
            if (solo)
                KinoEditorGUI.Badge(paper, $"{id}_b", "SOLO", EditorTheme.Amber400);
            else if (live)
                KinoEditorGUI.Badge(paper, $"{id}_b", "LIVE", EditorTheme.Green400);
            else
                KinoEditorGUI.Badge(paper, $"{id}_b", "STANDBY", EditorTheme.Ink300);

            paper.Box($"{id}_gap").Width(UnitValue.Stretch()).Height(1).IsNotInteractable();

            KinoEditorGUI.ToggleButton(paper, $"{id}_solo", "Solo", solo, () =>
            {
                // Solo is a preview state, not scene data: nothing is marked dirty by it.
                KinoCore.Solo = solo ? null : vcam;
            });

            KinoEditorGUI.Button(paper, $"{id}_top", "Make Live", () =>
            {
                Undo.Snapshot(vcam);
                KinoCore.Solo = null;
                RaiseAboveOthers(vcam);
                EditorSceneManager.MarkDirty();
            }, EditorTheme.Purple400, grow: false);
        }

        if (KinoCore.MainBrain.IsNotValid())
            KinoEditorGUI.Note(paper, $"{id}_nobrain",
                "No Kino Brain in the scene, so nothing is driving a camera from these shots. Add one to the GameObject that has your Camera.",
                KinoNoteKind.Problem);
        else if (solo)
            KinoEditorGUI.Note(paper, $"{id}_solonote",
                "Solo overrides priority everywhere, including in play mode. Turn it off when you are done looking.",
                KinoNoteKind.Warning);
    }

    /// <summary>Raises the priority just past every other camera, so this shot wins outright.</summary>
    private static void RaiseAboveOthers(KinoCamera vcam)
    {
        int highest = int.MinValue;
        foreach (KinoCamera other in KinoCore.Cameras)
            if (other.IsValid() && !ReferenceEquals(other, vcam))
                highest = Maths.Max(highest, other.Priority);

        if (highest > int.MinValue && vcam.Priority <= highest)
            vcam.Priority = highest + 1;

        vcam.MoveToTop();
    }

    private void DrawPreview(Paper paper, string id, KinoCamera vcam)
    {
        var m = Origami.Current.Metrics;

        using (paper.Row($"{id}_bar").Height(20)
            .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.Spacing).RowBetween(m.SpacingMedium).Enter())
        {
            KinoEditorGUI.ToggleButton(paper, $"{id}_toggle", _showPreview ? "Hide Shot" : "Show Shot",
                _showPreview, () => _showPreview = !_showPreview, EditorTheme.Blue400);

            paper.Box($"{id}_gap").Width(UnitValue.Stretch()).Height(1).IsNotInteractable();
        }

        if (!_showPreview)
            return;

        KinoComposition? composition = KinoEditorUtil.TryGetComposition(vcam, out KinoComposition found)
            ? found
            : null;

        using (paper.Box($"{id}_wrap").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
            .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.Spacing).Enter())
        {
            // The inspector is a column, so stretching gives the panel width; 16:9 comes out of it.
            if (!_preview.Draw(paper, $"{id}_rt", vcam, 320f, composition))
                KinoEditorGUI.Note(paper, $"{id}_nopreview", "The shot cannot be rendered from here.", KinoNoteKind.Info);
        }
    }

    #endregion

    #region Targets and pipeline

    private static void DrawTargetNotes(Paper paper, string id, KinoCamera vcam)
    {
        if (vcam.FollowGroup.IsValid())
            KinoEditorGUI.Note(paper, $"{id}_fgroup", "Follow is a target group - the camera follows the whole group's centre.");

        if (vcam.LookAtGroup.IsValid())
            KinoEditorGUI.Note(paper, $"{id}_lgroup", "Look At is a target group - the camera composes around the whole group.");

        List<KinoComponent> body = KinoEditorUtil.OfStage(vcam, KinoStage.Body);
        List<KinoComponent> aim = KinoEditorUtil.OfStage(vcam, KinoStage.Aim);

        if (!vcam.HasFollow && body.Count > 0)
            KinoEditorGUI.Note(paper, $"{id}_nofollow",
                "This camera has a body component but nothing to follow, so it stays where it is.", KinoNoteKind.Problem);

        if (!vcam.HasLookAt && aim.Count > 0 && !HasTargetlessAim(aim))
            KinoEditorGUI.Note(paper, $"{id}_nolookat",
                "This camera has an aim component but nothing to look at, so it keeps its current rotation.", KinoNoteKind.Problem);

        if (body.Count == 0 && aim.Count == 0)
        {
            KinoEditorGUI.Note(paper, $"{id}_static",
                "A locked-off shot: no body or aim component, so the camera goes exactly where this GameObject is.");
            return;
        }

        // The other way round, and the easier mistake to make: a target is assigned, but nothing is
        // attached that would ever read it.
        if (vcam.HasFollow && body.Count == 0)
            KinoEditorGUI.Note(paper, $"{id}_unusedfollow",
                "Follow is set but there is no body component, so nothing moves the camera with it. Add a Transposer.",
                KinoNoteKind.Warning);

        if (vcam.HasLookAt && aim.Count == 0)
            KinoEditorGUI.Note(paper, $"{id}_unusedlookat",
                "Look At is set but there is no aim component, so nothing points the camera at it. Add a Composer.",
                KinoNoteKind.Warning);
    }

    /// <summary>POV and Same As Follow Target aim without a Look At target, so their absence is not a problem.</summary>
    private static bool HasTargetlessAim(List<KinoComponent> aim)
    {
        foreach (KinoComponent component in aim)
            if (component is KinoPOV or KinoSameAsFollowTarget)
                return true;
        return false;
    }

    private static void DrawPipeline(Paper paper, string id, KinoCamera vcam)
    {
        KinoEditorGUI.Section(paper, $"{id}_h", "Pipeline", () =>
        {
            foreach ((KinoStage stage, string label, Type[] options) in s_stages)
                DrawStageRow(paper, $"{id}_{stage}", vcam, stage, label, options);
        });
    }

    private static void DrawStageRow(Paper paper, string id, KinoCamera vcam, KinoStage stage, string label, Type[] options)
    {
        List<KinoComponent> attached = KinoEditorUtil.OfStage(vcam, stage);
        KinoComponent? winner = KinoEditorUtil.StageWinner(vcam, stage);
        bool single = stage is KinoStage.Body or KinoStage.Aim;

        EditorGUI.Row(paper, id, label, () =>
        {
            var font = EditorTheme.DefaultFont;
            if (font == null) return;

            var m = Origami.Current.Metrics;
            using (paper.Row($"{id}_w").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .MinHeight(KinoEditorGUI.RowHeight).RowBetween(m.Spacing).Enter())
            {
                if (attached.Count == 0)
                {
                    paper.Box($"{id}_none").Width(UnitValue.Stretch()).Height(KinoEditorGUI.RowHeight)
                        .IsNotInteractable()
                        .Text("none", font).TextColor(EditorTheme.Ink200)
                        .FontSize(m.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
                }
                else
                {
                    for (int i = 0; i < attached.Count; i++)
                    {
                        KinoComponent component = attached[i];
                        bool running = ReferenceEquals(component, winner) || (!single && component.EnabledInHierarchy && component.IsUsable);
                        KinoEditorGUI.Badge(paper, $"{id}_c{i}", KinoEditorUtil.DisplayName(component.GetType()),
                            running ? EditorTheme.Green400 : EditorTheme.Ink300);
                    }
                }
            }
        });

        // Adding the usual component for an empty stage, without hunting through the Add Component menu.
        if (attached.Count == 0 && options.Length > 0)
        {
            var m = Origami.Current.Metrics;
            using (paper.Row($"{id}_add").Height(UnitValue.Auto)
                .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.Spacing).RowBetween(m.Spacing).Enter())
            {
                foreach (Type option in options)
                {
                    Type captured = option;
                    KinoEditorGUI.Button(paper, $"{id}_add_{option.Name}",
                        "+ " + KinoEditorUtil.DisplayName(option).Replace("Kino ", string.Empty),
                        () => AddComponent(vcam, captured), EditorTheme.Purple400, grow: false);
                }
            }
        }
    }

    private static void AddComponent(KinoCamera vcam, Type type)
    {
        if (vcam.IsNotValid() || vcam.GameObject.IsNotValid())
            return;

        Undo.Snapshot(vcam.GameObject);
        vcam.GameObject.AddComponent(type);
        vcam.InvalidateComponentCache();
        EditorSceneManager.MarkDirty();
    }

    #endregion
}
