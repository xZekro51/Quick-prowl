// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Linq;

using Prowl.Editor;
using Prowl.Editor.Core;
using Prowl.Editor.GUI.SceneView;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Kino.Editor;

/// <summary>
/// Creating a shot from the GameObject menu, already wired up. Every entry builds a rig that works the
/// moment it exists rather than a bare component waiting to be configured.
/// </summary>
public static class KinoMenuItems
{
    [MenuItem("GameObject/Kino/Kino Camera", priority: 70, Icon = "\uf030", Separator = true)]
    private static void CreateCamera()
    {
        GameObject go = Create("Kino Camera");
        go.AddComponent<KinoCamera>();
        Finish(go);
    }

    [MenuItem("GameObject/Kino/Follow Camera", priority: 71, Icon = "\uf0b2")]
    private static void CreateFollowCamera()
    {
        GameObject go = Create("Follow Camera");
        KinoCamera vcam = go.AddComponent<KinoCamera>();
        go.AddComponent<KinoTransposer>();
        go.AddComponent<KinoComposer>();
        AimAtSelection(vcam);
        Finish(go);
    }

    [MenuItem("GameObject/Kino/Third Person Camera", priority: 72, Icon = "\ue4bb")]
    private static void CreateThirdPersonCamera()
    {
        GameObject go = Create("Third Person Camera");
        KinoCamera vcam = go.AddComponent<KinoCamera>();

        KinoOrbitalTransposer orbital = go.AddComponent<KinoOrbitalTransposer>();
        orbital.Heading.RecenterWait = 1.5f;

        go.AddComponent<KinoComposer>();
        go.AddComponent<KinoCollider>();
        AimAtSelection(vcam);
        Finish(go);
    }

    [MenuItem("GameObject/Kino/First Person Camera", priority: 73, Icon = "\uf06e")]
    private static void CreateFirstPersonCamera()
    {
        GameObject go = Create("First Person Camera");
        KinoCamera vcam = go.AddComponent<KinoCamera>();
        go.AddComponent<KinoHardLockToTarget>();
        go.AddComponent<KinoPOV>();

        // First person aims where the player is looking, so only Follow means anything here.
        GameObject? selected = Selection.GetSelected<GameObject>().FirstOrDefault();
        if (selected.IsValid() && !ReferenceEquals(selected, go))
            vcam.Follow = selected;

        Finish(go);
    }

    [MenuItem("GameObject/Kino/2D Camera", priority: 74, Icon = "\uf125")]
    private static void Create2DCamera()
    {
        GameObject go = Create("2D Camera");
        KinoCamera vcam = go.AddComponent<KinoCamera>();
        vcam.Lens.Orthographic = true;

        KinoFramingTransposer framing = go.AddComponent<KinoFramingTransposer>();
        framing.DeadZoneWidth = 0.15f;
        framing.DeadZoneHeight = 0.15f;
        framing.LookaheadTime = 0.2f;

        AimAtSelection(vcam, followOnly: true);
        Finish(go);
    }

    [MenuItem("GameObject/Kino/Dolly Camera With Path", priority: 75, Icon = "\uf4d7", Separator = true)]
    private static void CreateDollyCamera()
    {
        GameObject pathGo = Create("Kino Path");
        KinoPath path = pathGo.AddComponent<KinoPath>();
        path.Waypoints.Add(new KinoPath.Waypoint { Position = new Float3(-5f, 0f, 0f) });
        path.Waypoints.Add(new KinoPath.Waypoint { Position = new Float3(0f, 0f, 3f) });
        path.Waypoints.Add(new KinoPath.Waypoint { Position = new Float3(5f, 0f, 0f) });

        GameObject go = Create("Dolly Camera");
        KinoCamera vcam = go.AddComponent<KinoCamera>();

        KinoTrackedDolly dolly = go.AddComponent<KinoTrackedDolly>();
        dolly.Path = path;
        dolly.AutoDolly = true;

        go.AddComponent<KinoComposer>();
        AimAtSelection(vcam);
        Finish(go);
    }

    [MenuItem("GameObject/Kino/Target Group", priority: 76, Icon = "\uf0c0")]
    private static void CreateTargetGroup()
    {
        GameObject go = Create("Kino Target Group");
        KinoTargetGroup group = go.AddComponent<KinoTargetGroup>();

        // Anything already selected is presumably what the group is for.
        foreach (GameObject selected in Selection.GetSelected<GameObject>())
            if (selected.IsValid() && !ReferenceEquals(selected, go))
                group.Add(selected);

        Finish(go);
    }

    [MenuItem("GameObject/Kino/Kino Brain On Main Camera", priority: 90, Icon = "\uf03d", Separator = true)]
    private static void AddBrainToCamera()
    {
        Camera? camera = FindCamera();
        if (camera.IsNotValid())
        {
            Debug.LogWarning("[Kino] No Camera in the scene to put a brain on.");
            return;
        }

        if (camera!.GameObject.GetComponent<KinoBrain>().IsValid())
        {
            Debug.Log("[Kino] That camera already has a brain.");
            Selection.Select(camera.GameObject);
            return;
        }

        Undo.Snapshot(camera.GameObject);
        camera.GameObject.AddComponent<KinoBrain>();
        Selection.Select(camera.GameObject);
        EditorSceneManager.MarkDirty();
    }

    /// <summary>Only offered while there is a camera to put it on.</summary>
    [MenuItem("GameObject/Kino/Kino Brain On Main Camera", isValidate: true)]
    private static bool ValidateAddBrain() => FindCamera().IsValid();

    private static Camera? FindCamera()
    {
        Scene? scene = Scene.Current;
        if (scene.IsNotValid())
            return null;

        // The lowest depth renders last and is the one people mean by "the main camera".
        Camera? best = null;
        foreach (Camera? camera in scene.FindObjectsOfType<Camera>())
        {
            // Hidden rigs - Kino's own shot preview among them - are not the scene's camera.
            if (camera.IsNotValid() || camera.GameObject.HideFlags.HasFlag(HideFlags.HideAndDontSave))
                continue;

            if (best.IsNotValid() || camera.Depth > best!.Depth)
                best = camera;
        }

        return best;
    }

    private static GameObject Create(string name)
    {
        var go = new GameObject(name);
        Scene.Current.Add(go);
        Undo.RegisterCreatedObject(go, $"Create {name}");
        return go;
    }

    /// <summary>Points a new shot at whatever was selected when it was created.</summary>
    private static void AimAtSelection(KinoCamera vcam, bool followOnly = false)
    {
        GameObject? selected = Selection.GetSelected<GameObject>().FirstOrDefault();
        if (selected.IsNotValid() || ReferenceEquals(selected, vcam.GameObject))
            return;

        vcam.Follow = selected;
        if (!followOnly)
            vcam.LookAt = selected;
    }

    private static void Finish(GameObject go)
    {
        Selection.Select(go);
        EditorSceneManager.MarkDirty();
    }
}
