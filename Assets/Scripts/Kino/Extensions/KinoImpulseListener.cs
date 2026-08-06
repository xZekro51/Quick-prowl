// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Lets a camera feel the shakes fired by <see cref="KinoImpulse"/>. Add it and explosions anywhere
/// in the world start reaching this shot, harder when they happen nearby.
/// </summary>
/// <remarks>
/// <code>
/// // anywhere in your game code
/// KinoImpulse.Shake(grenade.Transform.Position, force: 0.8f, radius: 20f);
/// </code>
/// </remarks>
[AddComponentMenu("Kino/Extensions/Kino Impulse Listener")]
[ComponentIcon("\uf0e7")] // Bolt
public class KinoImpulseListener : KinoComponent
{
    /// <summary>Scales everything this camera feels. Zero makes it deaf to impulses.</summary>
    public float Gain = 1f;

    /// <summary>
    /// Listen from where the subject is rather than from the camera. A distant telephoto shot of an
    /// explosion then shakes as hard as the thing it is pointed at, which usually reads better.
    /// </summary>
    public bool ListenAtTarget = false;

    public override KinoStage Stage => KinoStage.Finalize;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (Maths.Abs(Gain) < KinoMath.Epsilon)
            return;

        Float3 ear = state.Position;
        if (ListenAtTarget)
        {
            KinoCamera? vcam = VirtualCamera;
            if (vcam.IsValid() && vcam.HasLookAt)
                ear = vcam.LookAtPosition;
            else if (vcam.IsValid() && vcam.HasFollow)
                ear = vcam.FollowPosition;
        }

        if (!KinoImpulse.Sample(ear, out Float3 offset, out Float3 tilt))
            return;

        state.PositionShake += offset * Gain;
        state.RotationShake *= Quaternion.FromEuler(tilt * Gain);
    }
}
