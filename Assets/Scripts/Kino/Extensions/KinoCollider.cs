// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Pulls the camera in when something gets between it and the subject, and lets it back out once the
/// way is clear. Without this a follow camera spends its life inside walls.
/// </summary>
/// <remarks>
/// <para>
/// Runs after the shot has been composed, so the camera is only ever moved along the line to the
/// subject - the framing the body and aim components worked out is left intact.
/// </para>
/// <para>
/// Set <see cref="CollideAgainst"/> to the layers that should block the camera. Leaving it on
/// everything means the camera collides with the character it is following as well.
/// </para>
/// </remarks>
[AddComponentMenu("Kino/Extensions/Kino Collider")]
[ComponentIcon("\uf3ed")] // Shield
public class KinoCollider : KinoComponent
{
    /// <summary>Which layers can block the camera.</summary>
    public LayerMask CollideAgainst = LayerMask.Everything;

    /// <summary>How fat the camera is treated as being, so it stops short of a wall rather than clipping into it.</summary>
    public float CameraRadius = 0.2f;

    /// <summary>Never come closer to the subject than this, however tight the space is.</summary>
    public float MinimumDistanceFromTarget = 0.5f;

    /// <summary>Seconds to come back out once the way is clear. Slow enough to be calm, fast enough not to lag behind.</summary>
    public float Damping = 0.35f;

    /// <summary>Seconds to duck in when something appears. Usually zero - a camera that eases into a wall is inside it meanwhile.</summary>
    public float DampingWhenOccluded = 0f;

    private float _distance;
    private bool _initialised;

    /// <summary>True while something is between the camera and the subject.</summary>
    public bool IsOccluded { get; private set; }

    public override KinoStage Stage => KinoStage.Finalize;

    public override void ResetDamping() => _initialised = false;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        IsOccluded = false;

        KinoCamera? vcam = VirtualCamera;
        if (vcam.IsNotValid())
            return;

        // The subject is whatever the shot is about: what it looks at, or failing that what it follows.
        Float3 pivot = vcam.HasLookAt ? vcam.LookAtPosition : vcam.FollowPosition;
        if (!vcam.HasLookAt && !vcam.HasFollow)
            return;

        Float3 toCamera = state.Position - pivot;
        float distance = Float3.Length(toCamera);
        if (distance < KinoMath.Epsilon)
            return;

        Float3 direction = toCamera / distance;
        float minimum = Maths.Clamp(MinimumDistanceFromTarget, 0f, distance);
        float wanted = distance;

        Scene? scene = GameObject.Scene;
        if (scene.IsValid() && scene.Physics != null)
        {
            // Cast outwards from the subject rather than inwards from the camera: a camera that has
            // already ended up inside geometry still finds the wall it should be sitting in front of.
            float span = distance - minimum;
            if (span > KinoMath.Epsilon
                && scene.Physics.SphereCast(pivot + direction * minimum, Maths.Max(CameraRadius, 0.01f),
                    direction, span, out ShapeCastHit hit, CollideAgainst))
            {
                wanted = minimum + span * Maths.Saturate(hit.Fraction);
                IsOccluded = true;
            }
        }

        if (!_initialised || ctx.IsSnap)
        {
            _initialised = true;
            _distance = wanted;
        }
        else
        {
            float damping = wanted < _distance ? DampingWhenOccluded : Damping;
            _distance += KinoMath.Damp(wanted - _distance, damping, ctx.DeltaTime);
        }

        _distance = Maths.Clamp(_distance, minimum, distance);
        state.Position = pivot + direction * _distance;
    }

    public override void DrawComponentGizmos()
    {
        if (IsOccluded)
            Debug.DrawWireSphere(Transform.Position, Maths.Max(CameraRadius, 0.01f), Color.Red);
    }
}
