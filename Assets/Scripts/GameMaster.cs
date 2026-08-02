using Prowl.Runtime;
using Prowl.Runtime.Resources;

public class GameMaster : MonoBehaviour
{
    public AssetRef<Scene> GameSceneRef;
    
    public TransitionManager TransitionManager;
    
    public override void Start()
    {
        
    }

    public override void Update()
    {
        if (Input.GetKeyDown(KeyCode.G))
        {
            ChangeScene();
        }
    }

    public void ChangeScene()
    {
        TransitionManager.Transition(0f, 0.5f);
        GameSceneRef.EnsureLoaded();
        Scene.Load(GameSceneRef.Res);
    }
}
