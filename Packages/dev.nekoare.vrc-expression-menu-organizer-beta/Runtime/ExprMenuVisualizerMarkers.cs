using UnityEngine;

// Marker components used by the Expression Menu Visualizer.
// Keep these in the Runtime assembly so Editor code (which references
// the VRChatExpressionMenuVisualizer.Runtime asmdef) can find them.
// NOTE: ExprMenuVisualizerGenerated and ExprMenuVisualizerExcluded are defined
// in their own files (`ExprMenuVisualizerGenerated.cs`, `ExprMenuVisualizerExcluded.cs`) so
// Unity shows distinct script assets and avoids accidental duplicate type issues.
#if VRC_SDK_VRCSDK3
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerGeneratedRoot : MonoBehaviour, VRC.SDKBase.IEditorOnly { }
}
#else
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerGeneratedRoot : MonoBehaviour { }
}
#endif
