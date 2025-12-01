using UnityEngine;
using UnityEditor;

namespace VRCExpressionMenuVisualizer
{
    public class SubmenuNameInputWindow : EditorWindow
    {
        private string inputText = "";
        private string dialogTitle = "Enter Name";
        private string dialogMessage = "Please enter a name:";
        private System.Action<string> onComplete;
        private bool focusSet = false;

        public static void ShowDialog(string title, string message, string defaultValue, System.Action<string> onComplete)
        {
            var window = GetWindow<SubmenuNameInputWindow>(true, title, true);
            window.dialogTitle = title;
            window.dialogMessage = message;
            window.inputText = defaultValue ?? "";
            window.onComplete = onComplete;
            window.focusSet = false;
            window.minSize = new Vector2(300, 100);
            window.maxSize = new Vector2(300, 100);
            window.ShowModal();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(dialogMessage, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(5);
            
            // Set focus to text field
            GUI.SetNextControlName("InputField");
            string newText = EditorGUILayout.TextField(inputText);
            
            if (!focusSet)
            {
                GUI.FocusControl("InputField");
                focusSet = true;
            }
            
            if (newText != inputText)
            {
                inputText = newText;
            }
            
            EditorGUILayout.Space(10);
            
            GUILayout.BeginHorizontal();
            
            if (GUILayout.Button("OK") || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
            {
                onComplete?.Invoke(inputText);
                Close();
            }
            
            if (GUILayout.Button("Cancel") || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape))
            {
                onComplete?.Invoke(null);
                Close();
            }
            
            GUILayout.EndHorizontal();
            
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.Escape))
            {
                Event.current.Use();
            }
        }
    }
}
