# Prowl.Kino

A fast, small virtual-camera library for the Prowl Engine, in the spirit of Unity's Cinemachine.

Stop animating cameras. Place a few shots, say which one matters, and Kino works out where the camera
goes and blends between them when the answer changes.

```csharp
using Prowl.Kino;
```

---

## Thirty seconds to a follow camera

Select your player and use **GameObject → Kino → Third Person Camera**. That builds the whole rig,
points it at what you had selected, and shows you the shot. If there is no brain on your camera yet,
**GameObject → Kino → Kino Brain On Main Camera** puts one there.

By hand, the same thing is three steps:

1. Add a **Kino Brain** to the GameObject that already has your `Camera`.
2. Make an empty GameObject, add a **Kino Camera**, and drag your player into its **Follow** and
   **Look At** slots.
3. Add a **Kino Transposer** and a **Kino Composer** to that same GameObject.

That is a damped third-person camera. Nothing else is required - no manager object to place, no
initialisation call, no update loop of your own.

To add a second shot, duplicate the Kino Camera and give it a different `Priority`. The higher one is
what you see; change the priority (or just enable and disable them) and the brain blends across.

```csharp
public KinoCamera CombatShot;
public KinoCamera ExplorationShot;

void EnterCombat()
{
    CombatShot.Priority = 20;      // now the highest - the brain blends to it
    ExplorationShot.Priority = 10;
}
```

---

## How it fits together

**Kino Brain** sits on the real `Camera`. Once a frame, in `LateUpdate`, it asks which Kino Camera
should be showing, evaluates it, blends if the answer just changed, and writes the result onto the
camera's `Transform` and lens.

**Kino Camera** is a shot. It has a Follow target, a Look At target, a lens and a priority. On its own
it is a locked-off shot: wherever you drag its GameObject is where the camera goes.

**Kino Components** are what make a shot move. Add them to the same GameObject as the Kino Camera and
they are picked up automatically. Each one declares a stage, and stages always run in this order:

| Stage        | Answers                        | How many run                       |
|--------------|--------------------------------|------------------------------------|
| **Body**     | Where is the camera?           | The first enabled one              |
| **Aim**      | Where does it point?           | The first enabled one              |
| **Noise**    | How does it wobble?            | All of them                        |
| **Finalize** | What has to be corrected?      | All of them                        |

So an aim component always sees the position the body component just chose, and a collider always sees
the finished shot. A camera with only an aim component still works - the position just stays put.

**Blending** happens whenever the winning camera changes. The incoming camera's `Blend In` decides how,
falling back to the brain's `Default Blend` when it is left on `Inherit`. Interrupt a blend halfway and
the picture carries on from exactly where it had got to.

When both shots have a Look At target, the blend interpolates *the subject* and rebuilds the angle
around it, so the subject stays pinned to the screen while the camera travels. Blending the two
rotations directly would swing it off screen and back on again.

---

## Working in the editor

**The shot is previewed live, without pressing play.** The brain drives the real camera in edit mode,
so the Game view always shows the winning shot while you work. It is snapped rather than damped -
authoring wants to see the shot itself, not a camera easing towards one - and it is non-destructive:
the camera's own position, rotation and lens are remembered the first time a shot takes over and put
back the moment previewing stops. Untick **Preview In Edit Mode** on the brain, or press **Release
Camera**, and the camera is yours again.

**Every camera's inspector shows what it sees.** The shot preview at the top of a Kino Camera renders
the real scene from that camera, whether or not it is live, with the composition guides drawn over it:
dead zone in green, soft zone in amber, the point the subject belongs on as a reticle, and a blue dot
where the subject actually is right now. Drag the target, watch the dot move against the zones.

**Solo** makes the selected camera live for as long as the button is lit, priorities ignored - the way
to check one shot in a scene full of them. **Make Live** is the permanent version: it raises the
priority above every other camera.

**The pipeline is a set of dropdowns.** At the bottom of a Kino Camera, Body and Aim are one-of - the
pipeline only ever runs the first, so picking from the dropdown swaps the component out. Noise and
Finalize genuinely stack, so those are multi-selects: tick to add, untick to remove. Every body and
aim component also says in its own inspector when it is sitting the frame out, and why: no follow
target, no path, or another component of the same stage got there first.

**The brain lists every other brain.** Two of them quietly driving two cameras is what split screen
wants and a mistake everywhere else, and it is otherwise invisible until the shot starts flickering -
so the brain's inspector always shows who else is enabled and what each one is showing. Click a row to
select it.

**In the scene view**, selecting a camera draws its frame, the lines to its targets, and a drag handle
on whatever its body component uses to place it - the follow offset, the orbit pivot, the tracked
point. Dragging that handle edits the component, not the camera's transform, so the shot keeps working
when the target moves. Selecting a Kino Path gives every waypoint a handle: drag them, add one where
you are looking, delete the selected one.

**The GameObject → Kino menu** builds rigs that already work: follow, third person, first person, 2D,
dolly with a path, target group.

---

## Body components - where the camera is

### Kino Transposer

Holds the camera at a fixed offset from the Follow target. The everyday follow camera.

| | |
|---|---|
| `BindingMode` | What the offset is measured against. The whole component, really - see below. |
| `FollowOffset` | Where to sit. Negative Z is behind the target. |
| `Damping` | Seconds to catch up, per axis of the binding frame. |
| `RotationDamping` | Seconds to come around when the target turns. |

Binding modes:

- **WorldSpace** - the offset is a world direction. The camera never orbits; the target can spin on the
  spot and the shot does not move.
- **LockToTarget** - the offset rides the target's full orientation. A camera bolted to a plane rolls
  with it.
- **LockToTargetWithWorldUp** - the offset follows the target's heading only. Pitch and roll do not tip
  the shot. The usual choice for characters.
- **SimpleFollowWithWorldUp** - the camera keeps whatever bearing it already had, only correcting its
  distance and height. It never pushes itself around behind the target's back, which is what makes it
  feel like a camera operator rather than a boom arm.

### Kino Orbital Transposer

A third-person camera in one component: heading, elevation and distance, with the player steering.

| | |
|---|---|
| `TargetOffset` | Where the orbit is centred relative to the target. Raise Y to orbit the head, not the feet. |
| `Radius` | How far out to orbit. |
| `Heading` / `Elevation` | Player-driven axes. See [Axes and input](#axes-and-input). |
| `Damping` | Seconds to catch up, per axis of the orbit frame. |
| `StartBehindTarget` | Line up behind the target the first time it runs. |

Give `Heading` a `RecenterWait` and the camera drifts back behind the target when the player stops
steering. Taking over from another shot deliberately does *not* recenter - a camera cut should never
yank the view out of where the player pointed it.

### Kino Framing Transposer

Keeps the subject at a chosen spot on screen by *moving* the camera rather than turning it. The
component for side-on and top-down games, and for any shot where the framing matters more than where
the camera is.

Two things decide where the camera ends up: **which way it faces**, and how far back it sits along
that. Facing is the one that is easy to miss - a distance on its own cannot say where a camera goes:

- **Camera Rotation** - whatever the camera is already pointing, which means this GameObject's own
  rotation, or an aim component when there is one. Right for 2D and top-down, where the angle is fixed
  and the camera only ever slides.
- **Fixed Direction** - a direction you state outright, so the camera always sits on the same side of
  the subject however it moves.
- **Behind Target** - the follow target's own heading, so the camera comes around as it turns.

| | |
|---|---|
| `Facing` / `FacingAngles` | Which way the camera looks while framing, and so which side it sits on. |
| `CameraDistance` | How far back from the subject the camera sits. |
| `ScreenX` / `ScreenY` | Where the subject belongs. `0,0` is bottom-left, `1,1` is top-right. |
| `DeadZoneWidth` / `DeadZoneHeight` | Room to move before the camera reacts at all. |
| `SoftZoneWidth` / `SoftZoneHeight` | As far out as the subject is ever allowed to get. |
| `HorizontalDamping` / `VerticalDamping` / `DistanceDamping` | Seconds to catch up on each. |
| `LookaheadTime` | Seconds of movement to lead the subject by, so the camera looks where it is going. |
| `GroupFraming` | With a [target group](#kino-target-group), pull back to keep everyone in shot. |

Zones are fractions of the screen, measured from the point you asked for: `DeadZoneWidth = 0.2` lets the
subject wander a fifth of the screen either side before the camera bothers. A dead zone of zero is a
camera welded to its subject; widen it and the shot starts to breathe.

`TrackedObjectOffset` moves the point being framed, and the camera moves with it - that is what framing
a different point means. To aim off-centre *without* moving the camera, use the aim component's own
offset instead.

Rotation belongs to the aim stage. When an aim component is present this one neither writes it nor
reads it: writing it would restart the aim's damping every frame, and reading it would feed this
component's own result back through the aim - two controllers on one shot, which rings instead of
settling. With no aim component the rotation is the GameObject's own, and `Camera Rotation` facing
follows it live.

### Kino Tracked Dolly

Confines the camera to a [Kino Path](#kino-path). Either animate `PathPosition` for a scripted move, or
turn on `AutoDolly` and the camera slides along to keep up with the Follow target - a rail shot that
tracks a character down a corridor but can only travel where the rail goes.

Set `CameraUp` to `FromBody` and the path's banking tilts the shot.

### Kino Hard Lock To Target

Bolts the camera to the Follow target with an offset in the target's own axes. First-person heads,
cockpits, cameras mounted on vehicles.

---

## Aim components - where it points

### Kino Hard Look At

Points straight at the Look At target. `Damping` adds lag; zero is rigid.

### Kino Composer

Holds the subject at a spot on screen with room to move first - the same `ScreenX`/`ScreenY`, dead zone
and soft zone as the framing transposer, but corrected by turning the camera instead of moving it.

The dead zone is where the subject may wander unnoticed. The soft zone is as far out as it may ever
get: damping softens the move, but the subject never leaves the soft zone, however slow the damping is.

### Kino POV

Hands the aim to the player: two axes, mouse or stick, with limits and optional recentering. Needs no
Look At target. Pair it with **Kino Hard Lock To Target** for first person, or with a transposer for an
over-the-shoulder camera the player aims themselves.

`RelativeToFollowTarget` measures the angles from the target's facing instead of from the world, so
turning the character turns the view with it.

### Kino Same As Follow Target

Copies the Follow target's orientation, plus a fixed turn of your own. Whatever it is facing, the
camera faces.

---

## Noise - how it wobbles

### Kino Shake

Continuous procedural shake. Pick a `Preset` - `Handheld`, `HandheldMild`, `Wobble`, `Rumble` - or
choose `Custom` and set the six amplitude and frequency values yourself.

`AmplitudeGain` scales the whole thing, so animating it from 1 to 0 fades shake out. Give each camera a
different `Seed` and no two ever shake in step.

For a one-off jolt:

```csharp
shake.Pulse(strength: 2f, duration: 0.35f);
```

The signal is sampled from the clock rather than accumulated frame by frame, so it looks the same
whatever the framerate did on the way there.

---

## Finalize - corrections

### Kino Collider

Pulls the camera in when something gets between it and the subject, and lets it back out once the way
is clear. Without it a follow camera spends its life inside walls.

| | |
|---|---|
| `CollideAgainst` | Which layers block the camera. Leave everything on and it collides with the character it follows. |
| `CameraRadius` | How fat the camera is, so it stops short of a wall rather than clipping it. |
| `MinimumDistanceFromTarget` | Never closer than this, however tight the space. |
| `Damping` / `DampingWhenOccluded` | Seconds to come back out; seconds to duck in. Ducking in wants to be instant. |

Because it runs at Finalize, it only ever slides the camera along the line to the subject - the framing
the body and aim components worked out is left intact.

### Kino Confiner

Keeps the camera inside a box or a sphere, placed relative to its own GameObject. Stops a shot
wandering out of a room or past the edge of a level that was never built to be seen from there.

### Kino Impulse Listener

Lets a camera feel the shakes fired by `KinoImpulse` (below).

---

## Supporting pieces

### Kino Path

A smooth curve through waypoints for a camera to travel along. Waypoints are stored relative to the
path's GameObject, so moving it takes the whole path with it, and the curve runs through every waypoint
- what you place is exactly what the camera travels through.

Positions are measured in waypoints: `0` is the first, `1.5` is halfway between the second and third,
`MaxPosition` is the end. Turn on `Looped` to close the circuit.

```csharp
path.AddWaypoint(new Float3(0f, 2f, 0f));
Float3 point = path.EvaluatePosition(1.5f);
float nearest = path.FindClosestPoint(player.Transform.Position);
```

### Kino Target Group

Several objects treated as one target. Point a camera's Follow or Look At at the group's GameObject and
it tracks the whole group's centre - two fighters, a co-op party, a convoy. Each member has a `Weight`
(how much it pulls the centre) and a `Radius` (how much room to leave around it).

The group also reports how big it is, which is what lets `KinoFramingTransposer.GroupFraming` pull back
far enough to keep everyone in shot.

```csharp
group.Add(playerOne, weight: 1f, radius: 1.5f);
group.Add(playerTwo, weight: 1f, radius: 1.5f);
```

### Impulses

One-off shakes with a position in the world. Fire one from anywhere and every camera carrying a
**Kino Impulse Listener** feels it, harder if it is close and not at all if it is far away.

```csharp
KinoImpulse.Shake(explosion.Transform.Position, force: 1.5f, radius: 30f);
KinoImpulse.Generate(hit.Point, hit.Normal * 0.4f, duration: 0.25f);

KinoImpulse.GlobalGain = 0.5f;   // a "reduce camera shake" setting, in one line
```

Impulses live in a fixed-size ring, so firing them never allocates and never needs cleaning up.

### Axes and input

`KinoAxis` is a single number the player pushes around - a heading, a pitch, a zoom. It handles its own
input, limits, easing and recentering.

| | |
|---|---|
| `Min` / `Max` / `Wrap` | Limits, and whether to wrap around them or stop at them. |
| `Input` | Which channel drives it: mouse, wheel, either stick. |
| `Gain` | Units per unit of input, for mouse-style channels. |
| `MaxSpeed` / `AccelTime` / `DecelTime` | Units per second and easing, for stick-style channels. |
| `RecenterWait` / `RecenterTime` | Drift back to centre after the player lets go. |

Mouse and wheel channels report movement since the last frame, sticks report a held position, and each
is integrated the way it should be - a mouse flick turns the camera the same amount at 30fps and 240fps.

To route input through your own system instead of the raw devices:

```csharp
KinoInput.Provider = source => source switch
{
    KinoInputSource.MouseX => bindings.Look.X,
    KinoInputSource.MouseY => bindings.Look.Y,
    _ => 0f
};

KinoInput.Enabled = false;   // freeze every input-driven camera while a menu is open
```

---

## Recipes

**Third person, player-steered, ducking under scenery**

Kino Camera (Follow + Look At = player) with Kino Orbital Transposer, Kino Composer and Kino Collider.
Give the orbital's `Heading` a `RecenterWait` of about a second so the camera drifts back behind the
player when they let go.

**First person**

Kino Camera (Follow = the head bone or an empty parented to it) with Kino Hard Lock To Target and
Kino POV. No Look At target is needed.

**2D side-scroller**

Kino Camera (Follow = player) with Kino Framing Transposer. Set the lens `Orthographic`, `ScreenY` a
little above centre, a wide `DeadZoneWidth` so small hops are ignored, and a `LookaheadTime` of 0.2 or
so to lead the run.

**Cutscene move**

A Kino Path and a Kino Camera with Kino Tracked Dolly plus Kino Hard Look At. Animate `PathPosition`,
or tween it, and raise the camera's `Priority` to hand the shot over.

**Boss arena covering two fighters**

A Kino Target Group holding both, and a Kino Camera whose Follow and Look At are the group, with a
Kino Framing Transposer (`GroupFraming` on) and a Kino Composer.

**Cut, don't blend, for one shot**

Set that camera's `Blend In` style to `Cut`. Everything else keeps using the brain's default.

---

## Scripting

```csharp
KinoBrain brain = KinoCore.MainBrain;

brain.CameraActivated += camera => Debug.Log($"now showing {camera.GameObject.Name}");
brain.CameraCut += () => screenEffects.HideTheSeam();

bool blending = brain.IsBlending;      // and BlendWeight, 0 to 1
KinoCamera showing = brain.ActiveCamera;
KinoState onScreen = brain.CurrentState;
brain.ForceCut();                      // finish the running blend now

vcam.MoveToTop();                      // win the tie against equal priorities
vcam.Snap();                           // arrive composed, with no easing
vcam.Warp(delta);                      // the target teleported - bring the shot along
vcam.Evaluate();                       // where would this shot be, right now?
bool live = vcam.IsLive;

KinoCore.TopCamera();                  // whichever camera currently wins
KinoCore.Cameras;                      // every enabled camera
KinoCore.Solo = vcam;                  // force one live, priorities ignored; null gives them back
```

### Writing your own component

Subclass `KinoComponent`, declare a stage, and change the part of the shot you own:

```csharp
using Prowl.Kino;
using Prowl.Runtime;
using Prowl.Vector;

[AddComponentMenu("Kino/Body/Ceiling Camera")]
public class CeilingCamera : KinoComponent
{
    public float Height = 8f;
    public float Damping = 0.5f;

    public override KinoStage Stage => KinoStage.Body;

    // Opt out when the shot cannot work - the camera falls through to the next component.
    public override bool IsUsable => HasFollow;

    public override void Mutate(ref KinoState state, in KinoContext ctx)
    {
        Float3 wanted = FollowPosition + ctx.WorldUp * Height;
        state.Position += KinoMath.Damp(wanted - state.Position, Damping, ctx.DeltaTime);
    }
}
```

Two rules make a component behave properly:

- **Route all smoothing through `KinoMath.Damp`.** A damping time is the seconds to close 99% of the
  gap, and the curve is exponential, so the result is identical at any framerate.
- **Honour `ctx.DeltaTime <= 0` as "snap".** `KinoMath.Damp` already does. That is how a camera is
  *placed* when it goes live, and ignoring it is exactly why a camera slides in from where it was left
  instead of arriving composed. Cache damping history behind `ResetDamping()` so it can be thrown away.

`KinoState` carries `Position`, `Rotation`, `Lens`, the separate `PositionShake` and `RotationShake`
that noise adds, `ReferenceUp`, and `LookAtPoint`/`HasLookAt` - which is how blending knows what the
shot was about. `KinoContext` carries `DeltaTime`, `Aspect` and `WorldUp`.

---

## What makes it fast

- **Only what is on screen is evaluated.** The live camera, plus the outgoing one while a blend runs.
  Fifty shots in a scene cost the same as two. Idle cameras can be kept warm with `StandbyUpdate` when a
  heavily damped shot needs to arrive already settled, but the default costs nothing.
- **No scene searching, ever.** Cameras register themselves as they are enabled; picking the shot is a
  walk over a handful of known objects.
- **No allocation in the frame.** State is a struct, the pipeline is a cached array, shake is sampled
  from a stateless hash, and impulses live in a fixed ring.
- **One pipeline run per camera per frame**, however many brains or blends ask for it.
- **The screen solve is closed-form.** Placing a subject at a point on screen is solved outright rather
  than converged on, which is what lets a cut land composed on its very first frame.

---

## Files

```
Kino/
  KinoBrain.cs          drives the real Camera, owns blending
  KinoCamera.cs         a shot: targets, lens, priority, pipeline
  KinoComponent.cs      base class for everything in a pipeline
  KinoCore.cs           the registry, and who wins
  KinoState.cs          a shot, and the per-frame context
  KinoLens.cs           field of view, clip planes, dutch
  KinoBlend.cs          blend styles and curves
  KinoMath.cs           damping, angles, safe rotations
  KinoScreen.cs         world to screen, screen to rotation, zones
  KinoNoise.cs          the smooth random signal behind shake
  KinoAxis.cs           player-driven axes, and input routing
  KinoImpulse.cs        world-positioned one-off shakes
  KinoPath.cs           waypoint spline for dolly shots
  KinoTargetGroup.cs    several objects as one target
  KinoEnums.cs          the shared enums
  Body/                 Transposer, Orbital, Framing, Tracked Dolly, Hard Lock
  Aim/                  Composer, Hard Look At, POV, Same As Follow Target
  Noise/                Shake
  Extensions/           Collider, Confiner, Impulse Listener
  Editor/               inspectors, the shot preview, scene handles, the Kino menu
```

Everything under `Editor/` is compiled into the editor assembly and never ships in a build.
