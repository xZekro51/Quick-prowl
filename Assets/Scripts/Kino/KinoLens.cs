// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// The lens a shot is taken through. Every <see cref="KinoCamera"/> carries one, and the
/// <see cref="KinoBrain"/> copies the blended result onto the real <see cref="Camera"/>, so a blend
/// between two cameras also blends focal length and clipping planes.
/// </summary>
public struct KinoLens
{
    /// <summary>Vertical field of view in degrees. Ignored when <see cref="Orthographic"/> is set.</summary>
    public float FieldOfView;

    /// <summary>
    /// Size of the orthographic view volume. Matches the engine's <see cref="Camera.OrthographicSize"/>,
    /// which is the full width <i>and</i> full height of what the camera sees - it does not scale with
    /// the window's aspect ratio. Ignored unless <see cref="Orthographic"/> is set.
    /// </summary>
    public float OrthographicSize;

    /// <summary>Nothing closer than this is drawn.</summary>
    public float NearClip;

    /// <summary>Nothing further than this is drawn.</summary>
    public float FarClip;

    /// <summary>
    /// Roll around the view axis, in degrees. Applied last, on top of whatever the aim component
    /// decided, so it tilts the horizon without disturbing the framing.
    /// </summary>
    public float Dutch;

    /// <summary>Projects without perspective. Use <see cref="OrthographicSize"/> instead of <see cref="FieldOfView"/>.</summary>
    public bool Orthographic;

    /// <summary>A sane 60 degree perspective lens.</summary>
    public static KinoLens Default => new()
    {
        FieldOfView = 60f,
        OrthographicSize = 5f,
        NearClip = 0.1f,
        FarClip = 1000f,
        Dutch = 0f,
        Orthographic = false
    };

    /// <summary>Reads the lens settings off a real camera.</summary>
    public static KinoLens FromCamera(Camera camera) => new()
    {
        FieldOfView = camera.FieldOfView,
        OrthographicSize = camera.OrthographicSize,
        NearClip = camera.NearClipPlane,
        FarClip = camera.FarClipPlane,
        Dutch = 0f,
        Orthographic = camera.IsOrthographic
    };

    /// <summary>Writes these settings onto a real camera. Dutch is not part of it - that lives in the rotation.</summary>
    public readonly void ApplyTo(Camera camera)
    {
        camera.FieldOfView = FieldOfView;
        camera.OrthographicSize = OrthographicSize;
        camera.NearClipPlane = NearClip;
        camera.FarClipPlane = FarClip;
        camera.ProjectionMode = Orthographic ? Camera.ProjectionType.Orthographic : Camera.ProjectionType.Perspective;
    }

    /// <summary>
    /// Interpolates two lenses. Projection mode cannot be interpolated, so it snaps to
    /// <paramref name="b"/> once past the halfway point.
    /// </summary>
    public static KinoLens Lerp(in KinoLens a, in KinoLens b, float t) => new()
    {
        FieldOfView = KinoMath.LerpUnclamped(a.FieldOfView, b.FieldOfView, t),
        OrthographicSize = KinoMath.LerpUnclamped(a.OrthographicSize, b.OrthographicSize, t),
        NearClip = KinoMath.LerpUnclamped(a.NearClip, b.NearClip, t),
        FarClip = KinoMath.LerpUnclamped(a.FarClip, b.FarClip, t),
        Dutch = KinoMath.LerpUnclamped(a.Dutch, b.Dutch, t),
        Orthographic = t < 0.5f ? a.Orthographic : b.Orthographic
    };

    /// <summary>Clamps the values the projection math cannot survive.</summary>
    public void Validate()
    {
        FieldOfView = Maths.Clamp(FieldOfView, 1f, 179f);
        OrthographicSize = Maths.Max(OrthographicSize, 0.01f);
        NearClip = Maths.Max(NearClip, 0.001f);
        FarClip = Maths.Max(FarClip, NearClip + 0.01f);
    }

    /// <summary>Half the vertical extent of the view at <paramref name="distance"/>, in world units.</summary>
    public readonly float HalfHeightAt(float distance)
        => Orthographic ? OrthographicSize * 0.5f : Maths.Tan(FieldOfView * 0.5f * Maths.Deg2Rad) * Maths.Abs(distance);

    /// <summary>
    /// Half the horizontal extent of the view at <paramref name="distance"/>, in world units.
    /// An orthographic lens ignores <paramref name="aspect"/>, exactly as the engine's projection does.
    /// </summary>
    public readonly float HalfWidthAt(float distance, float aspect)
        => Orthographic ? OrthographicSize * 0.5f : HalfHeightAt(distance) * aspect;
}
