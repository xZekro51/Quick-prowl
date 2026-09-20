using Prowl.Runtime;
using Prowl.Runtime.Resources;

public class GameMaster : MonoBehaviour
{
    public static GameMaster? Instance;
    
    public AssetRef<Scene> GameSceneRef;
    
    public TransitionManager TransitionManager;
    
    public AssetRef<InputActionMap> InputActionMap;
    
    private InputActionMap _loadedInputActionMap;

    public InputActionMap LoadedInputActionMap
    {
        get
        {
            if (_loadedInputActionMap == null)
            {
                _loadedInputActionMap = InputActionMap.Res;
                Input.RegisterActionMap(_loadedInputActionMap);
            }
            return _loadedInputActionMap;
        }
    }
    
    public override void Start()
    {
        if (Instance != null)
        {
            GameObject.Destroy();
            return;
        }

        Instance = this;
        Scene.DontDestroyOnLoad(GameObject);
        
        InputActionMap.EnsureLoaded();
        _loadedInputActionMap = InputActionMap.Res;
        Input.RegisterActionMap(_loadedInputActionMap);
    }

    protected override void OnDispose()
    {
        base.OnDispose();
        if (Instance == this)
            Instance = null;
        if (_loadedInputActionMap != null)
            Input.UnregisterActionMap(_loadedInputActionMap);
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
            Debug.Log($"TransitionManager is null? {TransitionManager == null}");
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
