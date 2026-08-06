using Prowl.Runtime;
using Prowl.Runtime.Resources;

public class GameMaster : MonoBehaviour
{
    public AssetRef<Scene> GameSceneRef;
    
    public TransitionManager TransitionManager;
    
    public override void Start()
    {
        Scene.DontDestroyOnLoad(GameObject);
    }

    public override void Update()
    {
        if (Input.GetKeyDown(KeyCode.G))
        {
            NonAsyncChangeScene();
        }
    }

    public void NonAsyncChangeScene()
    {
        _ = ChangeScene();
    }
    
    public async System.Threading.Tasks.Task ChangeScene()
    {
        try
        {
            var masterInstance = GameObject;
            await TransitionManager.Transition(0f, 0.5f);
            //Scene.Remove(masterInstance);
            GameSceneRef.EnsureLoaded();
            Scene.Load(GameSceneRef.Res);
            //Scene.Current.Add(masterInstance);
            await TransitionManager.Transition(1.01f, 0.5f);
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
        }
    }
}
