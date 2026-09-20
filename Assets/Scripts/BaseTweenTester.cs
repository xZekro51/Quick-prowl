using System.Numerics;
using Prowl.Runtime;
using Prowl.Tweening;
using Prowl.Vector;

public class BaseTweenTester : MonoBehaviour
{
    public enum Modes
    {
        Mode1,
        Mode2,
        Mode3,
        Mode4,
        Mode5,
        Mode6
    }

    public Modes Mode;
    
    public override void Start()
    {
        if (Mode == Modes.Mode1)
        {
            Transform.Move(Transform.Position + new Float3(0, 5, 0), 1f).SetEase(Ease.InOutQuad)
                .SetLoops(-1, LoopType.Yoyo);
        }
        else if (Mode == Modes.Mode2)
        {
            Transform.Move(Transform.Position + new Float3(0, 5, 0), 1f).SetEase(Ease.InOutQuad)
                .SetLoops(-1, LoopType.Restart);
        }
        else if (Mode == Modes.Mode3)
        {
            Transform.Move(Transform.Position + new Float3(0, 5, 0), 1f).SetEase(Ease.InOutElastic)
                .SetLoops(-1, LoopType.Yoyo);
        }
        else if (Mode == Modes.Mode4)
        {
            Transform.Move(Transform.Position + new Float3(0, 5, 0), 1f).SetEase(Ease.InOutElastic)
                .SetLoops(4, LoopType.Incremental);
        }
        else if (Mode == Modes.Mode5)
        {
            Transform.Scale(Transform.LocalScale + new Float3(1, 1, 1), 1f).SetEase(Ease.InOutElastic)
                .SetLoops(-1, LoopType.Yoyo);
        }
        else if (Mode == Modes.Mode6)
        {
            var sequence = Tween.Sequence();

            sequence.Append(Transform.Move(new Float3(-3, 1, -3), 1f).SetEase(Ease.InOutQuad));
            sequence.Append(Transform.Move(new Float3(3, 1, -3), 1f).SetEase(Ease.InOutQuad));
            sequence.Append(Transform.Move(new Float3(3, 1, 3), 1f).SetEase(Ease.InOutQuad));
            sequence.Append(Transform.Move(new Float3(-3, 1, 3), 1f).SetEase(Ease.InOutQuad));

            sequence.SetLoops(-1, LoopType.Restart);
            sequence.Play();
        }
    }

    public override void Update()
    {

    }
}
