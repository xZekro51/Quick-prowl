// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Kino;

/// <summary>
/// Turns the camera to hold the subject at a chosen spot on the screen, with room to move before it
/// reacts. The aim to use for anything that should feel operated rather than bolted on.
/// </summary>
/// <remarks>
/// <para>
/// The dead zone is where the subject may wander with the camera ignoring it; the soft zone is as far
/// out as it is ever allowed to get. Between the two, damping decides how urgently the camera
/// catches up.
/// </para>
/// <para>
/// A dead zone of zero is <see cref="KinoHardLookAt"/> with damping. Widen it and the camera starts
/// to feel like someone is holding it.
/// </para>
/// </remarks>
[AddComponentMenu("Kino/Aim/Kino Composer")]
[ComponentIcon("\uf05b")] // Crosshairs
public class KinoComposer : KinoComponent
{
    /// <summary>World-space offset from the target to the point actually composed around.</summary>
    public Float3 TrackedObjectOffset = Float3.Zero;

    /// <summary>Horizontal screen position for the subject. 0 is the left edge, 1 the right.</summary>
    public float ScreenX = 0.5f;

    /// <summary>Vertical screen position for the subject. 0 is the bottom edge, 1 the top.</summary>
    public float ScreenY = 0.5f;

    /// <summary>Half-width of the region the subject may move in before the camera reacts, as a fraction of the screen.</summary>
    public float DeadZoneWidth = 0.05f;

    /// <summary>Half-height of the region the subject may move in before the camera reacts, as a fraction of the screen.</summary>
    public float DeadZoneHeight = 0.05f;

    /// <summary>Half-width of the region the subject may never leave, as a fraction of the screen.</summary>
    public float SoftZoneWidth = 0.8f;

    /// <summary>Half-height of the region the subject may never leave, as a fraction of the screen.</summary>
    public float SoftZoneHeight = 0.8f;

    /// <summary>Seconds to catch up horizontally.</summary>
    public float HorizontalDamping = 0.5f;

    /// <summary>Seconds to catch up vertically.</summary>
    public float VerticalDamping = 0.5f;

    public override KinoStage Stage => KinoStage.Aim;

    public override bool IsUsable => HasLookAt;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        if (!HasLookAt)
            return;

        Float3 point = LookAtPosition + TrackedObjectOffset;
        Float2 want = KinoScreen.FromScreenPoint(ScreenX, ScreenY);

        bool onScreen = KinoScreen.TargetToNdc(state.Position, state.Rotation, point, state.Lens, ctx.Aspect, out Float2 ndc, out _);

        // A subject behind the camera has no screen position to be soft about, so the camera simply
        // comes round to it - as it does on the frame the camera is placed.
        Float2 place = onScreen && !ctx.IsSnap
            ? new Float2(
                ndc.X - Correct(ndc.X - want.X, DeadZoneWidth, SoftZoneWidth, HorizontalDamping, ctx.DeltaTime),
                ndc.Y - Correct(ndc.Y - want.Y, DeadZoneHeight, SoftZoneHeight, VerticalDamping, ctx.DeltaTime))
            : want;

        state.Rotation = KinoScreen.RotateToPlace(state.Position, state.Rotation, point, place, state.Lens, ctx.Aspect, state.ReferenceUp);
        state.LookAtPoint = point;
        state.HasLookAt = true;
    }

    private static float Correct(float error, float deadZone, float softZone, float damping, float deltaTime)
    {
        float wanted = KinoScreen.ApplyZones(error, deadZone, Maths.Max(softZone, deadZone), out float hardLimit);
        return KinoScreen.AtLeast(KinoMath.Damp(wanted, damping, deltaTime), hardLimit);
    }
}
