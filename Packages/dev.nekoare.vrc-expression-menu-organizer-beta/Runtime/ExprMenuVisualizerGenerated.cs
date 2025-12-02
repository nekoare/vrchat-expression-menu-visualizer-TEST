using UnityEngine;

// Individual marker for generated menu items
#if VRC_SDK_VRCSDK3
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerGenerated : MonoBehaviour, VRC.SDKBase.IEditorOnly { }
}
#else
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerGenerated : MonoBehaviour { }
}
#endif
