using UnityEngine;

// Individual marker for included (non-excluded) menu items
#if VRC_SDK_VRCSDK3
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerIncluded : MonoBehaviour, VRC.SDKBase.IEditorOnly { }
}
#else
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerIncluded : MonoBehaviour { }
}
#endif
