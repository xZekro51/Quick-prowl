// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Editor.Core;
using Prowl.Editor.GUI;
using Prowl.Editor.GUI.SceneView;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino.Editor;

/// <summary>Shake: a preset picker, a test button, and the six numbers only when they are being used.</summary>
[CustomEditor(typeof(KinoShake))]
public class KinoShakeEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var shake = (KinoShake)target;
        if (shake.IsNotValid()) return;

        Undo.Snapshot(shake);

        KinoEditorGUI.EnumRow(paper, $"{id}_preset", "Preset", shake.Preset, v => shake.Preset = v);
        KinoEditorGUI.Note(paper, $"{id}_presetnote", shake.Preset switch
        {
            KinoShakePreset.Handheld => "Someone holding the camera.",
            KinoShakePreset.HandheldMild => "Barely there - steady, but not locked off.",
            KinoShakePreset.Wobble => "Slow, wide drift. Dreams, drunkenness, underwater.",
            KinoShakePreset.Rumble => "Fast and tight. Engines, machinery, a building coming down.",
            _ => "Your own amplitudes and frequencies, below."
        });

        KinoEditorGUI.SliderRow(paper, $"{id}_amp", "Amplitude Gain", shake.AmplitudeGain, 0f, 4f, v => shake.AmplitudeGain = v);
        KinoEditorGUI.SliderRow(paper, $"{id}_freq", "Frequency Gain", shake.FrequencyGain, 0f, 4f, v => shake.FrequencyGain = v);
        KinoEditorGUI.FieldRow(paper, $"{id}_seed", "Seed", typeof(int), shake.Seed, v => shake.Seed = (int)v!);

        if (shake.IsCustom)
        {
            KinoEditorGUI.Section(paper, $"{id}_pos", "Position", () =>
            {
                KinoEditorGUI.Float3Row(paper, $"{id}_pa", "Amplitude", shake.PositionAmplitude, v => shake.PositionAmplitude = v);
                KinoEditorGUI.Float3Row(paper, $"{id}_pf", "Frequency", shake.PositionFrequency, v => shake.PositionFrequency = v);
            });

            KinoEditorGUI.Section(paper, $"{id}_rot", "Rotation", () =>
            {
                KinoEditorGUI.Float3Row(paper, $"{id}_ra", "Amplitude", shake.RotationAmplitude, v => shake.RotationAmplitude = v);
                KinoEditorGUI.Float3Row(paper, $"{id}_rf", "Frequency", shake.RotationFrequency, v => shake.RotationFrequency = v);
            });
        }

        if (Application.IsPlaying)
            KinoEditorGUI.Button(paper, $"{id}_pulse", "Pulse", () => shake.Pulse(), EditorTheme.Amber400);
        else
            KinoEditorGUI.Note(paper, $"{id}_editnote",
                "Shake is frozen while editing so the scene stays still. It runs in play mode.");
    }
}

/// <summary>Collider: the layer mask is the field people get wrong, so it leads and is explained.</summary>
[CustomEditor(typeof(KinoCollider))]
public class KinoColliderEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var collider = (KinoCollider)target;
        if (collider.IsNotValid()) return;

        Undo.Snapshot(collider);
        KinoEditorGUI.PipelineStatus(paper, id, collider);

        KinoEditorGUI.FieldRow(paper, $"{id}_mask", "Collide Against", typeof(LayerMask), collider.CollideAgainst,
            v => collider.CollideAgainst = (LayerMask)v!);
        KinoEditorGUI.Note(paper, $"{id}_masknote",
            "Leave every layer on and the camera collides with the character it is following. Put the player on its own layer and exclude it.");

        KinoEditorGUI.FloatRow(paper, $"{id}_radius", "Camera Radius", collider.CameraRadius,
            v => collider.CameraRadius = v, "m", 0.01f);
        KinoEditorGUI.FloatRow(paper, $"{id}_min", "Minimum Distance", collider.MinimumDistanceFromTarget,
            v => collider.MinimumDistanceFromTarget = v, "m", 0f);

        KinoEditorGUI.Section(paper, $"{id}_damping", "Damping", () =>
        {
            KinoEditorGUI.DampingRow(paper, $"{id}_out", "Coming Back Out", collider.Damping, v => collider.Damping = v);
            KinoEditorGUI.DampingRow(paper, $"{id}_in", "Ducking In", collider.DampingWhenOccluded, v => collider.DampingWhenOccluded = v);

            if (collider.DampingWhenOccluded > 0f)
                KinoEditorGUI.Note(paper, $"{id}_innote",
                    "A camera that eases into a wall is inside it meanwhile. Ducking in usually wants to be instant.",
                    KinoNoteKind.Warning);
        });

        if (Application.IsPlaying && collider.IsOccluded)
            KinoEditorGUI.Note(paper, $"{id}_occluded", "Something is between the camera and its subject right now.", KinoNoteKind.Info);
    }
}

/// <summary>Confiner: shows only the shape's own fields.</summary>
[CustomEditor(typeof(KinoConfiner))]
public class KinoConfinerEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var confiner = (KinoConfiner)target;
        if (confiner.IsNotValid()) return;

        Undo.Snapshot(confiner);

        KinoEditorGUI.EnumRow(paper, $"{id}_shape", "Shape", confiner.Shape, v => confiner.Shape = v);
        KinoEditorGUI.Float3Row(paper, $"{id}_center", "Center", confiner.Center, v => confiner.Center = v);

        if (confiner.Shape == KinoConfineShape.Sphere)
            KinoEditorGUI.FloatRow(paper, $"{id}_radius", "Radius", confiner.Radius, v => confiner.Radius = v, "m", 0f);
        else
            KinoEditorGUI.Float3Row(paper, $"{id}_size", "Size", confiner.Size, v => confiner.Size = v);

        KinoEditorGUI.DampingRow(paper, $"{id}_damp", "Damping", confiner.Damping, v => confiner.Damping = v);
        KinoEditorGUI.Note(paper, $"{id}_note", "The volume is placed relative to this GameObject, and drawn as a gizmo in the scene view.");
    }
}

/// <summary>Impulse listener: two fields and the one line of code that makes it do anything.</summary>
[CustomEditor(typeof(KinoImpulseListener))]
public class KinoImpulseListenerEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var listener = (KinoImpulseListener)target;
        if (listener.IsNotValid()) return;

        Undo.Snapshot(listener);

        KinoEditorGUI.SliderRow(paper, $"{id}_gain", "Gain", listener.Gain, 0f, 4f, v => listener.Gain = v);
        KinoEditorGUI.ToggleRow(paper, $"{id}_ear", "Listen At Target", listener.ListenAtTarget, v => listener.ListenAtTarget = v);

        KinoEditorGUI.Note(paper, $"{id}_note",
            listener.ListenAtTarget
                ? "Impulses are felt from where the subject is, so a distant telephoto shot still shakes with what it is pointed at."
                : "Impulses are felt from where the camera is - closer means harder.");

        KinoEditorGUI.Note(paper, $"{id}_how", "Fire one from anywhere: KinoImpulse.Shake(position, force, duration, radius)");

        if (Application.IsPlaying)
            KinoEditorGUI.Button(paper, $"{id}_test", "Test Impulse", () =>
                KinoImpulse.Shake(listener.Transform.Position, 0.4f), EditorTheme.Amber400);
    }
}

/// <summary>Target group: a member list that can actually be edited, plus what it currently works out to.</summary>
[CustomEditor(typeof(KinoTargetGroup))]
public class KinoTargetGroupEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var group = (KinoTargetGroup)target;
        if (group.IsNotValid()) return;

        Undo.Snapshot(group);

        if (group.Members.Count == 0)
            KinoEditorGUI.Note(paper, $"{id}_empty",
                "No members yet. A camera pointed at this group falls back to this GameObject's own position.");
        else
            KinoEditorGUI.Note(paper, $"{id}_state",
                $"Centre is {group.Center.X:0.#}, {group.Center.Y:0.#}, {group.Center.Z:0.#} with a radius of {group.Radius:0.##} m.");

        for (int i = 0; i < group.Members.Count; i++)
        {
            KinoTargetGroup.Member member = group.Members[i];
            if (member == null)
                continue;

            int index = i;
            KinoEditorGUI.Section(paper, $"{id}_m{i}", $"Member {i + 1}", () =>
            {
                KinoEditorGUI.FieldRow(paper, $"{id}_m{index}_t", "Target", typeof(GameObject), member.Target,
                    v => member.Target = v as GameObject);
                KinoEditorGUI.SliderRow(paper, $"{id}_m{index}_w", "Weight", member.Weight, 0f, 4f, v => member.Weight = v);
                KinoEditorGUI.FloatRow(paper, $"{id}_m{index}_r", "Radius", member.Radius, v => member.Radius = v, "m", 0f);

                KinoEditorGUI.Button(paper, $"{id}_m{index}_x", "Remove", () =>
                {
                    Undo.Snapshot(group);
                    group.Members.RemoveAt(index);
                    group.Invalidate();
                    EditorSceneManager.MarkDirty();
                }, EditorTheme.Red400);
            });
        }

        KinoEditorGUI.Button(paper, $"{id}_add", "+ Add Member", () =>
        {
            Undo.Snapshot(group);
            group.Members.Add(new KinoTargetGroup.Member());
            group.Invalidate();
            EditorSceneManager.MarkDirty();
        });
    }
}

/// <summary>
/// Path: a waypoint list with the operations a path actually needs - insert after, remove, and adding
/// one where the scene view is looking.
/// </summary>
[CustomEditor(typeof(KinoPath))]
public class KinoPathEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var path = (KinoPath)target;
        if (path.IsNotValid()) return;

        Undo.Snapshot(path);

        KinoEditorGUI.ToggleRow(paper, $"{id}_loop", "Looped", path.Looped, v => path.Looped = v);
        KinoEditorGUI.FieldRow(paper, $"{id}_res", "Gizmo Resolution", typeof(int), path.GizmoResolution,
            v => path.GizmoResolution = Maths.Clamp((int)v!, 2, 64));

        if (!path.IsUsable)
            KinoEditorGUI.Note(paper, $"{id}_short",
                "A path needs at least two waypoints before a camera can travel along it.", KinoNoteKind.Problem);
        else
            KinoEditorGUI.Note(paper, $"{id}_len",
                $"{path.Waypoints.Count} waypoints, {path.SegmentCount} segments. Path positions run 0 to {path.MaxPosition:0.#}.");

        KinoEditorGUI.Note(paper, $"{id}_tool", "Waypoints can be dragged directly in the scene view while this is selected.");

        for (int i = 0; i < path.Waypoints.Count; i++)
        {
            KinoPath.Waypoint waypoint = path.Waypoints[i];
            if (waypoint == null)
                continue;

            int index = i;
            KinoEditorGUI.Section(paper, $"{id}_w{i}", $"Waypoint {i}", () =>
            {
                KinoEditorGUI.Float3Row(paper, $"{id}_w{index}_p", "Position", waypoint.Position, v => waypoint.Position = v);
                KinoEditorGUI.SliderRow(paper, $"{id}_w{index}_r", "Roll", waypoint.Roll, -180f, 180f, v => waypoint.Roll = v, "F0");

                var m = Origami.Current.Metrics;
                using (paper.Row($"{id}_w{index}_ops").Height(UnitValue.Auto)
                    .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.Spacing).Gap(m.Spacing).Enter())
                {
                    KinoEditorGUI.Button(paper, $"{id}_w{index}_ins", "Insert After", () =>
                    {
                        Undo.Snapshot(path);
                        Float3 next = index + 1 < path.Waypoints.Count
                            ? path.Waypoints[index + 1].Position
                            : waypoint.Position + new Float3(0f, 0f, 5f);

                        path.Waypoints.Insert(index + 1, new KinoPath.Waypoint
                        {
                            Position = (waypoint.Position + next) * 0.5f,
                            Roll = waypoint.Roll
                        });
                        EditorSceneManager.MarkDirty();
                    }, EditorTheme.Blue400, grow: false);

                    KinoEditorGUI.Button(paper, $"{id}_w{index}_del", "Remove", () =>
                    {
                        Undo.Snapshot(path);
                        path.Waypoints.RemoveAt(index);
                        EditorSceneManager.MarkDirty();
                    }, EditorTheme.Red400, grow: false);
                }
            });
        }

        KinoEditorGUI.Button(paper, $"{id}_add", "+ Add Waypoint", () =>
        {
            Undo.Snapshot(path);

            // Continues the path in the direction it was already going, so clicking add repeatedly
            // lays out a line rather than piling points on the origin.
            Float3 next = new(0f, 0f, 0f);
            int count = path.Waypoints.Count;
            if (count == 1)
                next = path.Waypoints[0].Position + new Float3(0f, 0f, 5f);
            else if (count > 1)
                next = path.Waypoints[count - 1].Position * 2f - path.Waypoints[count - 2].Position;

            path.Waypoints.Add(new KinoPath.Waypoint { Position = next });
            EditorSceneManager.MarkDirty();
        });
    }
}
