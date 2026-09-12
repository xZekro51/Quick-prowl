// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

using Prowl.Editor.Core;
using Prowl.Editor.GUI;
using Prowl.Editor.Projects;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using SColor = System.Drawing.Color;

namespace Prowl.Animate.Editor;

/// <summary>
/// A node-graph editor for <see cref="AnimatorController"/> assets. The canvas shows a layer's states
/// as nodes and its transitions as wires; the sidebars edit parameters, layers and whatever is
/// selected. Changes are written back to the <c>.animator</c> file on save (project save or the
/// toolbar button) and re-imported.
///
/// <para><b>Differences from the original hand-drawn canvas.</b> The graph is drawn by Origami's
/// <c>NodeGraph</c> widget rather than by a bespoke Quill canvas, which brings marquee selection,
/// consistent pan/zoom, proper wire routing and reusable context menus for free - and removes about
/// four hundred lines of hit-testing and arrow-fanning geometry that only this window understood.
/// Transitions are made by dragging a wire from a state's output port instead of a two-click
/// "make transition" mode.</para>
///
/// <para>It also shows the graph <i>running</i>: while a scene Animator is playing this controller,
/// the live state is highlighted, the active transition is drawn hot, and the parameter list shows
/// (and lets you drive) the real values.</para>
/// </summary>
public sealed class AnimatorGraphWindow : DockPanel
{
    private const string AnyStateNodeId = "__any";
    private const string EntryNodeId = "__entry";
    private const string InPort = "in";
    private const string OutPort = "out";

    // Auto-layout spacing, in graph units.
    private const float ColumnSpacing = 300f;
    private const float RowSpacing = 120f;
    private const float LayoutOriginX = 260f;
    private const float LayoutOriginY = 60f;

    /// <summary>
    /// The zoom below which the node widget stops drawing node bodies (and, further down, drops node
    /// text entirely and renders solid blocks). Framing a graph of any size lands well under it, which
    /// is exactly how you end up staring at unreadable cards - so framing is clamped to it.
    /// </summary>
    private const float MinReadableZoom = 0.56f;

    /// <summary>
    /// Roughly how big a state card draws (header plus its one body row). Only used to pad framing, so
    /// an approximation is fine - the widget owns the real measurement.
    /// </summary>
    private const float NodeWidthEstimate = 210f;
    private const float NodeHeightEstimate = 96f;

    private const float SidebarWidth = 260f;
    private const float InspectorWidth = 300f;
    private static float ToolbarHeight => EditorGUIMetrics.ToolbarHeight;

    /// <summary>
    /// Held as an <see cref="AssetRef{T}"/> rather than a raw field: saving reimports the asset,
    /// which disposes the instance and builds a new one, and the asset cache idle-sweeps anything
    /// nothing reports as in use. Re-reading <c>.Res</c> each frame handles both.
    /// </summary>
    private AssetRef<AnimatorController> _controllerRef;

    /// <summary>Resolved once at the top of each frame; null while an async load is streaming.</summary>
    private AnimatorController? _controller;

    private bool _dirty;
    private int _layerIndex;

    private readonly NodeGraphController _graph = new();
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphConnection> _edges = new();
    private readonly Dictionary<string, AnimatorState> _nodeStates = new(StringComparer.Ordinal);

    /// <summary>
    /// Node ids, handed out per <see cref="AnimatorState"/> <i>instance</i> and kept for as long as the
    /// controller instance lives.
    ///
    /// <para>The widget keys its selection and its in-flight drag by node id, so an id has to mean the
    /// same state from one frame to the next. Numbering by list index does not: deleting a state
    /// renumbers everything after it, so a selection silently jumps to a different state - and the id
    /// under an active drag changes out from under the widget. Renaming would break a name-derived id
    /// the same way, and the name field renames per keystroke.</para>
    /// </summary>
    private readonly Dictionary<AnimatorState, string> _nodeIds = new(ReferenceEqualityComparer.Instance);
    private int _nextNodeId;

    private readonly List<AnimatorIssue> _issues = new();
    private bool _issuesStale = true;
    private bool _layoutChecked;

    // Screen-space origin and size of the graph canvas, captured during layout. Needed to place
    // context menus, which the widget reports in graph space.
    private Float2 _graphOrigin;
    private float _graphWidth = 480f;
    private float _graphHeight = 320f;

    // Selection - one of these is active at a time.
    private AnimatorState? _selectedState;
    private AnimatorStateTransition? _selectedTransition;
    private AnimatorState? _selectedTransitionOwner;   // null + fromAny => an any-state transition
    private bool _selectedTransitionFromAny;
    private bool _anyStateSelected;
    private bool _entrySelected;

    // Live view.
    private Animator? _liveAnimator;
    private int _liveSearchFrame = -1;

    public override string Title => _controller.IsValid() ? $"Animator - {_controller!.Name}" : "Animator Controller";
    public override string Icon => ""; // person-running

    /// <summary>
    /// Whether this panel is currently attached to the project-save event.
    ///
    /// <para>The hook is deliberately held only while there is something to save. <c>SaveManager</c>
    /// is a static in the (non-collectible) editor assembly, and a delegate pointing back into a
    /// game-project script keeps that script's AssemblyLoadContext alive - so a permanent
    /// subscription would quietly defeat script hot-reload. Attaching on the first edit and
    /// detaching on save keeps the window in Ctrl+S while shrinking that root to the moments it is
    /// actually needed.</para>
    /// </summary>
    private bool _saveHookAttached;

    public override void OnClosed() => ReleaseSaveHook();

    private void EnsureSaveHook()
    {
        if (_saveHookAttached) return;
        SaveManager.OnSave += OnProjectSave;
        _saveHookAttached = true;
    }

    private void ReleaseSaveHook()
    {
        if (!_saveHookAttached) return;
        SaveManager.OnSave -= OnProjectSave;
        _saveHookAttached = false;
    }

    /// <summary>
    /// Open (or focus) the editor for a controller asset.
    ///
    /// <para>Deliberately reuses the window that is already open. Two editors on the same controller
    /// both attach to project-save; the first one to write reimports the asset, which disposes the
    /// instance the second one is holding - so it quietly discards everything it had. Retargeting the
    /// existing window is also just what a tool window opened from an asset should do. A window with
    /// unsaved work on a <i>different</i> controller is left alone and a second one is opened, because
    /// throwing away those edits to avoid a duplicate would be the worse trade.</para>
    /// </summary>
    public static void OpenFor(AnimatorController controller)
    {
        var app = EditorApplication.Instance;
        if (app == null) return;

        var reference = new AssetRef<AnimatorController>(controller);

        if (app.FindOpenPanel(typeof(AnimatorGraphWindow)) is AnimatorGraphWindow existing)
        {
            bool sameAsset = reference.AssetID != Guid.Empty && existing._controllerRef.AssetID == reference.AssetID;
            if (sameAsset || !existing._dirty)
            {
                if (!sameAsset) existing.SetControllerRef(reference);
                app.OpenPanel(typeof(AnimatorGraphWindow)); // focuses the one that is already open
                return;
            }
        }

        var panel = new AnimatorGraphWindow();
        panel.SetControllerRef(reference);
        app.OpenPanelInstance(panel, 1180, 720);
    }

    private void SetControllerRef(AssetRef<AnimatorController> reference)
    {
        _controllerRef = reference;
        _controller = _controllerRef.Res;
        _layerIndex = 0;
        _layoutChecked = false;
        _issuesStale = true;
        _nodeIds.Clear();
        ClearSelection();
    }

    /// <summary>
    /// Re-resolve the controller for this frame. A reimport replaces the instance, so everything
    /// pointing into the old object graph - the selection, the node ids - has to be dropped.
    ///
    /// <para>Every save reimports, so this runs constantly during normal editing. Which layer you were
    /// on and which state you had selected are therefore re-matched <i>by name</i> against the new
    /// instance rather than reset: having the editor jump back to layer 0 with nothing selected on
    /// every Ctrl+S is the kind of thing that makes a window feel broken.</para>
    /// </summary>
    private void ResolveController()
    {
        AnimatorController? resolved = _controllerRef.Res;
        if (ReferenceEquals(resolved, _controller)) return;

        string? layerName = CurrentLayer?.Name;
        string? stateName = _selectedState?.Name;

        _controller = resolved;
        _layerIndex = 0;
        _issuesStale = true;
        _nodeIds.Clear();
        ClearSelection();

        if (_controller.IsNotValid()) return;

        if (layerName != null)
        {
            int index = _controller!.Layers.FindIndex(l => l.Name == layerName);
            if (index >= 0) _layerIndex = index;
        }

        if (stateName != null && CurrentLayer?.FindState(stateName) is { } state)
            SelectState(state);
    }

    /// <summary>Open the editor from the Window menu - focusing the existing one if there is one.</summary>
    public static void OpenEmpty() => EditorApplication.Instance?.OpenPanel(typeof(AnimatorGraphWindow));

    [Prowl.Editor.AssetDoubleClickHandler(".animator")]
    private static bool OpenHandler(string relativePath, Guid guid)
    {
        var reference = new AssetRef<AnimatorController>(guid);
        if (reference.Res is AnimatorController controller)
        {
            OpenFor(controller);
            return true;
        }
        return false;
    }

    private string? OnProjectSave()
    {
        if (_controller.IsNotValid() || !_dirty) return null;

        // Read the name first: saving reimports, which disposes this instance.
        string name = _controller!.Name;
        Save();
        return $"Saved Animator {name}";
    }

    private AnimatorLayer? CurrentLayer
        => _controller.IsValid() && _layerIndex >= 0 && _layerIndex < _controller!.Layers.Count
            ? _controller.Layers[_layerIndex]
            : null;

    // ============================================================
    //  Frame
    // ============================================================

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        ResolveController();

        if (_controller.IsNotValid())
        {
            EditorGUI.EmptyState(paper, "anim_empty",
                _controllerRef.AssetID != Guid.Empty
                    ? "Loading controller..."
                    : "Open an .animator asset from the Project panel to edit it.",
                font);
            return;
        }

        // Known before anything asks to frame or centre the view: EnsureInitialLayout resets the view
        // on the first frame, and doing that against a stale default canvas size opens the graph
        // off-centre.
        _graphWidth = MathF.Max(160f, width - SidebarWidth - InspectorWidth);
        _graphHeight = MathF.Max(160f, height - ToolbarHeight);

        EnsureInitialLayout();
        RefreshLiveAnimator();

        using (paper.Column("anim_root").Width(UnitValue.Stretch()).Height(UnitValue.Stretch()).Enter())
        {
            DrawToolbar(paper, font);

            using (paper.Row("anim_body").Width(UnitValue.Stretch()).Height(UnitValue.Stretch()).Enter())
            {
                DrawLeftSidebar(paper, font, height);
                DrawGraph(paper, _graphWidth, _graphHeight);
                DrawInspector(paper, font, height);
            }
        }
    }

    // ============================================================
    //  Toolbar
    // ============================================================

    private void DrawToolbar(Paper paper, Prowl.Scribe.FontFile font)
    {
        var controller = _controller!;

        // One height for everything in the strip. Buttons have to be told it explicitly - they are the
        // only Origami control that does not default to Metrics.RowHeight (see EditorGUIMetrics).
        float h = EditorGUIMetrics.ToolbarControlHeight;

        using (paper.Row("anim_tb").Width(UnitValue.Stretch()).Height(ToolbarHeight)
            .BackgroundColor(EditorTheme.Neutral200)
            .Padding(10, 10, EditorGUIMetrics.ToolbarPadY, EditorGUIMetrics.ToolbarPadY)
            .Gap(8).Enter())
        {
            string[] layerNames = controller.Layers.Count > 0
                ? controller.Layers.Select(l => l.Name).ToArray()
                : new[] { "(no layers)" };

            paper.Box("anim_tb_llbl").Width(38).Height(h).IsNotInteractable()
                .Text("Layer", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            using (paper.Box("anim_tb_layer").Width(150).Height(h).Enter())
                Origami.Dropdown(paper, "anim_tb_layer_v", Math.Clamp(_layerIndex, 0, layerNames.Length - 1),
                    v => { _layerIndex = v; ClearSelection(); }, layerNames).Show();

            Origami.Button(paper, "anim_tb_addlayer", "+ Layer", () =>
            {
                controller.AddLayer(UniqueName("New Layer", n => controller.Layers.Any(x => x.Name == n)));
                _layerIndex = controller.Layers.Count - 1;
                MarkDirty();
            }).Subtle().Width(78).Height(h).Show();

            // Adding a state was right-click-only, which is not discoverable and is impossible to find
            // if you have never used a node graph before. The button drops one in the middle of the view.
            Origami.Button(paper, "anim_tb_addstate", "+ State", () => AddStateAt(ViewCentre()))
                .Width(80).Height(h).Show();

            paper.Box("anim_tb_spacer").Width(UnitValue.Stretch()).Height(h).IsNotInteractable();

            DrawIssueChip(paper, font);

            if (_liveAnimator.IsValid())
                paper.Box("anim_tb_live").Width(60).Height(h).IsNotInteractable()
                    .Text("● live", font).TextColor(EditorTheme.Green400)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight);

            paper.Box("anim_tb_status").Width(76).Height(h).IsNotInteractable()
                .Text(_dirty ? "● unsaved" : "✓ saved", font)
                .TextColor(_dirty ? EditorTheme.Amber400 : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight);

            Origami.Button(paper, "anim_tb_save", "Save", Save).Width(64).Height(h).Show();
            Origami.Button(paper, "anim_tb_layout", "Auto Layout", AutoLayout).Subtle().Width(92).Height(h).Show();
            Origami.Button(paper, "anim_tb_frame", "Frame", FrameGraph).Subtle().Width(60).Height(h).Show();
        }
    }

    private void DrawIssueChip(Paper paper, Prowl.Scribe.FontFile font)
    {
        EnsureIssues();
        if (_issues.Count == 0) return;

        int errors = 0;
        for (int i = 0; i < _issues.Count; i++)
            if (_issues[i].Severity == AnimatorIssueSeverity.Error) errors++;

        SColor color = errors > 0 ? EditorTheme.Red400 : EditorTheme.Amber400;
        string label = errors > 0 ? $"{errors} error{(errors == 1 ? "" : "s")}" : $"{_issues.Count} warning{(_issues.Count == 1 ? "" : "s")}";

        float chipH = MathF.Max(16f, EditorGUIMetrics.ToolbarControlHeight - 4f);
        paper.Box("anim_tb_issues").Width(UnitValue.Auto).Height(chipH)
            .Padding(8, 8, 0, 0).Rounded(chipH * 0.5f).IsNotInteractable()
            .BackgroundColor(SColor.FromArgb(40, color))
            .BorderColor(SColor.FromArgb(120, color)).BorderWidth(1)
            .Text(label, font).TextColor(color)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);
    }

    // ============================================================
    //  Left sidebar - parameters, layer settings, problems
    // ============================================================

    private void DrawLeftSidebar(Paper paper, Prowl.Scribe.FontFile font, float height)
    {
        using (paper.Column("anim_side").Width(SidebarWidth).Height(UnitValue.Stretch())
            .BackgroundColor(EditorTheme.Neutral200)
            .BorderColor(EditorTheme.Neutral300).BorderWidth(1)
            .Padding(8, 8, 8, 8).Enter())
        {
            Origami.ScrollView(paper, "anim_side_scroll", SidebarWidth - 20, MathF.Max(120f, height - 60f)).Body(() =>
            {
                Origami.Foldout(paper, "anim_fold_params", "Parameters").DefaultExpanded(true)
                    .Body(() => DrawParameters(paper, font));

                paper.Box("anim_side_gap1").Height(8).IsNotInteractable();

                Origami.Foldout(paper, "anim_fold_layer", "Layer Settings").DefaultExpanded(true)
                    .Body(() => DrawLayerSettings(paper, font));

                paper.Box("anim_side_gap2").Height(8).IsNotInteractable();

                Origami.Foldout(paper, "anim_fold_issues", "Problems").DefaultExpanded(false)
                    .Body(() => DrawIssues(paper, font));
            });
        }
    }

    private void DrawParameters(Paper paper, Prowl.Scribe.FontFile font)
    {
        var controller = _controller!;
        bool live = _liveAnimator.IsValid();

        for (int i = 0; i < controller.Parameters.Count; i++)
        {
            int index = i;
            var p = controller.Parameters[i];

            using (paper.Column($"anim_p{i}").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .BackgroundColor(EditorTheme.Neutral300).Rounded(4)
                .Padding(8, 8, 6, 6).Margin(0, 0, 0, 6).Gap(4).Enter())
            {
                using (paper.Row($"anim_p{i}_r").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Gap(6).Enter())
                {
                    using (paper.Box($"anim_p{i}_name").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Enter())
                        Origami.TextField(paper, $"anim_p{i}_name_v", p.Name, v =>
                        {
                            // Renaming through the controller repoints every condition, blend axis
                            // and speed parameter that referenced the old name.
                            if (!string.IsNullOrWhiteSpace(v) && v != p.Name && controller.FindParameter(v) == null)
                            {
                                controller.RenameParameter(p.Name, v);
                                MarkDirty();
                            }
                        }).Show();

                    DeleteButton(paper, $"anim_p{i}_x", font, () =>
                    {
                        controller.RemoveParameter(controller.Parameters[index]);
                        MarkDirty();
                    });
                }

                EditorGUI.Row(paper, $"anim_p{i}_type", "Type", () =>
                    Origami.EnumDropdown(paper, $"anim_p{i}_type_v", p.Type, v => { p.Type = v; MarkDirty(); }).Show());

                if (live)
                    DrawLiveParameterValue(paper, font, $"anim_p{i}_live", p);
                else
                    DrawParameterDefault(paper, $"anim_p{i}", p);
            }
        }

        paper.Box("anim_p_add_lbl").Height(16).Margin(0, 4, 0, 2).IsNotInteractable()
            .Text("Add Parameter", font).TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

        float rowH = EditorGUIMetrics.RowHeight;
        using (paper.Row("anim_p_add").Width(UnitValue.Stretch()).Height(rowH).Gap(4).Enter())
        {
            Origami.Button(paper, "anim_p_addf", "Float", () => AddParam(AnimatorControllerParameterType.Float)).Width(UnitValue.Stretch()).Height(rowH).Show();
            Origami.Button(paper, "anim_p_addi", "Int", () => AddParam(AnimatorControllerParameterType.Int)).Width(UnitValue.Stretch()).Height(rowH).Show();
            Origami.Button(paper, "anim_p_addb", "Bool", () => AddParam(AnimatorControllerParameterType.Bool)).Width(UnitValue.Stretch()).Height(rowH).Show();
            Origami.Button(paper, "anim_p_addt", "Trig", () => AddParam(AnimatorControllerParameterType.Trigger)).Width(UnitValue.Stretch()).Height(rowH).Show();
        }
    }

    private void DrawParameterDefault(Paper paper, string id, AnimatorControllerParameter p)
    {
        switch (p.Type)
        {
            case AnimatorControllerParameterType.Float:
                EditorGUI.Row(paper, $"{id}_df", "Default", () =>
                    Origami.NumericField(paper, $"{id}_df_v", p.DefaultFloat,
                        v => { p.DefaultFloat = v; MarkDirty(); }).Show());
                break;

            case AnimatorControllerParameterType.Int:
                EditorGUI.Row(paper, $"{id}_di", "Default", () =>
                    Origami.NumericField(paper, $"{id}_di_v", p.DefaultInt,
                        v => { p.DefaultInt = v; MarkDirty(); }).Show());
                break;

            case AnimatorControllerParameterType.Bool:
                EditorGUI.Row(paper, $"{id}_db", "Default", () =>
                    Origami.Checkbox(paper, $"{id}_db_v", p.DefaultBool,
                        v => { p.DefaultBool = v; MarkDirty(); }).Show());
                break;
        }
    }

    /// <summary>
    /// While an Animator is playing this controller the parameter rows drive the live values instead
    /// of the authored defaults - which is how you actually debug a state machine that will not leave
    /// a state.
    /// </summary>
    private void DrawLiveParameterValue(Paper paper, Prowl.Scribe.FontFile font, string id, AnimatorControllerParameter p)
    {
        var animator = _liveAnimator!;
        switch (p.Type)
        {
            case AnimatorControllerParameterType.Float:
                EditorGUI.Row(paper, $"{id}_f", "Value", () =>
                    Origami.NumericField(paper, $"{id}_f_v", animator.GetFloat(p.Name),
                        v => animator.SetFloat(p.Name, v)).Show());
                break;

            case AnimatorControllerParameterType.Int:
                EditorGUI.Row(paper, $"{id}_i", "Value", () =>
                    Origami.NumericField(paper, $"{id}_i_v", animator.GetInt(p.Name),
                        v => animator.SetInt(p.Name, v)).Show());
                break;

            case AnimatorControllerParameterType.Bool:
                EditorGUI.Row(paper, $"{id}_b", "Value", () =>
                    Origami.Checkbox(paper, $"{id}_b_v", animator.GetBool(p.Name),
                        v => animator.SetBool(p.Name, v)).Show());
                break;

            case AnimatorControllerParameterType.Trigger:
                EditorGUI.Row(paper, $"{id}_t", "Fire", () =>
                    Origami.Button(paper, $"{id}_t_v", animator.GetBool(p.Name) ? "set" : "trigger",
                        () => animator.SetTrigger(p.Name))
                        .Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show());
                break;
        }
    }

    private void AddParam(AnimatorControllerParameterType type)
    {
        var controller = _controller!;
        controller.AddParameter(UniqueName("New " + type, n => controller.Parameters.Any(p => p.Name == n)), type);
        MarkDirty();
    }

    private void DrawLayerSettings(Paper paper, Prowl.Scribe.FontFile font)
    {
        var layer = CurrentLayer;
        if (layer == null)
        {
            Origami.Label(paper, "anim_ls_none", "No layer selected.").Show();
            return;
        }

        EditorGUI.Row(paper, "anim_ls_name", "Name", () =>
            Origami.TextField(paper, "anim_ls_name_v", layer.Name, v =>
            {
                if (!string.IsNullOrWhiteSpace(v)) { layer.Name = v; MarkDirty(); }
            }).Show());

        if (_layerIndex > 0)
        {
            EditorGUI.Row(paper, "anim_ls_blend", "Blend", () =>
                Origami.EnumDropdown(paper, "anim_ls_blend_v", layer.BlendMode,
                    v => { layer.BlendMode = v; MarkDirty(); }).Show());

            EditorGUI.Row(paper, "anim_ls_weight", "Weight", () =>
                Origami.Slider(paper, "anim_ls_weight_v", layer.DefaultWeight,
                    v => { layer.DefaultWeight = v; MarkDirty(); }, 0f, 1f).Show());
        }
        else
        {
            Origami.Label(paper, "anim_ls_base", "Base layer (always full weight).").Show();
        }

        DrawMaskEditor(paper, font, layer);

        if (_layerIndex > 0)
            Origami.Button(paper, "anim_ls_del", "Delete Layer", () =>
            {
                _controller!.Layers.RemoveAt(_layerIndex);
                _controller.MarkStructureChanged();
                _layerIndex = Math.Max(0, _layerIndex - 1);
                ClearSelection();
                MarkDirty();
            }).Danger().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();
    }

    private void DrawMaskEditor(Paper paper, Prowl.Scribe.FontFile font, AnimatorLayer layer)
    {
        bool hasMask = layer.Mask != null;
        EditorGUI.Row(paper, "anim_ls_hasmask", "Avatar Mask", () =>
            Origami.Checkbox(paper, "anim_ls_hasmask_v", hasMask, v =>
            {
                layer.Mask = v ? new AvatarMask() : null;
                MarkDirty();
            }).Show());

        if (layer.Mask == null) return;
        var mask = layer.Mask;

        EditorGUI.Row(paper, "anim_ls_maskkids", "Include Children", () =>
            Origami.Checkbox(paper, "anim_ls_maskkids_v", mask.IncludeChildren,
                v => { mask.IncludeChildren = v; MarkDirty(); }).Show());

        for (int i = 0; i < mask.IncludedPaths.Count; i++)
        {
            int index = i;
            using (paper.Row($"anim_mask{i}").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Gap(4).Enter())
            {
                using (paper.Box($"anim_mask{i}_p").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Enter())
                    Origami.TextField(paper, $"anim_mask{i}_pv", mask.IncludedPaths[index],
                        v => { mask.IncludedPaths[index] = v; MarkDirty(); }).Show();

                DeleteButton(paper, $"anim_mask{i}_x", font, () =>
                {
                    mask.IncludedPaths.RemoveAt(index);
                    MarkDirty();
                });
            }
        }

        Origami.Button(paper, "anim_mask_add", "+ Bone Path", () =>
        {
            mask.IncludedPaths.Add(string.Empty);
            MarkDirty();
        }).Subtle().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();
    }

    private void DrawIssues(Paper paper, Prowl.Scribe.FontFile font)
    {
        EnsureIssues();
        if (_issues.Count == 0)
        {
            Origami.Label(paper, "anim_iss_none", "No problems found.").Show();
            return;
        }

        for (int i = 0; i < _issues.Count; i++)
        {
            var issue = _issues[i];
            SColor color = issue.Severity == AnimatorIssueSeverity.Error ? EditorTheme.Red400 : EditorTheme.Amber400;

            // Clicking a problem takes you to what it is about. A list of complaints you then have to
            // go and find by hand is the reason validation panels get ignored.
            paper.Box($"anim_iss{i}").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .MinHeight(EditorGUIMetrics.RowHeight)
                .Padding(8, 8, 4, 4).Margin(0, 0, 0, 4).Rounded(4)
                .BackgroundColor(SColor.FromArgb(26, color))
                .Hovered.BackgroundColor(SColor.FromArgb(52, color)).End()
                .BorderColor(SColor.FromArgb(90, color)).BorderWidth(1)
                .Text(issue.Message, font).TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSizeSmall)
                .Wrap(Prowl.Scribe.TextWrapMode.Wrap).Alignment(TextAlignment.MiddleLeft)
                .OnClick(_ => RevealIssue(issue));
        }
    }

    /// <summary>Switch to the layer a problem belongs to, then select and frame the state it names.</summary>
    private void RevealIssue(AnimatorIssue issue)
    {
        if (_controller.IsNotValid()) return;

        if (issue.LayerIndex >= 0 && issue.LayerIndex < _controller!.Layers.Count && issue.LayerIndex != _layerIndex)
        {
            _layerIndex = issue.LayerIndex;
            ClearSelection();
        }

        if (issue.StateName == null) return;

        var state = CurrentLayer?.FindState(issue.StateName);
        if (state == null) return;

        SelectState(state);
        string id = NodeId(state);
        _graph.SelectNodes(new[] { id });
        _graph.FocusNode(id);
    }

    // ============================================================
    //  Graph canvas
    // ============================================================

    private void DrawGraph(Paper paper, float width, float height)
    {
        _graphWidth = width;
        _graphHeight = height;

        RebuildGraphModel();

        // The widget reports context-menu positions in graph space, so the canvas' screen origin has
        // to be known to turn one back into a place to pop a menu. Wrapping it is the only way to
        // read that rect - the controller exposes pan and zoom but not where the canvas landed.
        using (paper.Box("anim_graph_host").Width(width).Height(height)
            .OnPostLayout((_, rect) => _graphOrigin = new Float2((float)rect.Min.X, (float)rect.Min.Y))
            .Enter())
        {
            Origami.NodeGraph(paper, "anim_graph", width, height)
                .Controller(_graph)
                .Nodes(_nodes)
                .Connections(_edges)
                .Grid(true)
                .OnSelectionChanged(HandleSelectionChanged)
                .OnNodesMoved(HandleNodesMoved)
                .OnConnect(HandleConnect)
                .OnValidateConnection(ValidateConnection)
                .OnDeleteSelection(HandleDeleteSelection)
                .OnBackgroundContext(HandleBackgroundContext)
                .OnNodeContext(HandleNodeContext)
                .OnNodeDoubleClick(HandleNodeDoubleClick)
                .Show();
        }
    }

    /// <summary>
    /// Frame the whole layer, but never so far out that the nodes stop being readable.
    ///
    /// <para>Computed here rather than by asking the widget to frame and then clamping its zoom on the
    /// next frame: clamping after the fact changes the zoom without re-centring, so a large graph
    /// framed itself and then slid off to one side.</para>
    /// </summary>
    private void FrameGraph()
    {
        var layer = CurrentLayer;
        if (layer == null) { ResetView(); return; }

        // Measured from the model, not from the node list: the toolbar draws before the canvas does,
        // so Auto Layout would otherwise frame the positions from the previous frame.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

        void Add(Float2 position)
        {
            minX = MathF.Min(minX, position.X);
            minY = MathF.Min(minY, position.Y);
            maxX = MathF.Max(maxX, position.X + NodeWidthEstimate);
            maxY = MathF.Max(maxY, position.Y + NodeHeightEstimate);
        }

        Add(layer.EntryEditorPosition);
        Add(layer.AnyStateEditorPosition);
        foreach (var state in layer.States) Add(state.EditorPosition);

        const float pad = 60f;
        float bw = MathF.Max(1f, maxX - minX) + pad * 2f;
        float bh = MathF.Max(1f, maxY - minY) + pad * 2f;

        float zoom = Math.Clamp(MathF.Min(_graphWidth / bw, _graphHeight / bh), MinReadableZoom, 1f);
        SetView(new Float2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f), zoom);
    }

    /// <summary>Open at 1:1 on the start of the flow, which is always legible.</summary>
    private void ResetView() => SetView(new Float2(LayoutOriginX + 60f, LayoutOriginY + 60f), 1f);

    /// <summary>Centre <paramref name="graphPoint"/> in the viewport at the given zoom, in one step.</summary>
    private void SetView(Float2 graphPoint, float zoom)
        => _graph.SetView(new Float2(_graphWidth * 0.5f - graphPoint.X * zoom,
                                     _graphHeight * 0.5f - graphPoint.Y * zoom), zoom);

    /// <summary>Graph-space point at the centre of the viewport - where "add this here" should land.</summary>
    private Float2 ViewCentre()
        => (new Float2(_graphWidth * 0.5f, _graphHeight * 0.5f) - _graph.Pan) / MathF.Max(_graph.Zoom, 1e-4f);

    /// <summary>Graph space to screen space, for placing context menus over the canvas.</summary>
    private Float2 GraphToScreen(Float2 graphPoint) => _graphOrigin + graphPoint * _graph.Zoom + _graph.Pan;

    /// <summary>
    /// Rebuild the node/edge lists from the authored layer. Cheap at editor scale, and it means the
    /// canvas can never drift from the model - every frame is drawn from the graph as it actually is.
    /// </summary>
    private void RebuildGraphModel()
    {
        _nodes.Clear();
        _edges.Clear();
        _nodeStates.Clear();

        var layer = CurrentLayer;
        if (layer == null) return;

        string? liveState = _liveAnimator.IsValid() ? _liveAnimator!.GetCurrentStateName(_layerIndex) : null;
        string? liveNext = _liveAnimator.IsValid() ? _liveAnimator!.GetNextStateName(_layerIndex) : null;

        // Entry: where the layer starts. Unity has this node and it is the single clearest cue for
        // "the flow begins here"; it also makes the default state re-targetable by dragging a wire.
        _nodes.Add(new GraphNode
        {
            Id = EntryNodeId,
            Title = "Entry",
            Position = layer.EntryEditorPosition,
            Width = 116f,
            Accent = EditorTheme.Green400,
            Outputs = new List<GraphPort> { new(OutPort, "start") { Side = PortSide.Right, Shape = PortShape.Arrow } },
        });

        // Any State gets a real card, not a pill: the pill renderer draws no text at all, so it was
        // an unlabelled purple blob with wires coming out of it.
        _nodes.Add(new GraphNode
        {
            Id = AnyStateNodeId,
            Title = "Any State",
            Position = layer.AnyStateEditorPosition,
            Width = 152f,
            Accent = EditorTheme.Purple400,
            Outputs = new List<GraphPort> { new(OutPort, "interrupts") { Side = PortSide.Right, Shape = PortShape.Arrow } },
        });

        for (int i = 0; i < layer.States.Count; i++)
        {
            var state = layer.States[i];
            string id = NodeId(state);
            _nodeStates[id] = state;

            bool isLive = liveState != null && state.Name == liveState;
            bool isBlendTarget = liveNext != null && state.Name == liveNext;
            bool isDefault = state.Name == layer.DefaultState;

            // One accent, one meaning, and the same fact repeated as a word on the right so the node
            // still reads correctly without relying on colour.
            SColor accent =
                isLive ? EditorTheme.Green400 :
                isBlendTarget ? EditorTheme.Blue400 :
                isDefault ? EditorTheme.Amber400 :
                EditorTheme.Neutral400;

            string status =
                isLive ? "playing" :
                isBlendTarget ? "blending in" :
                isDefault ? "entry" :
                state.Loop ? "loop" : "once";

            _nodes.Add(new GraphNode
            {
                Id = id,
                Title = state.Name,
                Position = state.EditorPosition,
                Width = 210f,
                Accent = accent,
                UserData = state,

                // The body row draws the input label on the left and the output label on the right.
                // Putting the motion on the left turns that row into a readable subtitle line
                // ("Walk_Forward            playing") instead of one truncated right-aligned scrap.
                Inputs = new List<GraphPort> { new(InPort, MotionSubtitle(state.Motion)) { Side = PortSide.Left, Shape = PortShape.Arrow } },
                Outputs = new List<GraphPort> { new(OutPort, status) { Side = PortSide.Right, Shape = PortShape.Arrow } },
            });
        }

        if (layer.FindState(layer.DefaultState) is { } defaultState)
            _edges.Add(new GraphConnection(EntryNodeId, OutPort, NodeId(defaultState), InPort)
            {
                Color = EditorTheme.Green400,
                // No EdgeRef: this wire is the default-state pointer, not an authored transition.
            });

        foreach (var t in layer.AnyStateTransitions)
            AddEdge(layer, AnyStateNodeId, null, t, liveState, liveNext);

        for (int i = 0; i < layer.States.Count; i++)
        {
            var owner = layer.States[i];
            foreach (var t in owner.Transitions)
                AddEdge(layer, NodeId(owner), owner, t, liveState, liveNext);
        }
    }

    private void AddEdge(AnimatorLayer layer, string fromId, AnimatorState? owner, AnimatorStateTransition t,
        string? liveState, string? liveNext)
    {
        var destination = layer.FindState(t.DestinationState);
        if (destination == null) return; // dangling; the Problems list explains it

        // The transition currently being crossed reads hot so you can see the blend happening.
        bool isLive = liveNext != null && t.DestinationState == liveNext
            && (owner == null || owner.Name == liveState);

        SColor color = t.Muted ? EditorTheme.Neutral500
            : isLive ? EditorTheme.Green400
            : _selectedTransition == t ? EditorTheme.Amber400
            : EditorTheme.Ink300;

        _edges.Add(new GraphConnection(fromId, OutPort, NodeId(destination), InPort)
        {
            Color = color,
            UserData = new EdgeRef(owner, t, owner == null),
        });
    }

    /// <summary>The stable id for a state instance, minted on first sight. See <see cref="_nodeIds"/>.</summary>
    private string NodeId(AnimatorState state)
    {
        if (_nodeIds.TryGetValue(state, out string? id)) return id;

        id = "s" + (++_nextNodeId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _nodeIds[state] = id;
        return id;
    }

    /// <summary>
    /// "→ Run   (Speed &gt; 0.1, exit 0.9)". Wires cannot carry labels, so the transition list is the
    /// only place the graph can tell you <i>why</i> a transition fires without a click per arrow.
    /// </summary>
    private static string DescribeTransition(AnimatorStateTransition t)
    {
        string destination = string.IsNullOrEmpty(t.DestinationState) ? "(none)" : t.DestinationState;

        var parts = new List<string>();
        foreach (var c in t.Conditions)
            parts.Add(DescribeCondition(c));
        if (t.HasExitTime)
            parts.Add($"exit {t.ExitTime:0.##}");

        string suffix = parts.Count > 0 ? $"   ({string.Join(", ", parts)})" : "   (immediate)";
        return $"→  {destination}{suffix}{(t.Muted ? "  · muted" : "")}";
    }

    private static string DescribeCondition(AnimatorCondition c)
    {
        string name = string.IsNullOrEmpty(c.Parameter) ? "?" : c.Parameter;
        return c.Mode switch
        {
            AnimatorConditionMode.If => name,
            AnimatorConditionMode.IfNot => "!" + name,
            AnimatorConditionMode.Greater => $"{name} > {c.Threshold:0.##}",
            AnimatorConditionMode.Less => $"{name} < {c.Threshold:0.##}",
            AnimatorConditionMode.Equals => $"{name} == {c.Threshold:0.##}",
            AnimatorConditionMode.NotEqual => $"{name} != {c.Threshold:0.##}",
            _ => name,
        };
    }

    private static string MotionSubtitle(Motion? motion) => motion switch
    {
        // A clip that has not streamed in yet has no name, but it is assigned - saying "(no clip)"
        // there reads as a broken state when nothing is wrong.
        ClipMotion cm => !string.IsNullOrEmpty(cm.Clip.Name) ? cm.Clip.Name
            : cm.Clip.AssetID != Guid.Empty ? "(loading…)"
            : "(no clip)",
        BlendTree bt => $"Blend Tree ({bt.Children.Count})",
        _ => "(no motion)",
    };

    /// <summary>Identifies which authored transition a wire came from.</summary>
    private sealed class EdgeRef
    {
        public readonly AnimatorState? Owner;
        public readonly AnimatorStateTransition Transition;
        public readonly bool FromAny;

        public EdgeRef(AnimatorState? owner, AnimatorStateTransition transition, bool fromAny)
        {
            Owner = owner;
            Transition = transition;
            FromAny = fromAny;
        }
    }

    // ── Graph callbacks ─────────────────────────────────────────────────────

    private void HandleSelectionChanged(GraphSelection selection)
    {
        if (selection.Edges != null && selection.Edges.Count > 0
            && selection.Edges[0].UserData is EdgeRef edge)
        {
            SelectTransition(edge.Owner, edge.Transition, edge.FromAny);
            return;
        }

        if (selection.Nodes != null && selection.Nodes.Count > 0)
        {
            var node = selection.Nodes[0];
            if (node.Id == AnyStateNodeId) { SelectAnyState(); return; }
            if (node.Id == EntryNodeId) { ClearSelection(); _entrySelected = true; return; }
            if (node.UserData is AnimatorState state) { SelectState(state); return; }
        }

        ClearSelection();
    }

    /// <summary>
    /// Commit a finished drag. The widget is a pure view: it never writes to the model, and reports the
    /// gesture as a <paramref name="delta"/> to <i>add</i> to each node's position - the nodes it hands
    /// back still carry their pre-drag position, because that is what this window put in them.
    ///
    /// <para>Assigning <c>node.Position</c> straight back was therefore a no-op, and since the node
    /// list is rebuilt from the model every frame, every drag snapped back the instant you let go.</para>
    /// </summary>
    private void HandleNodesMoved(IReadOnlyList<GraphNode> moved, Float2 delta)
    {
        var layer = CurrentLayer;
        if (layer == null) return;

        foreach (var node in moved)
        {
            if (node.Id == AnyStateNodeId) layer.AnyStateEditorPosition = node.Position + delta;
            else if (node.Id == EntryNodeId) layer.EntryEditorPosition = node.Position + delta;
            else if (node.UserData is AnimatorState state) state.EditorPosition = node.Position + delta;
        }

        // Position only: no structural change, so no recompile and no revalidation - but it is still an
        // edit, and without the save hook Ctrl+S would quietly not write it (so the layout came back
        // the way it was the next time the asset was opened).
        _dirty = true;
        EnsureSaveHook();
    }

    private bool ValidateConnection(ConnectionRequest request)
    {
        var layer = CurrentLayer;
        if (layer == null) return false;

        // Nothing transitions *into* Entry or Any State; both are sources only.
        if (request.ToNode is AnyStateNodeId or EntryNodeId) return false;
        return ResolveState(request.ToNode) != null;
    }

    private void HandleConnect(ConnectionRequest request)
    {
        var layer = CurrentLayer;
        if (layer == null) return;

        var destination = ResolveState(request.ToNode);
        if (destination == null) return;

        // Dragging the Entry wire onto a state is how you change which state the layer starts in.
        if (request.FromNode == EntryNodeId)
        {
            layer.DefaultState = destination.Name;
            SelectState(destination);
            MarkDirty();
            return;
        }

        if (request.FromNode == AnyStateNodeId)
        {
            var t = layer.AddAnyStateTransition(destination.Name);
            SelectTransition(null, t, true);
        }
        else
        {
            var source = ResolveState(request.FromNode);
            if (source == null) return;

            var t = source.AddTransition(destination.Name);
            if (source == destination) t.CanTransitionToSelf = true;
            SelectTransition(source, t, false);
        }
        MarkDirty();
    }

    private void HandleDeleteSelection(GraphSelection selection)
    {
        var layer = CurrentLayer;
        if (layer == null) return;

        // Edges without an EdgeRef are the Entry pointer, which is not deletable - a layer always
        // starts somewhere.
        if (selection.Edges != null)
            foreach (var edge in selection.Edges)
                if (edge.UserData is EdgeRef reference)
                {
                    if (reference.FromAny) layer.AnyStateTransitions.Remove(reference.Transition);
                    else reference.Owner?.Transitions.Remove(reference.Transition);
                }

        if (selection.Nodes != null)
            foreach (var node in selection.Nodes)
                if (node.UserData is AnimatorState state)
                    DeleteState(layer, state);

        ClearSelection();
        MarkDirty();
    }

    /// <summary>
    /// Right-click on empty canvas. <paramref name="graphPosition"/> arrives in <i>graph</i> space -
    /// the widget has already un-projected it - so it is what a new node should be placed at, and it
    /// has to be projected back to screen space to put the menu under the cursor.
    /// </summary>
    private void HandleBackgroundContext(Float2 graphPosition)
    {
        Float2 screen = GraphToScreen(graphPosition);
        Origami.ContextMenu(screen.X, screen.Y, menu =>
        {
            menu.Title(CurrentLayer?.Name ?? "Layer");
            menu.Item("Add State", () => AddStateAt(graphPosition));
            menu.Separator();
            menu.Item("Auto Layout", AutoLayout);
            menu.Item("Frame All", FrameGraph);
            menu.Item("Reset View (1:1)", ResetView);
        });
    }

    private void HandleNodeContext(GraphNode node, Float2 graphPosition)
    {
        var layer = CurrentLayer;
        if (layer == null) return;

        Float2 screen = GraphToScreen(graphPosition);

        if (node.Id is AnyStateNodeId or EntryNodeId)
        {
            bool entry = node.Id == EntryNodeId;
            Origami.ContextMenu(screen.X, screen.Y, menu =>
            {
                menu.Title(entry ? "Entry" : "Any State");
                menu.Item(entry
                    ? "Drag the wire onto a state to make it the default."
                    : "Drag from the port to add a transition that can fire from any state.",
                    () => { }, false);
                menu.Separator();
                menu.Item("Auto Layout", AutoLayout);
            });
            return;
        }

        if (node.UserData is not AnimatorState state) return;
        SelectState(state);

        Origami.ContextMenu(screen.X, screen.Y, menu =>
        {
            menu.Title(state.Name);
            menu.Item("Set as Default State", () => { layer.DefaultState = state.Name; MarkDirty(); },
                state.Name != layer.DefaultState);
            menu.Item("Duplicate", () => DuplicateState(layer, state));

            if (StateClip(state) is { } resolved)
                menu.Item("Edit Clip…", () => AnimationClipEditorWindow.OpenFor(resolved));

            menu.Separator();
            menu.Item("Add Transition To", () => { }, false);
            foreach (var other in layer.States)
            {
                if (ReferenceEquals(other, state)) continue;
                var destination = other;
                menu.Item("    → " + destination.Name, () =>
                {
                    SelectTransition(state, state.AddTransition(destination.Name), false);
                    MarkDirty();
                });
            }

            menu.Separator();
            menu.Item("Delete State", () =>
            {
                DeleteState(layer, state);
                ClearSelection();
                MarkDirty();
            });
        });
    }

    /// <summary>Double-clicking a state opens the clip it plays - the thing you actually want to edit.</summary>
    private void HandleNodeDoubleClick(GraphNode node)
    {
        if (node.UserData is not AnimatorState state) return;
        SelectState(state);
        if (StateClip(state) is { } clip) AnimationClipEditorWindow.OpenFor(clip);
    }

    private static AnimationClip? StateClip(AnimatorState state)
        => state.Motion is ClipMotion clip ? clip.Clip.Res : null;

    private AnimatorState? ResolveState(string nodeId)
        => _nodeStates.TryGetValue(nodeId, out var state) ? state : null;

    // ============================================================
    //  Layout
    // ============================================================

    /// <summary>
    /// States authored in code - or by the create-asset menu - all carry position (0,0), so the graph
    /// opens as a single unreadable pile of overlapping cards. Detect that once per controller and lay
    /// the layer out before the user ever sees it.
    /// </summary>
    private void EnsureInitialLayout()
    {
        if (_layoutChecked || _controller.IsNotValid()) return;
        _layoutChecked = true;

        foreach (var layer in _controller!.Layers)
            if (NeedsLayout(layer))
                LayoutLayer(layer);

        // Open at 1:1 rather than framing: framing a graph of any size zooms past the point where the
        // widget stops drawing node text, so a wall of unlabelled cards would be the first thing you see.
        ResetView();
    }

    private static bool NeedsLayout(AnimatorLayer layer)
    {
        if (layer.States.Count < 2) return false;

        // Degenerate if anything sits on the origin or two states share a position.
        var seen = new HashSet<(int, int)>();
        foreach (var state in layer.States)
        {
            var p = state.EditorPosition;
            if (p.X == 0f && p.Y == 0f) return true;
            if (!seen.Add(((int)p.X, (int)p.Y))) return true;
        }
        return false;
    }

    /// <summary>Lay out the current layer and frame it.</summary>
    private void AutoLayout()
    {
        var layer = CurrentLayer;
        if (layer == null) return;

        LayoutLayer(layer);
        FrameGraph();
        MarkDirty();
    }

    /// <summary>
    /// Arrange states in columns by how far they are from the entry state, following transitions.
    /// Reading left-to-right then matches the order the machine actually runs in, which is the thing
    /// a pile of arbitrarily-placed nodes never tells you. States nothing can reach are parked in a
    /// final column so they stand out as the orphans they are.
    /// </summary>
    private static void LayoutLayer(AnimatorLayer layer)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        void Seed(string name, int d)
        {
            if (string.IsNullOrEmpty(name) || layer.FindState(name) == null) return;
            if (depth.TryGetValue(name, out int existing) && existing <= d) return;
            depth[name] = d;
            frontier.Enqueue(name);
        }

        Seed(layer.DefaultState, 0);

        // Any-state targets are entry points too, but they are reactions rather than the main line,
        // so they start one column in.
        foreach (var t in layer.AnyStateTransitions)
            if (!t.Muted) Seed(t.DestinationState, 1);

        while (frontier.Count > 0)
        {
            string name = frontier.Dequeue();
            int d = depth[name];
            var state = layer.FindState(name)!;
            foreach (var t in state.Transitions)
                if (!t.Muted && t.DestinationState != name)
                    Seed(t.DestinationState, d + 1);
        }

        int maxDepth = 0;
        foreach (int d in depth.Values) maxDepth = Math.Max(maxDepth, d);

        int orphanColumn = depth.Count == 0 ? 0 : maxDepth + 1;
        var columnCounts = new Dictionary<int, int>();

        foreach (var state in layer.States)
        {
            int column = depth.TryGetValue(state.Name, out int d) ? d : orphanColumn;
            columnCounts.TryGetValue(column, out int row);
            columnCounts[column] = row + 1;

            state.EditorPosition = new Float2(
                LayoutOriginX + column * ColumnSpacing,
                LayoutOriginY + row * RowSpacing);
        }

        // Park Entry and Any State to the left of the first column, stacked.
        layer.EntryEditorPosition = new Float2(LayoutOriginX - ColumnSpacing, LayoutOriginY);
        layer.AnyStateEditorPosition = new Float2(LayoutOriginX - ColumnSpacing, LayoutOriginY + RowSpacing);
    }

    // ============================================================
    //  Inspector
    // ============================================================

    private void DrawInspector(Paper paper, Prowl.Scribe.FontFile font, float height)
    {
        using (paper.Column("anim_insp").Width(InspectorWidth).Height(UnitValue.Stretch())
            .BackgroundColor(EditorTheme.Neutral200)
            .BorderColor(EditorTheme.Neutral300).BorderWidth(1)
            .Padding(8, 8, 8, 8).Enter())
        {
            Origami.ScrollView(paper, "anim_insp_scroll", InspectorWidth - 20, MathF.Max(120f, height - 50f)).Body(() =>
            {
                if (_selectedState != null) DrawStateInspector(paper, font, _selectedState);
                else if (_selectedTransition != null) DrawTransitionInspector(paper, font);
                else if (_entrySelected) DrawEntryInspector(paper);
                else if (_anyStateSelected)
                    Origami.Label(paper, "anim_insp_any",
                        "Any State: the source for transitions that can fire from any state in the layer. Drag from its port to a state to add one.").Show();
                else
                    Origami.Label(paper, "anim_insp_none",
                        "Select a state or a transition. Right-click the canvas to add a state; drag from a state's right-hand port to another state to add a transition.").Show();
            });
        }
    }

    private void DrawEntryInspector(Paper paper)
    {
        var layer = CurrentLayer!;
        EditorGUI.SectionHeader(paper, "anim_ei_hdr", "Entry");
        Origami.Label(paper, "anim_ei_help",
            "The state this layer starts in. Drag the green wire onto a different state to change it.").Show();

        string[] names = layer.States.Select(s => s.Name).ToArray();
        int index = Array.IndexOf(names, layer.DefaultState);
        EditorGUI.Row(paper, "anim_ei_default", "Default", () =>
            Origami.Dropdown(paper, "anim_ei_default_v", index, v =>
            {
                if (v >= 0 && v < names.Length) { layer.DefaultState = names[v]; MarkDirty(); }
            }, names).Show());
    }

    private void DrawStateInspector(Paper paper, Prowl.Scribe.FontFile font, AnimatorState state)
    {
        var layer = CurrentLayer!;
        EditorGUI.SectionHeader(paper, "anim_si_hdr", "State");

        if (_liveAnimator.IsValid() && _liveAnimator!.GetCurrentStateName(_layerIndex) == state.Name)
        {
            var info = _liveAnimator.GetCurrentStateInfo(_layerIndex);
            Origami.Label(paper, "anim_si_live",
                $"Playing - {info.NormalizedTime % 1f:F2} of {info.Length:F2}s").Show();
        }

        EditorGUI.Row(paper, "anim_si_name", "Name", () =>
            Origami.TextField(paper, "anim_si_name_v", state.Name, v =>
            {
                if (string.IsNullOrWhiteSpace(v) || v == state.Name || layer.FindState(v) != null) return;
                _controller!.RenameState(layer, state.Name, v);
                MarkDirty();
            }).Show());

        int motionKind = state.Motion is ClipMotion ? 1 : state.Motion is BlendTree ? 2 : 0;
        EditorGUI.Row(paper, "anim_si_mk", "Motion", () =>
            Origami.Dropdown(paper, "anim_si_mk_v", motionKind, v =>
            {
                state.Motion = v switch
                {
                    1 => new ClipMotion(),
                    2 => new BlendTree(_controller!.Parameters.Count > 0 ? _controller.Parameters[0].Name : "Blend"),
                    _ => null,
                };
                MarkDirty();
            }, new[] { "None", "Clip", "Blend Tree" }).Show());

        if (state.Motion is ClipMotion clip)
            DrawClipField(paper, "anim_si_clip", "Clip", clip, state.Name);
        else if (state.Motion is BlendTree tree)
            DrawBlendTree(paper, font, "anim_si_bt", tree, depth: 0);

        EditorGUI.Row(paper, "anim_si_speed", "Speed", () =>
            Origami.NumericField(paper, "anim_si_speed_v", state.Speed, v => { state.Speed = v; MarkDirty(); }).Show());

        EditorGUI.Row(paper, "anim_si_speedp", "Speed Param", () =>
            Origami.Dropdown(paper, "anim_si_speedp_v", ParamIndexOrNone(state.SpeedParameter), v =>
            {
                state.SpeedParameter = v <= 0 ? string.Empty : _controller!.Parameters[v - 1].Name;
                MarkDirty();
            }, ParamNamesWithNone()).Show());

        EditorGUI.Row(paper, "anim_si_loop", "Loop", () =>
            Origami.Checkbox(paper, "anim_si_loop_v", state.Loop, v => { state.Loop = v; MarkDirty(); }).Show());

        EditorGUI.Row(paper, "anim_si_cyc", "Cycle Offset", () =>
            Origami.NumericField(paper, "anim_si_cyc_v", state.CycleOffset, v => { state.CycleOffset = v; MarkDirty(); }).Show());

        EditorGUI.Row(paper, "anim_si_tag", "Tag", () =>
            Origami.TextField(paper, "anim_si_tag_v", state.Tag, v => { state.Tag = v; MarkDirty(); }).Show());

        // Events.
        paper.Box("anim_si_sp1").Height(10).IsNotInteractable();
        EditorGUI.SectionHeader(paper, "anim_si_ev_hdr", "Events");

        for (int i = 0; i < state.Events.Count; i++)
        {
            int index = i;
            var marker = state.Events[i];
            using (paper.Row($"anim_si_ev{i}").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight)
                .Margin(0, 0, 0, 3).Gap(3).Enter())
            {
                using (paper.Box($"anim_si_ev{i}_t").Width(56).Height(EditorGUIMetrics.RowHeight).Enter())
                    Origami.NumericField(paper, $"anim_si_ev{i}_tv", marker.NormalizedTime,
                        v => { marker.NormalizedTime = Math.Clamp(v, 0f, 1f); MarkDirty(); }).Show();

                using (paper.Box($"anim_si_ev{i}_n").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Enter())
                    Origami.TextField(paper, $"anim_si_ev{i}_nv", marker.FunctionName,
                        v => { marker.FunctionName = v; MarkDirty(); }).Show();

                DeleteButton(paper, $"anim_si_ev{i}_x", font, () => { state.Events.RemoveAt(index); MarkDirty(); });
            }
        }

        Origami.Button(paper, "anim_si_ev_add", "+ Event", () => { state.AddEvent(0.5f, "Event"); MarkDirty(); })
            .Subtle().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();

        // Transitions.
        paper.Box("anim_si_sp2").Height(10).IsNotInteractable();
        EditorGUI.SectionHeader(paper, "anim_si_tr_hdr", "Transitions");

        if (state.Transitions.Count == 0)
            Origami.Label(paper, "anim_si_tr_none", "Drag from this state's right-hand port to add one.").Show();

        for (int i = 0; i < state.Transitions.Count; i++)
        {
            var t = state.Transitions[i];
            string label = DescribeTransition(t);

            paper.Box($"anim_si_tr{i}").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight)
                .Rounded(3).Margin(0, 0, 0, 2)
                .BackgroundColor(_selectedTransition == t ? EditorTheme.Purple400 : EditorTheme.Neutral300)
                .Hovered.BackgroundColor(EditorTheme.Neutral400).End()
                .Text("  " + label, font).TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft)
                .OnClick(_ => SelectTransition(state, t, false));
        }

        paper.Box("anim_si_sp3").Height(12).IsNotInteractable();
        Origami.Button(paper, "anim_si_del", "Delete State", () =>
        {
            DeleteState(layer, state);
            ClearSelection();
            MarkDirty();
        }).Danger().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();
    }

    private void DrawClipField(Paper paper, string id, string label, ClipMotion motion, string stateName)
    {
        PropertyGridUtils.DrawField(paper, id, label, typeof(AssetRef<AnimationClip>), motion.Clip, value =>
        {
            if (value is AssetRef<AnimationClip> reference) motion.Clip = reference;
            else if (value is AnimationClip clip) motion.Clip = new AssetRef<AnimationClip>(clip);
            MarkDirty();
        });

        // Straight into the dope sheet for the clip this state plays - the trip through the Project
        // panel to find the same asset again is pure friction.
        if (motion.Clip.Res is AnimationClip resolved)
            Origami.Button(paper, $"{id}_edit", "Edit Clip",
                () => AnimationClipEditorWindow.OpenFor(resolved))
                .Subtle().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();
        else if (motion.Clip.AssetID == Guid.Empty)
            // An empty slot was a dead end: you had to leave, create a clip through the Assets menu,
            // find it again and drag it back. Make the clip from here and open it.
            Origami.Button(paper, $"{id}_new", "New Clip", () => CreateClipFor(motion, stateName))
                .Subtle().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();
    }

    /// <summary>
    /// Create an empty <c>.anim</c> next to the controller, point <paramref name="motion"/> at it and
    /// open it for editing.
    /// </summary>
    private void CreateClipFor(ClipMotion motion, string stateName)
    {
        var backend = Prowl.Editor.EditorAssetBackend.Instance;
        if (backend == null || Project.Current == null || _controller.IsNotValid())
        {
            Debug.LogWarning("[Animate] Cannot create a clip: no project is open.");
            return;
        }

        // Beside the controller, which is where anyone would look for it.
        string? controllerPath = backend.GuidToPath(_controller!.AssetID);
        string folder = !string.IsNullOrEmpty(controllerPath)
            ? System.IO.Path.GetDirectoryName(System.IO.Path.Combine(Project.Current.AssetsPath, controllerPath!))
              ?? Project.Current.AssetsPath
            : Project.Current.AssetsPath;

        string baseName = string.IsNullOrWhiteSpace(stateName) ? "New Animation" : stateName;
        string fileName = Prowl.Editor.AssetCreateMenu.FindUniqueName(folder, baseName, ".anim");
        string absolute = System.IO.Path.Combine(folder, fileName);

        var clip = new AnimationClip
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(fileName),
            Duration = 1f,
            TicksPerSecond = 30f,
            Wrap = AnimationWrapMode.Loop,
        };

        if (!AnimationClipIO.SaveAs(clip, absolute)) return;

        Guid guid = backend.PathToGuid(backend.ToRelativePath(absolute));
        if (guid == Guid.Empty) return;

        motion.Clip = new AssetRef<AnimationClip>(guid);
        MarkDirty();

        if (motion.Clip.Res is AnimationClip created) AnimationClipEditorWindow.OpenFor(created);
    }

    /// <summary>
    /// Blend tree editing. Unlike the original, children may themselves be blend trees, so a speed
    /// tree can hold a directional tree - which is the whole point of the type being recursive.
    /// </summary>
    private void DrawBlendTree(Paper paper, Prowl.Scribe.FontFile font, string id, BlendTree tree, int depth)
    {
        EditorGUI.Row(paper, $"{id}_type", "Type", () =>
            Origami.EnumDropdown(paper, $"{id}_type_v", tree.Type, v => { tree.Type = v; MarkDirty(); }).Show());

        EditorGUI.Row(paper, $"{id}_px", "Param X", () =>
            Origami.Dropdown(paper, $"{id}_px_v", ParamIndexOrNone(tree.BlendParameter), v =>
            {
                tree.BlendParameter = v <= 0 ? string.Empty : _controller!.Parameters[v - 1].Name;
                MarkDirty();
            }, ParamNamesWithNone()).Show());

        if (tree.Is2D)
            EditorGUI.Row(paper, $"{id}_py", "Param Y", () =>
                Origami.Dropdown(paper, $"{id}_py_v", ParamIndexOrNone(tree.BlendParameterY), v =>
                {
                    tree.BlendParameterY = v <= 0 ? string.Empty : _controller!.Parameters[v - 1].Name;
                    MarkDirty();
                }, ParamNamesWithNone()).Show());

        for (int i = 0; i < tree.Children.Count; i++)
        {
            int index = i;
            var child = tree.Children[i];
            string childId = $"{id}_c{i}";

            using (paper.Column(childId).Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .BackgroundColor(EditorTheme.Neutral300).Rounded(4)
                .Padding(6, 6, 6, 6).Margin(depth > 0 ? 8 : 0, 0, 0, 6).Gap(3).Enter())
            {
                using (paper.Row($"{childId}_hdr").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Gap(3).Enter())
                {
                    int kind = child.Motion is BlendTree ? 1 : 0;
                    using (paper.Box($"{childId}_kind").Width(84).Height(EditorGUIMetrics.RowHeight).Enter())
                        Origami.Dropdown(paper, $"{childId}_kind_v", kind, v =>
                        {
                            child.Motion = v == 1
                                ? new BlendTree(tree.BlendParameter)
                                : new ClipMotion();
                            MarkDirty();
                        }, new[] { "Clip", "Tree" }).Show();

                    if (child.Motion is ClipMotion childClip)
                    {
                        using (paper.Box($"{childId}_clip").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Enter())
                            PropertyGridUtils.DrawField(paper, $"{childId}_clip_v", string.Empty,
                                typeof(AssetRef<AnimationClip>), childClip.Clip, value =>
                                {
                                    if (value is AssetRef<AnimationClip> reference) childClip.Clip = reference;
                                    else if (value is AnimationClip clip) childClip.Clip = new AssetRef<AnimationClip>(clip);
                                    MarkDirty();
                                });
                    }
                    else
                    {
                        paper.Box($"{childId}_lbl").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).IsNotInteractable()
                            .Text("Nested blend tree", font).TextColor(EditorTheme.Ink300)
                            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
                    }

                    DeleteButton(paper, $"{childId}_x", font, () => { tree.Children.RemoveAt(index); MarkDirty(); });
                }

                if (tree.Type == BlendTreeType.Simple1D)
                {
                    // Deliberately does *not* re-sort as you type. Sorting on every keystroke reorders
                    // the list under the cursor, so the row you are editing becomes a different child
                    // mid-edit and the field appears to fight you. The compiler sorts thresholds when
                    // it builds the blend, so authoring order never affects playback.
                    EditorGUI.Row(paper, $"{childId}_th", "Threshold", () =>
                        Origami.NumericField(paper, $"{childId}_th_v", child.Threshold,
                            v => { child.Threshold = v; MarkDirty(); }).Show());
                }
                else
                {
                    EditorGUI.Row(paper, $"{childId}_pos", "Position", () =>
                        Origami.Float2Field(paper, $"{childId}_pos_v", child.Position,
                            v => { child.Position = v; MarkDirty(); }).Show());
                }

                if (child.Motion is BlendTree nested && depth < 3)
                    DrawBlendTree(paper, font, $"{childId}_n", nested, depth + 1);
            }
        }

        using (paper.Row($"{id}_acts").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Gap(4).Enter())
        {
            Origami.Button(paper, $"{id}_add", "+ Child", () =>
            {
                tree.Children.Add(new BlendTreeChild { Motion = new ClipMotion() });
                MarkDirty();
            }).Subtle().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();

            if (tree.Type == BlendTreeType.Simple1D && tree.Children.Count > 1)
                Origami.Button(paper, $"{id}_sort", "Sort", () => { tree.SortChildren(); MarkDirty(); })
                    .Subtle().Width(64).Height(EditorGUIMetrics.RowHeight).Show();
        }
    }

    private void DrawTransitionInspector(Paper paper, Prowl.Scribe.FontFile font)
    {
        var layer = CurrentLayer!;
        var t = _selectedTransition!;
        string from = _selectedTransitionFromAny ? "Any State" : _selectedTransitionOwner?.Name ?? "?";

        EditorGUI.SectionHeader(paper, "anim_ti_hdr", "Transition");
        Origami.Label(paper, "anim_ti_from", $"From: {from}").Show();

        string[] names = layer.States.Select(s => s.Name).ToArray();
        int destIndex = Array.IndexOf(names, t.DestinationState);
        EditorGUI.Row(paper, "anim_ti_dest", "To", () =>
            Origami.Dropdown(paper, "anim_ti_dest_v", destIndex, v =>
            {
                if (v >= 0 && v < names.Length) { t.DestinationState = names[v]; MarkDirty(); }
            }, names).Show());

        EditorGUI.Row(paper, "anim_ti_mute", "Muted", () =>
            Origami.Checkbox(paper, "anim_ti_mute_v", t.Muted, v => { t.Muted = v; MarkDirty(); }).Show());

        // Conditions.
        paper.Box("anim_ti_sp1").Height(10).IsNotInteractable();
        EditorGUI.SectionHeader(paper, "anim_ti_cond_hdr", "Conditions");

        string[] paramNames = _controller!.Parameters.Select(p => p.Name).ToArray();
        if (paramNames.Length == 0)
            Origami.Label(paper, "anim_ti_cond_noparams", "Add a parameter first.").Show();

        for (int i = 0; i < t.Conditions.Count; i++)
        {
            int index = i;
            var condition = t.Conditions[i];

            using (paper.Column($"anim_ti_c{i}").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .BackgroundColor(EditorTheme.Neutral300).Rounded(4)
                .Padding(6, 6, 6, 6).Margin(0, 0, 0, 6).Gap(3).Enter())
            {
                using (paper.Row($"anim_ti_c{i}_r").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Gap(3).Enter())
                {
                    int paramIndex = Math.Max(0, Array.IndexOf(paramNames, condition.Parameter));
                    using (paper.Box($"anim_ti_c{i}_p").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Enter())
                        Origami.Dropdown(paper, $"anim_ti_c{i}_p_v", paramNames.Length > 0 ? paramIndex : -1, v =>
                        {
                            if (v >= 0 && v < paramNames.Length) { condition.Parameter = paramNames[v]; MarkDirty(); }
                        }, paramNames).Show();

                    DeleteButton(paper, $"anim_ti_c{i}_x", font, () => { t.Conditions.RemoveAt(index); MarkDirty(); });
                }

                using (paper.Row($"anim_ti_c{i}_r2").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Gap(3).Enter())
                {
                    using (paper.Box($"anim_ti_c{i}_m").Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Enter())
                        Origami.EnumDropdown(paper, $"anim_ti_c{i}_m_v", condition.Mode,
                            v => { condition.Mode = v; MarkDirty(); }).Show();

                    if (condition.UsesThreshold)
                        using (paper.Box($"anim_ti_c{i}_th").Width(70).Height(EditorGUIMetrics.RowHeight).Enter())
                            Origami.NumericField(paper, $"anim_ti_c{i}_th_v", condition.Threshold,
                                v => { condition.Threshold = v; MarkDirty(); }).Show();
                }
            }
        }

        if (paramNames.Length > 0)
            Origami.Button(paper, "anim_ti_cond_add", "+ Condition", () =>
            {
                t.Conditions.Add(new AnimatorCondition(paramNames[0], AnimatorConditionMode.Greater));
                MarkDirty();
            }).Subtle().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();

        // Timing.
        paper.Box("anim_ti_sp2").Height(10).IsNotInteractable();
        EditorGUI.SectionHeader(paper, "anim_ti_time_hdr", "Settings");

        EditorGUI.Row(paper, "anim_ti_het", "Has Exit Time", () =>
            Origami.Checkbox(paper, "anim_ti_het_v", t.HasExitTime, v => { t.HasExitTime = v; MarkDirty(); }).Show());

        if (t.HasExitTime)
            EditorGUI.Row(paper, "anim_ti_et", "Exit Time", () =>
                Origami.NumericField(paper, "anim_ti_et_v", t.ExitTime, v => { t.ExitTime = v; MarkDirty(); }).Show());

        EditorGUI.Row(paper, "anim_ti_fixed", "Fixed Duration", () =>
            Origami.Checkbox(paper, "anim_ti_fixed_v", t.FixedDuration, v => { t.FixedDuration = v; MarkDirty(); }).Show());

        EditorGUI.Row(paper, "anim_ti_dur", t.FixedDuration ? "Duration (s)" : "Duration (x)", () =>
            Origami.NumericField(paper, "anim_ti_dur_v", t.Duration, v => { t.Duration = MathF.Max(0f, v); MarkDirty(); }).Show());

        EditorGUI.Row(paper, "anim_ti_off", "Offset", () =>
            Origami.NumericField(paper, "anim_ti_off_v", t.Offset, v => { t.Offset = v; MarkDirty(); }).Show());

        EditorGUI.Row(paper, "anim_ti_self", "To Self", () =>
            Origami.Checkbox(paper, "anim_ti_self_v", t.CanTransitionToSelf, v => { t.CanTransitionToSelf = v; MarkDirty(); }).Show());

        EditorGUI.Row(paper, "anim_ti_pri", "Priority", () =>
            Origami.NumericField(paper, "anim_ti_pri_v", t.Priority, v => { t.Priority = v; MarkDirty(); }).Show());

        paper.Box("anim_ti_sp3").Height(12).IsNotInteractable();
        Origami.Button(paper, "anim_ti_del", "Delete Transition", () =>
        {
            if (_selectedTransitionFromAny) layer.AnyStateTransitions.Remove(t);
            else _selectedTransitionOwner?.Transitions.Remove(t);
            ClearSelection();
            MarkDirty();
        }).Danger().Width(UnitValue.Stretch()).Height(EditorGUIMetrics.RowHeight).Show();
    }

    // ============================================================
    //  Selection + mutations
    // ============================================================

    private void SelectState(AnimatorState state)
    {
        _selectedState = state;
        _selectedTransition = null;
        _anyStateSelected = false;
        _entrySelected = false;
    }

    private void SelectAnyState()
    {
        _anyStateSelected = true;
        _selectedState = null;
        _selectedTransition = null;
        _entrySelected = false;
    }

    private void SelectTransition(AnimatorState? owner, AnimatorStateTransition transition, bool fromAny)
    {
        _selectedTransition = transition;
        _selectedTransitionOwner = owner;
        _selectedTransitionFromAny = fromAny;
        _selectedState = null;
        _anyStateSelected = false;
        _entrySelected = false;
    }

    private void ClearSelection()
    {
        _selectedState = null;
        _selectedTransition = null;
        _selectedTransitionOwner = null;
        _selectedTransitionFromAny = false;
        _anyStateSelected = false;
        _entrySelected = false;
    }

    private void AddStateAt(Float2 graphPosition)
    {
        var layer = CurrentLayer;
        if (layer == null) return;

        var state = new AnimatorState(UniqueName("New State", n => layer.FindState(n) != null))
        {
            EditorPosition = graphPosition,
        };

        layer.States.Add(state);
        if (string.IsNullOrEmpty(layer.DefaultState)) layer.DefaultState = state.Name;
        SelectState(state);
        MarkDirty();
    }

    /// <summary>
    /// Remove a state and let go of the node id minted for it, so the id table does not accumulate
    /// entries (and keep the states themselves alive) for the life of the window.
    /// </summary>
    private void DeleteState(AnimatorLayer layer, AnimatorState state)
    {
        _controller!.RemoveState(layer, state);
        _nodeIds.Remove(state);
    }

    private void DuplicateState(AnimatorLayer layer, AnimatorState source)
    {
        var copy = new AnimatorState(UniqueName(source.Name + " Copy", n => layer.FindState(n) != null))
        {
            Motion = source.Motion,
            Speed = source.Speed,
            SpeedParameter = source.SpeedParameter,
            CycleOffset = source.CycleOffset,
            Loop = source.Loop,
            Tag = source.Tag,
            EditorPosition = source.EditorPosition + new Float2(30f, 30f),
        };

        layer.States.Add(copy);
        SelectState(copy);
        MarkDirty();
    }

    // ============================================================
    //  Live view
    // ============================================================

    /// <summary>
    /// Find a scene Animator that is playing this controller. Rechecked a few times a second rather
    /// than every frame - it walks the scene, and the answer only changes when play state does.
    ///
    /// <para>The interval is honoured whether or not one was found. Skipping it on a miss meant the
    /// overwhelmingly common case - editing a controller with the game not running - walked and
    /// allocated the whole scene's Animator list on <i>every single frame</i>.</para>
    /// </summary>
    private void RefreshLiveAnimator()
    {
        const int RecheckInterval = 20;
        int frame = (int)Time.FrameCount;
        if (frame - _liveSearchFrame < RecheckInterval) return;
        _liveSearchFrame = frame;
        _liveAnimator = null;

        var scene = Scene.Current;
        if (scene.IsNotValid()) return;

        foreach (var animator in scene.FindObjectsOfType<Animator>())
        {
            if (animator.IsNotValid()) continue;
            if (ReferenceEquals(animator.BoundController, _controller))
            {
                _liveAnimator = animator;
                return;
            }
        }
    }

    // ============================================================
    //  Helpers
    // ============================================================

    private void DeleteButton(Paper paper, string id, Prowl.Scribe.FontFile font, Action onClick)
    {
        paper.Box(id).Width(18).Height(EditorGUIMetrics.RowHeight).Rounded(3)
            .Hovered.BackgroundColor(EditorTheme.Red400).End()
            .Text("✕", font).TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter)
            .OnClick(_ => onClick());
    }

    private string[] ParamNamesWithNone()
    {
        var list = new List<string> { "(none)" };
        list.AddRange(_controller!.Parameters.Select(p => p.Name));
        return list.ToArray();
    }

    private int ParamIndexOrNone(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        int i = _controller!.Parameters.FindIndex(p => p.Name == name);
        return i < 0 ? 0 : i + 1;
    }

    private static string UniqueName(string baseName, Func<string, bool> exists)
    {
        if (!exists(baseName)) return baseName;
        for (int i = 1; ; i++)
        {
            string candidate = $"{baseName} {i}";
            if (!exists(candidate)) return candidate;
        }
    }

    private void EnsureIssues()
    {
        if (!_issuesStale || _controller.IsNotValid()) return;
        _issuesStale = false;
        _controller!.Validate(_issues);
    }

    // ============================================================
    //  Save / persistence
    // ============================================================

    private void MarkDirty()
    {
        _dirty = true;
        _issuesStale = true;
        EnsureSaveHook();

        // Tell any playing Animator its compiled copy is stale, so graph edits apply immediately.
        if (_controller.IsValid()) _controller!.MarkStructureChanged();
    }

    private void Save()
    {
        if (_controller.IsNotValid()) return;
        if (!AnimatorAssetIO.Save(_controller!)) return;

        _dirty = false;
        ReleaseSaveHook();
    }

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        if (_controllerRef.AssetID == Guid.Empty) return false;
        state["controller"] = _controllerRef.AssetID.ToString();
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (Guid.TryParse(state["controller"]?.GetValue<string>(), out var guid))
            SetControllerRef(new AssetRef<AnimatorController>(guid));
    }
}

/// <summary>
/// Shared metrics so both windows line up with each other and with the rest of the editor.
///
/// <para><b>Why buttons need an explicit height.</b> Every other Origami control - text field,
/// numeric field, dropdown, slider - defaults its height to <c>Metrics.RowHeight</c>, which the
/// editor exposes as a user preference. <c>ButtonBuilder</c> is the exception: it hardcodes 32px and
/// never reads the metric. So a stock button stands 8px taller than the field beside it, overflows
/// the toolbar strip it sits in, and ignores the Row Height setting entirely. Passing the metric back
/// in is what puts them on the same baseline.</para>
/// </summary>
internal static class EditorGUIMetrics
{
    /// <summary>Standard control height, from the active theme.</summary>
    public static float RowHeight => Origami.Current.Metrics.RowHeight;

    /// <summary>Vertical padding inside a toolbar strip, above and below its controls.</summary>
    public const float ToolbarPadY = 5f;

    /// <summary>Padding for a secondary strip sitting under a main one - tighter, so it reads as subordinate.</summary>
    public const float ToolbarPadYCompact = 3f;

    /// <summary>Height of a control in a toolbar strip.</summary>
    public static float ToolbarControlHeight => MathF.Max(20f, RowHeight);

    /// <summary>Total height of a toolbar strip: one control plus its padding.</summary>
    public static float ToolbarHeight => ToolbarControlHeight + ToolbarPadY * 2f;

    /// <summary>Total height of a secondary strip.</summary>
    public static float CompactToolbarHeight => ToolbarControlHeight + ToolbarPadYCompact * 2f;
}
