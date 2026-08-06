// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;

using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Quill;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino.Editor;

/// <summary>
/// Draws the screen composition - where the subject belongs, the dead zone it may drift inside, and
/// the soft zone it may never leave - as a picture instead of six numbers.
/// </summary>
/// <remarks>
/// The same guides are painted over the live shot preview and over the standalone widget, so the
/// numbers, the diagram and what the camera actually does are always the same thing.
/// </remarks>
public static class KinoCompositionGuide
{
    private static readonly Color32 s_deadZone = new(120, 220, 140, 190);
    private static readonly Color32 s_softZone = new(250, 200, 90, 170);
    private static readonly Color32 s_reticle = new(250, 250, 250, 230);
    private static readonly Color32 s_subject = new(120, 210, 255, 255);
    private static readonly Color32 s_thirds = new(255, 255, 255, 26);

    /// <summary>
    /// Paints the guides over a rectangle that is showing the camera's view.
    /// </summary>
    /// <param name="subject">
    /// Where the subject currently appears, in normalized device coordinates, when it is on screen.
    /// Drawn as a dot so the dead zone can be read against the thing it is meant to be holding.
    /// </param>
    public static void Draw(Canvas canvas, Rect rect, in KinoComposition composition, Float2? subject)
    {
        float x = (float)rect.Min.X;
        float y = (float)rect.Min.Y;
        float w = (float)rect.Size.X;
        float h = (float)rect.Size.Y;

        DrawThirds(canvas, x, y, w, h);

        // Screen space runs bottom-up, pixels run top-down.
        float cx = x + composition.ScreenX * w;
        float cy = y + (1f - composition.ScreenY) * h;

        // Soft zone first, so the tighter dead zone reads on top of it.
        DrawZone(canvas, cx, cy, composition.SoftZoneWidth * w, composition.SoftZoneHeight * h, s_softZone, 1f, x, y, w, h);
        DrawZone(canvas, cx, cy, composition.DeadZoneWidth * w, composition.DeadZoneHeight * h, s_deadZone, 1.5f, x, y, w, h);

        DrawReticle(canvas, cx, cy, s_reticle);

        if (subject.HasValue)
        {
            float sx = x + (subject.Value.X * 0.5f + 0.5f) * w;
            float sy = y + (1f - (subject.Value.Y * 0.5f + 0.5f)) * h;
            if (sx >= x && sx <= x + w && sy >= y && sy <= y + h)
            {
                canvas.CircleFilled(sx, sy, 4.5f, new Color32(0, 0, 0, 140));
                canvas.CircleFilled(sx, sy, 3f, s_subject);
            }
        }
    }

    /// <summary>
    /// A standalone composition diagram: a frame with the guides on it, dragged to move the point the
    /// subject should sit on. Used when there is no shot preview to draw the guides over.
    /// </summary>
    public static void Widget(Paper paper, string id, in KinoComposition composition,
        float height, Action<float, float> onMove)
    {
        KinoComposition captured = composition;

        var box = paper.Box(id)
            .Width(UnitValue.Stretch()).Height(height)
            .Margin(Origami.Current.Metrics.PaddingLarge, Origami.Current.Metrics.PaddingLarge, 0, Origami.Current.Metrics.Spacing)
            .Rounded(6)
            .BackgroundColor(System.Drawing.Color.FromArgb(255, 26, 26, 30))
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Clip()
            .StopEventPropagation();

        // Dragging anywhere in the frame moves the subject point there: the composition is what is
        // being edited, so the whole diagram is the control rather than a handle inside it.
        box.OnDragging(e =>
        {
            Float2 normalized = e.NormalizedPosition;
            onMove(Maths.Saturate((float)normalized.X), Maths.Saturate(1f - (float)normalized.Y));
        });

        box.OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
            Draw(canvas, r, captured, null)));
    }

    /// <summary>
    /// The whole composition block: the guides, then the numbers behind them. Shared by the two
    /// components that compose a shot, so a dead zone means the same thing and is edited the same way
    /// whether the camera turns or slides to hold it.
    /// </summary>
    public static void Section(Paper paper, string id, in KinoComposition composition, bool hasPreview)
    {
        KinoComposition c = composition;

        // The guides are already drawn over the live shot in the camera's own inspector; repeating
        // them here would be two pictures of the same thing.
        if (!hasPreview)
            Widget(paper, $"{id}_widget", c, 140f, (x, y) => c.Apply(x, y));

        KinoEditorGUI.SliderRow(paper, $"{id}_sx", "Screen X", c.ScreenX, 0f, 1f, v => c.Apply(v, c.ScreenY));
        KinoEditorGUI.SliderRow(paper, $"{id}_sy", "Screen Y", c.ScreenY, 0f, 1f, v => c.Apply(c.ScreenX, v));

        KinoEditorGUI.SliderRow(paper, $"{id}_dw", "Dead Zone Width", c.DeadZoneWidth, 0f, 1f,
            v => c.ApplyZones(v, c.DeadZoneHeight, c.SoftZoneWidth, c.SoftZoneHeight));
        KinoEditorGUI.SliderRow(paper, $"{id}_dh", "Dead Zone Height", c.DeadZoneHeight, 0f, 1f,
            v => c.ApplyZones(c.DeadZoneWidth, v, c.SoftZoneWidth, c.SoftZoneHeight));

        KinoEditorGUI.SliderRow(paper, $"{id}_sw", "Soft Zone Width", c.SoftZoneWidth, 0f, 2f,
            v => c.ApplyZones(c.DeadZoneWidth, c.DeadZoneHeight, v, c.SoftZoneHeight));
        KinoEditorGUI.SliderRow(paper, $"{id}_sh", "Soft Zone Height", c.SoftZoneHeight, 0f, 2f,
            v => c.ApplyZones(c.DeadZoneWidth, c.DeadZoneHeight, c.SoftZoneWidth, v));

        if (c.SoftZoneWidth < c.DeadZoneWidth || c.SoftZoneHeight < c.DeadZoneHeight)
            KinoEditorGUI.Note(paper, $"{id}_zoneorder",
                "A soft zone inside the dead zone does nothing extra - the soft zone is treated as at least the size of the dead zone.",
                KinoNoteKind.Warning);
    }

    private static void DrawThirds(Canvas canvas, float x, float y, float w, float h)
    {
        for (int i = 1; i <= 2; i++)
        {
            float tx = x + w * i / 3f;
            float ty = y + h * i / 3f;
            canvas.RectFilled(tx, y, 1f, h, s_thirds);
            canvas.RectFilled(x, ty, w, 1f, s_thirds);
        }
    }

    /// <summary>
    /// A zone rectangle, clipped to the frame. Zones are half-extents, and a zone wider than the
    /// screen simply means "never corrects on that axis" - so it is drawn running off the edge rather
    /// than pretending to be a smaller box.
    /// </summary>
    private static void DrawZone(Canvas canvas, float cx, float cy, float halfW, float halfH, Color32 color,
        float thickness, float x, float y, float w, float h)
    {
        if (halfW <= 0f && halfH <= 0f)
            return;

        float left = Maths.Max(cx - halfW, x);
        float right = Maths.Min(cx + halfW, x + w);
        float top = Maths.Max(cy - halfH, y);
        float bottom = Maths.Min(cy + halfH, y + h);

        if (right <= left || bottom <= top)
            return;

        canvas.RectFilled(left, top, right - left, thickness, color);
        canvas.RectFilled(left, bottom - thickness, right - left, thickness, color);
        canvas.RectFilled(left, top, thickness, bottom - top, color);
        canvas.RectFilled(right - thickness, top, thickness, bottom - top, color);
    }

    private static void DrawReticle(Canvas canvas, float cx, float cy, Color32 color)
    {
        const float arm = 7f;
        var shadow = new Color32(0, 0, 0, 120);

        canvas.RectFilled(cx - arm, cy - 1f, arm * 2f, 3f, shadow);
        canvas.RectFilled(cx - 1f, cy - arm, 3f, arm * 2f, shadow);
        canvas.RectFilled(cx - arm, cy, arm * 2f, 1f, color);
        canvas.RectFilled(cx, cy - arm, 1f, arm * 2f, color);
    }
}
