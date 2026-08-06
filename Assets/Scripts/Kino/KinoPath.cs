// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// A smooth curve through a list of waypoints for a camera to travel along. Drive it by hand for a
/// scripted move, or let <see cref="KinoTrackedDolly"/> slide the camera along it as a subject moves.
/// </summary>
/// <remarks>
/// <para>
/// Waypoints are stored relative to this GameObject, so moving or rotating it takes the whole path
/// with it. The curve runs through every waypoint - a catmull-rom spline - so what you place is
/// exactly what the camera travels through.
/// </para>
/// <para>
/// Positions along the path are measured in waypoints: 0 is the first, 1.5 is halfway between the
/// second and third, and <see cref="MaxPosition"/> is the end.
/// </para>
/// </remarks>
[AddComponentMenu("Kino/Kino Path")]
[ComponentIcon("\uf4d7")] // Route
public class KinoPath : MonoBehaviour
{
    /// <summary>One point on the path.</summary>
    public class Waypoint
    {
        /// <summary>Position, relative to the path's GameObject.</summary>
        public Float3 Position;

        /// <summary>Tilt in degrees as the camera passes through, for a banked corner.</summary>
        public float Roll;
    }

    /// <summary>The points the path runs through, in order.</summary>
    public List<Waypoint> Waypoints = [];

    /// <summary>Join the last waypoint back to the first, making a circuit.</summary>
    public bool Looped = false;

    /// <summary>How many line segments per waypoint the gizmo is drawn with.</summary>
    public int GizmoResolution = 12;

    /// <summary>How many waypoint-to-waypoint segments the path has. Zero when there is nothing to travel along.</summary>
    public int SegmentCount
    {
        get
        {
            int count = Waypoints.Count;
            if (count < 2)
                return 0;
            return Looped ? count : count - 1;
        }
    }

    /// <summary>The path position of the end of the path. Positions run from 0 to here.</summary>
    public float MaxPosition => SegmentCount;

    /// <summary>True when there is enough of a path to travel along.</summary>
    public bool IsUsable => SegmentCount > 0;

    /// <summary>Wraps or clamps a path position into range, depending on <see cref="Looped"/>.</summary>
    public float NormalizePosition(float position)
    {
        float max = MaxPosition;
        if (max <= 0f)
            return 0f;

        if (!Looped)
            return Maths.Clamp(position, 0f, max);

        position -= Maths.Floor(position / max) * max;
        return position;
    }

    /// <summary>The world-space point at <paramref name="position"/> along the path.</summary>
    public Float3 EvaluatePosition(float position)
    {
        if (!IsUsable)
            return Transform.Position;

        Resolve(position, out int index, out float u);
        Float3 local = CatmullRom(Point(index - 1), Point(index), Point(index + 1), Point(index + 2), u);
        return Transform.TransformPoint(local);
    }

    /// <summary>The world-space direction of travel at <paramref name="position"/> along the path.</summary>
    public Float3 EvaluateTangent(float position)
    {
        if (!IsUsable)
            return Transform.Forward;

        Resolve(position, out int index, out float u);
        Float3 local = CatmullRomDerivative(Point(index - 1), Point(index), Point(index + 1), Point(index + 2), u);
        Float3 world = Transform.TransformDirection(local);
        return KinoMath.SafeNormalize(world, Transform.Forward);
    }

    /// <summary>The banking, in degrees, at <paramref name="position"/> along the path.</summary>
    public float EvaluateRoll(float position)
    {
        if (!IsUsable)
            return 0f;

        Resolve(position, out int index, out float u);
        float a = Roll(index);
        float b = Roll(index + 1);
        return a + (b - a) * u;
    }

    /// <summary>
    /// The orientation of the path at <paramref name="position"/>: facing along the path, banked by
    /// its roll.
    /// </summary>
    public Quaternion EvaluateOrientation(float position, Float3 up)
    {
        Quaternion rotation = KinoMath.SafeLookRotation(EvaluateTangent(position), up, Transform.Rotation);
        float roll = EvaluateRoll(position);
        return Maths.Abs(roll) < KinoMath.Epsilon ? rotation : rotation * Quaternion.RotateZ(roll * Maths.Deg2Rad);
    }

    /// <summary>
    /// The path position closest to <paramref name="worldPoint"/>.
    /// </summary>
    /// <param name="stepsPerSegment">
    /// How finely each segment is sampled first. The result is then refined, so this only has to be
    /// fine enough to land on the right segment - eight is plenty for ordinary paths.
    /// </param>
    public float FindClosestPoint(Float3 worldPoint, int stepsPerSegment = 8)
    {
        if (!IsUsable)
            return 0f;

        int steps = Maths.Max(stepsPerSegment, 2);
        float step = 1f / steps;
        float max = MaxPosition;

        float bestPosition = 0f;
        float bestDistance = float.MaxValue;

        // Coarse pass over the whole path, then a couple of narrowing passes around the winner. Far
        // cheaper than sampling the whole path finely, and lands in the same place.
        for (float p = 0f; p <= max; p += step)
        {
            float distance = Float3.DistanceSquared(EvaluatePosition(p), worldPoint);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPosition = p;
            }
        }

        float window = step;
        for (int pass = 0; pass < 3; pass++)
        {
            window *= 0.5f;
            float before = NormalizePosition(bestPosition - window);
            float after = NormalizePosition(bestPosition + window);

            float distanceBefore = Float3.DistanceSquared(EvaluatePosition(before), worldPoint);
            float distanceAfter = Float3.DistanceSquared(EvaluatePosition(after), worldPoint);

            if (distanceBefore < bestDistance && distanceBefore <= distanceAfter)
            {
                bestDistance = distanceBefore;
                bestPosition = before;
            }
            else if (distanceAfter < bestDistance)
            {
                bestDistance = distanceAfter;
                bestPosition = after;
            }
        }

        return bestPosition;
    }

    /// <summary>Appends a waypoint, given in world space.</summary>
    public void AddWaypoint(Float3 worldPosition, float roll = 0f)
        => Waypoints.Add(new Waypoint { Position = Transform.InverseTransformPoint(worldPosition), Roll = roll });

    private void Resolve(float position, out int index, out float u)
    {
        position = NormalizePosition(position);
        index = (int)Maths.Floor(position);
        u = position - index;

        int segments = SegmentCount;
        if (index >= segments)
        {
            // Landing exactly on the end belongs to the last segment, at its far end.
            index = segments - 1;
            u = 1f;
        }
    }

    private Float3 Point(int index)
    {
        int count = Waypoints.Count;
        if (count == 0)
            return Float3.Zero;

        if (Looped)
        {
            index %= count;
            if (index < 0) index += count;
        }
        else
        {
            index = Maths.Clamp(index, 0, count - 1);
        }

        Waypoint waypoint = Waypoints[index];
        return waypoint == null ? Float3.Zero : waypoint.Position;
    }

    private float Roll(int index)
    {
        int count = Waypoints.Count;
        if (count == 0)
            return 0f;

        if (Looped)
        {
            index %= count;
            if (index < 0) index += count;
        }
        else
        {
            index = Maths.Clamp(index, 0, count - 1);
        }

        Waypoint waypoint = Waypoints[index];
        return waypoint == null ? 0f : waypoint.Roll;
    }

    private static Float3 CatmullRom(Float3 p0, Float3 p1, Float3 p2, Float3 p3, float u)
    {
        float u2 = u * u;
        float u3 = u2 * u;

        return 0.5f * ((2f * p1)
            + (-p0 + p2) * u
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2
            + (-p0 + 3f * p1 - 3f * p2 + p3) * u3);
    }

    private static Float3 CatmullRomDerivative(Float3 p0, Float3 p1, Float3 p2, Float3 p3, float u)
    {
        float u2 = u * u;

        return 0.5f * ((-p0 + p2)
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (2f * u)
            + (-p0 + 3f * p1 - 3f * p2 + p3) * (3f * u2));
    }

    public override void DrawGizmos()
    {
        if (!IsUsable)
            return;

        int steps = Maths.Max(GizmoResolution, 2);
        float max = MaxPosition;
        float step = 1f / steps;

        Float3 previous = EvaluatePosition(0f);
        for (float p = step; p <= max; p += step)
        {
            Float3 current = EvaluatePosition(p);
            Debug.DrawLine(previous, current, Color.Orange);
            previous = current;
        }

        if (Looped)
            Debug.DrawLine(previous, EvaluatePosition(0f), Color.Orange);

        for (int i = 0; i < Waypoints.Count; i++)
            Debug.DrawWireSphere(Transform.TransformPoint(Point(i)), 0.12f, Color.Yellow);
    }
}
