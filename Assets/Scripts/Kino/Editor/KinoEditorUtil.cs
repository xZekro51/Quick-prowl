// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text;

using Prowl.Runtime;

namespace Prowl.Kino.Editor;

/// <summary>
/// The questions every Kino inspector asks about a camera: which components are on it, which of them
/// actually run, and why one of them is sitting the frame out.
/// </summary>
public static class KinoEditorUtil
{
    /// <summary>"Kino Orbital Transposer" from <c>KinoOrbitalTransposer</c>.</summary>
    public static string DisplayName(Type type)
    {
        string name = type.Name;
        var text = new StringBuilder(name.Length + 8);

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                text.Append(' ');
            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>Every Kino component on the camera's object, in the order the pipeline sees them.</summary>
    public static List<KinoComponent> Components(KinoCamera vcam)
    {
        var found = new List<KinoComponent>();
        if (vcam.IsNotValid())
            return found;

        foreach (KinoComponent component in vcam.GetComponents<KinoComponent>())
            if (component.IsValid())
                found.Add(component);

        return found;
    }

    /// <summary>
    /// The component that actually runs for a stage, applying the pipeline's own rule: enabled, usable,
    /// and first of its stage.
    /// </summary>
    public static KinoComponent? StageWinner(KinoCamera vcam, KinoStage stage)
    {
        foreach (KinoComponent component in Components(vcam))
            if (component.Stage == stage && component.EnabledInHierarchy && component.IsUsable)
                return component;

        return null;
    }

    /// <summary>Everything of a stage that is attached, whether it runs or not.</summary>
    public static List<KinoComponent> OfStage(KinoCamera vcam, KinoStage stage)
    {
        var found = new List<KinoComponent>();
        foreach (KinoComponent component in Components(vcam))
            if (component.Stage == stage)
                found.Add(component);

        return found;
    }

    /// <summary>
    /// Why a component reports itself unusable, in the words of whatever it is missing. Components
    /// opt out for one of a very few reasons, and guessing wrong here is better than saying nothing.
    /// </summary>
    public static string WhyUnusable(KinoComponent component)
    {
        KinoCamera? vcam = component.VirtualCamera;

        if (component is KinoTrackedDolly dolly)
        {
            if (dolly.Path.IsNotValid())
                return "Not running: no path assigned. Point Path at a Kino Path to give the camera something to travel along.";
            if (!dolly.Path.IsUsable)
                return "Not running: the path needs at least two waypoints.";
        }

        if (vcam.IsValid() && component.Stage == KinoStage.Body && !vcam.HasFollow)
            return "Not running: the camera has no Follow target. Set one on the Kino Camera above.";

        if (vcam.IsValid() && component.Stage == KinoStage.Aim && !vcam.HasLookAt)
            return "Not running: the camera has no Look At target. Set one on the Kino Camera above.";

        return "Not running: something it needs is missing.";
    }

    /// <summary>
    /// The screen composition a camera is set up for, wherever it comes from - the aim component that
    /// turns towards the subject, or the body component that slides to keep it framed.
    /// </summary>
    public static bool TryGetComposition(KinoCamera vcam, out KinoComposition composition)
    {
        composition = default;
        if (vcam.IsNotValid())
            return false;

        foreach (KinoComponent component in Components(vcam))
        {
            if (!component.EnabledInHierarchy)
                continue;

            if (component is KinoComposer composer)
            {
                composition = new KinoComposition(composer.ScreenX, composer.ScreenY,
                    composer.DeadZoneWidth, composer.DeadZoneHeight,
                    composer.SoftZoneWidth, composer.SoftZoneHeight, composer);
                return true;
            }

            if (component is KinoFramingTransposer framing)
            {
                composition = new KinoComposition(framing.ScreenX, framing.ScreenY,
                    framing.DeadZoneWidth, framing.DeadZoneHeight,
                    framing.SoftZoneWidth, framing.SoftZoneHeight, framing);
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A camera's screen composition, read off whichever component owns it, so the preview and the guide
/// widget do not each have to know which components have zones.
/// </summary>
public readonly struct KinoComposition
{
    public readonly float ScreenX;
    public readonly float ScreenY;
    public readonly float DeadZoneWidth;
    public readonly float DeadZoneHeight;
    public readonly float SoftZoneWidth;
    public readonly float SoftZoneHeight;

    /// <summary>The component these numbers belong to, for writing edits back.</summary>
    public readonly KinoComponent Owner;

    public KinoComposition(float screenX, float screenY, float deadWidth, float deadHeight,
        float softWidth, float softHeight, KinoComponent owner)
    {
        ScreenX = screenX;
        ScreenY = screenY;
        DeadZoneWidth = deadWidth;
        DeadZoneHeight = deadHeight;
        SoftZoneWidth = softWidth;
        SoftZoneHeight = softHeight;
        Owner = owner;
    }

    /// <summary>Writes an edited screen position back onto whichever component it came from.</summary>
    public void Apply(float screenX, float screenY)
    {
        switch (Owner)
        {
            case KinoComposer composer:
                composer.ScreenX = screenX;
                composer.ScreenY = screenY;
                break;
            case KinoFramingTransposer framing:
                framing.ScreenX = screenX;
                framing.ScreenY = screenY;
                break;
        }
    }

    /// <summary>Writes edited zone sizes back onto whichever component they came from.</summary>
    public void ApplyZones(float deadWidth, float deadHeight, float softWidth, float softHeight)
    {
        switch (Owner)
        {
            case KinoComposer composer:
                composer.DeadZoneWidth = deadWidth;
                composer.DeadZoneHeight = deadHeight;
                composer.SoftZoneWidth = softWidth;
                composer.SoftZoneHeight = softHeight;
                break;
            case KinoFramingTransposer framing:
                framing.DeadZoneWidth = deadWidth;
                framing.DeadZoneHeight = deadHeight;
                framing.SoftZoneWidth = softWidth;
                framing.SoftZoneHeight = softHeight;
                break;
        }
    }
}
