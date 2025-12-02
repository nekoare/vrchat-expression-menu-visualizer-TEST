using UnityEngine;

// Individual marker for excluded menu items
#if VRC_SDK_VRCSDK3
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerExcluded : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        // 除外項目の元のメニューパス（GameObjectを移動しても保持される）
        public string originalMenuPath;
    }
}
#else
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerExcluded : MonoBehaviour
    {
        // 除外項目の元のメニューパス（GameObjectを移動しても保持される）
        public string originalMenuPath;
    }
}
#endif
