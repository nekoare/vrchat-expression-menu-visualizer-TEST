using System;
using UnityEngine;

#if VRC_SDK_VRCSDK3
namespace VRCExpressionMenuOrganizer
{
    // GUIDベースのマーカー。生成されたメニュー項目(GameObject)に付与して永続的に識別する。
    public class ExprMenuVisualizerGeneratedGuid : MonoBehaviour, VRC.SDKBase.IEditorOnly
    {
        // 永続化される一意ID (hex string)
        public string generatedGuid;

        // 生成時のメニュー fullPath（任意、デバッグ/マイグレーション用）
        public string originalMenuPath;

        // Install target 名などの補助情報（任意）
        public string installTargetName;

        private void OnValidate()
        {
            // Ensure we have a GUID assigned when edited/duplicated in the Editor
            if (string.IsNullOrEmpty(generatedGuid))
            {
                generatedGuid = System.Guid.NewGuid().ToString("N");
                return;
            }

            // If duplicated (same GUID found on another instance), regenerate to keep uniqueness
            var others = FindObjectsOfType<ExprMenuVisualizerGeneratedGuid>();
            foreach (var o in others)
            {
                if (o == this) continue;
                if (!string.IsNullOrEmpty(o.generatedGuid) && o.generatedGuid == generatedGuid)
                {
                    generatedGuid = System.Guid.NewGuid().ToString("N");
                    break;
                }
            }
        }
    }
}
#else
namespace VRCExpressionMenuOrganizer
{
    public class ExprMenuVisualizerGeneratedGuid : MonoBehaviour
    {
        public string generatedGuid;
        public string originalMenuPath;
        public string installTargetName;
    }
}
#endif
