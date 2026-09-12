# Prowl.Animate

A layered, parameter-driven animation state machine for the Prowl Game Engine, ported from the
Animator system on the engine's `additional-ui-components` branch into this project as a standalone
`Assets/Scripts` library — the same way `Prowl.Kino` and `Prowl.Tweening` live here.

Namespace: `Prowl.Animate` (runtime) and `Prowl.Animate.Editor` (editor-only).

## Layout

| File | What it holds |
| --- | --- |
| `AnimatorParameter.cs` | Parameters and transition conditions |
| `Motion.cs` | `Motion`, `ClipMotion`, `BlendTree` (1D / 2D, nestable) |
| `AnimatorState.cs` | States, transitions, events, layers, avatar masks |
| `AnimatorController.cs` | The `.animator` asset, code-first builder, refactoring helpers, validation |
| `AnimatorCompiler.cs` | The controller reduced to integer slots for the per-frame loop |
| `AnimatorRuntime.cs` | Pose buffers, skeleton binding, clip sampling |
| `Curves` | Not a file: clips store one multi-component `Prowl.Vector.AnimationCurve` per channel |
| `Animator.cs` | The `MonoBehaviour` that plays it all |
| `ClipPosePreview.cs` | Poses a rig from a clip at an arbitrary time, and puts it back |
| `AnimationClipImporter.cs` | Pulls clips out of model/animation files |
| `Editor/` | Importers, inspectors, the state-machine graph and the animation window (compiled into the `.Editor` assembly) |

## Quick start

```csharp
// Author a controller entirely in code - no asset required.
var controller = new AnimatorController("Player");
controller.AddFloat("Speed");
controller.AddTrigger("Jump");

var layer = controller.AddLayer("Base");
var locomotion = new BlendTree("Speed");
locomotion.AddChild(idleClip, 0f);
locomotion.AddChild(walkClip, 1f);
locomotion.AddChild(runClip,  2f);

layer.AddState("Locomotion", locomotion);
layer.AddState("Jump", jumpClip).Loop = false;
layer.AddAnyStateTransition("Jump").When("Jump", AnimatorConditionMode.If).WithDuration(0.1f);
layer.FindState("Jump")!.AddTransition("Locomotion").WithExitTime().WithDuration(0.2f);

var animator = gameObject.AddComponent<Animator>();
animator.Controller = controller;
```

Driving it from gameplay, allocation-free:

```csharp
static readonly int SpeedHash = Animator.StringToHash("Speed");

void Update() => animator.SetFloat(SpeedHash, velocity.Length());
```

Typed animation events, no reflection:

```csharp
animator.RegisterEvent("Footstep", e => audio.PlayOneShot(step, e.FloatParameter));
animator.OnStateEnter += (layer, state) => Debug.Log($"entered {state.Name}");
```

Playing a single clip with no controller at all:

```csharp
animator.Play(clip, loop: true);
animator.CrossFade(otherClip, 0.25f);
```

## The state-machine graph

Double-click an `.animator` asset, or use **Window ▸ Tools ▸ Animator Controller**. Create one with
**Assets ▸ Create ▸ Animation ▸ Animator Controller**.

Only one graph window is ever open: opening a second controller retargets the window you already
have. Two editors on one asset both hook project-save, and whichever writes second is holding an
instance the first one's reimport already disposed — so it silently threw its edits away. (A window
with unsaved work on a *different* controller is left alone and a second window opens.)

Reading the graph:

- **Entry** (green) points at the state the layer starts in. Drag its wire onto another state to
  change the default. **Any State** (purple) is the source for transitions that can interrupt from
  anywhere.
- Each state card shows its name, the motion it plays, and a status word — `entry`, `loop`, `once`,
  `playing`, `blending in` — so the graph reads correctly without relying on colour.
- **Auto Layout** arranges the layer left-to-right in the order the machine actually runs, following
  transitions out from Entry. States nothing can reach are parked in a final column. This runs
  automatically the first time a controller is opened whose states have never been positioned.
- **Frame** never zooms out past the point where the node widget stops drawing text, and centres what
  it framed at the zoom it settled on.
- The selected state's transition list spells out what fires each one, e.g.
  `→ Run   (Speed > 0.1, exit 0.9)`.

Editing:

- **+ State** on the toolbar drops one in the middle of the view; right-clicking the canvas adds one
  where you clicked.
- Drag from a state's right-hand port onto another state to add a transition, or use **Add Transition
  To** on a node's context menu.
- Right-click a node to set the default, duplicate, edit its clip, add a transition, or delete.
  Double-click one to open the clip it plays.
- A state whose clip slot is empty gets a **New Clip** button that creates the `.anim` next to the
  controller, assigns it and opens it.
- Node positions are saved with the asset — moving a node marks the controller unsaved, so Ctrl+S
  keeps your layout.
- The **Problems** section lists dangling transitions, missing parameters, unreachable states and
  duplicate blend thresholds. Click one to jump to the layer and state it is about.
- While a scene `Animator` is playing this controller the live state is highlighted green, the state
  being blended into is blue, and the parameter list drives the *real* values so you can debug a
  machine that will not leave a state.
- Graph edits apply to a running Animator immediately, keeping the play head and parameter values.
  Saving reimports the asset; the window re-matches your layer and selection by name across that, so
  Ctrl+S does not throw you back to layer 0.

## The animation window

**Window ▸ Tools ▸ Animation**, double-click a `.anim` asset, or use **Edit Clip** on a state.
Create a clip with **Assets ▸ Create ▸ Animation ▸ Animation Clip**.

A dope sheet and curve editor in the shape you'd expect from Unity's Animation window, with the
things people most often ask for:

| | |
| --- | --- |
| **Authoring from nothing** | **+ Property** mirrors the selected character's bone hierarchy as a menu; picking a bone adds its transform curves, keyed at the playhead with the bone's *current* pose. A bone's context menu adds just position, rotation or scale. Without this a clip created from the asset menu opened empty and could never be filled in. |
| **Rows are channels, not axes** | A bone has three rows - Position, Rotation, Scale - because that is exactly what the clip stores: one curve per channel, whose keys are shared by all of its components. Ten per-axis rows would have implied you could key Position.X on its own, and every such edit would silently have keyed Y and Z too. The curve view still draws one coloured line per component, and the key inspector edits the key as a vector. |
| **Preview that cleans up after itself** | Scrubbing poses the selected rig and snapshots every bone it touches, so closing the window (or toggling Preview off) restores the scene instead of leaving a character posed mid-animation. |
| **No hidden record mode** | Keys are only created when you ask. Moving something in the scene can never silently bake a key. |
| **Numeric key editing** | The selected key's exact time, frame, value, interpolation and tangents are fields, not mouse-nudges. |
| **Marquee selection** | Box-select across tracks, then move, shift, delete, copy/paste or re-interpolate the whole selection at once. |
| **Optional snapping** | Snap-to-frame is a toggle, so sub-frame keys are possible. |
| **Property filter** | A search box, because a rig with hundreds of channels is unusable without one. |
| **Clip utilities** | Fit length to the last key, reverse the clip, set length and sample rate directly. |
| **Missing-bone warning** | Tracks animating a bone the previewed rig does not have are shown in red. |
| **Extract** | A take embedded in a model has no file to save to. The window says so instead of offering a Save button that only logs a warning, and **Extract** writes it out as a standalone `.anim` beside the model and switches to it. |

Navigation: Ctrl+wheel zooms time around the cursor, Shift+wheel pans, wheel scrolls tracks, and
middle-drag moves the view. Drag in the ruler to scrub. Right-click for key and clip actions.

Keyboard, while the pointer is over the sheet (scoped by hover so a Delete key never fires in
whichever animation window happens to be visible in a docked layout):

| | |
| --- | --- |
| `Space` | play / pause |
| `←` `→` | step one frame |
| `K` | key every visible track at the playhead |
| `F` | frame the clip |
| `Del` | delete the selected keys |
| `Ctrl+C` / `Ctrl+V` | copy, paste at the playhead |
| `Ctrl+A` | select every key |
| `Esc` | clear the selection |

Stepped keys draw as squares and interpolated keys as diamonds, so constant segments are visible
without opening the curve view. A key's interpolation is **Linear**, **Stepped** or **Cubic**, and it
governs the segment to its *right* - that is where the engine's curve stores it. Tangents only do
anything on a cubic segment, so **Flatten Tangents** and **Auto Tangents** put the key on one.

The window's time axis runs over the clip's own span, from `StartTime` to `StartTime + Duration`, and
frame numbers count from the clip's first frame. A take cut out of a longer shared timeline - which
is what an embedded model animation usually is - has its first key several seconds in, and a ruler
that always started at zero showed such a clip as an empty sheet with every key off the right edge.

## Behaviour worth knowing

- **A channel a clip does not animate holds the bind pose, not zero.** Sampling returns the bone's
  reference transform for any of position/rotation/scale the clip leaves out. Rotation-only clips are
  the common case for a retargeted rig, and an identity default for the position of one collapses the
  whole skeleton onto its root the moment it plays.
- **Rotations are sampled as rotations.** Evaluation goes through the engine's curve, which slerps a
  linear segment and renormalises a cubic one. The sampler used to hold four scalar curves per bone
  and lerp them component-wise before normalising, which takes the short way round only by accident.
- **Exit time 1.0 fires on the loop boundary.** An exit time below 1 opens a window - the tail of
  every loop past that phase - which gives the transition's conditions a few frames to come true. An
  exit time of exactly 1 is an *instant*, so it is a crossing test: a one-second loop at 60fps
  advances 0.0167 of normalized time a frame and would step over any epsilon-width window every time.
  1.0 is the default, so this is the case most transitions hit.
- **A blend tree child's `TimeScale` divides into its contribution to the tree's length.** Children
  are sampled at a shared normalized phase so they stay in step; scaling a child's own clock would
  desynchronise the thing the tree exists to synchronise, so the state's play head moves faster
  instead. The same result, still in sync.
- **`AnimationClip.StartTime` is honoured.** Playback samples at `StartTime + phase * Duration`.

## Notes

- Renaming a parameter or a state through the controller (`RenameParameter` / `RenameState`, which is
  what the editor calls) repoints every condition, blend axis and transition that referenced it.
  Assigning to `.Name` directly does not.
- A 1D `BlendTree`'s children are sorted by threshold **when the controller is compiled**, so the
  order you add them in never affects which pair blends. `SortChildren()` is only a tidy-up for the
  authored list (the editor exposes it as a **Sort** button) — the editor deliberately does not
  re-sort while you type a threshold, because reordering the list under the cursor makes the field
  you are editing jump to a different child.
- Mutating a controller from code at runtime requires a follow-up `MarkStructureChanged()` so playing
  Animators recompile.
- Clip sampling is O(log n) in the key count (the engine's curve binary-searches), not the O(1)
  hint-resumed step the old per-axis sampler used. It stays allocation-free, and it is three searches
  per bone rather than ten; reimplementing the walk against the curve's packed spans to get the hint
  back would mean owning a second copy of the engine's wrap, extrapolation and tangent rules.
- `AnimatorController.Validate` flags two parameters whose names hash to the same value. They collapse
  to one slot at compile time and the second silently addresses the first, which presents as the
  Animator ignoring a `SetFloat`.
- `AvatarMask` includes a listed bone *and its descendants* by default; set `IncludeChildren = false`
  for exact-path masking.
- If the engine itself later ships an `Animator` in `Prowl.Runtime`, a file that has
  `using Prowl.Runtime;` and `using Prowl.Animate;` will need to disambiguate the name.
- **Never hold an asset in a raw field across frames.** Saving an asset reimports it, which disposes
  the instance and builds a new one, and the asset cache idle-sweeps anything nothing reports as in
  use — either way a raw reference becomes a corpse that throws `ObjectDisposedException` on the next
  property read. Hold an `AssetRef<T>` and re-read `.Res` each frame: that counts as activity *and*
  returns the post-reimport instance. Both editor windows and `CompiledClip` do this, and anything
  caching pointers into the asset's object graph (a key selection, a graph selection, a clip binding)
  must be dropped when the instance changes.
