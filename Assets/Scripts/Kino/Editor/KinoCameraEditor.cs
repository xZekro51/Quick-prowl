// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

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

    // What each stage can be filled with. Body and Aim are one-of - the pipeline only ever runs the
    // first - so they are a single choice. Noise and Finalize genuinely stack, so those are multiple.
    private static readonly Type[] s_bodyTypes =
    [
        typeof(KinoTransposer), typeof(KinoOrbitalTransposer), typeof(KinoFramingTransposer),
        typeof(KinoHardLockToTarget), typeof(KinoTrackedDolly)
    ];

    private static readonly Type[] s_aimTypes =
    [
        typeof(KinoComposer), typeof(KinoHardLookAt), typeof(KinoPOV), typeof(KinoSameAsFollowTarget)
    ];

    private static readonly Type[] s_noiseTypes = [typeof(KinoShake)];

    private static readonly Type[] s_finalizeTypes =
    [
        typeof(KinoCollider), typeof(KinoConfiner), typeof(KinoImpulseListener)
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
            SingleStage(paper, $"{id}_body", vcam, KinoStage.Body, "Body", s_bodyTypes);
            SingleStage(paper, $"{id}_aim", vcam, KinoStage.Aim, "Aim", s_aimTypes);
            MultiStage(paper, $"{id}_noise", vcam, KinoStage.Noise, "Noise", s_noiseTypes);
            MultiStage(paper, $"{id}_final", vcam, KinoStage.Finalize, "Finalize", s_finalizeTypes);
        });
    }

    /// <summary>
    /// A stage the pipeline only runs one of. Picking from the dropdown swaps the component out, so the
    /// inspector cannot end up describing a camera with two body components where only one runs.
    /// </summary>
    private static void SingleStage(Paper paper, string id, KinoCamera vcam, KinoStage stage, string label, Type[] options)
    {
        List<KinoComponent> attached = KinoEditorUtil.OfStage(vcam, stage);
        Type? current = attached.Count > 0 ? attached[0].GetType() : null;
        bool known = current == null || Array.IndexOf(options, current) >= 0;

        // Index 0 is "None", so the options line up one along. A component this list has never heard of
        // - one someone wrote themselves - gets an entry of its own rather than being shown as "None"
        // and quietly deleted by the next pick.
        var labels = new List<string>(options.Length + 2) { "None" };
        foreach (Type option in options)
            labels.Add(ShortName(option));
        if (!known)
            labels.Add(ShortName(current!));

        int custom = labels.Count - 1;
        int selected = current == null ? 0 : known ? Array.IndexOf(options, current) + 1 : custom;

        EditorGUI.Row(paper, id, label, () =>
            Origami.Dropdown(paper, $"{id}_dd", selected, choice =>
                    {
                        if (!known && choice == custom)
                            return; // already the one it is on

                        SetStage(vcam, stage, choice <= 0 ? null : options[choice - 1]);
                    }, labels)
                .Width(UnitValue.Stretch())
                .Show());

        if (attached.Count > 1)
            KinoEditorGUI.Note(paper, $"{id}_dupe",
                $"{attached.Count} {label} components are attached and only the first runs. Choosing from the dropdown removes the rest.",
                KinoNoteKind.Warning);
    }

    /// <summary>
    /// A stage the pipeline runs all of, so the dropdown is a multi-select: ticking adds the component,
    /// unticking removes it.
    /// </summary>
    private static void MultiStage(Paper paper, string id, KinoCamera vcam, KinoStage stage, string label, Type[] options)
    {
        List<KinoComponent> attached = KinoEditorUtil.OfStage(vcam, stage);

        var present = new List<Type>();
        foreach (KinoComponent component in attached)
        {
            Type type = component.GetType();
            if (Array.IndexOf(options, type) >= 0 && !present.Contains(type))
                present.Add(type);
        }

        EditorGUI.Row(paper, id, label, () =>
            Origami.MultiDropdown(paper, $"{id}_dd", present,
                    chosen => SyncStage(vcam, stage, options, chosen), options)
                .Display(ShortName)
                .Width(UnitValue.Stretch())
                .Show());
    }

    /// <summary>Leaves the camera with exactly one component of <paramref name="stage"/>, or none.</summary>
    private static void SetStage(KinoCamera vcam, KinoStage stage, Type? wanted)
    {
        if (vcam.IsNotValid() || vcam.GameObject.IsNotValid())
            return;

        Undo.Snapshot(vcam.GameObject);

        bool alreadyThere = false;
        foreach (KinoComponent component in KinoEditorUtil.OfStage(vcam, stage))
        {
            // The one being asked for is kept rather than replaced, so re-picking the current entry does
            // not quietly reset everything that was configured on it.
            if (wanted != null && component.GetType() == wanted && !alreadyThere)
                alreadyThere = true;
            else
                vcam.GameObject.RemoveComponent(component);
        }

        if (wanted != null && !alreadyThere)
            vcam.GameObject.AddComponent(wanted);

        vcam.InvalidateComponentCache();
        EditorSceneManager.MarkDirty();
    }

    /// <summary>Adds and removes until the camera carries exactly the chosen set for a stage.</summary>
    private static void SyncStage(KinoCamera vcam, KinoStage stage, Type[] options, IReadOnlyList<Type> chosen)
    {
        if (vcam.IsNotValid() || vcam.GameObject.IsNotValid())
            return;

        Undo.Snapshot(vcam.GameObject);

        foreach (KinoComponent component in KinoEditorUtil.OfStage(vcam, stage))
        {
            Type type = component.GetType();

            // Anything the dropdown does not know about is left alone: it is a component someone wrote
            // themselves, and this list is not the authority on it.
            if (Array.IndexOf(options, type) >= 0 && !chosen.Contains(type))
                vcam.GameObject.RemoveComponent(component);
        }

        foreach (Type type in chosen)
            if (vcam.GameObject.GetComponent(type).IsNotValid())
                vcam.GameObject.AddComponent(type);

        vcam.InvalidateComponentCache();
        EditorSceneManager.MarkDirty();
    }

    /// <summary>"Orbital Transposer" - the type name without the prefix every one of them shares.</summary>
    private static string ShortName(Type type)
        => KinoEditorUtil.DisplayName(type).Replace("Kino ", string.Empty);

    #endregion
}
