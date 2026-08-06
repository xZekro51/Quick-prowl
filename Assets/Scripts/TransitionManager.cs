using Prowl.Runtime;
using Prowl.Runtime.UI;
using Prowl.Tweening;

public class TransitionManager : MonoBehaviour
{
    public UIImage Image;
    
    public override void Start()
    {
        Image.Material.EnsureLoaded();
        Image.Material.Res.SyncShaderDefaults();
        Image.Material = Image.Material.Res.Clone();
        Image.Material.Res.SetFloat("_TransitionStep", 1.01f);
    }

    public override void Update()
    {

    }

    public async System.Threading.Tasks.Task Transition(float targetValue = 0f,  float duration = 0.5f, Ease ease = Ease.InOutCubic)
    {
        var material = Image.Material.Res;
        if (material == null) return;

        // Unavailable, in Prowl.Tweening package
        await Tween.To(() => material._properties.GetFloat("_TransitionStep"),
                x => material.SetFloat("_TransitionStep", x), targetValue, duration)
            .SetEase(ease)
            .Play().AsyncWaitForCompletion();
    }
}
