// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Several objects treated as one target. Point a camera's Follow or Look At at the GameObject this
/// sits on and the camera tracks the whole group's centre instead of a single object - two fighters,
/// a co-op party, a convoy.
/// </summary>
/// <remarks>
/// The group also reports how big it is, which is what lets <see cref="KinoFramingTransposer"/> pull
/// back to keep everyone in shot.
/// </remarks>
[AddComponentMenu("Kino/Kino Target Group")]
[ComponentIcon("\uf0c0")] // Users
public class KinoTargetGroup : MonoBehaviour
{
    /// <summary>One object in the group.</summary>
    public class Member
    {
        /// <summary>The object to track. An empty slot is skipped.</summary>
        public GameObject? Target;

        /// <summary>How much this object pulls the centre towards itself. Zero leaves it out entirely.</summary>
        public float Weight = 1f;

        /// <summary>How much room to leave around this object when sizing the group.</summary>
        public float Radius = 0f;
    }

    /// <summary>The objects in the group.</summary>
    public List<Member> Members = [];

    private long _cachedFrame = -1;
    private Float3 _center;
    private float _radius;
    private bool _isEmpty = true;

    /// <summary>True when nothing in the group is worth tracking.</summary>
    public bool IsEmpty
    {
        get
        {
            Recalculate();
            return _isEmpty;
        }
    }

    /// <summary>The weighted centre of the group, in world space.</summary>
    public Float3 Center
    {
        get
        {
            Recalculate();
            return _center;
        }
    }

    /// <summary>Radius of a sphere around <see cref="Center"/> that contains every member.</summary>
    public float Radius
    {
        get
        {
            Recalculate();
            return _radius;
        }
    }

    /// <summary>Adds an object to the group.</summary>
    public void Add(GameObject target, float weight = 1f, float radius = 0f)
    {
        if (target.IsNotValid())
            return;

        Members.Add(new Member { Target = target, Weight = weight, Radius = radius });
        _cachedFrame = -1;
    }

    /// <summary>Removes every entry pointing at <paramref name="target"/>. Returns how many went.</summary>
    public int Remove(GameObject target)
    {
        int removed = Members.RemoveAll(m => ReferenceEquals(m.Target, target));
        if (removed > 0)
            _cachedFrame = -1;
        return removed;
    }

    /// <summary>Forgets this frame's cached centre, for when members move after it was first read.</summary>
    public void Invalidate() => _cachedFrame = -1;

    private void Recalculate()
    {
        // Every component asking for the centre in the same frame gets the same answer for the price
        // of one pass; the group is walked at most once per frame however many cameras want it.
        long frame = Time.FrameCount;
        if (_cachedFrame == frame)
            return;
        _cachedFrame = frame;

        _center = Float3.Zero;
        _radius = 0f;
        _isEmpty = true;

        float totalWeight = 0f;
        for (int i = 0; i < Members.Count; i++)
        {
            Member m = Members[i];
            if (m == null || m.Target.IsNotValid() || m.Weight <= 0f)
                continue;

            _center += m.Target.Transform.Position * m.Weight;
            totalWeight += m.Weight;
            _isEmpty = false;
        }

        if (_isEmpty)
        {
            _center = Transform.Position;
            return;
        }

        _center /= totalWeight;

        for (int i = 0; i < Members.Count; i++)
        {
            Member m = Members[i];
            if (m == null || m.Target.IsNotValid() || m.Weight <= 0f)
                continue;

            float reach = Float3.Distance(m.Target.Transform.Position, _center) + Maths.Max(m.Radius, 0f);
            if (reach > _radius)
                _radius = reach;
        }
    }

    public override void DrawGizmos()
    {
        if (IsEmpty)
            return;

        Debug.DrawWireSphere(Center, Maths.Max(Radius, 0.05f), Color.Cyan);
        for (int i = 0; i < Members.Count; i++)
        {
            Member m = Members[i];
            if (m == null || m.Target.IsNotValid() || m.Weight <= 0f)
                continue;

            Debug.DrawLine(Center, m.Target.Transform.Position, Color.Cyan);
        }
    }
}
