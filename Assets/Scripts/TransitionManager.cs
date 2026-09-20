using Prowl.Runtime;
using Prowl.Runtime.Resources;
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
        Material material = Image.Material.Res;
        if (material == null) return;

        // The state overload: the material is passed in rather than captured, so both lambdas stay
        // static and the whole call allocates nothing. The Tween.To(getter, setter, ...) form reads
        // a little shorter but costs two closures and two delegates every transition.
        await Tween.To<float, FloatAdapter, Material>(
                material,
                static m => m._properties.GetFloat("_TransitionStep"),
                static (v, m) => m.SetFloat("_TransitionStep", v),
                targetValue, duration)
            .SetEase(ease)
            .SetLink(material, static m => m.IsValid())
            .AsyncWaitForCompletion();
    }
}
