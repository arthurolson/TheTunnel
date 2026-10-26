namespace UnityEngine.XR.Templates.MRTTabletopAssets
{
    public class PlayerListInitializer : MonoBehaviour
    {
        [SerializeField] PlayerListUI[] m_PlayerListUIs;

        void Start()
        {
            foreach (var l in m_PlayerListUIs)
            {
                l.InitializeCallbacks();
            }
        }

        [ContextMenu("Find Player List UIs")]
        void FindPlayerListUIs()
        {
#if UNITY_6000_5_OR_NEWER
            m_PlayerListUIs = FindObjectsByType<PlayerListUI>(FindObjectsInactive.Include);
#else
            m_PlayerListUIs = FindObjectsByType<PlayerListUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#endif
        }
    }
}
