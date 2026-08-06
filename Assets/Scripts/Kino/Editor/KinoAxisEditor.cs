// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;

using Prowl.Editor.GUI;
using Prowl.PaperUI;
using Prowl.Vector;

namespace Prowl.Kino.Editor;

/// <summary>
/// Draws a <see cref="KinoAxis"/> as the thing it is - a value with limits and a way for the player to
/// push it - instead of the flat list of eleven fields reflection would give.
/// </summary>
/// <remarks>
/// Which fields matter depends on the input channel: a mouse reports movement and is scaled by Gain, a
/// stick reports a held position and is scaled by speed and easing. Showing both at once is what makes
/// an axis confusing, so only the half that applies is drawn.
/// </remarks>
public static class KinoAxisEditor
{
    public static void Draw(Paper paper, string id, KinoAxis axis)
    {
        if (axis == null)
            return;

        bool wrapped = axis.Wrap && axis.Max > axis.Min;

        if (axis.Max > axis.Min && !wrapped)
            KinoEditorGUI.SliderRow(paper, $"{id}_val", "Value", axis.Value, axis.Min, axis.Max,
                v => axis.Set(v), "F1");
        else
            KinoEditorGUI.FloatRow(paper, $"{id}_val", "Value", axis.Value, v => axis.Set(v));

        KinoEditorGUI.FloatRow(paper, $"{id}_min", "Min", axis.Min, v => axis.Min = v);
        KinoEditorGUI.FloatRow(paper, $"{id}_max", "Max", axis.Max, v => axis.Max = v);
        KinoEditorGUI.ToggleRow(paper, $"{id}_wrap", "Wrap", axis.Wrap, v => axis.Wrap = v);

        if (axis.Max <= axis.Min)
            KinoEditorGUI.Note(paper, $"{id}_range", "Max is not above Min, so this axis cannot move.", KinoNoteKind.Problem);

        KinoEditorGUI.EnumRow(paper, $"{id}_input", "Input", axis.Input, v => axis.Input = v);

        if (axis.Input == KinoInputSource.None)
        {
            KinoEditorGUI.Note(paper, $"{id}_noinput",
                "No input channel: this axis only moves when your own code assigns its Value.");
        }
        else if (KinoInput.IsDelta(axis.Input))
        {
            KinoEditorGUI.FloatRow(paper, $"{id}_gain", "Gain", axis.Gain, v => axis.Gain = v, "per px");
            KinoEditorGUI.Note(paper, $"{id}_deltanote",
                "A mouse reports how far it moved, so Gain alone sets the sensitivity - it is already the same at any framerate.");
        }
        else
        {
            KinoEditorGUI.FloatRow(paper, $"{id}_speed", "Max Speed", axis.MaxSpeed, v => axis.MaxSpeed = v, "/s");
            KinoEditorGUI.DampingRow(paper, $"{id}_accel", "Accel Time", axis.AccelTime, v => axis.AccelTime = v);
            KinoEditorGUI.DampingRow(paper, $"{id}_decel", "Decel Time", axis.DecelTime, v => axis.DecelTime = v);
        }

        if (axis.Input != KinoInputSource.None)
            KinoEditorGUI.ToggleRow(paper, $"{id}_invert", "Invert", axis.Invert, v => axis.Invert = v);

        KinoEditorGUI.FloatRow(paper, $"{id}_rwait", "Recenter Wait", axis.RecenterWait,
            v => axis.RecenterWait = Maths.Max(v, 0f), "s", 0f);

        if (axis.RecenterWait > 0f)
            KinoEditorGUI.DampingRow(paper, $"{id}_rtime", "Recenter Time", axis.RecenterTime, v => axis.RecenterTime = v);
    }
}

/// <summary>
/// Makes every <see cref="KinoAxis"/> field draw as an axis, including in the default property grid -
/// so a component that grows an axis later gets the same editor without one being written for it.
/// </summary>
[CustomPropertyEditor(typeof(KinoAxis))]
public class KinoAxisPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        if (value is not KinoAxis axis)
            return;

        if (!string.IsNullOrEmpty(label))
            EditorGUI.SectionHeader(paper, $"{id}_h", label);

        // The axis is a reference type edited in place; the callback exists to tell the grid something
        // changed, not to hand back a new instance.
        KinoAxisEditor.Draw(paper, id, axis);
        onChange(axis);
    }
}
