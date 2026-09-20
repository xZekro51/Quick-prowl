using Prowl.Runtime;
using Prowl.Kino;
using Prowl.Vector;

public class ImpulseTest : MonoBehaviour
{
    public float Force = 0.3f;
    public float Duration = 0.4f;
    public float Radius = 25f;
    
    public override void Start()
    {
        
    }

    public override void OnTriggerEnter(Rigidbody3D other)
    {
        Debug.LogSuccess($"{other.GameObject.Name} ENTERED TRIGGER!");
        KinoImpulse.Shake(other.Transform.Position, Force, Duration, Radius);
    }

    public override void Update()
    {

    }
}
