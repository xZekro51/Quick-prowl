// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Collections.Generic;

using Prowl.Runtime;

namespace Prowl.Kino;

/// <summary>
/// Keeps track of every live <see cref="KinoCamera"/> and <see cref="KinoBrain"/>, and answers the
/// only question that really matters each frame: which camera should we be looking at?
/// </summary>
/// <remarks>
/// Cameras register themselves when they are enabled, so there is no scene search anywhere in Kino -
/// picking the shot is a walk over a handful of already-known objects, and cameras that are switched
/// off are not even in the list.
/// </remarks>
public static class KinoCore
{
    private static readonly List<KinoCamera> s_cameras = [];
    private static readonly List<KinoBrain> s_brains = [];

    private static int s_sequence;
    private static int s_roundRobin;
    private static long s_lastTickFrame = -1;
    private static KinoCamera? s_solo;

    /// <summary>Every enabled camera, in the order they were enabled.</summary>
    public static IReadOnlyList<KinoCamera> Cameras => s_cameras;

    /// <summary>Every enabled brain.</summary>
    public static IReadOnlyList<KinoBrain> Brains => s_brains;

    /// <summary>The first enabled brain, which in a single-camera game is the only one.</summary>
    public static KinoBrain? MainBrain
    {
        get
        {
            for (int i = 0; i < s_brains.Count; i++)
                if (s_brains[i].IsValid())
                    return s_brains[i];
            return null;
        }
    }

    #region Registration

    internal static void Register(KinoCamera camera)
    {
        if (s_cameras.Contains(camera))
            return;

        camera.Sequence = ++s_sequence;
        s_cameras.Add(camera);
    }

    internal static void Unregister(KinoCamera camera) => s_cameras.Remove(camera);

    internal static void MoveToTop(KinoCamera camera) => camera.Sequence = ++s_sequence;

    internal static void RegisterBrain(KinoBrain brain)
    {
        if (!s_brains.Contains(brain))
            s_brains.Add(brain);
    }

    internal static void UnregisterBrain(KinoBrain brain) => s_brains.Remove(brain);

    #endregion

    /// <summary>
    /// Forces one camera live and ignores priority entirely while it is set. Null gives the priorities
    /// back their say.
    /// </summary>
    /// <remarks>
    /// This is what the inspector's Solo button drives, and it is just as usable from game code when
    /// something has to be shown right now regardless of what else is running.
    /// </remarks>
    public static KinoCamera? Solo
    {
        get => s_solo.IsValid() ? s_solo : null;
        set => s_solo = value;
    }

    /// <summary>
    /// The camera that should be live: <see cref="Solo"/> if one is set, otherwise the highest
    /// <see cref="KinoCamera.Priority"/>, and among equals the one enabled (or
    /// <see cref="KinoCamera.MoveToTop"/>'d) most recently.
    /// </summary>
    public static KinoCamera? TopCamera()
    {
        if (s_solo.IsValid() && s_solo.EnabledInHierarchy)
            return s_solo;

        KinoCamera? best = null;

        for (int i = 0; i < s_cameras.Count; i++)
        {
            KinoCamera candidate = s_cameras[i];
            if (candidate.IsNotValid() || !candidate.EnabledInHierarchy)
                continue;

            if (best.IsNotValid()
                || candidate.Priority > best.Priority
                || (candidate.Priority == best.Priority && candidate.Sequence > best.Sequence))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Per-frame housekeeping: ages the impulses and updates whichever idle cameras asked to be kept
    /// warm. Brains call this; it only does the work once however many of them there are.
    /// </summary>
    internal static void Tick(in KinoContext ctx)
    {
        long frame = Time.FrameCount;
        if (s_lastTickFrame == frame)
            return;
        s_lastTickFrame = frame;

        KinoImpulse.Advance(ctx.DeltaTime);

        UpdateStandbyCameras(ctx);
    }

    private static void UpdateStandbyCameras(in KinoContext ctx)
    {
        int count = s_cameras.Count;
        if (count == 0)
            return;

        bool robinDone = false;
        long frame = Time.FrameCount;

        // While authoring, every shot is evaluated so its gizmo sits where the camera would really go -
        // the reason to look at a scene full of cameras in the first place. The cost only exists in the
        // editor, where a handful of extra pipeline runs is nothing.
        bool previewAll = !Application.IsPlaying;

        // Walking from a rotating start means the round-robin cameras take it in turns: one per frame,
        // whatever the list looks like, and no bookkeeping per camera to keep in sync.
        for (int i = 0; i < count; i++)
        {
            KinoCamera camera = s_cameras[(s_roundRobin + i) % count];
            if (camera.IsNotValid() || !camera.EnabledInHierarchy || camera.LiveFrame >= frame)
                continue;

            if (previewAll || camera.StandbyUpdate == KinoStandbyUpdate.Always)
            {
                camera.UpdatePipeline(ctx);
            }
            else if (camera.StandbyUpdate == KinoStandbyUpdate.RoundRobin && !robinDone)
            {
                camera.UpdatePipeline(ctx);
                robinDone = true;
            }
        }

        s_roundRobin = (s_roundRobin + 1) % count;
    }
}
