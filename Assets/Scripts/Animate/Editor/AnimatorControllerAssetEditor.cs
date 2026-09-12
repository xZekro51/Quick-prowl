// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;

using Prowl.Editor;
using Prowl.Editor.Inspector;
using Prowl.Editor.GUI;
using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;

using SColor = System.Drawing.Color;

namespace Prowl.Animate.Editor;

/// <summary>
/// Inspector for <see cref="AnimatorController"/> assets: what the controller contains, anything
/// wrong with it, and a button into the graph editor where the actual editing happens.
/// </summary>
[CustomAssetEditor(typeof(AnimatorController))]
public class AnimatorControllerAssetEditor : AssetImporterEditor
{
    private readonly List<AnimatorIssue> _issues = new();

    /// <summary>
    /// The controller the cached <see cref="_issues"/> were produced from. Validation walks every
    /// layer, state and transition and allocates a set and a queue doing it; running that on every
    /// inspector repaint is pure waste, and a reimport hands us a new instance anyway - which is
    /// exactly when the result can have changed.
    /// </summary>
    private AnimatorController? _validated;

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        EditorGUI.SectionHeader(paper, $"{id}_hdr", "Animator Controller");

        if (asset is not AnimatorController controller)
        {
            Origami.Label(paper, $"{id}_noasset", "Asset failed to load.").Show();
            return;
        }

        int stateCount = controller.Layers.Sum(l => l.States.Count);
        int transitionCount = controller.Layers.Sum(l => l.AnyStateTransitions.Count + l.States.Sum(s => s.Transitions.Count));

        Origami.Label(paper, $"{id}_layers", $"Layers: {controller.Layers.Count}").Show();
        Origami.Label(paper, $"{id}_states", $"States: {stateCount}").Show();
        Origami.Label(paper, $"{id}_trans", $"Transitions: {transitionCount}").Show();
        Origami.Label(paper, $"{id}_params", $"Parameters: {controller.Parameters.Count}").Show();

        if (!ReferenceEquals(_validated, controller))
        {
            _validated = controller;
            controller.Validate(_issues);
        }

        if (_issues.Count > 0)
        {
            paper.Box($"{id}_sp0").Height(8).IsNotInteractable();
            EditorGUI.SectionHeader(paper, $"{id}_iss_hdr", "Problems");

            for (int i = 0; i < _issues.Count && i < 12; i++)
            {
                var issue = _issues[i];
                SColor color = issue.Severity == AnimatorIssueSeverity.Error ? EditorTheme.Red400 : EditorTheme.Amber400;

                paper.Box($"{id}_iss{i}").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                    .MinHeight(20).Padding(8, 8, 4, 4).Margin(0, 0, 0, 4).Rounded(4).IsNotInteractable()
                    .BackgroundColor(SColor.FromArgb(26, color))
                    .BorderColor(SColor.FromArgb(90, color)).BorderWidth(1)
                    .Text(issue.Message, font).TextColor(EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Wrap(Prowl.Scribe.TextWrapMode.Wrap).Alignment(TextAlignment.MiddleLeft);
            }

            if (_issues.Count > 12)
                Origami.Label(paper, $"{id}_iss_more", $"... and {_issues.Count - 12} more").Show();
        }

        paper.Box($"{id}_sp").Height(8).IsNotInteractable();

        Origami.Button(paper, $"{id}_open", "Open Graph Editor",
            () => AnimatorGraphWindow.OpenFor(controller)).Primary().Width(180).Show();
    }
}
