// This file is part of Prowl.Animate, the animation state machine library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Prowl.Runtime;

namespace Prowl.Animate;

/// <summary>
/// A reusable, layered animation state machine asset - the data an <see cref="Animator"/> plays.
/// Holds the parameter set and one or more <see cref="AnimatorLayer"/>s.
///
/// <para><b>Improvements over a classic Animator Controller</b></para>
/// <list type="bullet">
/// <item>Fully constructable in code - no asset file required (see the builder methods). The same
/// object can be authored in the editor or generated at runtime.</item>
/// <item>Plain serializable data (Echo persists it automatically), so controllers are diff-friendly
/// and can be procedurally edited, merged, or unit-tested.</item>
/// <item>Parameters are addressed by stable hashes at runtime, making per-frame parameter writes
/// allocation-free.</item>
/// <item>Structural edits bump <see cref="StructureVersion"/>, so a running <see cref="Animator"/>
/// picks up graph changes without being restarted - you can retune a state machine while the game
/// is playing.</item>
/// </list>
///
/// <para>The controller is treated as immutable by the Animator's hot path: it is shared across
/// every Animator that references it, and all per-instance state lives on the component. Editing it
/// is legal at any time as long as <see cref="MarkStructureChanged"/> is called afterwards.</para>
/// </summary>
[CreateAssetMenu("Animator Controller", Extension = ".animator", Order = 3, Icon = "")] // sitemap
public sealed class AnimatorController : EngineObject
{
    public List<AnimatorControllerParameter> Parameters = new();

    public List<AnimatorLayer> Layers = new();

    /// <summary>
    /// Bumped whenever the graph's shape changes. Animators compare it against the version they
    /// compiled from and rebuild their cached, index-resolved copy when it moves. Not serialized -
    /// it only has to be consistent within a session.
    /// </summary>
    [NonSerialized] private int _structureVersion;

    public int StructureVersion => _structureVersion;

    public AnimatorController() : base("New AnimatorController") { }

    public AnimatorController(string name) : base(name) { }

    /// <summary>The base (first) layer, or null if the controller has none yet.</summary>
    public AnimatorLayer? BaseLayer => Layers.Count > 0 ? Layers[0] : null;

    /// <summary>
    /// Tell every playing <see cref="Animator"/> that the graph changed and its compiled copy is
    /// stale. Call this after mutating states, transitions, parameters or motions at runtime.
    /// </summary>
    public void MarkStructureChanged() => _structureVersion++;

    // ============================================================
    //  Code-first builder API
    // ============================================================

    /// <summary>Add a parameter and return it.</summary>
    public AnimatorControllerParameter AddParameter(string name, AnimatorControllerParameterType type)
    {
        var p = new AnimatorControllerParameter(name, type);
        Parameters.Add(p);
        MarkStructureChanged();
        return p;
    }

    /// <summary>Add a float parameter with a default value.</summary>
    public AnimatorControllerParameter AddFloat(string name, float defaultValue = 0f)
    {
        var p = AddParameter(name, AnimatorControllerParameterType.Float);
        p.DefaultFloat = defaultValue;
        return p;
    }

    /// <summary>Add an int parameter with a default value.</summary>
    public AnimatorControllerParameter AddInt(string name, int defaultValue = 0)
    {
        var p = AddParameter(name, AnimatorControllerParameterType.Int);
        p.DefaultInt = defaultValue;
        return p;
    }

    /// <summary>Add a bool parameter with a default value.</summary>
    public AnimatorControllerParameter AddBool(string name, bool defaultValue = false)
    {
        var p = AddParameter(name, AnimatorControllerParameterType.Bool);
        p.DefaultBool = defaultValue;
        return p;
    }

    /// <summary>Add a trigger parameter.</summary>
    public AnimatorControllerParameter AddTrigger(string name)
        => AddParameter(name, AnimatorControllerParameterType.Trigger);

    /// <summary>Add a layer and return it. The first layer added is the base layer.</summary>
    public AnimatorLayer AddLayer(string name)
    {
        var layer = new AnimatorLayer(name);
        Layers.Add(layer);
        MarkStructureChanged();
        return layer;
    }

    /// <summary>Find a parameter by name, or null.</summary>
    public AnimatorControllerParameter? FindParameter(string name)
    {
        foreach (var p in Parameters)
            if (p.Name == name) return p;
        return null;
    }

    /// <summary>Find a layer by name, or null.</summary>
    public AnimatorLayer? FindLayer(string name)
    {
        foreach (var l in Layers)
            if (l.Name == name) return l;
        return null;
    }

    // ============================================================
    //  Refactoring helpers
    // ============================================================

    /// <summary>
    /// Rename a parameter and repoint everything that referenced it - transition conditions, blend
    /// tree axes and per-state speed parameters. Renaming without this silently detaches every
    /// condition that used the old name, which is the single easiest way to break a controller.
    /// </summary>
    public void RenameParameter(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) return;

        var param = FindParameter(oldName);
        if (param == null) return;
        param.Name = newName;

        foreach (var layer in Layers)
        {
            foreach (var (_, t) in layer.AllTransitions())
                foreach (var c in t.Conditions)
                    if (c.Parameter == oldName) c.Parameter = newName;

            foreach (var state in layer.States)
            {
                if (state.SpeedParameter == oldName) state.SpeedParameter = newName;
                RenameInMotion(state.Motion, oldName, newName);
            }
        }
        MarkStructureChanged();
    }

    private static void RenameInMotion(Motion? motion, string oldName, string newName)
    {
        if (motion is not BlendTree tree) return;
        if (tree.BlendParameter == oldName) tree.BlendParameter = newName;
        if (tree.BlendParameterY == oldName) tree.BlendParameterY = newName;
        foreach (var child in tree.Children)
            RenameInMotion(child.Motion, oldName, newName);
    }

    /// <summary>
    /// Rename a state within a layer and repoint the transitions (and the layer's default state)
    /// that targeted it by name.
    /// </summary>
    public void RenameState(AnimatorLayer layer, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(newName) || oldName == newName) return;

        var state = layer.FindState(oldName);
        if (state == null || layer.FindState(newName) != null) return;

        if (layer.DefaultState == oldName) layer.DefaultState = newName;
        foreach (var (_, t) in layer.AllTransitions())
            if (t.DestinationState == oldName) t.DestinationState = newName;

        state.Name = newName;
        MarkStructureChanged();
    }

    /// <summary>Remove a state along with every transition that pointed at it.</summary>
    public void RemoveState(AnimatorLayer layer, AnimatorState state)
    {
        layer.States.Remove(state);
        layer.AnyStateTransitions.RemoveAll(t => t.DestinationState == state.Name);
        foreach (var other in layer.States)
            other.Transitions.RemoveAll(t => t.DestinationState == state.Name);

        if (layer.DefaultState == state.Name)
            layer.DefaultState = layer.States.Count > 0 ? layer.States[0].Name : string.Empty;

        MarkStructureChanged();
    }

    /// <summary>Remove a parameter and every condition that tested it.</summary>
    public void RemoveParameter(AnimatorControllerParameter parameter)
    {
        Parameters.Remove(parameter);
        foreach (var layer in Layers)
            foreach (var (_, t) in layer.AllTransitions())
                t.Conditions.RemoveAll(c => c.Parameter == parameter.Name);
        MarkStructureChanged();
    }

    // ============================================================
    //  Validation
    // ============================================================

    /// <summary>
    /// Collect everything wrong with the graph: dangling transitions, missing motions, conditions on
    /// parameters that no longer exist, states nothing can reach. Pure data analysis - the editor
    /// surfaces it as banners, and gameplay code can assert on it in a test.
    /// </summary>
    public void Validate(List<AnimatorIssue> into)
    {
        into.Clear();
        if (Layers.Count == 0)
            into.Add(new AnimatorIssue(AnimatorIssueSeverity.Warning, -1, null, "Controller has no layers."));

        var paramNames = new HashSet<string>(StringComparer.Ordinal);
        var paramHashes = new Dictionary<int, string>();
        foreach (var p in Parameters)
        {
            if (!paramNames.Add(p.Name))
                into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, -1, null,
                    $"Duplicate parameter name '{p.Name}'. Only the first one is reachable."));

            // Two names that hash the same collapse to one slot at compile time and the second one
            // silently addresses the first - the kind of bug that looks like the Animator ignoring
            // your SetFloat. Vanishingly rare, but free to rule out here.
            int hash = Animator.StringToHash(p.Name);
            if (paramHashes.TryGetValue(hash, out string? clash) && clash != p.Name)
                into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, -1, null,
                    $"Parameters '{clash}' and '{p.Name}' hash to the same value; setting either by hash writes the first."));
            else
                paramHashes[hash] = p.Name;
        }

        for (int li = 0; li < Layers.Count; li++)
        {
            var layer = Layers[li];
            var stateNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var s in layer.States)
                if (!stateNames.Add(s.Name))
                    into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, li, s.Name,
                        $"Duplicate state name '{s.Name}' - transitions can only reach the first one."));

            if (layer.States.Count > 0 && layer.FindState(layer.DefaultState) == null)
                into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, li, null,
                    $"Layer '{layer.Name}' has no valid default state, so it will never play anything."));

            foreach (var s in layer.States)
            {
                if (s.Motion == null)
                    into.Add(new AnimatorIssue(AnimatorIssueSeverity.Warning, li, s.Name,
                        $"State '{s.Name}' has no motion and will hold the reference pose."));
                else
                    ValidateMotion(s.Motion, li, s.Name, paramNames, into);

                if (!string.IsNullOrEmpty(s.SpeedParameter) && !paramNames.Contains(s.SpeedParameter))
                    into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, li, s.Name,
                        $"State '{s.Name}' scales its speed by missing parameter '{s.SpeedParameter}'."));
            }

            foreach (var (owner, t) in layer.AllTransitions())
            {
                string from = owner?.Name ?? "Any State";
                if (layer.FindState(t.DestinationState) == null)
                    into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, li, owner?.Name,
                        $"Transition {from} -> '{t.DestinationState}' points at a state that does not exist."));

                foreach (var c in t.Conditions)
                    if (!paramNames.Contains(c.Parameter))
                        into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, li, owner?.Name,
                            $"Transition {from} -> {t.DestinationState} tests missing parameter '{c.Parameter}'."));

                if (t.Conditions.Count == 0 && !t.HasExitTime && !t.Muted)
                    into.Add(new AnimatorIssue(AnimatorIssueSeverity.Warning, li, owner?.Name,
                        $"Transition {from} -> {t.DestinationState} has no conditions and no exit time, so it fires immediately."));
            }

            FindUnreachableStates(layer, li, into);
        }
    }

    private static void ValidateMotion(Motion motion, int layerIndex, string stateName,
        HashSet<string> paramNames, List<AnimatorIssue> into)
    {
        switch (motion)
        {
            case ClipMotion cm when cm.Clip.IsExplicitNull || (cm.Clip.AssetID == Guid.Empty && cm.Clip.Res == null):
                into.Add(new AnimatorIssue(AnimatorIssueSeverity.Warning, layerIndex, stateName,
                    $"State '{stateName}' has a clip motion with no clip assigned."));
                break;

            case BlendTree tree:
            {
                if (tree.Children.Count == 0)
                {
                    into.Add(new AnimatorIssue(AnimatorIssueSeverity.Warning, layerIndex, stateName,
                        $"Blend tree in '{stateName}' has no children."));
                    break;
                }

                if (!paramNames.Contains(tree.BlendParameter))
                    into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, layerIndex, stateName,
                        $"Blend tree in '{stateName}' is driven by missing parameter '{tree.BlendParameter}'."));

                if (tree.Is2D && !paramNames.Contains(tree.BlendParameterY))
                    into.Add(new AnimatorIssue(AnimatorIssueSeverity.Error, layerIndex, stateName,
                        $"Blend tree in '{stateName}' is driven by missing Y parameter '{tree.BlendParameterY}'."));

                // Out-of-order thresholds used to be reported here. They no longer matter: the compiler
                // sorts a 1D tree's children when it builds the blend, so authoring order is cosmetic.
                // Duplicate thresholds still do matter - two children at the same point make the pair
                // that gets picked arbitrary.
                if (tree.Type == BlendTreeType.Simple1D)
                {
                    var seen = new HashSet<float>();
                    foreach (var child in tree.Children)
                        if (!seen.Add(child.Threshold))
                        {
                            into.Add(new AnimatorIssue(AnimatorIssueSeverity.Warning, layerIndex, stateName,
                                $"1D blend tree in '{stateName}' has two children at threshold {child.Threshold:0.##}; one of them can never win."));
                            break;
                        }
                }

                foreach (var child in tree.Children)
                {
                    if (child.Motion == null)
                        into.Add(new AnimatorIssue(AnimatorIssueSeverity.Warning, layerIndex, stateName,
                            $"Blend tree in '{stateName}' has an empty child slot."));
                    else
                        ValidateMotion(child.Motion, layerIndex, stateName, paramNames, into);
                }
                break;
            }
        }
    }

    private static void FindUnreachableStates(AnimatorLayer layer, int layerIndex, List<AnimatorIssue> into)
    {
        if (layer.States.Count == 0) return;

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        void Seed(string name)
        {
            if (!string.IsNullOrEmpty(name) && layer.FindState(name) != null && reachable.Add(name))
                frontier.Enqueue(name);
        }

        Seed(layer.DefaultState);
        foreach (var t in layer.AnyStateTransitions)
            if (!t.Muted) Seed(t.DestinationState);

        while (frontier.Count > 0)
        {
            var state = layer.FindState(frontier.Dequeue())!;
            foreach (var t in state.Transitions)
                if (!t.Muted) Seed(t.DestinationState);
        }

        foreach (var s in layer.States)
            if (!reachable.Contains(s.Name))
                into.Add(new AnimatorIssue(AnimatorIssueSeverity.Warning, layerIndex, s.Name,
                    $"State '{s.Name}' cannot be reached from the default state or Any State."));
    }

    // ============================================================
    //  Binding helpers
    // ============================================================

    /// <summary>
    /// Walks the whole controller and collects the set of bone paths every referenced clip animates.
    /// The Animator uses this to size its pose buffers and resolve bone transforms once up front,
    /// avoiding per-frame name lookups. Clips that have not streamed in yet are skipped (the binding
    /// is rebuilt lazily as they arrive).
    /// </summary>
    public void CollectAnimatedBonePaths(HashSet<string> into)
    {
        foreach (var layer in Layers)
            foreach (var state in layer.States)
                CollectMotionBones(state.Motion, into);
    }

    private static void CollectMotionBones(Motion? motion, HashSet<string> into)
    {
        switch (motion)
        {
            case ClipMotion cm:
                var clip = cm.Clip.ResWeak; // don't force a blocking load here
                if (clip != null)
                    foreach (var bone in clip.Bones)
                        into.Add(bone.BoneName);
                break;

            case BlendTree bt:
                foreach (var child in bt.Children)
                    CollectMotionBones(child.Motion, into);
                break;
        }
    }
}

/// <summary>How badly a <see cref="AnimatorIssue"/> breaks the graph.</summary>
public enum AnimatorIssueSeverity
{
    /// <summary>The graph runs, but probably not the way it looks like it should.</summary>
    Warning,

    /// <summary>Something is definitively broken and will not run.</summary>
    Error,
}

/// <summary>One problem found by <see cref="AnimatorController.Validate"/>.</summary>
public readonly struct AnimatorIssue
{
    public readonly AnimatorIssueSeverity Severity;

    /// <summary>Layer the issue belongs to, or -1 when it is controller-wide.</summary>
    public readonly int LayerIndex;

    /// <summary>State the issue points at, or null when it is layer-wide.</summary>
    public readonly string? StateName;

    public readonly string Message;

    public AnimatorIssue(AnimatorIssueSeverity severity, int layerIndex, string? stateName, string message)
    {
        Severity = severity;
        LayerIndex = layerIndex;
        StateName = stateName;
        Message = message;
    }
}
