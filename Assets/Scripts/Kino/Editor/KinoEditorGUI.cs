// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;

using Prowl.Editor.GUI;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Vector;

using SColor = System.Drawing.Color;

namespace Prowl.Kino.Editor;

/// <summary>How loudly a banner speaks.</summary>
public enum KinoNoteKind
{
    /// <summary>Something worth knowing. Neutral.</summary>
    Info,

    /// <summary>The shot works, but not the way it looks like it should.</summary>
    Warning,

    /// <summary>The component is not doing anything at all until this is fixed.</summary>
    Problem,

    /// <summary>This is the shot you are looking at.</summary>
    Live
}

/// <summary>
/// The shared furniture every Kino inspector is built from: banners that explain when a component is
/// not doing what it looks like it is doing, section headers, and rows with the units spelled out.
/// </summary>
public static class KinoEditorGUI
{
    /// <summary>Standard height of a control row.</summary>
    public static float RowHeight => Origami.Current.Metrics.RowHeight;

    #region Banners and headers

    /// <summary>
    /// A one-line explanation with a coloured edge. Used wherever a value is legal but the result is
    /// not what the inspector appears to promise - a body component with nothing to follow, an aim
    /// component that a second one is shadowing.
    /// </summary>
    public static void Note(Paper paper, string id, string text, KinoNoteKind kind = KinoNoteKind.Info)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        var m = Origami.Current.Metrics;
        (SColor accent, string icon) = kind switch
        {
            KinoNoteKind.Problem => (EditorTheme.Red400, "\uf071"),   // triangle-exclamation
            KinoNoteKind.Warning => (EditorTheme.Amber400, "\uf06a"), // circle-exclamation
            KinoNoteKind.Live => (EditorTheme.Green400, "\uf030"),    // camera
            _ => (EditorTheme.Blue400, "\uf05a")                      // circle-info
        };

        using (paper.Row($"{id}_note").Height(UnitValue.Auto).MinHeight(RowHeight)
            .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.Spacing)
            .Padding(m.SpacingLarge, m.SpacingLarge, m.SpacingSmall, m.SpacingSmall)
            .RowBetween(m.SpacingMedium)
            .Rounded(m.SmallRounding)
            .BackgroundColor(SColor.FromArgb(28, accent))
            .BorderColor(SColor.FromArgb(90, accent)).BorderWidth(1)
            .IsNotInteractable().Enter())
        {
            paper.Box($"{id}_note_i").Width(14).Height(RowHeight).IsNotInteractable()
                .Text(icon, font).TextColor(accent).FontSize(m.FontSizeSmall)
                .Alignment(TextAlignment.MiddleCenter);

            paper.Box($"{id}_note_t").Width(UnitValue.Stretch()).Height(UnitValue.Auto).MinHeight(RowHeight)
                .IsNotInteractable()
                .Text(text, font).TextColor(EditorTheme.Ink500).FontSize(m.FontSizeSmall)
                .Wrap(Prowl.Scribe.TextWrapMode.Wrap).Alignment(TextAlignment.MiddleLeft);
        }
    }

    /// <summary>A titled group of rows, matching the rest of the inspector's rhythm.</summary>
    public static void Section(Paper paper, string id, string title, Action body)
    {
        EditorGUI.SectionHeader(paper, $"{id}_h", title);
        body();
    }

    /// <summary>A pill showing a short piece of live state - what a value currently works out to.</summary>
    public static void Badge(Paper paper, string id, string text, SColor color)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        var m = Origami.Current.Metrics;
        paper.Box(id).Width(UnitValue.Auto).Height(18)
            .Padding(m.SpacingLarge, m.SpacingLarge, 0, 0)
            .Rounded(9)
            .BackgroundColor(SColor.FromArgb(40, color))
            .BorderColor(SColor.FromArgb(110, color)).BorderWidth(1)
            .IsNotInteractable()
            .Text(text, font).TextColor(color).FontSize(m.FontSizeSmall)
            .Alignment(TextAlignment.MiddleCenter);
    }

    /// <summary>
    /// A button that reads as an action rather than a value. A full-width one carries the inspector's
    /// own gutter, so it lines up with the rows above it instead of running to the panel edge; one
    /// inside a row the caller already padded does not.
    /// </summary>
    public static void Button(Paper paper, string id, string label, Action onClick, SColor? tint = null, bool grow = true)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        SColor color = tint ?? EditorTheme.Purple400;
        var m = Origami.Current.Metrics;

        var box = paper.Box(id)
            .Width(grow ? UnitValue.Stretch() : UnitValue.Auto)
            .Height(22)
            .Margin(grow ? m.PaddingLarge : 0f, grow ? m.PaddingLarge : 0f, 0f, grow ? m.Spacing : 0f)
            .Padding(m.PaddingLarge, m.PaddingLarge, 0, 0)
            .Rounded(m.SmallRounding)
            .BackgroundColor(SColor.FromArgb(38, color))
            .BorderColor(SColor.FromArgb(120, color)).BorderWidth(1)
            .Text(label, font).TextColor(color).FontSize(m.FontSizeSmall)
            .Alignment(TextAlignment.MiddleCenter)
            .OnClick(_ => onClick());

        box.Hovered.BackgroundColor(SColor.FromArgb(70, color)).End();
    }

    /// <summary>A toggle that stays visibly on, for modes rather than settings.</summary>
    public static void ToggleButton(Paper paper, string id, string label, bool active, Action onClick, SColor? tint = null)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        SColor color = tint ?? EditorTheme.Amber400;
        var m = Origami.Current.Metrics;

        var box = paper.Box(id)
            .Width(UnitValue.Auto).Height(24)
            .Padding(m.PaddingLarge, m.PaddingLarge, 0, 0)
            .Rounded(m.SmallRounding)
            .BackgroundColor(active ? SColor.FromArgb(200, color) : SColor.FromArgb(30, color))
            .BorderColor(SColor.FromArgb(active ? 255 : 110, color)).BorderWidth(1)
            .Text(label, font).TextColor(active ? EditorTheme.Neutral100 : color).FontSize(m.FontSizeSmall)
            .Alignment(TextAlignment.MiddleCenter)
            .OnClick(_ => onClick());

        box.Hovered.BackgroundColor(active ? SColor.FromArgb(230, color) : SColor.FromArgb(70, color)).End();
    }

    #endregion

    #region Rows

    /// <summary>A float row that says what the number means - seconds, metres, degrees.</summary>
    public static void FloatRow(Paper paper, string id, string label, float value, Action<float> setter,
        string? unit = null, float min = float.MinValue, float max = float.MaxValue)
    {
        EditorGUI.Row(paper, id, label, () =>
        {
            using (paper.Row($"{id}_w").Height(RowHeight).RowBetween(6).Enter())
            {
                Origami.NumericField(paper, $"{id}_v", value,
                        v => setter(Maths.Clamp(v, min, max)))
                    .Show();

                if (!string.IsNullOrEmpty(unit))
                    Unit(paper, $"{id}_u", unit);
            }
        });
    }

    /// <summary>A 0-to-1 slider row, for screen positions and zone sizes.</summary>
    public static void SliderRow(Paper paper, string id, string label, float value, float min, float max,
        Action<float> setter, string format = "F2")
        => EditorGUI.Row(paper, id, label, () =>
            Origami.Slider(paper, $"{id}_v", value, setter, min, max).Format(format).Show());

    /// <summary>A checkbox row.</summary>
    public static void ToggleRow(Paper paper, string id, string label, bool value, Action<bool> setter)
        => EditorGUI.Row(paper, id, label, () => Origami.Checkbox(paper, $"{id}_v", value, setter).Show());

    /// <summary>An enum dropdown row.</summary>
    public static void EnumRow<T>(Paper paper, string id, string label, T value, Action<T> setter)
        where T : struct, Enum
        => EditorGUI.Row(paper, id, label, () => Origami.EnumDropdown(paper, $"{id}_v", value, setter).Show());

    /// <summary>A field row drawn by the property grid, so object references keep their drag-and-drop.</summary>
    public static void FieldRow(Paper paper, string id, string label, Type type, object? value, Action<object?> setter)
        => PropertyGridUtils.DrawField(paper, id, label, type, value, setter);

    /// <summary>A three-float row.</summary>
    public static void Float3Row(Paper paper, string id, string label, Float3 value, Action<Float3> setter)
        => PropertyGridUtils.DrawField(paper, id, label, typeof(Float3), value, v => setter((Float3)v!));

    /// <summary>
    /// A damping row. Damping is always "seconds to close the gap", so the unit is spelled out and
    /// zero is labelled as rigid rather than left as a bare 0.
    /// </summary>
    public static void DampingRow(Paper paper, string id, string label, float value, Action<float> setter)
    {
        EditorGUI.Row(paper, id, label, () =>
        {
            using (paper.Row($"{id}_w").Height(RowHeight).RowBetween(6).Enter())
            {
                Origami.NumericField(paper, $"{id}_v", value, v => setter(Maths.Max(v, 0f))).Show();
                Unit(paper, $"{id}_u", value <= 0f ? "rigid" : "s");
            }
        });
    }

    /// <summary>Per-axis damping, with the axes named for what they mean rather than X, Y and Z.</summary>
    public static void DampingRow3(Paper paper, string id, string label, Float3 value, Action<Float3> setter,
        string xName = "X", string yName = "Y", string zName = "Z")
    {
        EditorGUI.Row(paper, id, label, () =>
        {
            using (paper.Row($"{id}_w").Height(RowHeight).RowBetween(4).Enter())
            {
                Axis(paper, $"{id}_x", xName, value.X, v => setter(new Float3(Maths.Max(v, 0f), value.Y, value.Z)));
                Axis(paper, $"{id}_y", yName, value.Y, v => setter(new Float3(value.X, Maths.Max(v, 0f), value.Z)));
                Axis(paper, $"{id}_z", zName, value.Z, v => setter(new Float3(value.X, value.Y, Maths.Max(v, 0f))));
            }
        });

        static void Axis(Paper paper, string id, string name, float value, Action<float> setter)
        {
            var font = EditorTheme.DefaultFont;
            using (paper.Row(id).Width(UnitValue.Stretch()).Height(RowHeight).RowBetween(3).Enter())
            {
                if (font != null)
                    paper.Box($"{id}_l").Width(UnitValue.Auto).Height(RowHeight).IsNotInteractable()
                        .Text(name, font).TextColor(EditorTheme.Ink300)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

                Origami.NumericField(paper, $"{id}_v", value, setter).Show();
            }
        }
    }

    /// <summary>Vertical breathing room between groups.</summary>
    public static void Space(Paper paper, string id, float height = 6f)
        => paper.Box(id).Height(height).IsNotInteractable();

    private static void Unit(Paper paper, string id, string text)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        paper.Box(id).Width(38).Height(RowHeight).IsNotInteractable()
            .Text(text, font).TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
    }

    #endregion

    #region Shared component checks

    /// <summary>
    /// Explains, in the component's own inspector, the two reasons a pipeline component silently does
    /// nothing: it has no target to work from, or another component of the same stage got there first.
    /// </summary>
    public static void PipelineStatus(Paper paper, string id, KinoComponent component)
    {
        KinoCamera? vcam = component.VirtualCamera;
        if (vcam.IsNotValid())
        {
            Note(paper, $"{id}_novcam", "No Kino Camera on this object, so nothing runs this component. Add one.", KinoNoteKind.Problem);
            return;
        }

        KinoComponent? winner = KinoEditorUtil.StageWinner(vcam, component.Stage);
        if ((component.Stage == KinoStage.Body || component.Stage == KinoStage.Aim)
            && winner.IsValid() && !ReferenceEquals(winner, component))
        {
            Note(paper, $"{id}_shadowed",
                $"Not running: {KinoEditorUtil.DisplayName(winner.GetType())} is this camera's {component.Stage} component. "
                + "Only the first one of a stage runs - remove or disable one of them.",
                KinoNoteKind.Warning);
        }

        if (!component.IsUsable)
        {
            Note(paper, $"{id}_unusable",
                KinoEditorUtil.WhyUnusable(component),
                KinoNoteKind.Problem);
        }
    }

    #endregion
}
