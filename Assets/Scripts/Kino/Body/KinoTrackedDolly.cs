// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Confines the camera to a <see cref="KinoPath"/>. Either park it at a fixed spot on the path and
/// animate that, or let it slide along to keep up with the follow target.
/// </summary>
/// <remarks>
/// This is the rail shot: a camera that tracks a character down a corridor but can only travel where
/// the rail goes, so the shot stays composed however the character moves.
/// </remarks>
[AddComponentMenu("Kino/Body/Kino Tracked Dolly")]
[ComponentIcon("\uf4d7")] // Route
public class KinoTrackedDolly : KinoComponent
{
    /// <summary>The path to travel along.</summary>
    public KinoPath? Path;

    /// <summary>Where on the path the camera sits, in waypoints. Animate this for a scripted move.</summary>
    public float PathPosition = 0f;

    /// <summary>Slide along the path to stay level with the follow target instead of using <see cref="PathPosition"/>.</summary>
    public bool AutoDolly = false;

    /// <summary>How finely the path is searched when auto-dollying. Raise it for paths that double back on themselves.</summary>
    public int AutoDollyResolution = 8;

    /// <summary>Offset from the path, in the path's own axes at that point.</summary>
    public Float3 PathOffset = Float3.Zero;

    /// <summary>Seconds to catch up along the path. Damps how fast the camera travels, not where it ends up.</summary>
    public float PathDamping = 0.5f;

    /// <summary>Seconds to catch up on each axis when the path itself moves.</summary>
    public Float3 Damping = new(0.2f, 0.2f, 0.2f);

    /// <summary>Take the up direction from the path's banking, so a rolled path tilts the shot.</summary>
    public KinoUpMode CameraUp = KinoUpMode.World;

    private float _position;
    private bool _initialised;

    public override KinoStage Stage => KinoStage.Body;

    public override bool IsUsable => Path.IsValid() && Path.IsUsable;

    public override void ResetDamping() => _initialised = false;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (Path.IsNotValid() || !Path.IsUsable)
            return;

        float wanted = PathPosition;
        if (AutoDolly && HasFollow)
            wanted = Path.FindClosestPoint(FollowPosition, AutoDollyResolution);

        if (!_initialised || ctx.IsSnap)
        {
            _initialised = true;
            _position = wanted;
        }
        else
        {
            // Damped in path units and around the loop the short way, so a looped path does not send
            // the camera the long way round when the target crosses the seam.
            float gap = Path.Looped
                ? ShortestWayRound(_position, wanted, Path.MaxPosition)
                : wanted - _position;

            _position = Path.NormalizePosition(_position + KinoMath.Damp(gap, PathDamping, ctx.DeltaTime));
        }

        Quaternion pathRotation = Path.EvaluateOrientation(_position, ctx.WorldUp);
        Float3 desired = Path.EvaluatePosition(_position) + pathRotation * PathOffset;

        Float3 gapToPath = Quaternion.Inverse(pathRotation) * (desired - state.Position);
        state.Position += pathRotation * KinoMath.Damp(gapToPath, Damping, ctx.DeltaTime);

        if (CameraUp == KinoUpMode.FromBody)
            state.ReferenceUp = KinoMath.SafeNormalize(pathRotation * Float3.UnitY, ctx.WorldUp);
    }

    /// <summary>Signed distance from <paramref name="from"/> to <paramref name="to"/> around a loop of length <paramref name="length"/>.</summary>
    private static float ShortestWayRound(float from, float to, float length)
    {
        if (length <= 0f)
            return 0f;

        float delta = (to - from) % length;
        if (delta > length * 0.5f) delta -= length;
        else if (delta < -length * 0.5f) delta += length;
        return delta;
    }

    public override void DrawComponentGizmos()
    {
        if (Path.IsNotValid() || !Path.IsUsable)
            return;

        Debug.DrawWireSphere(Path.EvaluatePosition(_position), 0.15f, Color.Green);
    }
}
