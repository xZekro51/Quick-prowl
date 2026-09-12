// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Prowl.Editor;
using Prowl.Editor.Core;
using Prowl.Editor.GUI;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.Events;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Quill;
using Prowl.Runtime;
using Prowl.Vector;

using SColor = System.Drawing.Color;

namespace Prowl.Animate.Editor;

/// <summary>
/// A dope-sheet / curve editor for <see cref="AnimationClip"/> assets, in the shape people expect
/// from Unity's Animation window, with the rough edges that window is regularly complained about
/// filed off:
///
/// <list type="bullet">
/// <item><b>Preview does not dirty your scene.</b> Scrubbing poses the rig through
/// <see cref="ClipPosePreview"/>, which snapshots every bone it touches and puts them back when you
/// stop previewing or close the window.</item>
/// <item><b>No hidden record mode.</b> Keys are only ever created when you ask for one, so moving a
/// transform in the scene can never silently bake a key you did not want.</item>
/// <item><b>Numeric key editing.</b> The selected key's exact time and value are editable fields
/// rather than something you have to nudge with the mouse.</item>
/// <item><b>Marquee selection and multi-key edits</b> - box-select across tracks, then move, scale,
/// delete, copy/paste or re-interpolate the whole selection at once.</item>
/// <item><b>Optional frame snapping.</b> Snap is a toggle, not a law, so sub-frame keys are possible.</item>
/// <item><b>A property filter.</b> A rig with three hundred channels is unusable without one.</item>
/// <item><b>Clip utilities</b> - fit length to the last key, reverse, and scale the whole clip in time.</item>
/// </list>
/// </summary>
public sealed class AnimationClipEditorWindow : DockPanel
{
    // ── Layout constants ────────────────────────────────────────────────────
    private static float ToolbarHeight => EditorGUIMetrics.ToolbarHeight;
    private static float SecondToolbarHeight => EditorGUIMetrics.CompactToolbarHeight;
    private const float RulerHeight = 26f;
    private const float RowHeight = 22f;
    private const float GutterWidth = 250f;
    private static float InspectorHeight => EditorGUIMetrics.RowHeight * 2f + 36f;
    private const float KeyEpsilon = 1e-4f;
    private const float MinPixelsPerSecond = 12f;
    private const float MaxPixelsPerSecond = 6000f;

    /// <summary>
    /// The clip is held as an <see cref="AssetRef{T}"/>, never as a raw field.
    ///
    /// <para>Two things break a raw reference. The asset cache idle-sweeps assets nothing reports as
    /// in use, and - far more immediately - saving reimports the asset, which disposes the instance
    /// and builds a new one, leaving a raw field pointing at a corpse. Re-reading <c>.Res</c> each
    /// frame both counts as activity and picks up the post-reimport instance.</para>
    /// </summary>
    private AssetRef<AnimationClip> _clipRef;

    /// <summary>Resolved once at the top of each frame; null while an async load is still streaming.</summary>
    private AnimationClip? _clip;

    private bool _dirty;
    private bool _saveHookAttached;

    // ── View ────────────────────────────────────────────────────────────────
    private float _viewStart;                 // seconds at the left edge of the timeline
    private float _pixelsPerSecond = 240f;
    private float _scrollY;
    private float _time;                      // playhead, seconds
    private int _frameRate = 30;
    private bool _snapToFrames = true;
    private bool _curveMode;
    private string _search = string.Empty;

    // ── Playback ────────────────────────────────────────────────────────────
    private bool _playing;
    private bool _loopPlayback = true;
    private readonly Stopwatch _clock = new();
    private double _lastClockSeconds;

    // ── Tracks (derived from the clip; rebuilt when something changes shape) ─
    private readonly List<TrackRow> _rows = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    /// <summary>
    /// Set whenever the row list could have changed shape - a different clip, an edit, a new filter, a
    /// group folded, a different rig previewed.
    ///
    /// <para>These rows used to be rebuilt from scratch on every single frame, which on a real
    /// character meant a few thousand allocations a frame plus an O(bones²) resolved-bone lookup, all
    /// to produce exactly the same list. That is felt as a window that stutters while you drag.</para>
    /// </summary>
    private bool _rowsStale = true;

    // ── Selection / clipboard ───────────────────────────────────────────────
    private readonly List<KeyRef> _selection = new();
    private readonly List<ClipboardKey> _clipboard = new();

    // ── Interaction ─────────────────────────────────────────────────────────
    private Rect _bodyRect;
    private DragKind _drag = DragKind.None;
    private Float2 _dragOrigin;
    private Float2 _dragCurrent;
    private float _dragStartTime;
    private PaperMouseBtn _pressedButton = PaperMouseBtn.Unknown;

    // ── Preview ─────────────────────────────────────────────────────────────
    private readonly ClipPosePreview _preview = new();
    private bool _previewEnabled = true;
    private GameObject? _previewTarget;

    /// <summary>Whether a rig is available to author new tracks against, resolved once per frame.</summary>
    private bool _hasRigForAuthoring;

    public override string Title => _clip.IsValid() ? $"Animation - {_clip!.Name}" : "Animation";
    public override string Icon => "";

    private enum DragKind { None, Playhead, Keys, Marquee, PanView, CurveValue }

    /// <summary>One row in the sheet: either a bone/group header or a single animated channel.</summary>
    private sealed class TrackRow
    {
        public string GroupKey = string.Empty;
        public string Label = string.Empty;
        public string Detail = string.Empty;
        public bool IsGroup;
        public bool Collapsed;
        public AnimationCurve? Curve;                       // null on group rows
        public readonly List<AnimationCurve> Merged = new(); // group rows: every child curve
        public SColor Accent = SColor.Gray;
        public Action? Remove;
        public bool Resolved = true;

        /// <summary>Component count of <see cref="Curve"/> (3 for position/scale, 4 for rotation, 1 for a weight).</summary>
        public int Dimension = 1;

        /// <summary>Per-component axis letters, for the curve view's legend and the key inspector.</summary>
        public string Components = string.Empty;
    }

    /// <summary>A selected key, identified by its curve plus its time (keys are re-created when moved).</summary>
    private sealed class KeyRef
    {
        public AnimationCurve Curve = null!;
        public float Time;
        public float DragStartTime;
    }

    /// <summary>
    /// A copied key, remembered against the curve it came from rather than against a row index. Rows
    /// are rebuilt whenever anything changes shape - a filter, a folded group, a deleted track - so a
    /// row index copied before such a change pasted into whichever channel had since taken that slot.
    /// </summary>
    private readonly struct ClipboardKey
    {
        public readonly AnimationCurve Curve;
        public readonly float Offset;
        public readonly Keyframe Key;

        public ClipboardKey(AnimationCurve curve, float offset, Keyframe key)
        {
            Curve = curve;
            Offset = offset;
            Key = key;
        }
    }

    /// <summary>
    /// The three transform channels an <see cref="AnimationClip.AnimBone"/> carries, with accessors so
    /// rows can add and clear them.
    ///
    /// <para>One row per channel rather than one per axis, because that is how the clip stores them:
    /// a channel is a single multi-component <see cref="AnimationCurve"/> whose keys are shared across
    /// its components. Ten per-axis rows would have implied you could key Position.X alone, and every
    /// such edit would have silently keyed Y and Z too.</para>
    /// </summary>
    private static readonly (string Name, SColor Accent, int Dimension, string Components,
        Func<AnimationClip.AnimBone, AnimationCurve?> Get,
        Action<AnimationClip.AnimBone, AnimationCurve?> Set)[] Channels =
    {
        ("Position", SColor.IndianRed,      3, "XYZ",  b => b.Position, (b, c) => b.Position = c),
        ("Rotation", SColor.CornflowerBlue, 4, "XYZW", b => b.Rotation, (b, c) => b.Rotation = c),
        ("Scale",    SColor.YellowGreen,    3, "XYZ",  b => b.Scale,    (b, c) => b.Scale    = c),
    };

    /// <summary>Per-component line colours in the curve view, indexed by component.</summary>
    private static readonly SColor[] ComponentColors =
    {
        SColor.IndianRed, SColor.YellowGreen, SColor.CornflowerBlue, SColor.Goldenrod,
    };

    // ============================================================
    //  Entry points
    // ============================================================

    /// <summary>
    /// Open (or focus) the animation window on a clip. Reuses the window that is already open for the
    /// same reason the Animator graph does: two windows on one clip both hook project-save, and the
    /// one that saves second is holding an instance the first one's reimport already disposed.
    /// </summary>
    public static void OpenFor(AnimationClip clip)
    {
        var app = EditorApplication.Instance;
        if (app == null) return;

        var reference = clip.IsValid() ? new AssetRef<AnimationClip>(clip) : default;

        if (app.FindOpenPanel(typeof(AnimationClipEditorWindow)) is AnimationClipEditorWindow existing)
        {
            bool sameAsset = reference.AssetID != Guid.Empty && existing._clipRef.AssetID == reference.AssetID;
            if (sameAsset || !existing._dirty)
            {
                if (!sameAsset) existing.SetClipRef(reference);
                app.OpenPanel(typeof(AnimationClipEditorWindow));
                return;
            }
        }

        var panel = new AnimationClipEditorWindow();
        panel.SetClipRef(reference);
        app.OpenPanelInstance(panel, 1180, 640);
    }

    public static void OpenEmpty() => EditorApplication.Instance?.OpenPanel(typeof(AnimationClipEditorWindow));

    [AssetDoubleClickHandler(".anim")]
    private static bool OpenHandler(string relativePath, Guid guid)
    {
        var reference = new AssetRef<AnimationClip>(guid);
        if (reference.Res is AnimationClip clip)
        {
            OpenFor(clip);
            return true;
        }
        return false;
    }

    private void SetClipRef(AssetRef<AnimationClip> reference)
    {
        _preview.Release();
        _clipRef = reference;
        _clip = _clipRef.Res;
        _selection.Clear();
        _clipboard.Clear();
        _collapsed.Clear();
        _rows.Clear();
        _rowsStale = true;
        _time = 0f;
        _viewStart = 0f;
        _playing = false;

        if (_clip != null)
        {
            _time = _clip.StartTime;
            _viewStart = _clip.StartTime;

            _frameRate = _clip.TicksPerSecond > 0f
                ? Math.Clamp((int)MathF.Round(_clip.TicksPerSecond), 1, 240)
                : 30;

            // Fit the clip across a comfortable default width.
            _pixelsPerSecond = _clip.Duration > 0.01f
                ? Math.Clamp(700f / _clip.Duration, MinPixelsPerSecond, MaxPixelsPerSecond)
                : 240f;
        }
    }

    public override void OnClosed()
    {
        _preview.Release();
        ReleaseSaveHook();
    }

    /// <summary>
    /// Re-resolve the clip for this frame. A reimport (which every save triggers) replaces the
    /// instance, so anything holding pointers into the old object graph - the key selection, the
    /// clipboard, the preview binding - has to be dropped when that happens.
    /// </summary>
    private void ResolveClip()
    {
        AnimationClip? resolved = _clipRef.Res;
        if (ReferenceEquals(resolved, _clip)) return;

        _clip = resolved;
        _rows.Clear();
        _rowsStale = true;
        _selection.Clear();
        _clipboard.Clear();
        _preview.Release();
        _previewTarget = null;
    }

    // ============================================================
    //  Frame
    // ============================================================

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        ResolveClip();

        if (_clip.IsNotValid())
        {
            EditorGUI.EmptyState(paper, "clip_empty",
                _clipRef.AssetID != Guid.Empty
                    ? "Loading clip..."
                    : "Open an animation clip: double-click a .anim asset, or use a state's Edit Clip action in the Animator graph.",
                font);
            return;
        }

        TickPlayback();
        SyncPreview();
        HandleShortcuts(paper);

        // A context-menu item closes the menu *after* its action runs, which would also close a menu
        // the action just opened - so opening one from another has to wait a frame.
        if (_pendingAddPropertyMenu is { } at)
        {
            _pendingAddPropertyMenu = null;
            OpenAddPropertyMenu(at);
        }

        using (paper.Column("clip_root").Width(UnitValue.Stretch()).Height(UnitValue.Stretch()).Enter())
        {
            DrawToolbar(paper, font);
            DrawClipBar(paper, font);
            DrawSheet(paper, font, height);
            DrawKeyInspector(paper, font);
        }
    }

    /// <summary>
    /// Timeline keyboard shortcuts, scoped to when the pointer is over the sheet.
    ///
    /// <para>Scoping by hover rather than by panel focus is deliberate: several panels are visible at
    /// once in a docked layout, and a Delete key that fires in whichever animation window happens to
    /// be on screen is worse than no shortcut at all. Typing into any field suppresses them.</para>
    /// </summary>
    private void HandleShortcuts(Paper paper)
    {
        if (paper.WantsCaptureKeyboard || !PointerOverSheet(paper)) return;

        bool ctrl = Input.IsCtrlPressed;

        if (paper.IsKeyPressed(PaperKey.Delete) || paper.IsKeyPressed(PaperKey.Backspace))
        {
            if (_selection.Count > 0) DeleteSelection();
            return;
        }

        if (ctrl && paper.IsKeyPressed(PaperKey.C)) { CopySelection(); return; }
        if (ctrl && paper.IsKeyPressed(PaperKey.V)) { PasteAtPlayhead(); return; }
        if (ctrl && paper.IsKeyPressed(PaperKey.A)) { SelectAllKeys(); return; }

        // No Ctrl+S here on purpose: the project-save hook already covers it, and handling it twice
        // would write and reimport the asset twice in one frame.
        if (ctrl) return; // don't let Ctrl+F, Ctrl+K and friends fall through to the bare-letter set

        if (paper.IsKeyPressed(PaperKey.Space)) { TogglePlay(); return; }
        if (paper.IsKeyPressedOrRepeating(PaperKey.Left)) { StepFrames(-1); return; }
        if (paper.IsKeyPressedOrRepeating(PaperKey.Right)) { StepFrames(1); return; }
        if (paper.IsKeyPressed(PaperKey.F)) { FrameAll(); return; }
        if (paper.IsKeyPressed(PaperKey.K)) { KeyAllVisible(); return; }
        if (paper.IsKeyPressed(PaperKey.Escape)) _selection.Clear();
    }

    private bool PointerOverSheet(Paper paper) => _bodyRect.Contains(paper.PointerPos);

    private void SelectAllKeys()
    {
        _selection.Clear();
        foreach (var row in _rows)
        {
            if (row.Curve == null) continue;
            for (int i = 0; i < row.Curve.Count; i++)
                _selection.Add(new KeyRef { Curve = row.Curve, Time = row.Curve[i].Time });
        }
    }

    // ============================================================
    //  Toolbars
    // ============================================================

    private void DrawToolbar(Paper paper, Prowl.Scribe.FontFile font)
    {
        var clip = _clip!;

        // One height for everything in the strip; buttons are the only Origami control that does not
        // pick up Metrics.RowHeight on its own (see EditorGUIMetrics).
        float h = EditorGUIMetrics.ToolbarControlHeight;

        using (paper.Row("clip_tb").Width(UnitValue.Stretch()).Height(ToolbarHeight)
            .BackgroundColor(EditorTheme.Neutral200)
            .Padding(8, 8, EditorGUIMetrics.ToolbarPadY, EditorGUIMetrics.ToolbarPadY)
            .Gap(6).Enter())
        {
            Origami.Button(paper, "clip_tb_start", "|◀", () => SetTime(ClipStart)).Subtle().Width(30).Height(h).Show();
            Origami.Button(paper, "clip_tb_prev", "◀", () => StepFrames(-1)).Subtle().Width(30).Height(h).Show();
            Origami.Button(paper, "clip_tb_play", _playing ? "❚❚" : "▶", TogglePlay)
                .Width(38).Height(h).Show();
            Origami.Button(paper, "clip_tb_next", "▶", () => StepFrames(1)).Subtle().Width(30).Height(h).Show();
            Origami.Button(paper, "clip_tb_end", "▶|", () => SetTime(ClipEnd)).Subtle().Width(30).Height(h).Show();

            Origami.Checkbox(paper, "clip_tb_loop", _loopPlayback, v => _loopPlayback = v)
                .LabelRight("Loop").Show();

            paper.Box("clip_tb_gap1").Width(10).Height(h).IsNotInteractable();

            paper.Box("clip_tb_flbl").Width(38).Height(h).IsNotInteractable()
                .Text("Frame", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            using (paper.Box("clip_tb_frame").Width(64).Height(h).Enter())
                Origami.NumericField(paper, "clip_tb_frame_v", CurrentFrame,
                    v => SetTime(ClipStart + v / MathF.Max(1, _frameRate))).Show();

            paper.Box("clip_tb_tlbl").Width(90).Height(h).IsNotInteractable()
                .Text($"{_time - ClipStart:F3}s / {clip.Duration:F3}s", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            paper.Box("clip_tb_spacer").Width(UnitValue.Stretch()).Height(h).IsNotInteractable();

            Origami.Checkbox(paper, "clip_tb_snap", _snapToFrames, v => _snapToFrames = v)
                .LabelRight("Snap").Show();

            Origami.Button(paper, "clip_tb_mode", _curveMode ? "Curves" : "Dope Sheet",
                () => _curveMode = !_curveMode).Subtle().Width(92).Height(h).Show();

            // A clip embedded in a model has no file to write back to. Saying so up front - and giving
            // the one action that fixes it - beats a Save button that only ever logs a warning.
            bool writable = AnimationClipIO.WritablePath(clip) != null;

            paper.Box("clip_tb_status").Width(writable ? 70 : 110).Height(h).IsNotInteractable()
                .Text(writable ? (_dirty ? "● unsaved" : "✓ saved") : "read-only sub-asset", font)
                .TextColor(!writable ? EditorTheme.Amber400 : _dirty ? EditorTheme.Amber400 : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight);

            if (writable)
                Origami.Button(paper, "clip_tb_save", "Save", Save).Width(60).Height(h).Show();
            else
                Origami.Button(paper, "clip_tb_extract", "Extract…", ExtractClip).Width(74).Height(h).Show();
        }
    }

    private void DrawClipBar(Paper paper, Prowl.Scribe.FontFile font)
    {
        var clip = _clip!;

        float h = EditorGUIMetrics.ToolbarControlHeight;

        using (paper.Row("clip_cb").Width(UnitValue.Stretch()).Height(SecondToolbarHeight)
            .BackgroundColor(EditorTheme.Neutral300)
            .Padding(8, 8, EditorGUIMetrics.ToolbarPadYCompact, EditorGUIMetrics.ToolbarPadYCompact)
            .Gap(6).Enter())
        {
            paper.Box("clip_cb_llbl").Width(44).Height(h).IsNotInteractable()
                .Text("Length", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            using (paper.Box("clip_cb_len").Width(70).Height(h).Enter())
                Origami.NumericField(paper, "clip_cb_len_v", clip.Duration, v =>
                {
                    clip.Duration = MathF.Max(0.001f, v);
                    MarkDirty();
                }).Show();

            paper.Box("clip_cb_rlbl").Width(64).Height(h).IsNotInteractable()
                .Text("Sample Rate", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            using (paper.Box("clip_cb_rate").Width(56).Height(h).Enter())
                Origami.NumericField(paper, "clip_cb_rate_v", _frameRate, v =>
                {
                    _frameRate = Math.Clamp(v, 1, 240);
                    clip.TicksPerSecond = _frameRate;
                    MarkDirty();
                }).Show();

            paper.Box("clip_cb_wlbl").Width(36).Height(h).IsNotInteractable()
                .Text("Wrap", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            using (paper.Box("clip_cb_wrap").Width(96).Height(h).Enter())
                Origami.EnumDropdown(paper, "clip_cb_wrap_v", clip.Wrap, v => { clip.Wrap = v; MarkDirty(); }).Show();

            // Authoring a clip from scratch was impossible before this: the sheet only ever listed
            // curves that already existed, so a clip created from the asset menu opened empty and
            // stayed empty forever. This is the way in.
            Origami.Button(paper, "clip_cb_addprop", "+ Property",
                () => OpenAddPropertyMenu(paper.PointerPos)).Width(88).Height(h).Show();

            Origami.Button(paper, "clip_cb_fit", "Fit To Keys", FitLengthToKeys).Subtle().Width(86).Height(h).Show();
            Origami.Button(paper, "clip_cb_rev", "Reverse", ReverseClip).Subtle().Width(72).Height(h).Show();

            paper.Box("clip_cb_spacer").Width(UnitValue.Stretch()).Height(h).IsNotInteractable();

            Origami.Checkbox(paper, "clip_cb_prev", _previewEnabled, v =>
            {
                _previewEnabled = v;
                if (!v) _preview.Release();
            }).LabelRight("Preview").Show();

            paper.Box("clip_cb_target").Width(150).Height(h).IsNotInteractable()
                .Text(PreviewStatus(), font)
                .TextColor(_preview.IsBound ? EditorTheme.Green400 : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight);

            using (paper.Box("clip_cb_search").Width(150).Height(h).Enter())
                Origami.TextField(paper, "clip_cb_search_v", _search, v => { _search = v; _rowsStale = true; })
                    .Placeholder("Filter properties").Show();
        }
    }

    private string PreviewStatus()
    {
        if (!_previewEnabled) return "preview off";
        if (!_preview.IsBound) return "select a rig";

        string name = _previewTarget.IsValid() ? _previewTarget!.Name : "previewing";
        int missing = _preview.UnresolvedBoneCount;
        return missing > 0 ? $"{name}  ({missing} missing)" : name;
    }

    // ============================================================
    //  Sheet (ruler + gutter + tracks), drawn as one canvas
    // ============================================================

    private void DrawSheet(Paper paper, Prowl.Scribe.FontFile font, float windowHeight)
    {
        EnsureRows();

        // Resolved here rather than inside the paint pass: draw callbacks are deferred to the end of
        // the frame, and the empty-state text should not be reaching into editor selection from there.
        _hasRigForAuthoring = AddPropertyRoot() != null;

        using (paper.Box("clip_sheet").Width(UnitValue.Stretch()).Height(UnitValue.Stretch()).Clip()
            .OnScroll(HandleScroll)
            .OnPress(HandlePress)
            .OnRelease(_ => _pressedButton = PaperMouseBtn.Unknown)
            .OnClick(HandleClick)
            .OnRightClick(HandleRightClick)
            .OnDragStart(HandleDragStart)
            .OnDragging(HandleDragging)
            .OnDragEnd(HandleDragEnd)
            .OnPostLayout((handle, rect) =>
            {
                _bodyRect = rect;
                paper.Draw(ref handle, (canvas, r) => RenderSheet(canvas, r, font));
            })
            .Enter())
        {
        }
    }

    private void RenderSheet(Canvas canvas, Rect rect, Prowl.Scribe.FontFile font)
    {
        // Paper defers draw callbacks to EndFrame, so this runs after OnGUI returned - long enough
        // for a project save to have reimported (and disposed) the clip we resolved at frame start.
        if (_clip.IsNotValid()) return;

        float x0 = (float)rect.Min.X, y0 = (float)rect.Min.Y;
        float w = (float)rect.Size.X, h = (float)rect.Size.Y;
        float trackX = x0 + GutterWidth;
        float trackW = MathF.Max(1f, w - GutterWidth);
        float tracksTop = y0 + RulerHeight;

        canvas.RectFilled(x0, y0, w, h, Rgb(EditorTheme.Neutral100));

        // A clip with no tracks used to draw as a blank grid with no explanation, which is the state a
        // freshly created .anim opens in. Say what to do instead - but keep the ruler and playhead so
        // the playhead is already where you want it when you do add a track.
        if (_rows.Count == 0)
        {
            DrawTimeGrid(canvas, trackX, tracksTop, trackW, h - RulerHeight, font);
            DrawEmptyMessage(canvas, trackX, tracksTop, trackW, h - RulerHeight, font);
            DrawRuler(canvas, trackX, y0, trackW, font);
            DrawPlayhead(canvas, trackX, y0, trackW, h);
            return;
        }

        DrawTimeGrid(canvas, trackX, tracksTop, trackW, h - RulerHeight, font);

        if (_curveMode) DrawCurveArea(canvas, trackX, tracksTop, trackW, h - RulerHeight);
        else DrawDopeArea(canvas, trackX, tracksTop, trackW, h - RulerHeight);

        DrawGutter(canvas, x0, tracksTop, GutterWidth, h - RulerHeight, font);
        DrawRuler(canvas, trackX, y0, trackW, font);
        DrawPlayhead(canvas, trackX, y0, trackW, h);

        if (_drag == DragKind.Marquee)
        {
            float mx = MathF.Min(_dragOrigin.X, _dragCurrent.X), my = MathF.Min(_dragOrigin.Y, _dragCurrent.Y);
            float mw = MathF.Abs(_dragCurrent.X - _dragOrigin.X), mh = MathF.Abs(_dragCurrent.Y - _dragOrigin.Y);
            canvas.RectFilled(mx, my, mw, mh, new Color32(120, 170, 255, 40));
            canvas.BeginPath();
            canvas.Rect(mx, my, mw, mh);
            canvas.SetStrokeColor(new Color32(140, 190, 255, 200));
            canvas.SetStrokeWidth(1f);
            canvas.Stroke();
        }
    }

    private void DrawEmptyMessage(Canvas canvas, float x, float y, float w, float h, Prowl.Scribe.FontFile font)
    {
        bool filtered = _search.Trim().Length > 0 && _clip!.Bones.Count > 0;
        bool hasRig = _hasRigForAuthoring;

        string headline = filtered
            ? "No property matches the filter."
            : "This clip animates nothing yet.";

        string hint = filtered
            ? "Clear the filter box to see the clip's tracks."
            : hasRig
                ? "Use + Property to pick a bone from the selected character."
                : "Select the character in the scene, then use + Property to pick a bone.";

        float cx = x + w * 0.5f;
        float cy = y + h * 0.5f;
        canvas.DrawText(headline, cx - headline.Length * 3.4f, cy - 14f, Rgb(EditorTheme.Ink500), 13f, font);
        canvas.DrawText(hint, cx - hint.Length * 2.7f, cy + 6f, Rgb(EditorTheme.Ink300), 11f, font);
    }

    // ── Ruler ───────────────────────────────────────────────────────────────

    private void DrawRuler(Canvas canvas, float x, float y, float w, Prowl.Scribe.FontFile font)
    {
        canvas.RectFilled(x, y, w, RulerHeight, Rgb(EditorTheme.Neutral300));

        float step = ChooseTickStep();
        float first = MathF.Floor(_viewStart / step) * step;

        for (float t = first; ; t += step)
        {
            float px = TimeToX(t);
            if (px < x - 1f) continue;
            if (px > x + w) break;

            canvas.BeginPath();
            canvas.MoveTo(px, y + RulerHeight - 8f);
            canvas.LineTo(px, y + RulerHeight);
            canvas.SetStrokeColor(Rgb(EditorTheme.Ink300));
            canvas.SetStrokeWidth(1f);
            canvas.Stroke();

            int frame = (int)MathF.Round((t - ClipStart) * _frameRate);
            canvas.DrawText(frame.ToString(), px + 3f, y + 4f, Rgb(EditorTheme.Ink300), 10f, font);
        }

        // Clip end marker: everything past the clip's length is dead space.
        float endX = TimeToX(ClipEnd);
        if (endX < x + w)
            canvas.RectFilled(MathF.Max(endX, x), y, x + w - MathF.Max(endX, x), RulerHeight, new Color32(0, 0, 0, 60));
    }

    /// <summary>Pick a tick spacing that keeps labels from colliding at the current zoom.</summary>
    private float ChooseTickStep()
    {
        float frame = 1f / MathF.Max(1, _frameRate);
        float[] candidates = { frame, frame * 5f, frame * 10f, 0.5f, 1f, 2f, 5f, 10f, 30f, 60f };
        foreach (float c in candidates)
            if (c * _pixelsPerSecond >= 46f) return c;
        return candidates[^1];
    }

    private void DrawTimeGrid(Canvas canvas, float x, float y, float w, float h, Prowl.Scribe.FontFile font)
    {
        // Past-the-end shading, so the clip's length is visible in the sheet and not just the ruler.
        float endX = TimeToX(ClipEnd);
        if (endX < x + w)
            canvas.RectFilled(MathF.Max(endX, x), y, x + w - MathF.Max(endX, x), h, new Color32(0, 0, 0, 45));

        float step = ChooseTickStep();
        float first = MathF.Floor(_viewStart / step) * step;

        for (float t = first; ; t += step)
        {
            float px = TimeToX(t);
            if (px < x - 1f) continue;
            if (px > x + w) break;

            canvas.BeginPath();
            canvas.MoveTo(px, y);
            canvas.LineTo(px, y + h);
            canvas.SetStrokeColor(new Color32(255, 255, 255, 14));
            canvas.SetStrokeWidth(1f);
            canvas.Stroke();
        }
    }

    // ── Gutter (track names) ────────────────────────────────────────────────

    private void DrawGutter(Canvas canvas, float x, float y, float w, float h, Prowl.Scribe.FontFile font)
    {
        canvas.SaveState();
        canvas.IntersectScissor(x, y, w, h);
        canvas.RectFilled(x, y, w, h, Rgb(EditorTheme.Neutral200));

        for (int i = 0; i < _rows.Count; i++)
        {
            float top = RowTop(i);
            if (top + RowHeight < y || top > y + h) continue;

            var row = _rows[i];
            if (i % 2 == 1) canvas.RectFilled(x, top, w, RowHeight, new Color32(255, 255, 255, 6));

            float textX = x + (row.IsGroup ? 20f : 34f);

            if (row.IsGroup)
            {
                // Disclosure triangle.
                float cx = x + 9f, cy = top + RowHeight * 0.5f;
                canvas.BeginPath();
                if (row.Collapsed)
                {
                    canvas.MoveTo(cx - 3f, cy - 5f);
                    canvas.LineTo(cx + 4f, cy);
                    canvas.LineTo(cx - 3f, cy + 5f);
                }
                else
                {
                    canvas.MoveTo(cx - 5f, cy - 3f);
                    canvas.LineTo(cx + 5f, cy - 3f);
                    canvas.LineTo(cx, cy + 4f);
                }
                canvas.ClosePath();
                canvas.SetFillColor(Rgb(EditorTheme.Ink300));
                canvas.Fill();
            }
            else
            {
                canvas.CircleFilled(x + 24f, top + RowHeight * 0.5f, 3.5f, Rgb(row.Accent), 10);
            }

            Color32 text = row.Resolved ? Rgb(EditorTheme.Ink500) : Rgb(EditorTheme.Red400);
            canvas.DrawText(row.Label, textX, top + 5f, text, row.IsGroup ? 12f : 11.5f, font);

            if (!string.IsNullOrEmpty(row.Detail))
                canvas.DrawText(row.Detail, x + w - 46f, top + 6f, Rgb(EditorTheme.Ink300), 10f, font);
        }

        // Divider between gutter and timeline.
        canvas.BeginPath();
        canvas.MoveTo(x + w, y - RulerHeight);
        canvas.LineTo(x + w, y + h);
        canvas.SetStrokeColor(Rgb(EditorTheme.Neutral400));
        canvas.SetStrokeWidth(1f);
        canvas.Stroke();

        canvas.RestoreState();
    }

    // ── Dope sheet ──────────────────────────────────────────────────────────

    private void DrawDopeArea(Canvas canvas, float x, float y, float w, float h)
    {
        canvas.SaveState();
        canvas.IntersectScissor(x, y, w, h);

        for (int i = 0; i < _rows.Count; i++)
        {
            float top = RowTop(i);
            if (top + RowHeight < y || top > y + h) continue;

            var row = _rows[i];
            if (i % 2 == 1) canvas.RectFilled(x, top, w, RowHeight, new Color32(255, 255, 255, 5));

            float cy = top + RowHeight * 0.5f;

            if (row.IsGroup)
            {
                foreach (float t in MergedKeyTimes(row))
                    DrawDiamond(canvas, TimeToX(t), cy, 4.2f, Rgb(EditorTheme.Ink300), false);
            }
            else if (row.Curve != null)
            {
                for (int k = 0; k < row.Curve.Count; k++)
                {
                    Keyframe key = row.Curve[k];
                    bool selected = IsSelected(row.Curve, key.Time);
                    Color32 color = selected ? Rgb(EditorTheme.Amber400) : Rgb(row.Accent);
                    DrawDiamond(canvas, TimeToX(key.Time), cy, selected ? 5.6f : 4.6f, color,
                        key.Interpolation == CurveInterpolation.Step);
                }
            }
        }

        canvas.RestoreState();
    }

    private static void DrawDiamond(Canvas canvas, float cx, float cy, float r, Color32 color, bool stepped)
    {
        canvas.BeginPath();
        if (stepped)
        {
            // Stepped keys read as squares, so constant segments are visible at a glance.
            canvas.MoveTo(cx - r, cy - r);
            canvas.LineTo(cx + r, cy - r);
            canvas.LineTo(cx + r, cy + r);
            canvas.LineTo(cx - r, cy + r);
        }
        else
        {
            canvas.MoveTo(cx, cy - r);
            canvas.LineTo(cx + r, cy);
            canvas.LineTo(cx, cy + r);
            canvas.LineTo(cx - r, cy);
        }
        canvas.ClosePath();
        canvas.SetFillColor(color);
        canvas.Fill();
    }

    // ── Curve view ──────────────────────────────────────────────────────────

    private void DrawCurveArea(Canvas canvas, float x, float y, float w, float h)
    {
        canvas.SaveState();
        canvas.IntersectScissor(x, y, w, h);

        var curves = _rows.Where(r => !r.IsGroup && r.Curve != null && r.Curve.Count > 0).ToList();
        if (curves.Count == 0)
        {
            canvas.RestoreState();
            return;
        }

        // Auto-fit the value axis across every component of everything visible.
        float min = float.MaxValue, max = float.MinValue;
        foreach (var row in curves)
        {
            var curve = row.Curve!;
            for (int i = 0; i < curve.Count; i++)
            {
                Float4 v = curve[i].Value4;
                for (int c = 0; c < curve.Dimension; c++)
                {
                    min = MathF.Min(min, v[c]);
                    max = MathF.Max(max, v[c]);
                }
            }
        }
        if (max - min < 1e-4f) { min -= 0.5f; max += 0.5f; }
        float pad = (max - min) * 0.12f;
        min -= pad; max += pad;

        float ValueToY(float v) => y + h - (v - min) / (max - min) * h;

        // Zero line and value labels.
        if (min < 0f && max > 0f)
        {
            float zy = ValueToY(0f);
            canvas.BeginPath();
            canvas.MoveTo(x, zy);
            canvas.LineTo(x + w, zy);
            canvas.SetStrokeColor(new Color32(255, 255, 255, 30));
            canvas.SetStrokeWidth(1f);
            canvas.Stroke();
        }

        float endX = TimeToX(ClipEnd);

        // One line per component, not per channel: a rotation drawn as a single line would be three
        // quarters of the data thrown away, and the component colours are what make a curve readable.
        foreach (var row in curves)
        {
            var curve = row.Curve!;
            int dim = curve.Dimension;

            for (int c = 0; c < dim; c++)
            {
                Color32 color = Rgb(dim == 1 ? row.Accent : ComponentColors[Math.Min(c, ComponentColors.Length - 1)]);

                // Polyline sampled per pixel: exact enough to read, and cheap.
                canvas.BeginPath();
                bool started = false;
                for (float px = x; px <= MathF.Min(x + w, endX); px += 2f)
                {
                    float py = ValueToY(curve.EvaluateComponent(c, XToTime(px)));
                    if (!started) { canvas.MoveTo(px, py); started = true; }
                    else canvas.LineTo(px, py);
                }
                if (started)
                {
                    canvas.SetStrokeColor(color);
                    canvas.SetStrokeWidth(1.6f);
                    canvas.Stroke();
                }

                for (int k = 0; k < curve.Count; k++)
                {
                    Keyframe key = curve[k];
                    bool selected = IsSelected(curve, key.Time);
                    canvas.CircleFilled(TimeToX(key.Time), ValueToY(key.Value4[c]),
                        selected ? 5f : 3.5f, selected ? Rgb(EditorTheme.Amber400) : color, 12);
                }
            }
        }

        canvas.RestoreState();
    }

    // ── Playhead ────────────────────────────────────────────────────────────

    private void DrawPlayhead(Canvas canvas, float trackX, float y, float trackW, float h)
    {
        float px = TimeToX(_time);
        if (px < trackX - 1f || px > trackX + trackW) return;

        canvas.BeginPath();
        canvas.MoveTo(px, y);
        canvas.LineTo(px, y + h);
        canvas.SetStrokeColor(Rgb(EditorTheme.Red400));
        canvas.SetStrokeWidth(1.5f);
        canvas.Stroke();

        canvas.BeginPath();
        canvas.MoveTo(px - 6f, y);
        canvas.LineTo(px + 6f, y);
        canvas.LineTo(px, y + 9f);
        canvas.ClosePath();
        canvas.SetFillColor(Rgb(EditorTheme.Red400));
        canvas.Fill();
    }

    // ============================================================
    //  Rows
    // ============================================================

    private void EnsureRows()
    {
        if (!_rowsStale) return;
        _rowsStale = false;
        RebuildRows();
    }

    private void RebuildRows()
    {
        _rows.Clear();
        var clip = _clip!;
        string filter = _search.Trim();

        // Resolved-bone lookup once per rebuild instead of a linear scan per bone.
        HashSet<string>? unresolved = null;
        if (_preview.IsBound)
        {
            unresolved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, resolved) in _preview.EnumerateBones())
                if (!resolved) unresolved.Add(path);
        }

        foreach (var bone in clip.Bones)
        {
            var channelRows = new List<TrackRow>();
            foreach (var channel in Channels)
            {
                var curve = channel.Get(bone);
                if (curve == null) continue;
                if (filter.Length > 0
                    && bone.BoneName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && channel.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var captured = channel;
                var capturedBone = bone;
                channelRows.Add(new TrackRow
                {
                    GroupKey = bone.BoneName,
                    Label = channel.Name,
                    Curve = curve,
                    Accent = channel.Accent,
                    Dimension = curve.Dimension,
                    Components = channel.Components,
                    Detail = curve.Count.ToString(),
                    Remove = () => { captured.Set(capturedBone, null); MarkDirty(); _preview.Rebuild(); },
                });
            }

            if (channelRows.Count == 0) continue;

            bool collapsed = _collapsed.Contains(bone.BoneName);
            var group = new TrackRow
            {
                GroupKey = bone.BoneName,
                Label = ShortBoneName(bone.BoneName),
                Detail = channelRows.Count.ToString(),
                IsGroup = true,
                Collapsed = collapsed,
                Resolved = unresolved == null || !unresolved.Contains(bone.BoneName),
            };
            foreach (var row in channelRows)
                if (row.Curve != null) group.Merged.Add(row.Curve);

            _rows.Add(group);
            if (!collapsed) _rows.AddRange(channelRows);
        }

        foreach (var shape in clip.BlendShapes)
        {
            if (shape.Weight == null) continue;
            string label = string.IsNullOrEmpty(shape.Path) ? shape.ShapeName : $"{shape.Path}/{shape.ShapeName}";
            if (filter.Length > 0 && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            var captured = shape;
            _rows.Add(new TrackRow
            {
                GroupKey = "__blendshapes",
                Label = label,
                Curve = shape.Weight,
                Accent = SColor.MediumPurple,
                Dimension = shape.Weight.Dimension,
                Components = "W",
                Detail = shape.Weight.Count.ToString(),
                Remove = () => { _clip!.BlendShapes.Remove(captured); MarkDirty(); _preview.Rebuild(); },
            });
        }
    }

    private static string ShortBoneName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    private IEnumerable<float> MergedKeyTimes(TrackRow group)
    {
        var seen = new HashSet<int>();
        foreach (var curve in group.Merged)
        {
            for (int i = 0; i < curve.Count; i++)
            {
                float t = curve[i].Time;
                int bucket = (int)MathF.Round(t * 10000f);
                if (seen.Add(bucket)) yield return t;
            }
        }
    }

    // ============================================================
    //  Coordinate mapping
    // ============================================================

    private float TimeToX(float t) => (float)_bodyRect.Min.X + GutterWidth + (t - _viewStart) * _pixelsPerSecond;
    private float XToTime(float x) => _viewStart + (x - (float)_bodyRect.Min.X - GutterWidth) / _pixelsPerSecond;
    private float RowTop(int index) => (float)_bodyRect.Min.Y + RulerHeight + index * RowHeight - _scrollY;

    private int RowAt(float y)
    {
        int index = (int)MathF.Floor((y - (float)_bodyRect.Min.Y - RulerHeight + _scrollY) / RowHeight);
        return index >= 0 && index < _rows.Count ? index : -1;
    }

    /// <summary>
    /// First and last second the clip covers. A clip cut out of a longer shared timeline - which is
    /// what a take embedded in a model usually is - has its first key at <see cref="AnimationClip.StartTime"/>,
    /// not at zero. The window used to run its ruler from 0 regardless, so such a clip opened showing
    /// an empty sheet with every key off the right-hand edge.
    /// </summary>
    private float ClipStart => _clip.IsValid() ? _clip!.StartTime : 0f;
    private float ClipEnd => ClipStart + (_clip.IsValid() ? MathF.Max(_clip!.Duration, 0f) : 0f);

    private bool InGutter(float x) => x < (float)_bodyRect.Min.X + GutterWidth;
    private bool InRuler(float y) => y < (float)_bodyRect.Min.Y + RulerHeight;

    private int CurrentFrame => (int)MathF.Round((_time - ClipStart) * _frameRate);

    private float Snap(float t)
    {
        if (!_snapToFrames) return t;
        float start = ClipStart;
        return start + MathF.Round((t - start) * _frameRate) / _frameRate;
    }

    // ============================================================
    //  Input
    // ============================================================

    private void HandleScroll(ScrollEvent e)
    {
        if (Input.IsCtrlPressed)
        {
            // Zoom around the cursor so the frame under the mouse stays put.
            float anchorTime = XToTime(e.PointerPosition.X);
            _pixelsPerSecond = Math.Clamp(_pixelsPerSecond * MathF.Exp(e.Delta * 0.14f),
                MinPixelsPerSecond, MaxPixelsPerSecond);
            _viewStart = anchorTime - (e.PointerPosition.X - (float)_bodyRect.Min.X - GutterWidth) / _pixelsPerSecond;
        }
        else if (Input.IsShiftPressed)
        {
            _viewStart -= e.Delta * 60f / _pixelsPerSecond;
        }
        else
        {
            _scrollY = Math.Clamp(_scrollY - e.Delta * 40f, 0f, MaxScrollY());
        }
    }

    /// <summary>
    /// How far the track list can scroll: content height minus what fits. Clamping against the row
    /// count alone (as it did) let a short clip scroll its only tracks straight off the top.
    /// </summary>
    private float MaxScrollY()
    {
        float viewport = MathF.Max(0f, (float)_bodyRect.Size.Y - RulerHeight);
        return MathF.Max(0f, _rows.Count * RowHeight - viewport);
    }

    private void HandlePress(ClickEvent e)
    {
        _pressedButton = e.Button;
    }

    private void HandleClick(ClickEvent e)
    {
        if (e.Button != PaperMouseBtn.Left) return;

        Float2 p = e.PointerPosition;

        if (InRuler(p.Y)) { SetTime(Snap(XToTime(p.X))); return; }

        if (InGutter(p.X))
        {
            int row = RowAt(p.Y);
            if (row >= 0 && _rows[row].IsGroup) ToggleGroup(_rows[row].GroupKey);
            return;
        }

        var hit = HitKey(p);
        if (hit != null)
        {
            if (Input.IsShiftPressed || Input.IsCtrlPressed) ToggleSelection(hit);
            else { _selection.Clear(); _selection.Add(hit); }
            return;
        }

        _selection.Clear();
    }

    private void HandleRightClick(ClickEvent e)
    {
        Float2 p = e.PointerPosition;
        int row = RowAt(p.Y);
        float time = Snap(XToTime(p.X));

        Origami.ContextMenu(p.X, p.Y, menu =>
        {
            var hit = HitKey(p);
            if (hit != null && !IsSelectedRef(hit)) { _selection.Clear(); _selection.Add(hit); }

            if (_selection.Count > 0)
            {
                menu.Title($"{_selection.Count} key{(_selection.Count == 1 ? "" : "s")}");
                menu.Item("Delete", DeleteSelection);
                menu.Item("Copy", CopySelection);
                menu.Separator();
                menu.Item("Linear", () => SetSelectionInterpolation(CurveInterpolation.Linear));
                menu.Item("Stepped", () => SetSelectionInterpolation(CurveInterpolation.Step));
                menu.Item("Cubic", () => SetSelectionInterpolation(CurveInterpolation.CubicSpline));
                menu.Item("Auto Tangents", SmoothSelectionTangents);
                menu.Item("Flatten Tangents", FlattenSelectionTangents);
                menu.Separator();
            }

            if (_clipboard.Count > 0)
                menu.Item("Paste At Playhead", PasteAtPlayhead);

            if (row >= 0 && !InGutter(p.X))
            {
                var target = _rows[row];
                if (target.Curve != null)
                    menu.Item("Add Key Here", () => AddKey(target.Curve, time));
            }

            menu.Item("Key All Visible At Playhead", KeyAllVisible);

            if (row >= 0)
            {
                var target = _rows[row];
                menu.Separator();

                if (target.IsGroup)
                {
                    menu.Item(target.Collapsed ? "Expand" : "Collapse", () => ToggleGroup(target.GroupKey));

                    // Channel groups are added per-bone here rather than only wholesale, so a bone that
                    // only needs rotation does not have to carry position and scale curves as well.
                    string bonePath = target.GroupKey;
                    menu.Submenu("Add Channels", sub =>
                    {
                        sub.Item("Position", () => AddTransformTrack(bonePath, "Position"));
                        sub.Item("Rotation", () => AddTransformTrack(bonePath, "Rotation"));
                        sub.Item("Scale", () => AddTransformTrack(bonePath, "Scale"));
                        sub.Separator();
                        sub.Item("All", () => AddTransformTrack(bonePath));
                    });
                    menu.Item($"Remove {target.Label}", () => RemoveBoneTrack(bonePath));
                }
                else if (target.Remove != null)
                {
                    menu.Item($"Remove {target.Label}", () => { target.Remove(); _selection.Clear(); });
                }
            }

            menu.Separator();
            menu.Item("Add Property…", () => _pendingAddPropertyMenu = p);
            menu.Item("Frame All", FrameAll);
        });
    }

    private void HandleDragStart(DragEvent e)
    {
        _dragOrigin = e.StartPosition;
        _dragCurrent = e.PointerPosition;

        if (_pressedButton == PaperMouseBtn.Middle)
        {
            _drag = DragKind.PanView;
            return;
        }

        if (_pressedButton != PaperMouseBtn.Left) { _drag = DragKind.None; return; }

        if (InRuler(e.StartPosition.Y)) { _drag = DragKind.Playhead; SetTime(Snap(XToTime(e.PointerPosition.X))); return; }
        if (InGutter(e.StartPosition.X)) { _drag = DragKind.None; return; }

        var hit = HitKey(e.StartPosition);
        if (hit != null)
        {
            if (!IsSelectedRef(hit))
            {
                if (!Input.IsShiftPressed && !Input.IsCtrlPressed) _selection.Clear();
                _selection.Add(hit);
            }
            foreach (var key in _selection) key.DragStartTime = key.Time;
            _dragStartTime = XToTime(e.StartPosition.X);
            _drag = DragKind.Keys;
            return;
        }

        _drag = DragKind.Marquee;
    }

    private void HandleDragging(DragEvent e)
    {
        _dragCurrent = e.PointerPosition;

        switch (_drag)
        {
            case DragKind.Playhead:
                SetTime(Snap(XToTime(e.PointerPosition.X)));
                break;

            case DragKind.PanView:
                _viewStart -= e.Delta.X / _pixelsPerSecond;
                _scrollY = Math.Clamp(_scrollY - e.Delta.Y, 0f, MaxScrollY());
                break;

            case DragKind.Keys:
            {
                float delta = XToTime(e.PointerPosition.X) - _dragStartTime;
                MoveSelection(delta);
                break;
            }
        }
    }

    private void HandleDragEnd(DragEvent e)
    {
        if (_drag == DragKind.Marquee) ApplyMarquee();
        if (_drag == DragKind.Keys) MarkDirty();

        _drag = DragKind.None;
        _pressedButton = PaperMouseBtn.Unknown;
    }

    private KeyRef? HitKey(Float2 p)
    {
        int row = RowAt(p.Y);
        if (row < 0) return null;

        var track = _rows[row];
        if (track.Curve == null) return null;

        float best = float.MaxValue;
        KeyRef? found = null;
        for (int i = 0; i < track.Curve.Count; i++)
        {
            float t = track.Curve[i].Time;
            float dx = MathF.Abs(TimeToX(t) - p.X);
            if (dx <= 7f && dx < best)
            {
                best = dx;
                found = new KeyRef { Curve = track.Curve, Time = t };
            }
        }
        return found;
    }

    private void ApplyMarquee()
    {
        float minX = MathF.Min(_dragOrigin.X, _dragCurrent.X), maxX = MathF.Max(_dragOrigin.X, _dragCurrent.X);
        float minY = MathF.Min(_dragOrigin.Y, _dragCurrent.Y), maxY = MathF.Max(_dragOrigin.Y, _dragCurrent.Y);

        if (!Input.IsShiftPressed && !Input.IsCtrlPressed) _selection.Clear();

        float t0 = XToTime(minX), t1 = XToTime(maxX);

        for (int i = 0; i < _rows.Count; i++)
        {
            var track = _rows[i];
            if (track.Curve == null) continue;

            float cy = RowTop(i) + RowHeight * 0.5f;
            if (cy < minY || cy > maxY) continue;

            for (int k = 0; k < track.Curve.Count; k++)
            {
                float t = track.Curve[k].Time;
                if (t < t0 || t > t1) continue;
                if (!IsSelected(track.Curve, t))
                    _selection.Add(new KeyRef { Curve = track.Curve, Time = t });
            }
        }
    }

    // ============================================================
    //  Selection helpers
    // ============================================================

    private bool IsSelected(AnimationCurve curve, float time)
    {
        foreach (var key in _selection)
            if (ReferenceEquals(key.Curve, curve) && MathF.Abs(key.Time - time) <= KeyEpsilon) return true;
        return false;
    }

    private bool IsSelectedRef(KeyRef reference) => IsSelected(reference.Curve, reference.Time);

    private void ToggleSelection(KeyRef reference)
    {
        for (int i = 0; i < _selection.Count; i++)
            if (ReferenceEquals(_selection[i].Curve, reference.Curve)
                && MathF.Abs(_selection[i].Time - reference.Time) <= KeyEpsilon)
            {
                _selection.RemoveAt(i);
                return;
            }
        _selection.Add(reference);
    }

    private static int IndexOfKeyAt(AnimationCurve curve, float time)
    {
        for (int i = 0; i < curve.Count; i++)
            if (MathF.Abs(curve[i].Time - time) <= KeyEpsilon) return i;
        return -1;
    }

    /// <summary>
    /// The value every component of <paramref name="curve"/> holds at <paramref name="time"/>, as the
    /// seed for a new key. Keying a channel at a time it already covers must not change the pose, so a
    /// new key starts at whatever the curve already evaluates to there.
    /// </summary>
    private static Float4 EvaluateAll(AnimationCurve curve, float time, Float4 fallback = default)
    {
        if (curve.Count == 0) return fallback;

        Float4 value = default;
        for (int c = 0; c < curve.Dimension; c++)
            value[c] = curve.EvaluateComponent(c, time);
        return value;
    }

    /// <summary>
    /// Interpolation a newly inserted key should take: whatever its neighbour to the left uses, so
    /// keying in the middle of a stepped run does not silently punch a smooth segment through it.
    /// </summary>
    private static CurveInterpolation InterpolationNear(AnimationCurve curve, float time)
    {
        CurveInterpolation mode = CurveInterpolation.Linear;
        for (int i = 0; i < curve.Count && curve[i].Time <= time; i++)
            mode = curve[i].Interpolation;
        if (curve.Count > 0 && curve[0].Time > time) mode = curve[0].Interpolation;
        return mode;
    }

    // ============================================================
    //  Key operations
    // ============================================================

    /// <summary>
    /// Move the whole selection by <paramref name="deltaSeconds"/> from where the drag started.
    /// Keys carry a read-only position, so each one is lifted out and re-inserted; anything an
    /// unselected key collides with is overwritten, which is the behaviour a dope sheet implies.
    /// </summary>
    private void MoveSelection(float deltaSeconds)
    {
        if (_selection.Count == 0) return;

        var byCurve = _selection.GroupBy(k => k.Curve, ReferenceEqualityComparer.Instance);

        foreach (var group in byCurve)
        {
            var curve = (AnimationCurve)group.Key;
            var moving = group.OrderByDescending(k => k.Time).ToList();

            // Lift.
            var lifted = new List<(KeyRef Ref, Keyframe Key)>();
            foreach (var reference in moving)
            {
                int index = IndexOfKeyAt(curve, reference.Time);
                if (index < 0) continue;
                lifted.Add((reference, curve[index]));
                curve.RemoveKey(index);
            }

            // Drop.
            foreach (var (reference, key) in lifted)
            {
                float target = MathF.Max(ClipStart, Snap(reference.DragStartTime + deltaSeconds));

                int collision = IndexOfKeyAt(curve, target);
                if (collision >= 0) curve.RemoveKey(collision);

                Keyframe moved = key;
                moved.Time = target;
                curve.AddKey(moved);
                reference.Time = target;
            }
        }
    }

    private void DeleteSelection()
    {
        foreach (var group in _selection.GroupBy(k => k.Curve, ReferenceEqualityComparer.Instance))
        {
            var curve = (AnimationCurve)group.Key;
            foreach (var reference in group.OrderByDescending(k => k.Time))
            {
                int index = IndexOfKeyAt(curve, reference.Time);
                if (index >= 0) curve.RemoveKey(index);
            }
        }
        _selection.Clear();
        MarkDirty();
    }

    /// <summary>
    /// Set how the selected keys interpolate <i>towards the next key</i> - the engine's curve stores
    /// the mode on the segment's left-hand key.
    /// </summary>
    private void SetSelectionInterpolation(CurveInterpolation interpolation)
    {
        foreach (var reference in _selection)
        {
            int index = IndexOfKeyAt(reference.Curve, reference.Time);
            if (index < 0) continue;

            Keyframe key = reference.Curve[index];
            key.Interpolation = interpolation;
            reference.Curve[index] = key;
        }
        MarkDirty();
    }

    /// <summary>
    /// Zero the selected keys' tangents on every component and put them on a cubic segment, which is
    /// the only interpolation tangents affect - flattening a linear key otherwise does nothing visible.
    /// </summary>
    private void FlattenSelectionTangents()
    {
        foreach (var reference in _selection)
        {
            int index = IndexOfKeyAt(reference.Curve, reference.Time);
            if (index < 0) continue;

            Keyframe key = reference.Curve[index];
            key.InTangent4 = default;
            key.OutTangent4 = default;
            key.Interpolation = CurveInterpolation.CubicSpline;
            reference.Curve[index] = key;
        }
        MarkDirty();
    }

    /// <summary>Auto-smooth the selected keys' tangents from their neighbours, on every component.</summary>
    private void SmoothSelectionTangents()
    {
        foreach (var group in _selection.GroupBy(k => k.Curve, ReferenceEqualityComparer.Instance))
        {
            var curve = (AnimationCurve)group.Key;
            foreach (var reference in group)
            {
                int index = IndexOfKeyAt(curve, reference.Time);
                if (index < 0) continue;

                Keyframe key = curve[index];
                key.Interpolation = CurveInterpolation.CubicSpline;
                curve[index] = key;
                curve.SmoothTangent(index, CurveTangentMode.ClampedAuto);
            }
        }
        MarkDirty();
    }

    private void CopySelection()
    {
        _clipboard.Clear();
        if (_selection.Count == 0) return;

        float origin = _selection.Min(k => k.Time);
        foreach (var reference in _selection)
        {
            int index = IndexOfKeyAt(reference.Curve, reference.Time);
            if (index < 0) continue;
            _clipboard.Add(new ClipboardKey(reference.Curve, reference.Time - origin, reference.Curve[index]));
        }
    }

    private void PasteAtPlayhead()
    {
        if (_clipboard.Count == 0) return;
        _selection.Clear();

        foreach (var entry in _clipboard)
        {
            var curve = entry.Curve;

            // The clip is reimported on save, which replaces every curve in it; ResolveClip drops the
            // clipboard when that happens, so anything still here belongs to the live clip - unless
            // its track was deleted in the meantime, which this catches.
            if (!_rows.Any(r => ReferenceEquals(r.Curve, curve))) continue;

            float t = MathF.Max(ClipStart, Snap(_time + entry.Offset));
            int collision = IndexOfKeyAt(curve, t);
            if (collision >= 0) curve.RemoveKey(collision);

            Keyframe pasted = entry.Key;
            pasted.Time = t;
            curve.AddKey(pasted);
            _selection.Add(new KeyRef { Curve = curve, Time = t });
        }
        MarkDirty();
    }

    private void AddKey(AnimationCurve curve, float time)
    {
        time = MathF.Max(ClipStart, Snap(time));
        KeyAt(curve, time);

        _selection.Clear();
        _selection.Add(new KeyRef { Curve = curve, Time = time });
        MarkDirty();
    }

    /// <summary>Key a curve at an exact time, holding whatever it already evaluates to there.</summary>
    private static void KeyAt(AnimationCurve curve, float time)
    {
        Float4 value = EvaluateAll(curve, time);
        int existing = IndexOfKeyAt(curve, time);

        if (existing >= 0)
        {
            Keyframe key = curve[existing];
            key.Value4 = value;
            curve[existing] = key;
            return;
        }

        curve.AddKey(new Keyframe(time, value, default, default, InterpolationNear(curve, time)));
    }

    /// <summary>Key every visible channel at the playhead - the "record a pose" action, made explicit.</summary>
    private void KeyAllVisible()
    {
        _selection.Clear();
        float t = MathF.Max(ClipStart, Snap(_time));

        foreach (var row in _rows)
        {
            if (row.Curve == null) continue;
            KeyAt(row.Curve, t);
            _selection.Add(new KeyRef { Curve = row.Curve, Time = t });
        }
        MarkDirty();
    }

    // ============================================================
    //  Creating tracks
    // ============================================================

    /// <summary>
    /// The transform new bone paths are built relative to. Prefers the preview's resolved search root
    /// so an authored path matches the one the preview (and the Animator) will look the bone up by;
    /// falls back to the scene selection when nothing is previewing yet, which is how an empty clip
    /// gets its first track.
    /// </summary>
    private Transform? AddPropertyRoot()
    {
        if (_preview.SearchRoot != null) return _preview.SearchRoot;
        if (_previewTarget.IsValid()) return _previewTarget!.Transform;

        var selected = Selection.GetSelected<GameObject>().FirstOrDefault();
        return selected.IsValid() ? selected!.Transform : null;
    }

    /// <summary>Bounds the hierarchy walk so a pathological rig cannot build a menu with no end.</summary>
    private int _boneMenuBudget;

    /// <summary>Where to open the bone picker on the next frame. See the note in <c>OnGUI</c>.</summary>
    private Float2? _pendingAddPropertyMenu;

    private void OpenAddPropertyMenu(Float2 screenPosition)
    {
        Transform? root = AddPropertyRoot();

        Origami.ContextMenu(screenPosition.X, screenPosition.Y, menu =>
        {
            menu.Title("Add Property");

            if (root == null)
            {
                menu.Item("Select the character in the scene first.", () => { }, false);
                return;
            }

            if (root.ChildCount == 0)
            {
                menu.Item($"'{root.GameObject.Name}' has no child bones.", () => { }, false);
                return;
            }

            // The menu's build action runs every frame the menu is up, and it walks the whole rig - so
            // the "already animated" test is a set lookup rather than a scan of the clip per bone.
            var animated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var track in _clip!.Bones) animated.Add(track.BoneName);

            _boneMenuBudget = 800;
            foreach (var child in root.GetChildren())
                BuildBoneMenu(menu, child, root, animated);
        });
    }

    /// <summary>
    /// Mirror the rig hierarchy as nested submenus. A leaf adds its bone directly; a bone with children
    /// gets a submenu whose first entry adds the bone itself, so any level of the skeleton is one or
    /// two clicks away rather than something you have to type a path for.
    /// </summary>
    private void BuildBoneMenu(ContextBuilder menu, Transform node, Transform root, HashSet<string> animated)
    {
        if (_boneMenuBudget <= 0) return;
        _boneMenuBudget--;

        string path = Transform.GetRelativePath(node, root);
        bool tracked = animated.Contains(path);
        string label = node.GameObject.Name + (tracked ? "  ·" : string.Empty);

        if (node.ChildCount == 0)
        {
            menu.Item(label, () => AddTransformTrack(path));
            return;
        }

        menu.Submenu(label, sub =>
        {
            sub.Item(tracked ? "Complete Curves" : "Add " + node.GameObject.Name, () => AddTransformTrack(path));
            sub.Separator();
            foreach (var child in node.GetChildren())
                BuildBoneMenu(sub, child, root, animated);
        });
    }

    /// <summary>
    /// Find a clip's track for a bone path.
    ///
    /// <para>Scans the list rather than calling <see cref="AnimationClip.GetBone"/>: the clip keeps a
    /// private name map that <c>AddBone</c> fills but nothing removes from, so after a track is deleted
    /// the map still hands back the removed <c>AnimBone</c> - and curves written into that detached
    /// object would never be saved.</para>
    /// </summary>
    private AnimationClip.AnimBone? FindBone(string bonePath)
    {
        foreach (var bone in _clip!.Bones)
            if (string.Equals(bone.BoneName, bonePath, StringComparison.Ordinal)) return bone;
        return null;
    }

    /// <summary>
    /// Add (or complete) a bone's transform channels, keyed at the playhead with the bone's current
    /// pose. Seeding from the live pose rather than from zero means the first key is the rig as it
    /// stands, so a new track does not fold the character into a heap the moment it is previewed.
    /// </summary>
    /// <param name="channelName">
    /// Restrict to one channel ("Position", "Rotation", "Scale"), or null for all three.
    /// </param>
    private void AddTransformTrack(string bonePath, string? channelName = null)
    {
        if (_clip.IsNotValid() || string.IsNullOrEmpty(bonePath)) return;

        Transform? bone = AddPropertyRoot()?.Find(bonePath);
        Float3 position = bone?.LocalPosition ?? Float3.Zero;
        Quaternion rotation = bone?.LocalRotation ?? Quaternion.Identity;
        Float3 scale = bone?.LocalScale ?? Float3.One;

        // Parallel to Channels, which is declared in this order.
        Float4[] seed =
        {
            new(position, 0f),
            new(rotation.X, rotation.Y, rotation.Z, rotation.W),
            new(scale, 0f),
        };

        var track = FindBone(bonePath);
        if (track == null)
        {
            track = new AnimationClip.AnimBone { BoneName = bonePath };
            _clip!.AddBone(track);
        }

        float time = MathF.Max(ClipStart, Snap(_time));

        _selection.Clear();
        for (int i = 0; i < Channels.Length; i++)
        {
            var channel = Channels[i];
            if (channelName != null && channel.Name != channelName) continue;

            var curve = channel.Get(track);
            if (curve == null)
            {
                curve = new AnimationCurve(channel.Dimension);
                curve.AddKey(new Keyframe(time, seed[i]));
                channel.Set(track, curve);
            }
            else if (IndexOfKeyAt(curve, time) < 0)
            {
                // An existing channel keeps its shape - hold whatever it already evaluates to here.
                curve.AddKey(new Keyframe(time, EvaluateAll(curve, time, seed[i]),
                    default, default, InterpolationNear(curve, time)));
            }

            _selection.Add(new KeyRef { Curve = curve, Time = time });
        }

        // A filter that hides the track you just made looks exactly like the button doing nothing.
        if (_search.Trim().Length > 0
            && bonePath.IndexOf(_search.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            _search = string.Empty;

        _collapsed.Remove(bonePath);
        MarkDirty();

        // The preview's binding is a snapshot of the track list, so it has to be rebuilt or the new
        // bone silently will not move.
        _preview.Rebuild();
    }

    /// <summary>Drop every curve on a bone, removing the track from the clip entirely.</summary>
    private void RemoveBoneTrack(string bonePath)
    {
        var track = FindBone(bonePath);
        if (track == null) return;

        _clip!.Bones.Remove(track);
        _selection.Clear();
        MarkDirty();
        _preview.Rebuild();
    }

    // ============================================================
    //  Clip utilities
    // ============================================================

    private void FitLengthToKeys()
    {
        float last = ClipStart;
        foreach (var curve in AllCurves())
            if (curve.Count > 0) last = MathF.Max(last, curve.EndTime);

        _clip!.Duration = MathF.Max(1f / _frameRate, last - ClipStart);
        SetTime(_time);
        MarkDirty();
    }

    private void ReverseClip()
    {
        // Mirror within the clip's own span, so a clip that starts at 4s stays at 4s.
        float pivot = ClipStart + ClipEnd;

        foreach (var curve in AllCurves())
        {
            Keyframe[] source = curve.GetKeys();
            int n = source.Length;
            if (n == 0) continue;

            curve.Clear();
            for (int i = n - 1; i >= 0; i--)
            {
                Keyframe key = source[i];
                key.Time = MathF.Max(ClipStart, pivot - source[i].Time);

                // Tangents swap ends and flip sign, because time now runs the other way.
                key.InTangent4 = -source[i].OutTangent4;
                key.OutTangent4 = -source[i].InTangent4;

                // Interpolation lives on a segment's left-hand key, so it has to shift one key along
                // when the order flips: reversed key j leads the segment that used to trail key i.
                key.Interpolation = i > 0 ? source[i - 1].Interpolation : source[0].Interpolation;

                curve.AddKey(key);
            }
        }

        _selection.Clear();
        MarkDirty();
    }

    private IEnumerable<AnimationCurve> AllCurves()
    {
        foreach (var bone in _clip!.Bones)
            foreach (var channel in Channels)
            {
                var curve = channel.Get(bone);
                if (curve != null) yield return curve;
            }


        foreach (var shape in _clip.BlendShapes)
            if (shape.Weight != null) yield return shape.Weight;
    }

    private void FrameAll()
    {
        float span = MathF.Max(_clip!.Duration, 0.1f);
        float width = MathF.Max(200f, (float)_bodyRect.Size.X - GutterWidth);
        _viewStart = ClipStart;
        _pixelsPerSecond = Math.Clamp(width / (span * 1.05f), MinPixelsPerSecond, MaxPixelsPerSecond);
        _scrollY = 0f;
    }

    private void ToggleGroup(string groupKey)
    {
        if (!_collapsed.Add(groupKey)) _collapsed.Remove(groupKey);
        _rowsStale = true;
    }

    // ============================================================
    //  Key inspector
    // ============================================================

    private void DrawKeyInspector(Paper paper, Prowl.Scribe.FontFile font)
    {
        using (paper.Row("clip_ki").Width(UnitValue.Stretch()).Height(InspectorHeight)
            .BackgroundColor(EditorTheme.Neutral200).Padding(10, 10, 6, 6).Gap(10).Enter())
        {
            if (_selection.Count == 0)
            {
                paper.Box("clip_ki_none").Width(UnitValue.Stretch()).Height(UnitValue.Stretch()).IsNotInteractable()
                    .Text("No key selected.  Drag a box to select several, or right-click for key and clip actions.  "
                        + "Ctrl+wheel zooms, Shift+wheel pans, middle-drag moves the view.\n"
                        + "Over the sheet:  Space plays  ·  ←/→ step a frame  ·  K keys every visible track  ·  "
                        + "F frames the clip  ·  Del removes  ·  Ctrl+C/V copy and paste  ·  Ctrl+A selects all.", font)
                    .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                    .Wrap(Prowl.Scribe.TextWrapMode.Wrap).Alignment(TextAlignment.MiddleLeft);
                return;
            }

            if (_selection.Count == 1)
            {
                DrawSingleKeyInspector(paper, font, _selection[0]);
                return;
            }

            DrawMultiKeyInspector(paper, font);
        }
    }

    private void DrawSingleKeyInspector(Paper paper, Prowl.Scribe.FontFile font, KeyRef reference)
    {
        var curve = reference.Curve;
        int index = IndexOfKeyAt(curve, reference.Time);
        if (index < 0) { _selection.Clear(); return; }

        Keyframe key = curve[index];

        // The key is a value type on a packed curve, so an edit is read-modify-write rather than a
        // field poke. Going through one helper keeps every field below writing the key back.
        void Write(Keyframe edited)
        {
            int at = IndexOfKeyAt(curve, reference.Time);
            if (at < 0) return;
            curve[at] = edited;
            MarkDirty();
        }

        using (paper.Column("clip_ki_c1").Width(190).Height(UnitValue.Stretch()).Gap(4).Enter())
        {
            EditorGUI.Row(paper, "clip_ki_time", "Time (s)", () =>
                Origami.NumericField(paper, "clip_ki_time_v", reference.Time, v =>
                {
                    MoveSingleKey(reference, MathF.Max(ClipStart, v));
                    MarkDirty();
                }).Show());

            EditorGUI.Row(paper, "clip_ki_frame", "Frame", () =>
                Origami.NumericField(paper, "clip_ki_frame_v",
                    (int)MathF.Round((reference.Time - ClipStart) * _frameRate), v =>
                {
                    MoveSingleKey(reference, MathF.Max(ClipStart, ClipStart + v / (float)_frameRate));
                    MarkDirty();
                }).Show());
        }

        using (paper.Column("clip_ki_c2").Width(280).Height(UnitValue.Stretch()).Gap(4).Enter())
        {
            EditorGUI.Row(paper, "clip_ki_value", "Value", () =>
                DrawComponentField(paper, "clip_ki_value_v", curve.Dimension, key.Value4,
                    v => { Keyframe k = key; k.Value4 = v; Write(k); }));

            EditorGUI.Row(paper, "clip_ki_interp", "Interp", () =>
                Origami.EnumDropdown(paper, "clip_ki_interp_v", key.Interpolation, v =>
                {
                    Keyframe k = key;
                    k.Interpolation = v;
                    Write(k);
                }).Show());
        }

        using (paper.Column("clip_ki_c3").Width(280).Height(UnitValue.Stretch()).Gap(4).Enter())
        {
            EditorGUI.Row(paper, "clip_ki_tin", "Tangent In", () =>
                DrawComponentField(paper, "clip_ki_tin_v", curve.Dimension, key.InTangent4,
                    v => { Keyframe k = key; k.InTangent4 = v; Write(k); }));

            EditorGUI.Row(paper, "clip_ki_tout", "Tangent Out", () =>
                DrawComponentField(paper, "clip_ki_tout_v", curve.Dimension, key.OutTangent4,
                    v => { Keyframe k = key; k.OutTangent4 = v; Write(k); }));
        }

        float h = EditorGUIMetrics.RowHeight;
        using (paper.Column("clip_ki_c4").Width(UnitValue.Stretch()).Height(UnitValue.Stretch()).Gap(4).Enter())
        {
            Origami.Button(paper, "clip_ki_del", "Delete Key", DeleteSelection)
                .Danger().Width(UnitValue.Stretch()).Height(h).Show();
            Origami.Button(paper, "clip_ki_flat", "Flatten Tangents", FlattenSelectionTangents)
                .Subtle().Width(UnitValue.Stretch()).Height(h).Show();
        }
    }

    /// <summary>
    /// One numeric field per component of a channel: a plain float for a blend-shape weight, an XYZ
    /// field for position/scale, XYZW for a rotation. Values outside the curve's dimension are never
    /// shown, so a three-component channel cannot be given a stray W.
    /// </summary>
    private static void DrawComponentField(Paper paper, string id, int dimension, Float4 value,
        Action<Float4> setter)
    {
        switch (dimension)
        {
            case 1:
                Origami.NumericField(paper, id, value.X, v => setter(new Float4(v, 0f, 0f, 0f))).Show();
                break;
            case 2:
                Origami.Float2Field(paper, id, value.XY, v => setter(new Float4(v, 0f, 0f))).Show();
                break;
            case 3:
                Origami.Float3Field(paper, id, value.XYZ, v => setter(new Float4(v, 0f))).Show();
                break;
            default:
                Origami.Float4Field(paper, id, value, setter).Show();
                break;
        }
    }

    private void DrawMultiKeyInspector(Paper paper, Prowl.Scribe.FontFile font)
    {
        float first = _selection.Min(k => k.Time);
        float last = _selection.Max(k => k.Time);

        using (paper.Column("clip_ki_m1").Width(240).Height(UnitValue.Stretch()).Gap(4).Enter())
        {
            paper.Box("clip_ki_m_lbl").Height(20).IsNotInteractable()
                .Text($"{_selection.Count} keys selected  ({first:F3}s → {last:F3}s)", font)
                .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft);

            EditorGUI.Row(paper, "clip_ki_m_shift", "Shift By (s)", () =>
                Origami.NumericField(paper, "clip_ki_m_shift_v", 0f, v =>
                {
                    if (MathF.Abs(v) < 1e-6f) return;
                    foreach (var key in _selection) key.DragStartTime = key.Time;
                    MoveSelection(v);
                    MarkDirty();
                }).Show());
        }

        float h = EditorGUIMetrics.RowHeight;
        using (paper.Column("clip_ki_m2").Width(UnitValue.Stretch()).Height(UnitValue.Stretch()).Gap(4).Enter())
        {
            using (paper.Row("clip_ki_m_row1").Width(UnitValue.Stretch()).Height(h).Gap(6).Enter())
            {
                Origami.Button(paper, "clip_ki_m_linear", "Linear", () => SetSelectionInterpolation(CurveInterpolation.Linear))
                    .Subtle().Width(UnitValue.Stretch()).Height(h).Show();
                Origami.Button(paper, "clip_ki_m_step", "Stepped", () => SetSelectionInterpolation(CurveInterpolation.Step))
                    .Subtle().Width(UnitValue.Stretch()).Height(h).Show();
                Origami.Button(paper, "clip_ki_m_cubic", "Cubic", () => SetSelectionInterpolation(CurveInterpolation.CubicSpline))
                    .Subtle().Width(UnitValue.Stretch()).Height(h).Show();
                Origami.Button(paper, "clip_ki_m_auto", "Auto", SmoothSelectionTangents)
                    .Subtle().Width(UnitValue.Stretch()).Height(h).Show();
                Origami.Button(paper, "clip_ki_m_flat", "Flatten", FlattenSelectionTangents)
                    .Subtle().Width(UnitValue.Stretch()).Height(h).Show();
            }
            using (paper.Row("clip_ki_m_row2").Width(UnitValue.Stretch()).Height(h).Gap(6).Enter())
            {
                Origami.Button(paper, "clip_ki_m_copy", "Copy", CopySelection)
                    .Subtle().Width(UnitValue.Stretch()).Height(h).Show();
                Origami.Button(paper, "clip_ki_m_paste", "Paste At Playhead", PasteAtPlayhead)
                    .Subtle().Width(UnitValue.Stretch()).Height(h).Show();
                Origami.Button(paper, "clip_ki_m_del", "Delete", DeleteSelection)
                    .Danger().Width(UnitValue.Stretch()).Height(h).Show();
            }
        }
    }

    private void MoveSingleKey(KeyRef reference, float newTime)
    {
        var curve = reference.Curve;
        int index = IndexOfKeyAt(curve, reference.Time);
        if (index < 0) return;

        Keyframe key = curve[index];
        curve.RemoveKey(index);

        int collision = IndexOfKeyAt(curve, newTime);
        if (collision >= 0) curve.RemoveKey(collision);

        key.Time = newTime;
        curve.AddKey(key);
        reference.Time = newTime;
    }

    // ============================================================
    //  Playback + preview
    // ============================================================

    private void TogglePlay()
    {
        _playing = !_playing;
        if (_playing)
        {
            _clock.Restart();
            _lastClockSeconds = 0d;
        }
        else
        {
            _clock.Stop();
        }
    }

    private void TickPlayback()
    {
        if (!_playing) return;

        // Wall clock rather than Time.DeltaTime: the editor is not necessarily ticking game time,
        // and playback should run at real speed regardless.
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)(now - _lastClockSeconds);
        _lastClockSeconds = now;

        float start = ClipStart;
        float duration = MathF.Max(_clip!.Duration, 1e-4f);
        float t = _time + dt;

        if (t > start + duration)
        {
            if (_loopPlayback) t = start + (t - start) % duration;
            else { t = start + duration; _playing = false; _clock.Stop(); }
        }
        _time = t;
    }

    private void StepFrames(int frames)
    {
        _playing = false;
        SetTime(_time + frames / (float)_frameRate);
    }

    private void SetTime(float t)
    {
        _time = _clip.IsValid() ? Math.Clamp(t, ClipStart, MathF.Max(ClipStart, ClipEnd)) : 0f;
    }

    /// <summary>
    /// Keep the preview bound to the selected rig and posed at the playhead. Selecting nothing (or
    /// turning preview off) restores whatever the preview last touched.
    /// </summary>
    private void SyncPreview()
    {
        if (!_previewEnabled || _clip.IsNotValid())
        {
            _preview.Release();
            _previewTarget = null;
            return;
        }

        GameObject? target = Selection.GetSelected<GameObject>().FirstOrDefault();
        if (target.IsNotValid()) target = null;

        if (!ReferenceEquals(target, _previewTarget))
        {
            _previewTarget = target;
            _preview.Bind(_clip, target != null ? target.Transform : null);
            _rowsStale = true; // which tracks resolve against the rig just changed
        }

        if (_preview.IsBound) _preview.Apply(_time);
    }

    // ============================================================
    //  Persistence
    // ============================================================

    private void MarkDirty()
    {
        _dirty = true;
        _rowsStale = true;
        EnsureSaveHook();
    }

    private void EnsureSaveHook()
    {
        if (_saveHookAttached) return;
        Prowl.Editor.Projects.SaveManager.OnSave += OnProjectSave;
        _saveHookAttached = true;
    }

    private void ReleaseSaveHook()
    {
        if (!_saveHookAttached) return;
        Prowl.Editor.Projects.SaveManager.OnSave -= OnProjectSave;
        _saveHookAttached = false;
    }

    private string? OnProjectSave()
    {
        if (_clip.IsNotValid() || !_dirty) return null;

        // Read the name first: saving reimports, which disposes this instance.
        string name = _clip!.Name;
        Save();
        return $"Saved Animation {name}";
    }

    private void Save()
    {
        if (_clip.IsNotValid()) return;
        if (!AnimationClipIO.Save(_clip!)) return;

        _dirty = false;
        ReleaseSaveHook();
    }

    /// <summary>
    /// Write a model-embedded take out as a standalone <c>.anim</c> and switch this window over to it,
    /// so the edits you were about to lose land in something that can be saved.
    /// </summary>
    private void ExtractClip()
    {
        if (_clip.IsNotValid()) return;

        Guid guid = AnimationClipIO.Extract(_clip!);
        if (guid == Guid.Empty) return;

        _dirty = false;
        ReleaseSaveHook();
        SetClipRef(new AssetRef<AnimationClip>(guid));
    }

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        if (_clipRef.AssetID == Guid.Empty) return false;
        state["clip"] = _clipRef.AssetID.ToString();
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (Guid.TryParse(state["clip"]?.GetValue<string>(), out var guid))
            SetClipRef(new AssetRef<AnimationClip>(guid));
    }

    private static Color32 Rgb(SColor c) => new(c.R, c.G, c.B, c.A);
}
