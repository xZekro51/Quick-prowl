using Prowl.Runtime;
using Prowl.Tweening;

public class TweenMaster : MonoBehaviour
{
    public override void FixedUpdate()
    {
        Tween.FixedUpdate(Time.FixedDeltaTime);
    }

    public override void Update()
    {
        float tweenDelta = Application.ShouldRunGameplay ? Time.DeltaTime : 0f;
        Tween.Update(tweenDelta, Time.UnscaledDeltaTime);
    }


    public override void LateUpdate()
    {
        float tweenDelta = Application.ShouldRunGameplay ? Time.DeltaTime : 0f;
        Tween.LateUpdate(tweenDelta, Time.UnscaledDeltaTime);
    }
}
