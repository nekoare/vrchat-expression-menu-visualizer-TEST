using UnityEngine;

namespace VRCExpressionMenuOrganizer
{
#if VRC_SDK_VRCSDK3
    public class ExprMenuVisualizerGeneratedMetadata : MonoBehaviour, VRC.SDKBase.IEditorOnly
#else
    public class ExprMenuVisualizerGeneratedMetadata : MonoBehaviour
#endif
    {
        // 保存されるのは変換時のメニューパス（fullPath）
        public string fullPath;
    }
}
