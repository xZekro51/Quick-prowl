using System;
using System.Numerics;
using Prowl.Runtime;
using Prowl.Vector;

[RequireComponent(typeof(Rigidbody3D))]
public class PlayerController : MonoBehaviour
{
    public float MoveSpeed = 5f;
    public float JumpImpulse = 6f;
    public float Gravity = 9.8f;

    public CapsuleCollider CapsuleCollider;

    public enum JumpType
    {
        Velocity,
        Impulse
    }

    public Transform PlayerContainer;

    public Prowl.Animate.Animator Animator;
    
    /// <summary>
    /// How fast <see cref="PlayerContainer"/> turns to face the direction of travel, in degrees per second.
    /// </summary>
    public float TurnSpeed = 720f;

    public JumpType Jump = JumpType.Impulse;

    /// <summary>
    /// How much the ground-check capsule is inset from the real collider, so a resting character
    /// does not start the cast already touching the floor.
    /// </summary>
    public float SkinWidth = 0.02f;

    /// <summary>
    /// How far below the skin the ground check probes. This is only how far we look, not how far
    /// counts as standing - see <see cref="GroundedTolerance"/>.
    /// </summary>
    public float GroundCheckDistance = 0.1f;

    /// <summary>
    /// Gap to the surface at or below which the character counts as standing on it. Seeing the floor
    /// is not the same as touching it: without this the character is grounded a whole
    /// <see cref="GroundCheckDistance"/> early and a buffered press fires in mid-air.
    /// </summary>
    public float GroundedTolerance = 0.02f;

    /// <summary>
    /// How long after taking off the ground check stays suppressed. One fixed step only lifts the
    /// character about as far as the probe reaches, so without this the floor we just left re-arms
    /// the jump we just spent.
    /// </summary>
    public float TakeoffLockout = 0.1f;

    /// <summary>
    /// Grace period after walking off an edge during which a jump is still allowed.
    /// </summary>
    public float CoyoteTime = 0.1f;

    /// <summary>
    /// How long a jump press is remembered, so pressing slightly before landing still jumps.
    /// </summary>
    public float JumpBufferTime = 0.1f;

    private Rigidbody3D _body = null!;

    private ShapeCastHit lastGroundHit;

    private bool _grounded;
    private bool _jumpArmed;
    private float _coyoteTimer;
    private float _jumpBufferTimer;
    private float _takeoffTimer;

    /// <summary>
    /// Maximum angle in degrees for a surface to be considered walkable (default: 45 degrees)
    /// </summary>
    public float MaxSlopeAngle = 55.0f;

    /// <summary>
    /// Move the capsule on startup so its feet sit at the transform origin. Everything here assumes
    /// that: a capsule hanging below the origin leaves the character standing inside the floor, where
    /// the ground cast can never measure a gap. Turn off if you place the capsule yourself.
    /// </summary>
    public bool AlignCapsuleToFeet = true;

    private QueryFilter _filter = new QueryFilter();

    /// <summary>
    /// Inset actually used by the cast. A zero skin makes the cast shape identical to the collider, so
    /// it begins in contact with whatever we are standing on and the sweep has no gap to measure.
    /// </summary>
    private float Skin => Maths.Max(SkinWidth, 0.01f);

    /// <summary>
    /// Whether the character is standing on walkable ground. Sampled once per fixed step, so it stays
    /// in step with the physics the movement code is reading.
    /// </summary>
    public bool IsGrounded => _grounded;


    private Vector2 _movement = new Vector2();
    
    public void OnMove(InputActionContext ctx)
    {
        _movement = ctx.ReadValue<Float2>();
        _movement = new Vector2(_movement.X, _movement.Y);
    }
    
    public void OnJump(InputActionContext ctx)
    {
        _jumpBufferTimer = JumpBufferTime;
    }


    public void RegisterInputCallbacks()
    {
        if (GameMaster.Instance != null)
        {
            GameMaster.Instance.LoadedInputActionMap.FindAction("Move").Performed += OnMove;
            GameMaster.Instance.LoadedInputActionMap.FindAction("Move").Enable();
            
            
            GameMaster.Instance.LoadedInputActionMap.FindAction("Jump").Performed += OnJump;
            GameMaster.Instance.LoadedInputActionMap.FindAction("Jump").Enable();
        }
    }

    public void DeregisterInputCallbacks()
    {
        
        if (GameMaster.Instance != null)
        {
            GameMaster.Instance.LoadedInputActionMap.FindAction("Move").Performed -= OnMove;
            GameMaster.Instance.LoadedInputActionMap.FindAction("Move").Disable();
            
            GameMaster.Instance.LoadedInputActionMap.FindAction("Jump").Performed -= OnJump;
            GameMaster.Instance.LoadedInputActionMap.FindAction("Jump").Disable();
        }
    }

    protected override void OnDispose()
    {
        DeregisterInputCallbacks();
    }
    
    public override void Start()
    {
        RegisterInputCallbacks();
        
        _body = GetComponent<Rigidbody3D>()!;

        // Ignore the whole body, not just the one collider: IgnoreCollider resolves through the shape
        // owner registry and is accepted when that lookup misses, whereas IgnoreRigidbody is compared
        // straight off the body. It also covers any collider added to the player later.
        _filter = QueryFilter.Default.Ignoring(_body).Ignoring(CapsuleCollider);

        if (AlignCapsuleToFeet)
            AlignCapsule();
    }

    /// <summary>
    /// Puts the capsule's lowest point on the transform origin. CapsuleCollider.Height is the
    /// cylindrical section only, so the caps add Radius at each end and the feet sit at
    /// Center.Y - Height/2 - Radius.
    /// </summary>
    private void AlignCapsule()
    {
        float feet = CapsuleCollider.Center.Y - CapsuleCollider.Height * 0.5f - CapsuleCollider.Radius;
        if (Maths.Abs(feet) < 0.0001f)
            return;

        Float3 center = CapsuleCollider.Center;
        center.Y -= feet;
        CapsuleCollider.Center = center; // the setter rebuilds the shapes, so physics picks it up too

        Debug.LogWarning(
            $"Capsule feet sat {-feet:0.###} from the transform origin, which leaves the character " +
            $"inside the floor and the ground cast unable to measure a gap. Moved Center.Y to " +
            $"{center.Y:0.###}. Set it there in the inspector to silence this, or turn off AlignCapsuleToFeet.");
    }

    /// <summary>
    /// Lower endpoint of the collider's capsule segment. CapsuleCollider.Height is the cylindrical
    /// section only, so the cap extends a further Radius below this point.
    /// </summary>
    private Float3 GetCapsuleBottom(Float3 position)
    {
        //Debug.Log($"Height : {CapsuleCollider.Height} Radius : {CapsuleCollider.Radius}");
        return position + CapsuleCollider.Center - 
               (new Float3(0, CapsuleCollider.Height * 0.5f, 0) - new Float3(0,CapsuleCollider.Radius,0)); 
        // - new Float3(0, CapsuleCollider.Height * 0.5f + CapsuleCollider.Radius, 0);
    }

    /// <summary>
    /// Upper endpoint of the collider's capsule segment.
    /// </summary>
    private Float3 GetCapsuleTop(Float3 position)
    {
        return position + CapsuleCollider.Center + 
               (new Float3(0, CapsuleCollider.Height * 0.5f, 0) - new Float3(0,CapsuleCollider.Radius,0));
    }

    /// <summary>
    /// Calculates the angle of a surface in degrees from horizontal.
    /// </summary>
    private float GetSlopeAngle(Float3 normal)
    {
        // Angle between surface normal and up vector
        return Maths.Acos(normal.Y) * (180.0f / Maths.PI);
    }


    /// <summary>
    /// Performs a shape cast based on the current shape type.
    /// </summary>
    private bool PerformShapeCast(Float3 position, Float3 direction, float distance, out ShapeCastHit hitInfo)
    {
        return GameObject.Scene.Physics.CapsuleCast(
            GetCapsuleBottom(position),
            GetCapsuleTop(position),
            CapsuleCollider.Radius - Skin,
            direction,
            distance,
            out hitInfo,
            _filter
        );
    }

    /// <summary>
    /// Performs a ground check using shape casting.
    /// Only considers the character grounded if the surface angle is walkable.
    /// </summary>
    private bool PerformGroundCheck(Float3 position, float distance, out ShapeCastHit hitInfo)
    {
        bool hit = PerformShapeCast(position + new Float3(0,.01f,0), new Float3(0, -1, 0), distance, out hitInfo);

        if (!hit)
            return false;


        // An initial overlap is not a measured contact: the cast began inside the geometry, so the
        // sweep never ran and the distance is 0 wherever we are. Treating that as ground is what let
        // the jump re-arm at the top of an arc.
        /*if (hitInfo.Distance <= 0)
        {
            WarnCapsuleStartsOverlapping();
            return false;
        }*/

        // Check if the surface is walkable
        float slopeAngle = GetSlopeAngle(hitInfo.Normal);
        return slopeAngle <= MaxSlopeAngle;
    }

    private readonly int MovementMagnitudeHash = Prowl.Animate.Animator.StringToHash("MovementMagnitude");
    
    public override void Update()
    {
        var position = Transform.Position;

        /*Debug.DrawLine(position, position + new Float3(0, -0.1f, 0), Color.Red);

        Debug.DrawWireCapsule(GetCapsuleBottom(position+ new Float3(0,.01f,0)),
            GetCapsuleTop(position+ new Float3(0,.01f,0)),
            CapsuleCollider.Radius - Skin,
            Color.Red);*/

        
        Debug.Log(Input.GetGamepadLeftStick());

        Animator.SetFloat(MovementMagnitudeHash, GetMagnitude(new Vector3(_movement.X, 0, _movement.Y)));
        
        FaceMoveDirection(Time.DeltaTime);


        /*// Only sample input here. Update runs after the fixed loop and can run several times between
        // steps, so grounded state and the jump itself both live in FixedUpdate.
        if (Input.GetKeyDown(KeyCode.Space))
            _jumpBufferTimer = JumpBufferTime;*/
    }

    /// <summary>
    /// Turns <see cref="PlayerContainer"/> toward the horizontal direction of travel at
    /// <see cref="TurnSpeed"/>. Works on quaternions rather than Euler angles, so it always takes the
    /// short way round instead of spinning when the heading crosses 0/360.
    /// </summary>
    private void FaceMoveDirection(float dt)
    {
        if (PlayerContainer == null)
            return;

        // Flatten, so jumping and falling do not pitch the model.
        Float3 heading = _body.LinearVelocity;
        heading.Y = 0;
        if (Float3.LengthSquared(heading) < 0.0001f)
            return;

        Prowl.Vector.Quaternion current = PlayerContainer.Rotation;
        Prowl.Vector.Quaternion target = Prowl.Vector.Quaternion.LookRotation(Float3.Normalize(heading), Float3.UnitY);

        float remaining = Prowl.Vector.Quaternion.Angle(current, target); // radians
        float maxStep = TurnSpeed * Maths.Deg2Rad * dt;

        PlayerContainer.Rotation = remaining <= maxStep
            ? target
            : Prowl.Vector.Quaternion.Slerp(current, target, maxStep / remaining);
    }

    public float GetMagnitude(Vector3 v)
    {
        return MathF.Sqrt(MathF.Pow(v.X,2) + MathF.Pow(v.Y,2) + MathF.Pow(v.Z,2));
    }

    /// <summary>
    /// The ground cast can only measure a gap if it starts outside the world. Reports the collider
    /// geometry once so a bad Center/Height/Radius combination is visible instead of silently
    /// reading as permanent ground contact.
    /// </summary>
    private void WarnCapsuleStartsOverlapping()
    {
        float bottomBelowOrigin = -(CapsuleCollider.Center.Y - CapsuleCollider.Height * 0.5f - CapsuleCollider.Radius);
        float centerForFeetAtOrigin = CapsuleCollider.Height * 0.5f + CapsuleCollider.Radius;

        Debug.LogWarningOnce("PlayerController.GroundCastOverlap",
            $"Ground cast starts inside geometry, so it cannot measure a gap and the character never reads as " +
            $"airborne. The capsule hangs {bottomBelowOrigin:0.###} below the transform origin " +
            $"(Center.Y {CapsuleCollider.Center.Y}, Height {CapsuleCollider.Height}, Radius {CapsuleCollider.Radius}). " +
            $"Set Center.Y to {centerForFeetAtOrigin:0.###}, or turn on AlignCapsuleToFeet to have that done here.");
    }

    /// <summary>
    /// Refreshes <see cref="IsGrounded"/> and the coyote/takeoff timers for this step.
    /// </summary>
    private void UpdateGroundState(float dt)
    {
        if (_takeoffTimer > 0)
        {
            _takeoffTimer -= dt;
            _grounded = false;
            lastGroundHit = default;
            return;
        }

        bool foundGround = PerformGroundCheck(Transform.Position, Skin + GroundCheckDistance, out lastGroundHit);

        // The cast shape is inset by the skin, so subtract it back off to get the real gap between
        // the collider and the surface. Zero while resting on it.
        float gap = lastGroundHit.Distance;// - Skin;
        
        //Debug.Log($"Grounded: {foundGround} && {gap <= GroundedTolerance} ({gap} <= {GroundedTolerance}) ON {(foundGround ? lastGroundHit.Collider.GameObject : "NOTHING")}");
        _grounded = foundGround && gap <= GroundedTolerance;

        if (_grounded)
        {
            _coyoteTimer = CoyoteTime;
            // Only an actual landing re-arms the jump, so no amount of input can produce a second one
            // in the same airborne stretch.
            _jumpArmed = true;
        }
        else
        {
            _coyoteTimer = Maths.Max(0, _coyoteTimer - dt);
        }
    }

    public override void FixedUpdate()
    {
        float dt = Time.FixedDeltaTime;

        UpdateGroundState(dt);
        _jumpBufferTimer = Maths.Max(0, _jumpBufferTimer - dt);

        //Float2 wasd = Input.GetWASD();
        
        Float2 wasd = _movement;
        Float3 wish = Transform.Right * wasd.X + Transform.Forward * wasd.Y;

        Float3 v = _body.LinearVelocity;
        v.X = wish.X * MoveSpeed;
        v.Z = wish.Z * MoveSpeed;
        v.Y = v.Y - Gravity * dt;

        bool jumping = _jumpArmed && _jumpBufferTimer > 0 && _coyoteTimer > 0;
        if (jumping)
        {
            // Spend the press, the ground and the arming in one go, and hold the ground check off
            // until we have physically cleared the floor. Re-arming needs a real landing.
            _jumpArmed = false;
            _jumpBufferTimer = 0;
            _coyoteTimer = 0;
            _grounded = false;
            _takeoffTimer = TakeoffLockout;

            // Discard the fall speed so the takeoff is the same height however fast we were dropping.
            v.Y = 0;

            var groundTransform = lastGroundHit.Transform;
            //string groundName = groundTransform != null ? groundTransform.GameObject.Name : "<unknown>";
            //Debug.LogSuccess($"Jump! ground={groundName} y={Transform.Position.Y:0.###} " +
            //                 $"distance={lastGroundHit.Distance:0.###} gap={lastGroundHit.Distance:0.###}");
        }

        if (Jump == JumpType.Impulse)
        {
            _body.LinearVelocity = v;
            if (jumping)
                _body.ApplyImpulse(new Float3(0, JumpImpulse, 0));
        }
        else if (Jump == JumpType.Velocity)
        {
            if (jumping)
                v = new Float3(v.X, JumpImpulse, v.Z);

            _body.LinearVelocity = v;
        }
    }
}
