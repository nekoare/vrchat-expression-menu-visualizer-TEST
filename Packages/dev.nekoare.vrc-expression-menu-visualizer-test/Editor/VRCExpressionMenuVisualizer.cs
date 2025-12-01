using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace VRCExpressionMenuVisualizer
{
    public class VRCExpressionMenuVisualizerWindow : EditorWindow
    {
        private VRCAvatarDescriptor selectedAvatar;
        private Vector2 scrollPosition;
        private Dictionary<VRCExpressionsMenu, bool> menuFoldouts = new Dictionary<VRCExpressionsMenu, bool>();
        private Dictionary<VRCExpressionsMenu.Control, bool> controlFoldouts = new Dictionary<VRCExpressionsMenu.Control, bool>();
        private Dictionary<object, bool> mergedMenuFoldouts = new Dictionary<object, bool>(); // For MergedMenuItem
        private HashSet<VRCExpressionsMenu> visitedMenus = new HashSet<VRCExpressionsMenu>();
        private GUIStyle treeNodeStyle;
        private GUIStyle parameterStyle;
        private VRCExpressionsMenu.Control editingControl;
        private bool isEditingControl = false;
        private string searchQuery = "";
        private bool showStats = true;
        private bool useEnglish = false; // デフォルトは日本語
        private bool useGridView = true; // デフォルトはグリッド表示
        private bool editMode = false; // グローバル編集モード
        
        // Selection and editing state
        private List<MergedMenuItem> selectedItems = new List<MergedMenuItem>();
        private MergedMenuItem lastClickedItem = null;
        
        // Drag and drop state
        private MergedMenuItem draggedItem = null;
        private Vector2 dragStartPosition;
        private bool isDragging = false;
        private MergedMenuItem dragTargetParent = null;
        private int dragTargetIndex = -1;
        private Dictionary<MergedMenuItem, Rect> itemRects = new Dictionary<MergedMenuItem, Rect>();
        private List<MergedMenuItem> currentMenuItems = new List<MergedMenuItem>();
        
        // Hierarchy change state
        private bool dragTargetIsSubmenu = false;
        private bool dragTargetIsParentLevel = false;
        private bool dragTargetIsDeleteArea = false;
        private MergedMenuItem hoveredSubmenu = null;
        private Rect backNavigationDropArea;
        private Rect deleteDropArea;
        
        // Temporary storage for edited menu structure
        private MergedMenuItem editedMenuStructure = null;
        
        // ModularAvatar support - using reflection for safe type handling
        private static Type modularAvatarMenuInstallerType;
        private static Type modularAvatarObjectToggleType;
        private static Type modularAvatarMenuItemType;
        private static bool checkedForModularAvatar = false;
        
        private static Type GetModularAvatarMenuInstallerType()
        {
            if (!checkedForModularAvatar)
            {
                checkedForModularAvatar = true;
                try
                {
                    modularAvatarMenuInstallerType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller, nadena.dev.modular-avatar.core");
                    modularAvatarObjectToggleType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarObjectToggle, nadena.dev.modular-avatar.core");
                    modularAvatarMenuItemType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuItem, nadena.dev.modular-avatar.core");
                }
                catch
                {
                    modularAvatarMenuInstallerType = null;
                    modularAvatarObjectToggleType = null;
                    modularAvatarMenuItemType = null;
                }
            }
            return modularAvatarMenuInstallerType;
        }
        
        private static Type GetModularAvatarObjectToggleType()
        {
            GetModularAvatarMenuInstallerType(); // Initialize all types
            return modularAvatarObjectToggleType;
        }
        
        private static Type GetModularAvatarMenuItemType()
        {
            GetModularAvatarMenuInstallerType(); // Initialize all types
            return modularAvatarMenuItemType;
        }
        
        private static bool IsModularAvatarAvailable()
        {
            return GetModularAvatarMenuInstallerType() != null;
        }
        
        private bool IsComponentOrParentEditorOnly(Component component)
        {
            if (component == null || selectedAvatar == null) return false;
            
            // Check the component's GameObject and its direct parent up to avatar root for Editor Only tag
            Transform current = component.transform;
            Transform avatarTransform = selectedAvatar.transform;
            
            while (current != null && current != avatarTransform.parent)
            {
                // Check if this GameObject has the "EditorOnly" tag
                if (current.gameObject.CompareTag("EditorOnly"))
                {
                    return true;
                }
                
                // Stop at avatar root - don't check beyond avatar
                if (current == avatarTransform)
                {
                    break;
                }
                
                // Move to parent
                current = current.parent;
            }
            
            return false;
        }
        
        // ローカライゼーション用ヘルパーメソッド
        private string GetLocalizedText(string japanese, string english)
        {
            return useEnglish ? english : japanese;
        }
        
        [MenuItem("Tools/VRChat Expression Menu Visualizer")]
        public static void ShowWindow()
        {
            GetWindow<VRCExpressionMenuVisualizerWindow>("Expression Menu Visualizer");
        }

        private void OnGUI()
        {
            InitializeStyles();
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(GetLocalizedText("VRChat Expression Menu ビジュアライザー", "VRChat Expression Menu Visualizer"), EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // Avatar Selection
            selectedAvatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                new GUIContent(GetLocalizedText("アバター記述子", "Avatar Descriptor"), GetLocalizedText("表現メニューを表示するVRCAvatarDescriptorを選択", "Select the VRCAvatarDescriptor to visualize its expression menus")), 
                selectedAvatar, 
                typeof(VRCAvatarDescriptor), 
                true
            );

            if (selectedAvatar == null)
            {
                EditorGUILayout.HelpBox(GetLocalizedText("表現メニューを表示するVRCAvatarDescriptorを選択してください。", "Please select a VRCAvatarDescriptor to visualize its expression menus."), MessageType.Info);
                EditorGUILayout.HelpBox(GetLocalizedText("このツールでは以下が表示されます:\n• メイン表現メニュー構造\n• ModularAvatarメニューインストーラー\n• コントロールパラメータと値\n• 任意のコントロールの ✏️ ボタンをクリックして編集", "This tool will show:\n• Main expression menu structure\n• ModularAvatar menu installers\n• Control parameters and values\n• Click any control's ✏️ button to edit"), MessageType.Info);
                return;
            }
            
            // Additional validation
            if (selectedAvatar.expressionsMenu == null)
            {
                EditorGUILayout.HelpBox(GetLocalizedText("このアバターにはメインのExpression Menuが設定されていません。", "This avatar does not have a main Expression Menu configured."), MessageType.Warning);
            }

            EditorGUILayout.Space();
            
            // Control buttons row
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(GetLocalizedText("メニューツリー更新", "Refresh Menu Tree"), GetLocalizedText("メニューツリーを再読み込みします", "Refresh the menu tree"))))
            {
                RefreshMenuTree();
            }
            
            showStats = GUILayout.Toggle(showStats, new GUIContent(GetLocalizedText("統計表示", "Show Stats"), GetLocalizedText("統計情報を表示します", "Show statistics")), GUILayout.Width(100));
            
            useGridView = GUILayout.Toggle(useGridView, new GUIContent(GetLocalizedText("グリッド表示", "Grid View"), GetLocalizedText("ゲーム内のようなグリッド表示", "Game-like grid display")), GUILayout.Width(100));
            
            editMode = GUILayout.Toggle(editMode, new GUIContent(GetLocalizedText("編集モード", "Edit Mode"), GetLocalizedText("メニュー構造を編集します", "Edit menu structure")), GUILayout.Width(100));
            
            useEnglish = GUILayout.Toggle(useEnglish, new GUIContent(GetLocalizedText("English", "English"), GetLocalizedText("英語で表示", "Display in English")), GUILayout.Width(80));
            GUILayout.EndHorizontal();
            
            // Search bar
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent(GetLocalizedText("検索:", "Search:"), GetLocalizedText("コントロール名、タイプ、パラメータ名で検索", "Search by control name, type, or parameter name")), GUILayout.Width(50));
            string newSearchQuery = EditorGUILayout.TextField(searchQuery);
            if (newSearchQuery != searchQuery)
            {
                searchQuery = newSearchQuery.ToLower();
            }
            
            if (GUILayout.Button(new GUIContent(GetLocalizedText("クリア", "Clear"), GetLocalizedText("検索をクリア", "Clear search")), GUILayout.Width(50)))
            {
                searchQuery = "";
            }
            GUILayout.EndHorizontal();
            
            // Edit mode panel
            if (editMode)
            {
                EditorGUILayout.Space();
                
                // Main edit mode info and buttons row
                GUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(GetLocalizedText("編集モード: アイテムをドラッグして移動・並び替えできます。アイコンクリックで選択し、ドラッグで階層変更できます。", "Edit Mode: Drag items to move and reorder them. Click icons to select, drag to change hierarchy."), MessageType.Info);
                
                if (GUILayout.Button(new GUIContent(GetLocalizedText("💾 保存", "💾 Save"), GetLocalizedText("編集したメニュー構造を新しいExpressionMenuとして保存し、アバターに割り当てます", "Save edited menu structure as new ExpressionMenu and assign to avatar")), GUILayout.Width(80)))
                {
                    SaveEditedMenuStructure();
                }
                
                if (GUILayout.Button(new GUIContent(GetLocalizedText("🔄 リセット", "🔄 Reset"), GetLocalizedText("編集内容を破棄して元の状態に戻します", "Discard edits and reset to original state")), GUILayout.Width(80)))
                {
                    ResetEditedMenuStructure();
                }
                
                if (selectedItems.Count > 0)
                {
                    EditorGUILayout.LabelField(GetLocalizedText($"{selectedItems.Count}個選択中", $"{selectedItems.Count} selected"), GUILayout.Width(80));
                }
                
                GUILayout.EndHorizontal();
            }
            
            // Statistics
            if (showStats)
            {
                DrawStatistics();
            }

            EditorGUILayout.Space();

            // Menu Tree Display
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            // Edit actions inside scroll view for proper coordinate alignment
            if (editMode)
            {
                EditorGUILayout.Space(5);
                DrawEditActionsPanel();
                EditorGUILayout.Space(10);
            }
            
            try
            {
                DrawMenuTree();
                
                // Control Editor (if editing)
                if (isEditingControl)
                {
                    DrawControlEditor();
                }
            }
            catch (Exception e)
            {
                EditorGUILayout.HelpBox(GetLocalizedText($"エラーが発生しました: {e.Message}", $"An error occurred: {e.Message}"), MessageType.Error);
                EditorGUILayout.HelpBox(GetLocalizedText("このエラーが継続する場合は、アバターのExpression Menu設定を確認してください。", "If this error persists, please check your avatar's Expression Menu configuration."), MessageType.Info);
                
                if (GUILayout.Button(GetLocalizedText("リフレッシュして再試行", "Refresh and Retry")))
                {
                    RefreshMenuTree();
                }
                
                Debug.LogError($"VRCExpressionMenuVisualizer Error: {e}");
            }
            
            EditorGUILayout.EndScrollView();
        }

        private void InitializeStyles()
        {
            if (treeNodeStyle == null)
            {
                treeNodeStyle = new GUIStyle(EditorStyles.foldout);
                treeNodeStyle.fontStyle = FontStyle.Bold;
            }
            
            if (parameterStyle == null)
            {
                parameterStyle = new GUIStyle(EditorStyles.label);
                parameterStyle.fontSize = 11;
                parameterStyle.normal.textColor = Color.gray;
            }
        }

        private void RefreshMenuTree()
        {
            menuFoldouts.Clear();
            controlFoldouts.Clear();
            mergedMenuFoldouts.Clear();
            visitedMenus.Clear();
            menuNavigationStack.Clear();
            
            // If in edit mode, refresh the edited structure from current avatar
            if (editMode)
            {
                InitializeEditedMenuStructure();
                Debug.Log("Refreshed edited menu structure from avatar");
            }
            
            // Clear edit mode tracking data
            selectedItems.Clear();
            draggedItem = null;
            isDragging = false;
            itemRects.Clear();
            currentMenuItems.Clear();
            dragTargetParent = null;
            dragTargetIndex = -1;
            
            Repaint();
        }

        private void DrawStatistics()
        {
            if (selectedAvatar == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GetLocalizedText("📊 統計情報", "📊 Statistics"), EditorStyles.miniBoldLabel);

            var stats = GatherStatistics();
            
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(GetLocalizedText($"総メニュー数: {stats.totalMenus}", $"Total Menus: {stats.totalMenus}"), GUILayout.Width(120));
            EditorGUILayout.LabelField(GetLocalizedText($"総コントロール数: {stats.totalControls}", $"Total Controls: {stats.totalControls}"), GUILayout.Width(140));
            EditorGUILayout.LabelField(GetLocalizedText($"最大深度: {stats.maxDepth}", $"Max Depth: {stats.maxDepth}"), GUILayout.Width(100));
            GUILayout.EndHorizontal();
            
            if (stats.controlTypeCounts.Count > 0)
            {
                EditorGUILayout.LabelField(GetLocalizedText("コントロールタイプ:", "Control Types:"), EditorStyles.miniLabel);
                GUILayout.BeginHorizontal();
                foreach (var kvp in stats.controlTypeCounts)
                {
                    EditorGUILayout.LabelField($"{kvp.Key}: {kvp.Value}", parameterStyle, GUILayout.Width(80));
                }
                GUILayout.EndHorizontal();
            }
            
            if (IsModularAvatarAvailable() && stats.maInstallersCount > 0)
            {
                EditorGUILayout.LabelField(GetLocalizedText("ModularAvatarコンポーネント:", "ModularAvatar Components:"), EditorStyles.miniLabel);
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(GetLocalizedText($"インストーラー: {stats.maInstallersCount}", $"Installers: {stats.maInstallersCount}"), parameterStyle, GUILayout.Width(120));
                GUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }
        
        private void DrawEditActionsPanel()
        {
            EditorGUILayout.BeginHorizontal();
            
            // Submenu creation button
            if (GUILayout.Button(new GUIContent(GetLocalizedText("📁 サブメニュー生成", "📁 Create Submenu"), 
                GetLocalizedText("現在の階層に新しい空のサブメニューを追加します", "Add a new empty submenu to current level")), 
                GUILayout.Width(120)))
            {
                CreateNewSubmenu();
            }
            
            GUILayout.Space(10);
            
            // Delete area - use button instead of label for proper event handling
            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = dragTargetIsDeleteArea ? Color.red : new Color(1f, 0.3f, 0.3f);
            
            string deleteText = GetLocalizedText("🗑️ 削除エリア", "🗑️ Delete Zone");
            if (isDragging)
            {
                deleteText += GetLocalizedText(" (ここにドロップ)", " (Drop Here)");
            }
            
            // Create delete button and get its rect immediately
            Rect deleteRect = GUILayoutUtility.GetRect(new GUIContent(deleteText), EditorStyles.helpBox, 
                GUILayout.Width(120), GUILayout.Height(25));
            
            // Draw the delete area
            GUI.Label(deleteRect, deleteText, EditorStyles.helpBox);
            
            // Always update the delete drop area during layout events
            deleteDropArea = deleteRect;
            
            GUI.backgroundColor = originalColor;
            
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
        
        private void CreateNewSubmenu()
        {
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // Get current menu context
            var currentMenu = GetCurrentEditedMenu();
            if (currentMenu == null) return;
            
            // Ensure children list exists
            if (currentMenu.children == null)
                currentMenu.children = new List<MergedMenuItem>();
            
            // Show name input dialog
            ShowSubmenuNameInputDialog((submenuName) => {
                if (string.IsNullOrEmpty(submenuName))
                {
                    // User cancelled or entered empty name
                    return;
                }
                
                // Generate unique name if needed
                string uniqueName = GenerateUniqueSubmenuName(currentMenu, submenuName);
                
                // Create actual VRCExpressionsMenu asset immediately
                var submenuAsset = CreateVRCExpressionsMenuAsset(uniqueName);
                
                // Create new submenu item with proper asset linkage
                var newSubmenu = new MergedMenuItem
                {
                    name = uniqueName,
                    source = "VRC",
                    children = new List<MergedMenuItem>(),
                    subMenu = submenuAsset,
                    control = new VRCExpressionsMenu.Control
                    {
                        name = uniqueName,
                        type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                        subMenu = submenuAsset
                    },
                    originalIndex = currentMenu.children.Count
                };
                
                // Add to current menu
                currentMenu.children.Add(newSubmenu);
                
                // Update display
                UpdateCurrentMenuItems();
                Repaint();
                
                Debug.Log(GetLocalizedText(
                    $"新しいサブメニュー '{uniqueName}' を作成しました",
                    $"Created new submenu '{uniqueName}'"
                ));
            });
        }
        
        private VRCExpressionsMenu CreateVRCExpressionsMenuAsset(string menuName)
        {
            // Create a new VRCExpressionsMenu asset
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls = new List<VRCExpressionsMenu.Control>();
            
            // Ensure the directory exists
            string folderPath = "Assets/GeneratedExpressionMenus";
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                AssetDatabase.CreateFolder("Assets", "GeneratedExpressionMenus");
            }
            
            // Generate unique asset name
            string assetName = menuName.Replace(" ", "_").Replace("/", "_");
            string assetPath = $"{folderPath}/{assetName}.asset";
            
            // Make sure the asset name is unique
            int counter = 1;
            while (AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(assetPath) != null)
            {
                assetPath = $"{folderPath}/{assetName}_{counter}.asset";
                counter++;
            }
            
            // Create the asset
            AssetDatabase.CreateAsset(menu, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            Debug.Log(GetLocalizedText(
                $"VRCExpressionsMenuアセットを作成しました: {assetPath}",
                $"Created VRCExpressionsMenu asset: {assetPath}"
            ));
            
            return menu;
        }
        
        private string GenerateUniqueSubmenuName(MergedMenuItem parentMenu, string baseName)
        {
            if (parentMenu?.children == null) return baseName;
            
            var existingNames = new HashSet<string>(parentMenu.children.Select(item => item.name));
            
            if (!existingNames.Contains(baseName))
                return baseName;
            
            int counter = 1;
            string uniqueName;
            do
            {
                uniqueName = $"{baseName} {counter}";
                counter++;
            } while (existingNames.Contains(uniqueName));
            
            return uniqueName;
        }
        
        private void ShowSubmenuNameInputDialog(System.Action<string> onComplete)
        {
            string defaultName = GetLocalizedText("新しいサブメニュー", "New Submenu");
            string dialogTitle = GetLocalizedText("サブメニュー名を入力", "Enter Submenu Name");
            string dialogMessage = GetLocalizedText("新しいサブメニューの名前を入力してください:", "Please enter the name for the new submenu:");
            
            // Create a popup window for text input
            SubmenuNameInputWindow.ShowDialog(dialogTitle, dialogMessage, defaultName, onComplete);
        }

        private MenuStatistics GatherStatistics()
        {
            var stats = new MenuStatistics();
            
            // Main menu
            if (selectedAvatar?.expressionsMenu != null)
            {
                GatherMenuStats(selectedAvatar.expressionsMenu, stats, 0);
            }
            
            // ModularAvatar menus (safe handling)
            if (IsModularAvatarAvailable())
            {
                var installers = GetModularAvatarMenuInstallers();
                stats.maInstallersCount = installers.Count;
                
                foreach (var installer in installers)
                {
                    var menuToAppend = GetMenuToAppendFromInstaller(installer);
                    if (menuToAppend != null)
                    {
                        GatherMenuStats(menuToAppend, stats, 0);
                    }
                    // For installers without direct menus, they still contribute to the installer count
                    // but we can't preview their generated content
                }
                
                // Individual MA Menu Items and Object Toggles are not counted separately
                // They are handled through the installer's generation process
            }
            
            return stats;
        }

        private void GatherMenuStats(VRCExpressionsMenu menu, MenuStatistics stats, int depth)
        {
            if (menu == null) return;
            
            stats.totalMenus++;
            stats.maxDepth = Mathf.Max(stats.maxDepth, depth);
            
            if (menu.controls != null)
            {
                stats.totalControls += menu.controls.Count;
                
                foreach (var control in menu.controls)
                {
                    var type = control.type.ToString();
                    if (!stats.controlTypeCounts.ContainsKey(type))
                        stats.controlTypeCounts[type] = 0;
                    stats.controlTypeCounts[type]++;
                    
                    if (control.subMenu != null)
                    {
                        GatherMenuStats(control.subMenu, stats, depth + 1);
                    }
                }
            }
        }

        private class MenuStatistics
        {
            public int totalMenus = 0;
            public int totalControls = 0;
            public int maxDepth = 0;
            public int maInstallersCount = 0;
            public Dictionary<string, int> controlTypeCounts = new Dictionary<string, int>();
        }

        // Merged menu structure for integrated display
        private class MergedMenuItem
        {
            public string name;
            public string source; // "VRC", "MA_Installer", "MA_ObjectToggle", "MA_MenuItem"
            public VRCExpressionsMenu.Control control;
            public VRCExpressionsMenu subMenu;
            public List<MergedMenuItem> children = new List<MergedMenuItem>();
            public Component sourceComponent; // For ModularAvatar components
            public int originalIndex = -1; // For preserving order
        }

        private MergedMenuItem BuildMergedMenuStructure()
        {
            if (selectedAvatar == null) return null;

            var rootItem = new MergedMenuItem 
            { 
                name = GetLocalizedText("ルートメニュー", "Root Menu"),
                source = "VRC"
            };

            // Start with the main VRC expression menu
            var mainMenu = GetMainExpressionMenu();
            if (mainMenu != null)
            {
                BuildMenuItemsFromVRCMenu(mainMenu, rootItem, "VRC");
            }

            // Integrate ModularAvatar components
            if (IsModularAvatarAvailable())
            {
                IntegrateModularAvatarMenus(rootItem);
            }

            return rootItem;
        }

        private void BuildMenuItemsFromVRCMenu(VRCExpressionsMenu menu, MergedMenuItem parentItem, string source)
        {
            if (menu?.controls == null) return;

            for (int i = 0; i < menu.controls.Count; i++)
            {
                var control = menu.controls[i];
                var mergedItem = new MergedMenuItem
                {
                    name = string.IsNullOrEmpty(control.name) ? GetLocalizedText("無名コントロール", "Unnamed Control") : control.name,
                    source = source,
                    control = control,
                    subMenu = control.subMenu,
                    originalIndex = i
                };

                parentItem.children.Add(mergedItem);

                // Recursively process submenus
                if (control.subMenu != null)
                {
                    // If this is from MA_Installer source, check if submenu contains MA Menu Items
                    // If so, completely ignore the submenu (don't show hierarchy or content)
                    if (source == "MA_Installer" && IsMenuContainingMAMenuItems(control.subMenu))
                    {
                        // For MA Menu Installer, completely ignore MA Menu Item submenus
                        // Don't process or display anything related to MA Menu Items
                        mergedItem.subMenu = null; // Remove the submenu reference
                    }
                    else
                    {
                        // Normal recursive processing for regular VRC submenus
                        BuildMenuItemsFromVRCMenu(control.subMenu, mergedItem, source);
                    }
                }
            }
        }
        
        private bool IsMenuContainingMAMenuItems(VRCExpressionsMenu menu)
        {
            if (menu == null || !IsModularAvatarAvailable()) return false;
            
            try
            {
                // Check if this menu is directly referenced by any MA Menu Installer as menuToAppend
                var menuInstallers = GetModularAvatarMenuInstallers();
                
                foreach (var installer in menuInstallers)
                {
                    var menuToAppend = GetMenuToAppendFromInstaller(installer);
                    if (menuToAppend == menu)
                    {
                        // This menu is directly installed by an MA Menu Installer
                        // So it should not show MA Menu Item details
                        return true;
                    }
                }
                
                // Also check if this menu is referenced by any MA Menu Item in the scene
                var menuItems = GetModularAvatarMenuItems();
                
                foreach (var menuItem in menuItems)
                {
                    var controlField = menuItem.GetType().GetField("Control");
                    if (controlField != null)
                    {
                        var control = controlField.GetValue(menuItem);
                        if (control != null)
                        {
                            var subMenuField = control.GetType().GetField("subMenu");
                            if (subMenuField != null)
                            {
                                var subMenu = subMenuField.GetValue(control) as VRCExpressionsMenu;
                                if (subMenu == menu)
                                {
                                    // This menu is referenced by an MA Menu Item
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore reflection errors
            }
            
            return false;
        }

        private void IntegrateModularAvatarMenus(MergedMenuItem rootItem)
        {
            // Process Menu Installers only
            // Individual MA Menu Items are handled through the installer's generation process
            var menuInstallers = GetModularAvatarMenuInstallers();
            
            // Filter out installers whose menuToAppend is already being installed by another installer
            var filteredInstallers = FilterNestedMenuInstallers(menuInstallers);
            
            foreach (var installer in filteredInstallers)
            {
                IntegrateMenuInstaller(installer, rootItem);
            }
        }
        
        private List<Component> FilterNestedMenuInstallers(List<Component> installers)
        {
            // Include all MA Menu Installers regardless of their install target
            // This ensures "ギミック２" and similar items appear whether they target root or specific menus
            return new List<Component>(installers);
        }

        private void IntegrateMenuInstaller(Component installer, MergedMenuItem rootItem)
        {
            if (installer == null) return;

            // Check if this installer has a direct menu to append
            var menuToInstall = GetMenuToAppendFromInstaller(installer);
            if (menuToInstall != null)
            {
                // Direct menu installation
                IntegrateDirectMenuInstaller(installer, menuToInstall, rootItem);
            }
            else
            {
                // Menu generation from MA Menu Items
                IntegrateGeneratedMenuInstaller(installer, rootItem);
            }
        }
        
        private void IntegrateDirectMenuInstaller(Component installer, VRCExpressionsMenu menuToInstall, MergedMenuItem rootItem)
        {
            // Get the install target menu
            var installTargetMenu = GetInstallTargetFromInstaller(installer);
            MergedMenuItem targetItem = null;
            
            if (installTargetMenu != null)
            {
                // Find the specific menu item that matches the install target
                targetItem = FindMenuItemByVRCMenu(rootItem, installTargetMenu);
            }
            
            // If no specific target found, use root menu
            if (targetItem == null)
            {
                targetItem = rootItem;
            }

            // Add only the installer entry, not the detailed menu content
            // This prevents MA Menu Item details from being displayed
            var installerItem = new MergedMenuItem
            {
                name = installer.gameObject.name,
                source = "MA_Installer",
                sourceComponent = installer,
                subMenu = menuToInstall
            };
            
            targetItem.children.Add(installerItem);
        }
        
        private void IntegrateGeneratedMenuInstaller(Component installer, MergedMenuItem rootItem)
        {
            // For installers without direct menus, create an empty placeholder entry
            // This shows that the installer exists but the content is generated at runtime
            // MA Menu Items are ignored and not displayed
            
            var installTargetMenu = GetInstallTargetFromInstaller(installer);
            MergedMenuItem targetItem = null;
            
            if (installTargetMenu != null)
            {
                targetItem = FindMenuItemByVRCMenu(rootItem, installTargetMenu);
            }
            
            if (targetItem == null)
            {
                targetItem = rootItem;
            }
            
            // Create a placeholder item indicating this installer generates content at runtime
            var placeholderItem = new MergedMenuItem
            {
                name = $"{installer.gameObject.name} ({GetLocalizedText("実行時生成", "Runtime Generated")})",
                source = "MA_Installer",
                sourceComponent = installer
            };
            
            targetItem.children.Add(placeholderItem);
        }

        private void IntegrateObjectToggle(Component toggle, MergedMenuItem rootItem)
        {
            if (toggle == null) return;

            try
            {
                // Get Object Toggle properties via reflection
                var objectField = toggle.GetType().GetField("Object");
                var savedField = toggle.GetType().GetField("Saved");
                
                if (objectField != null)
                {
                    var targetObject = objectField.GetValue(toggle) as GameObject;
                    if (targetObject != null)
                    {
                        bool isSaved = savedField != null ? (bool)savedField.GetValue(toggle) : false;
                        
                        // Create a virtual toggle control
                        var toggleItem = new MergedMenuItem
                        {
                            name = $"{targetObject.name} ({GetLocalizedText("トグル", "Toggle")})",
                            source = "MA_ObjectToggle",
                            sourceComponent = toggle
                        };

                        // Object toggles integrate into the root menu structure
                        // They appear as individual toggle controls
                        rootItem.children.Add(toggleItem);
                    }
                }
            }
            catch
            {
                // Ignore reflection errors
            }
        }

        private void IntegrateMenuItem(Component menuItem, MergedMenuItem rootItem)
        {
            if (menuItem == null) return;

            try
            {
                // Get Menu Item properties via reflection
                var controlField = menuItem.GetType().GetField("Control");
                
                if (controlField != null)
                {
                    var control = controlField.GetValue(menuItem);
                    if (control != null)
                    {
                        // Extract control properties
                        var nameField = control.GetType().GetField("name");
                        var typeField = control.GetType().GetField("type");
                        
                        string controlName = nameField?.GetValue(control) as string ?? menuItem.name;
                        string controlType = typeField?.GetValue(control)?.ToString() ?? "Unknown";
                        
                        var menuItemEntry = new MergedMenuItem
                        {
                            name = $"{controlName} ({controlType})",
                            source = "MA_MenuItem",
                            sourceComponent = menuItem
                        };

                        // Menu items integrate into the root menu structure
                        // They appear as individual menu controls
                        rootItem.children.Add(menuItemEntry);
                    }
                }
            }
            catch
            {
                // Ignore reflection errors
            }
        }

        private MergedMenuItem FindOrCreateMenuPath(MergedMenuItem rootItem, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return rootItem;

            // Split path and navigate/create the structure
            var pathParts = path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var currentItem = rootItem;

            foreach (var part in pathParts)
            {
                var childItem = currentItem.children.FirstOrDefault(c => c.name == part);
                if (childItem == null)
                {
                    childItem = new MergedMenuItem
                    {
                        name = part,
                        source = "VRC"
                    };
                    currentItem.children.Add(childItem);
                }
                currentItem = childItem;
            }

            return currentItem;
        }

        private void DrawMergedMenu(MergedMenuItem item, int indentLevel)
        {
            if (item == null) return;

            EditorGUI.indentLevel = indentLevel;
            
            // Root item doesn't need a foldout
            if (indentLevel == 0)
            {
                // Just draw children for root - separate VRC items from MA items for better organization
                var vrcItems = item.children.Where(c => c.source == "VRC").OrderBy(c => c.originalIndex);
                var maItems = item.children.Where(c => c.source != "VRC").OrderBy(c => c.originalIndex);
                
                // Draw VRC items first (they form the base structure)
                foreach (var child in vrcItems)
                {
                    DrawMergedMenu(child, indentLevel);
                }
                
                // Then draw MA items that don't have a specific parent
                foreach (var child in maItems)
                {
                    DrawMergedMenu(child, indentLevel);
                }
                return;
            }

            // Menu item header with icon based on source
            if (!mergedMenuFoldouts.ContainsKey(item))
                mergedMenuFoldouts[item] = item.children.Count > 0; // Auto-expand if has children

            GUILayout.BeginHorizontal();
            
            // Tree structure visual guide
            for (int i = 1; i < indentLevel; i++)
            {
                GUILayout.Label("│   ", GUILayout.Width(20), GUILayout.Height(16));
            }
            
            if (indentLevel > 0)
            {
                GUILayout.Label("├─", GUILayout.Width(16), GUILayout.Height(16));
            }
            
            // Source-specific icons
            string icon = GetMenuItemIcon(item);
            string displayName = $"{icon} {item.name}";
            
            if (item.children.Count > 0)
            {
                mergedMenuFoldouts[item] = EditorGUILayout.Foldout(mergedMenuFoldouts[item], displayName, treeNodeStyle);
            }
            else
            {
                EditorGUILayout.LabelField(displayName, treeNodeStyle);
            }
            
            // Source indicator
            string sourceText = GetSourceDisplayText(item.source);
            GUILayout.Label(sourceText, parameterStyle, GUILayout.Width(100));
            
            // Component reference button for ModularAvatar items
            if (item.sourceComponent != null)
            {
                if (GUILayout.Button("→", GUILayout.Width(25)))
                {
                    Selection.activeGameObject = item.sourceComponent.gameObject;
                    EditorGUIUtility.PingObject(item.sourceComponent.gameObject);
                }
            }
            
            GUILayout.EndHorizontal();

            // Draw children if expanded
            if (item.children.Count > 0 && (mergedMenuFoldouts.ContainsKey(item) && mergedMenuFoldouts[item]))
            {
                // Group children by source for better organization
                var vrcChildren = item.children.Where(c => c.source == "VRC").OrderBy(c => c.originalIndex);
                var maChildren = item.children.Where(c => c.source != "VRC").OrderBy(c => c.originalIndex);
                
                // Draw VRC children first (original menu structure)
                foreach (var child in vrcChildren)
                {
                    DrawMergedMenu(child, indentLevel + 1);
                }
                
                // Then draw MA children (integrated items)
                foreach (var child in maChildren)
                {
                    DrawMergedMenu(child, indentLevel + 1);
                }
            }

            // Draw control details for leaf items
            if (item.control != null && item.children.Count == 0)
            {
                DrawControlDetails(item.control, indentLevel + 1);
            }

            EditorGUI.indentLevel = 0;
        }

        private string GetMenuItemIcon(MergedMenuItem item)
        {
            switch (item.source)
            {
                case "VRC": 
                    return item.children.Count > 0 ? "📂" : "⚙️";
                case "MA_Installer": 
                    return "🔧";
                case "MA_ObjectToggle": 
                    return "🔘";
                case "MA_MenuItem": 
                    return "⚙️";
                case "MA_Generated":
                    return "🎯"; // Generated menu items get a target icon
                default: 
                    return "📄";
            }
        }

        private string GetSourceDisplayText(string source)
        {
            switch (source)
            {
                case "VRC": 
                    return GetLocalizedText("VRC", "VRC");
                case "MA_Installer": 
                    return GetLocalizedText("MA設置", "MA Install");
                case "MA_ObjectToggle": 
                    return GetLocalizedText("MAトグル", "MA Toggle");
                case "MA_MenuItem": 
                    return GetLocalizedText("MAアイテム", "MA Item");
                case "MA_Generated":
                    return GetLocalizedText("MA生成", "MA Generated");
                default: 
                    return source;
            }
        }

        private void DrawModularAvatarComponentsReference()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(GetLocalizedText("📋 ModularAvatarコンポーネント参考情報", "📋 ModularAvatar Components Reference"), EditorStyles.boldLabel);
            
            // Foldout for reference information
            if (!mergedMenuFoldouts.ContainsKey("reference")) // Use "reference" as a key for reference section
                mergedMenuFoldouts["reference"] = false;
            
            mergedMenuFoldouts["reference"] = EditorGUILayout.Foldout(mergedMenuFoldouts["reference"], GetLocalizedText("参考: メニューインストーラー詳細", "Reference: Menu Installer Details"));
            
            if (mergedMenuFoldouts["reference"])
            {
                EditorGUI.indentLevel++;
                
                // Menu Installers only
                var menuInstallers = GetModularAvatarMenuInstallers();
                if (menuInstallers.Count > 0)
                {
                    EditorGUILayout.LabelField(GetLocalizedText("メニューインストーラー", "Menu Installers"), EditorStyles.miniBoldLabel);
                    foreach (var installer in menuInstallers)
                    {
                        DrawMenuInstaller(installer);
                    }
                }
                else
                {
                    EditorGUILayout.LabelField(GetLocalizedText("メニューインストーラーが見つかりません", "No Menu Installers found"), parameterStyle);
                }
                
                EditorGUI.indentLevel--;
            }
        }

        private void DrawMenuInstaller(Component installer)
        {
            if (installer == null) return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField($"📋 {installer.gameObject.name}", EditorStyles.boldLabel);
            
            // Menu Installerの詳細表示
            DrawMenuInstallerDetails(installer.gameObject, installer);
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawMenu(VRCExpressionsMenu menu, int indentLevel)
        {
            if (menu == null) return;

            // Circular reference detection
            if (visitedMenus.Contains(menu))
            {
                EditorGUI.indentLevel = indentLevel;
                EditorGUILayout.LabelField(GetLocalizedText($"⚠️ 循環参照: {menu.name}", $"⚠️ Circular Reference: {menu.name}"), EditorStyles.helpBox);
                EditorGUI.indentLevel = 0;
                return;
            }

            visitedMenus.Add(menu);
            
            EditorGUI.indentLevel = indentLevel;
            
            // Menu Header with icon
            if (!menuFoldouts.ContainsKey(menu))
                menuFoldouts[menu] = true;

            string menuName = menu.name;
            if (string.IsNullOrEmpty(menuName))
                menuName = GetLocalizedText("無名メニュー", "Unnamed Menu");

            GUILayout.BeginHorizontal();
            
            // Tree structure visual guide
            for (int i = 0; i < indentLevel; i++)
            {
                GUILayout.Label("│   ", GUILayout.Width(20), GUILayout.Height(16));
            }
            
            if (indentLevel > 0)
            {
                GUILayout.Label("├─", GUILayout.Width(16), GUILayout.Height(16));
            }
            
            string menuIcon = indentLevel == 0 ? "📁" : "📂";
            menuFoldouts[menu] = EditorGUILayout.Foldout(menuFoldouts[menu], $"{menuIcon} {menuName}", treeNodeStyle);
            
            // Menu info
            if (menu.controls != null)
            {
                GUILayout.Label(GetLocalizedText($"({menu.controls.Count} コントロール)", $"({menu.controls.Count} controls)"), parameterStyle, GUILayout.Width(80));
            }
            
            GUILayout.EndHorizontal();

            if (menuFoldouts[menu])
            {
                // Menu Controls
                if (menu.controls != null)
                {
                    for (int i = 0; i < menu.controls.Count; i++)
                    {
                        var control = menu.controls[i];
                        DrawControl(control, i, indentLevel + 1);
                    }
                }
                
                if (menu.controls == null || menu.controls.Count == 0)
                {
                    EditorGUI.indentLevel = indentLevel + 1;
                    EditorGUILayout.LabelField(GetLocalizedText("(コントロールなし)", "(No controls)"), parameterStyle);
                    EditorGUI.indentLevel = 0;
                }
            }
            
            visitedMenus.Remove(menu);
            EditorGUI.indentLevel = 0;
        }

        private void DrawControl(VRCExpressionsMenu.Control control, int index, int indentLevel)
        {
            string controlName = string.IsNullOrEmpty(control.name) ? GetLocalizedText($"コントロール {index}", $"Control {index}") : control.name;
            
            // Search filtering
            if (!string.IsNullOrEmpty(searchQuery))
            {
                bool matches = controlName.ToLower().Contains(searchQuery) ||
                              control.type.ToString().ToLower().Contains(searchQuery) ||
                              (control.parameter?.name.ToLower().Contains(searchQuery) ?? false);
                
                if (!matches) return;
            }
            string controlType = control.type.ToString();
            
            if (!controlFoldouts.ContainsKey(control))
                controlFoldouts[control] = false;

            GUILayout.BeginHorizontal();
            
            // Tree structure visual guide
            for (int i = 0; i < indentLevel; i++)
            {
                GUILayout.Label("│   ", GUILayout.Width(20), GUILayout.Height(16));
            }
            
            GUILayout.Label("├─", GUILayout.Width(16), GUILayout.Height(16));
            
            // Control icon based on type
            string controlIcon = GetControlIcon(control.type);
            
            controlFoldouts[control] = EditorGUILayout.Foldout(
                controlFoldouts[control], 
                $"{controlIcon} {controlName}", 
                true
            );
            
            // Type and value info
            GUILayout.Label($"({controlType})", parameterStyle, GUILayout.Width(80));
            
            if (control.parameter != null)
            {
                GUILayout.Label($"[{control.parameter.name}]", parameterStyle, GUILayout.Width(100));
            }
            
            // Edit Button
            if (GUILayout.Button("✏️", GUILayout.Width(25), GUILayout.Height(16)))
            {
                OpenControlEditor(control);
            }
            
            GUILayout.EndHorizontal();

            // Control Details
            if (controlFoldouts[control])
            {
                EditorGUI.indentLevel = indentLevel + 1;
                
                // Control properties in a box
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                
                EditorGUILayout.LabelField(GetLocalizedText("プロパティ:", "Properties:"), EditorStyles.miniBoldLabel);
                
                EditorGUILayout.LabelField(GetLocalizedText($"タイプ: {control.type}", $"Type: {control.type}"));
                
                if (control.parameter != null)
                {
                    EditorGUILayout.LabelField(GetLocalizedText($"パラメータ: {control.parameter.name}", $"Parameter: {control.parameter.name}"));
                    
                    // Show parameter type if available
                    var paramType = GetParameterType(control.parameter);
                    if (!string.IsNullOrEmpty(paramType))
                    {
                        EditorGUILayout.LabelField(GetLocalizedText($"パラメータタイプ: {paramType}", $"Parameter Type: {paramType}"), parameterStyle);
                    }
                }
                
                if (control.value != 0)
                {
                    EditorGUILayout.LabelField(GetLocalizedText($"値: {control.value}", $"Value: {control.value}"));
                }
                
                // Icon display
                if (control.icon != null)
                {
                    EditorGUILayout.LabelField(GetLocalizedText($"アイコン: {control.icon.name}", $"Icon: {control.icon.name}"));
                    
                    // 小さなアイコンプレビューも表示
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(GetLocalizedText("プレビュー:", "Preview:"), GUILayout.Width(60));
                    
                    // 小さなアイコン表示
                    Rect smallIconRect = GUILayoutUtility.GetRect(32, 32, GUILayout.Width(32), GUILayout.Height(32));
                    EditorGUI.DrawRect(smallIconRect, new Color(0.9f, 0.9f, 0.9f, 1f));
                    GUI.DrawTexture(smallIconRect, control.icon, ScaleMode.ScaleToFit, true);
                    
                    EditorGUILayout.EndHorizontal();
                }
                
                // Sub Menu
                if (control.subMenu != null)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(GetLocalizedText("サブメニュー:", "Sub Menu:"), EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(GetLocalizedText($"メニュー: {control.subMenu.name}", $"Menu: {control.subMenu.name}"));
                    
                    if (GUILayout.Button(GetLocalizedText("→ サブメニューを展開", "→ Expand Sub Menu"), GUILayout.Height(20)))
                    {
                        if (!menuFoldouts.ContainsKey(control.subMenu))
                            menuFoldouts[control.subMenu] = true;
                        menuFoldouts[control.subMenu] = !menuFoldouts[control.subMenu];
                    }
                    
                    if (menuFoldouts.ContainsKey(control.subMenu) && menuFoldouts[control.subMenu])
                    {
                        DrawMenu(control.subMenu, indentLevel + 2);
                    }
                }
                
                // Sub Parameters
                if (control.subParameters != null && control.subParameters.Length > 0)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(GetLocalizedText("サブパラメータ:", "Sub Parameters:"), EditorStyles.miniBoldLabel);
                    for (int i = 0; i < control.subParameters.Length; i++)
                    {
                        if (control.subParameters[i] != null)
                        {
                            EditorGUILayout.LabelField($"[{i}] {control.subParameters[i].name}");
                        }
                    }
                }
                
                EditorGUILayout.EndVertical();
                EditorGUI.indentLevel = 0;
            }
        }

        private void DrawControlDetails(VRCExpressionsMenu.Control control, int indentLevel)
        {
            if (control == null) return;

            EditorGUI.indentLevel = indentLevel;
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            EditorGUILayout.LabelField(GetLocalizedText("コントロール詳細:", "Control Details:"), EditorStyles.miniBoldLabel);
            
            EditorGUILayout.LabelField(GetLocalizedText($"タイプ: {control.type}", $"Type: {control.type}"));
            
            if (control.parameter != null)
            {
                EditorGUILayout.LabelField(GetLocalizedText($"パラメータ: {control.parameter.name}", $"Parameter: {control.parameter.name}"));
                
                // Show parameter type if available
                var paramType = GetParameterType(control.parameter);
                if (!string.IsNullOrEmpty(paramType))
                {
                    EditorGUILayout.LabelField(GetLocalizedText($"パラメータタイプ: {paramType}", $"Parameter Type: {paramType}"), parameterStyle);
                }
            }
            
            if (control.value != 0)
            {
                EditorGUILayout.LabelField(GetLocalizedText($"値: {control.value}", $"Value: {control.value}"));
            }
            
            // Icon display with large preview
            if (control.icon != null)
            {
                EditorGUILayout.LabelField(GetLocalizedText($"アイコン: {control.icon.name}", $"Icon: {control.icon.name}"));
                DrawLargeIconPreview(control.icon);
            }
            
            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel = 0;
        }

        private void DrawLargeIconPreview(Texture2D icon)
        {
            if (icon == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(GetLocalizedText("アイコンプレビュー:", "Icon Preview:"), EditorStyles.miniBoldLabel);
            
            // アイコンプレビューのサイズ
            float previewSize = 128f;
            
            // プレビューエリアを視覚的に明確にするためのボックス
            EditorGUILayout.BeginVertical("box");
            
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            
            // アイコンを大きく表示するためのRect作成
            Rect iconRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.Width(previewSize), GUILayout.Height(previewSize));
            
            // 背景を描画（チェッカーボード風の背景で透明部分が見やすくなる）
            EditorGUI.DrawRect(iconRect, new Color(0.8f, 0.8f, 0.8f, 1f));
            
            // アイコンを描画
            if (icon != null)
            {
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
            }
            
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            
            EditorGUILayout.Space();
            
            // アイコンの詳細情報を表示
            EditorGUILayout.LabelField(GetLocalizedText("ファイル名", "File Name"), icon.name, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(GetLocalizedText("アイコンサイズ", "Icon Size"), $"{icon.width} x {icon.height}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(GetLocalizedText("フォーマット", "Format"), icon.format.ToString(), EditorStyles.miniLabel);
            
            // アセットパス情報
            string assetPath = AssetDatabase.GetAssetPath(icon);
            if (!string.IsNullOrEmpty(assetPath))
            {
                EditorGUILayout.LabelField(GetLocalizedText("パス", "Path"), assetPath, EditorStyles.miniLabel);
            }
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawMenuInstallerDetails(GameObject obj, Component menuInstaller)
        {
            // MenuInstallerの基本情報
            EditorGUILayout.LabelField(GetLocalizedText("タイプ", "Type"), "Menu Installer");
            
            var installerType = GetModularAvatarMenuInstallerType();
            if (installerType == null) return;
            
            var menuToAppendField = installerType.GetField("menuToAppend");
            var installTargetMenuField = installerType.GetField("installTargetMenu");
            
            if (menuToAppendField != null && installTargetMenuField != null)
            {
                var menuToAppend = menuToAppendField.GetValue(menuInstaller);
                var installTargetMenu = installTargetMenuField.GetValue(menuInstaller);
                
                // インストール元メニュー
                EditorGUILayout.ObjectField(
                    GetLocalizedText("追加するメニュー", "Menu to Append"), 
                    menuToAppend as UnityEngine.Object, 
                    typeof(VRCExpressionsMenu), 
                    false
                );
                
                // インストール先メニューの変更UI
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(GetLocalizedText("インストール先", "Install Target"), GUILayout.Width(100));
                
                var newInstallTarget = EditorGUILayout.ObjectField(
                    installTargetMenu as UnityEngine.Object, 
                    typeof(VRCExpressionsMenu), 
                    false
                );
                
                // インストール先が変更された場合
                if (!ReferenceEquals(newInstallTarget, installTargetMenu))
                {
                    try
                    {
                        installTargetMenuField.SetValue(menuInstaller, newInstallTarget);
                        EditorUtility.SetDirty(menuInstaller);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"Failed to set install target: {ex.Message}");
                    }
                }
                EditorGUILayout.EndHorizontal();
                
                // 現在のインストール先パス表示
                if (installTargetMenu != null)
                {
                    var targetMenu = installTargetMenu as VRCExpressionsMenu;
                    string installPath = GetMenuPath(targetMenu);
                    EditorGUILayout.LabelField(
                        GetLocalizedText("インストール先パス", "Install Path"), 
                        string.IsNullOrEmpty(installPath) ? GetLocalizedText("ルートメニュー", "Root Menu") : installPath
                    );
                }
                
                // インストール先変更のヘルプ
                EditorGUILayout.HelpBox(
                    GetLocalizedText(
                        "上記のフィールドからインストール先メニューを変更できます。変更は即座に反映されます。",
                        "You can change the install target menu from the field above. Changes are applied immediately."
                    ), 
                    MessageType.Info
                );
            }
        }

        private void DrawMenuTree()
        {
            if (selectedAvatar == null) return;

            if (useGridView)
            {
                DrawMenuTreeGrid();
            }
            else
            {
                DrawMenuTreeList();
            }
        }
        
        private void DrawMenuTreeList()
        {
            // Main Expression Menu - always show this first
            var mainMenu = GetMainExpressionMenu();
            if (mainMenu != null)
            {
                EditorGUILayout.LabelField(GetLocalizedText("メイン表現メニュー", "Main Expression Menu"), EditorStyles.boldLabel);
                DrawMenu(mainMenu, 0);
                EditorGUILayout.Space();
            }

            // Build and display the merged menu structure if ModularAvatar components exist
            if (IsModularAvatarAvailable())
            {
                var hasAnyMA = GetModularAvatarMenuInstallers().Count > 0;
                              
                if (hasAnyMA)
                {
                    EditorGUILayout.LabelField(GetLocalizedText("統合メニュー構造 (ModularAvatar統合)", "Integrated Menu Structure (ModularAvatar Integration)"), EditorStyles.boldLabel);
                    
                    var mergedMenuStructure = BuildMergedMenuStructure();
                    if (mergedMenuStructure != null && mergedMenuStructure.children.Count > 0)
                    {
                        DrawMergedMenu(mergedMenuStructure, 0);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(GetLocalizedText("統合メニュー構造を構築できませんでした", "Could not build integrated menu structure"), MessageType.Info);
                    }
                    
                    EditorGUILayout.Space();
                    DrawModularAvatarComponentsReference();
                }
            }
            else if (GetMainExpressionMenu() == null)
            {
                EditorGUILayout.HelpBox(GetLocalizedText("メニュー構造が見つかりませんでした", "No menu structure found"), MessageType.Info);
            }
        }
        
        private void DrawMenuTreeGrid()
        {
            // Use edited structure in edit mode, otherwise use original structure
            MergedMenuItem menuStructureToDisplay;
            
            if (editMode)
            {
                // Initialize edited structure if not already created
                if (editedMenuStructure == null)
                {
                    InitializeEditedMenuStructure();
                }
                
                menuStructureToDisplay = editedMenuStructure;
            }
            else
            {
                // Use original structure from avatar
                menuStructureToDisplay = BuildMergedMenuStructure();
            }
            
            if (menuStructureToDisplay != null && menuStructureToDisplay.children.Count > 0)
            {
                string menuTitle = editMode ? 
                    GetLocalizedText("表現メニュー (編集中)", "Expression Menu (Editing)") : 
                    GetLocalizedText("表現メニュー", "Expression Menu");
                EditorGUILayout.LabelField(menuTitle, EditorStyles.boldLabel);
                DrawMenuGrid(menuStructureToDisplay);
            }
            else if (!editMode)
            {
                // Fallback for original structure when not in edit mode
                var mainMenu = GetMainExpressionMenu();
                if (mainMenu != null)
                {
                    EditorGUILayout.LabelField(GetLocalizedText("表現メニュー", "Expression Menu"), EditorStyles.boldLabel);
                    var rootItem = new MergedMenuItem 
                    { 
                        name = GetLocalizedText("ルートメニュー", "Root Menu"),
                        source = "VRC"
                    };
                    BuildMenuItemsFromVRCMenu(mainMenu, rootItem, "VRC");
                    DrawMenuGrid(rootItem);
                }
                else
                {
                    EditorGUILayout.HelpBox(GetLocalizedText("メニュー構造が見つかりませんでした", "No menu structure found"), MessageType.Info);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(GetLocalizedText("編集用メニューを初期化できませんでした", "Failed to initialize menu for editing"), MessageType.Warning);
            }
        }

        private Dictionary<MergedMenuItem, bool> currentMenuStack = new Dictionary<MergedMenuItem, bool>();
        private List<MergedMenuItem> menuNavigationStack = new List<MergedMenuItem>();
        
        private void DrawMenuGrid(MergedMenuItem rootItem)
        {
            if (rootItem == null) return;
            
            // Get current menu to display (root or submenu)
            var currentMenu = menuNavigationStack.Count > 0 ? menuNavigationStack[menuNavigationStack.Count - 1] : rootItem;
            
            // Navigation breadcrumb
            DrawNavigationBreadcrumb();
            
            // Usage hint for grid view interaction
            if (!editMode)
            {
                EditorGUILayout.HelpBox(GetLocalizedText("操作方法: 単クリック=コントロール編集、ダブルクリック/Shift+クリック=サブメニューに移動", "Usage: Single click = Edit control, Double click/Shift+click = Navigate to submenu"), MessageType.Info);
            }
            
            // Menu items grid
            var itemsToShow = currentMenu.children;
            if (itemsToShow.Count == 0)
            {
                EditorGUILayout.HelpBox(GetLocalizedText("このメニューにはアイテムがありません", "This menu has no items"), MessageType.Info);
                return;
            }
            
            // Apply search filter
            if (!string.IsNullOrEmpty(searchQuery))
            {
                itemsToShow = itemsToShow.Where(item => 
                    item.name.ToLower().Contains(searchQuery) ||
                    (item.control?.type.ToString().ToLower().Contains(searchQuery) ?? false) ||
                    (item.control?.parameter?.name.ToLower().Contains(searchQuery) ?? false)
                ).ToList();
            }
            
            // Store current menu items for drag and drop calculations
            if (editMode)
            {
                currentMenuItems.Clear();
                currentMenuItems.AddRange(itemsToShow);
                itemRects.Clear();
            }
            
            // Calculate grid layout
            float windowWidth = EditorGUIUtility.currentViewWidth - 40; // Account for scrollbar and padding
            int itemsPerRow = Mathf.Max(1, Mathf.FloorToInt(windowWidth / 120f)); // 120px per item
            int rows = Mathf.CeilToInt((float)itemsToShow.Count / itemsPerRow);
            
            // Draw grid
            for (int row = 0; row < rows; row++)
            {
                GUILayout.BeginHorizontal();
                
                for (int col = 0; col < itemsPerRow; col++)
                {
                    int index = row * itemsPerRow + col;
                    if (index >= itemsToShow.Count) break;
                    
                    var item = itemsToShow[index];
                    DrawMenuItemGrid(item, currentMenu);
                }
                
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(5);
            }
        }
        
        private void DrawNavigationBreadcrumb()
        {
            Rect breadcrumbRect = EditorGUILayout.BeginHorizontal();
            
            // Set up back navigation drop area for drag and drop
            if (editMode && menuNavigationStack.Count > 0)
            {
                backNavigationDropArea = new Rect(breadcrumbRect.x, breadcrumbRect.y, 300, breadcrumbRect.height + 10);
            }
            else
            {
                backNavigationDropArea = Rect.zero;
            }
            
            // Back button (if not at root)
            if (menuNavigationStack.Count > 0)
            {
                string backButtonText = "← " + GetLocalizedText("戻る", "Back");
                if (editMode)
                {
                    backButtonText += GetLocalizedText(" (ドロップエリア)", " (Drop Zone)");
                }
                
                if (GUILayout.Button(backButtonText, GUILayout.Width(editMode ? 120 : 60)))
                {
                    menuNavigationStack.RemoveAt(menuNavigationStack.Count - 1);
                }
                GUILayout.Space(10);
            }
            
            // Breadcrumb trail
            GUILayout.Label(GetLocalizedText("現在の場所:", "Current: "), EditorStyles.miniLabel);
            
            if (menuNavigationStack.Count == 0)
            {
                GUILayout.Label(GetLocalizedText("ルートメニュー", "Root Menu"), EditorStyles.boldLabel);
            }
            else
            {
                for (int i = 0; i < menuNavigationStack.Count; i++)
                {
                    if (i > 0) GUILayout.Label(" > ", EditorStyles.miniLabel);
                    GUILayout.Label(menuNavigationStack[i].name, EditorStyles.boldLabel);
                }
            }
            
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            
            // Draw hierarchy change visual feedback
            if (editMode)
            {
                DrawHierarchyChangeVisualFeedback();
            }
            
            GUILayout.Space(10);
        }
        
        private void DrawHierarchyChangeVisualFeedback()
        {
            if (!isDragging) return;
            
            // Draw delete area feedback
            if (!deleteDropArea.Equals(Rect.zero))
            {
                Color deleteColor = dragTargetIsDeleteArea ? 
                    new Color(1f, 0.2f, 0.2f, 0.9f) : // Bright red when hovering
                    new Color(1f, 0.4f, 0.4f, 0.5f);  // Light red otherwise
                
                EditorGUI.DrawRect(deleteDropArea, deleteColor);
                
                // Add border for better visibility
                DrawBorder(deleteDropArea, dragTargetIsDeleteArea ? Color.red : new Color(0.8f, 0.2f, 0.2f), 3f);
                
                // Add warning text
                if (dragTargetIsDeleteArea)
                {
                    string deleteMessage = GetLocalizedText("削除します", "Will Delete");
                    var style = new GUIStyle(EditorStyles.whiteBoldLabel) 
                    { 
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 12
                    };
                    GUI.Label(deleteDropArea, deleteMessage, style);
                }
            }
            
            // Draw back navigation drop area feedback
            if (menuNavigationStack.Count > 0 && !backNavigationDropArea.Equals(Rect.zero))
            {
                Color dropAreaColor = dragTargetIsParentLevel ? 
                    new Color(0.3f, 0.7f, 1f, 0.8f) : // Bright blue when hovering
                    new Color(0.5f, 0.5f, 1f, 0.3f);  // Light blue otherwise
                
                EditorGUI.DrawRect(backNavigationDropArea, dropAreaColor);
                
                // Add border for better visibility
                DrawBorder(backNavigationDropArea, dragTargetIsParentLevel ? Color.cyan : Color.blue, 2f);
                
                // Add text indicator
                if (dragTargetIsParentLevel)
                {
                    var parentMenu = GetParentMenu();
                    string parentName = parentMenu?.name ?? GetLocalizedText("ルートメニュー", "Root Menu");
                    string message = GetLocalizedText($"「{parentName}」に移動", $"Move to '{parentName}'");
                    GUI.Label(new Rect(backNavigationDropArea.x + 5, backNavigationDropArea.y + 5, 
                                     backNavigationDropArea.width - 10, 20), 
                             message, EditorStyles.whiteBoldLabel);
                }
            }
            
            // Draw submenu drop area feedback
            foreach (var item in currentMenuItems)
            {
                if (!HasSubmenu(item) || !itemRects.ContainsKey(item)) continue;
                
                var rect = itemRects[item];
                bool isHovering = dragTargetIsSubmenu && dragTargetParent == item;
                
                if (isHovering)
                {
                    // Orange overlay for submenu drop
                    Color submenuColor = new Color(1f, 0.6f, 0f, 0.7f);
                    EditorGUI.DrawRect(rect, submenuColor);
                    DrawBorder(rect, Color.yellow, 3f);
                    
                    // Add submenu icon and text
                    GUI.Label(new Rect(rect.x + rect.width - 25, rect.y + 5, 20, 20), "📁", 
                             new GUIStyle { fontSize = 16 });
                    GUI.Label(new Rect(rect.x + 5, rect.y + rect.height - 20, rect.width - 10, 15),
                             GetLocalizedText("サブメニューに追加", "Add to submenu"), 
                             new GUIStyle { normal = { textColor = Color.white }, fontSize = 10, fontStyle = FontStyle.Bold });
                }
                else if (HasSubmenu(item))
                {
                    // Subtle highlight for submenu items during drag
                    Color submenuHint = new Color(0f, 1f, 0f, 0.2f);
                    EditorGUI.DrawRect(rect, submenuHint);
                    
                    // Small submenu indicator
                    GUI.Label(new Rect(rect.x + rect.width - 20, rect.y + 5, 15, 15), "📁", 
                             new GUIStyle { fontSize = 12 });
                }
            }
        }
        
        private void DrawGradientRect(Rect rect, Color topColor, Color bottomColor)
        {
            // Simple gradient effect by drawing multiple horizontal lines
            int steps = 20;
            float stepHeight = rect.height / steps;
            
            for (int i = 0; i < steps; i++)
            {
                float t = (float)i / (steps - 1);
                Color currentColor = Color.Lerp(topColor, bottomColor, t);
                
                Rect lineRect = new Rect(
                    rect.x, 
                    rect.y + i * stepHeight, 
                    rect.width, 
                    stepHeight + 1 // Slight overlap to avoid gaps
                );
                
                EditorGUI.DrawRect(lineRect, currentColor);
            }
        }
        
        private void DrawMenuItemGrid(MergedMenuItem item, MergedMenuItem parentMenu)
        {
            if (item == null) return;
            
            bool hasSubMenu = item.children.Count > 0 || (item.control?.subMenu != null);
            bool isEmptySubmenu = (item.control?.type == VRCExpressionsMenu.Control.ControlType.SubMenu) && 
                                 (item.children == null || item.children.Count == 0);
            
            GUILayout.BeginVertical(GUILayout.Width(110), GUILayout.Height(100));
            
            // Create a clickable button style area
            bool clicked = false;
            
            // Background box for the item
            Rect itemRect = GUILayoutUtility.GetRect(110, 100);
            
            // Record item position for drag and drop calculations
            if (editMode)
            {
                itemRects[item] = itemRect;
            }
            
            // Handle drag and drop / selection in edit mode
            if (editMode)
            {
                HandleEditModeInput(item, itemRect, parentMenu, ref clicked);
            }
            else
            {
                // View mode - simple click for navigation
                if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                {
                    clicked = true;
                    Event.current.Use();
                }
            }
            
            // Get icon texture
            Texture2D icon = item.control?.icon;
            
            if (icon != null)
            {
                // Fill the entire grid with the icon as background
                GUI.DrawTexture(itemRect, icon, ScaleMode.ScaleAndCrop, true);
                
                // Add subtle overlay for better text readability
                Color overlayColor;
                if (isEmptySubmenu)
                    overlayColor = new Color(0.8f, 0.8f, 0.2f, 0.4f); // Yellow overlay for empty submenus
                else if (hasSubMenu)
                    overlayColor = new Color(0.2f, 0.4f, 0.8f, 0.3f); // Blue overlay for regular submenus
                else
                    overlayColor = new Color(0, 0, 0, 0.2f); // Default overlay
                
                EditorGUI.DrawRect(itemRect, overlayColor);
            }
            else
            {
                // Draw attractive gradient background if no icon
                Color topColor, bottomColor;
                if (isEmptySubmenu)
                {
                    topColor = new Color(1f, 1f, 0.4f, 0.8f);   // Light yellow top
                    bottomColor = new Color(0.9f, 0.8f, 0.2f, 0.9f); // Darker yellow bottom
                }
                else if (hasSubMenu)
                {
                    topColor = new Color(0.4f, 0.6f, 1f, 0.8f);   // Light blue top
                    bottomColor = new Color(0.2f, 0.4f, 0.9f, 0.9f); // Darker blue bottom
                }
                else
                {
                    // Different colors based on control type for better visual distinction
                    var controlType = item.control?.type ?? VRCExpressionsMenu.Control.ControlType.Button;
                    switch (controlType)
                    {
                        case VRCExpressionsMenu.Control.ControlType.Toggle:
                            topColor = new Color(0.4f, 0.8f, 0.4f, 0.8f);   // Light green
                            bottomColor = new Color(0.2f, 0.6f, 0.2f, 0.9f); // Dark green
                            break;
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            topColor = new Color(0.8f, 0.4f, 0.8f, 0.8f);   // Light purple
                            bottomColor = new Color(0.6f, 0.2f, 0.6f, 0.9f); // Dark purple
                            break;
                        default: // Button
                            topColor = new Color(0.8f, 0.6f, 0.4f, 0.8f);   // Light orange
                            bottomColor = new Color(0.7f, 0.4f, 0.2f, 0.9f); // Dark orange
                            break;
                    }
                }
                
                // Draw gradient background
                DrawGradientRect(itemRect, topColor, bottomColor);
                
                // Draw control name in the center instead of just an icon
                var nameStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    normal = { textColor = Color.white }
                };
                
                // Add shadow for better readability
                var shadowStyle = new GUIStyle(nameStyle)
                {
                    normal = { textColor = new Color(0, 0, 0, 0.7f) }
                };
                
                // Truncate name for center display
                string centerDisplayName = item.name;
                if (centerDisplayName.Length > 16)
                {
                    centerDisplayName = centerDisplayName.Substring(0, 13) + "...";
                }
                
                // Draw shadow
                var shadowRect = new Rect(itemRect.x + 1, itemRect.y + 1, itemRect.width, itemRect.height);
                GUI.Label(shadowRect, centerDisplayName, shadowStyle);
                
                // Draw main text
                GUI.Label(itemRect, centerDisplayName, nameStyle);
                
                // Draw small default icon in corner
                string defaultIcon = GetMenuItemIcon(item);
                var iconStyle = new GUIStyle(EditorStyles.label) 
                { 
                    fontSize = 24, 
                    alignment = TextAnchor.UpperRight,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.7f) }
                };
                
                var iconRect = new Rect(itemRect.x + itemRect.width - 30, itemRect.y + 5, 25, 25);
                GUI.Label(iconRect, defaultIcon, iconStyle);
            }
            
            // Draw border
            Color borderColor;
            float borderWidth;
            if (isEmptySubmenu)
            {
                borderColor = new Color(1f, 0.8f, 0.2f, 0.9f); // Yellow border for empty submenus
                borderWidth = 3f;
            }
            else if (hasSubMenu)
            {
                borderColor = new Color(0.2f, 0.4f, 1f, 0.9f); // Blue border for regular submenus
                borderWidth = 3f;
            }
            else
            {
                borderColor = new Color(0.4f, 0.4f, 0.4f, 0.9f); // Gray border for regular items
                borderWidth = 2f;
            }
            
            EditorGUI.DrawRect(new Rect(itemRect.x, itemRect.y, itemRect.width, borderWidth), borderColor);
            EditorGUI.DrawRect(new Rect(itemRect.x, itemRect.y + itemRect.height - borderWidth, itemRect.width, borderWidth), borderColor);
            EditorGUI.DrawRect(new Rect(itemRect.x, itemRect.y, borderWidth, itemRect.height), borderColor);
            EditorGUI.DrawRect(new Rect(itemRect.x + itemRect.width - borderWidth, itemRect.y, borderWidth, itemRect.height), borderColor);
            
            // Only draw name overlay for texture items (non-texture items show name in center)
            if (icon != null)
            {
                GUILayout.BeginArea(itemRect);
                GUILayout.BeginVertical();
                
                // Item name at the top with background for readability
                var nameStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontSize = 10,
                    normal = { textColor = Color.white }
                };
                
                // Background for text readability
                Rect nameBackgroundRect = new Rect(2, 2, itemRect.width - 4, 18);
                EditorGUI.DrawRect(nameBackgroundRect, new Color(0, 0, 0, 0.6f));
                
                // Truncate long names
                string displayName = item.name;
                if (displayName.Length > 24)
                {
                    displayName = displayName.Substring(0, 21) + "...";
                }
                
                GUILayout.Label(displayName, nameStyle, GUILayout.Height(16));
                
                // Add "Empty" indicator for empty submenus
                if (isEmptySubmenu && editMode)
                {
                    var emptyStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 8,
                        normal = { textColor = new Color(1f, 1f, 0.5f, 0.9f) }
                    };
                    GUILayout.Label(GetLocalizedText("(空)", "(Empty)"), emptyStyle, GUILayout.Height(12));
                }
                
                GUILayout.FlexibleSpace();
                
                // Source indicator at the bottom with background
                var sourceStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 8,
                    normal = { textColor = Color.white }
                };
                
                // Background for source text
                Rect sourceBackgroundRect = new Rect(2, itemRect.height - 14, itemRect.width - 4, 12);
                EditorGUI.DrawRect(sourceBackgroundRect, new Color(0, 0, 0, 0.6f));
                
                string sourceText = GetSourceDisplayText(item.source);
                GUILayout.Label(sourceText, sourceStyle, GUILayout.Height(10));
                
                GUILayout.EndVertical();
                GUILayout.EndArea();
            }
            else
            {
                // For non-texture items, only show source indicator at bottom
                GUILayout.BeginArea(itemRect);
                GUILayout.BeginVertical();
                
                GUILayout.FlexibleSpace();
                
                // Source indicator at the bottom
                var sourceStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 8,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.8f) }
                };
                
                // Semi-transparent background for source text
                Rect sourceBackgroundRect = new Rect(2, itemRect.height - 14, itemRect.width - 4, 12);
                EditorGUI.DrawRect(sourceBackgroundRect, new Color(0, 0, 0, 0.4f));
                
                string sourceText = GetSourceDisplayText(item.source);
                GUILayout.Label(sourceText, sourceStyle, GUILayout.Height(10));
                
                GUILayout.EndVertical();
                GUILayout.EndArea();
            }
            
            GUILayout.EndVertical();
            
            // Draw selection and drag feedback
            if (editMode)
            {
                DrawEditModeVisualFeedback(item, itemRect);
            }
            
            // Draw insertion point feedback
            if (editMode && isDragging)
            {
                DrawInsertionPointFeedback(item, itemRect);
            }
            
            // Handle click
            if (clicked)
            {
                if (editMode)
                {
                    // Edit mode - maintain original behavior (single click navigates to submenu)
                    if (hasSubMenu)
                    {
                        // Navigate to submenu
                        if (item.children.Count > 0)
                        {
                            menuNavigationStack.Add(item);
                        }
                        else if (item.control?.subMenu != null)
                        {
                            // Create temporary item for VRC submenu
                            var tempItem = new MergedMenuItem
                            {
                                name = item.control.subMenu.name,
                                source = "VRC",
                                subMenu = item.control.subMenu
                            };
                            BuildMenuItemsFromVRCMenu(item.control.subMenu, tempItem, "VRC");
                            menuNavigationStack.Add(tempItem);
                        }
                    }
                    else
                    {
                        // Show item details or trigger action
                        if (item.control != null)
                        {
                            OpenControlEditor(item.control);
                        }
                        else if (item.sourceComponent != null)
                        {
                            // Highlight the component in hierarchy
                            Selection.activeGameObject = item.sourceComponent.gameObject;
                            EditorGUIUtility.PingObject(item.sourceComponent.gameObject);
                        }
                    }
                }
                else
                {
                    // View mode - new behavior (single click for control edit, double/shift+click for navigation)
                    bool navigateToSubmenu = hasSubMenu && (Event.current.clickCount == 2 || Event.current.shift);
                    
                    if (navigateToSubmenu)
                    {
                        // Navigate to submenu only on double-click or Shift+click
                        if (item.children.Count > 0)
                        {
                            menuNavigationStack.Add(item);
                        }
                        else if (item.control?.subMenu != null)
                        {
                            // Create temporary item for VRC submenu
                            var tempItem = new MergedMenuItem
                            {
                                name = item.control.subMenu.name,
                                source = "VRC",
                                subMenu = item.control.subMenu
                            };
                            BuildMenuItemsFromVRCMenu(item.control.subMenu, tempItem, "VRC");
                            menuNavigationStack.Add(tempItem);
                        }
                    }
                    else
                    {
                        // Single click - show control editor for all items with controls
                        if (item.control != null)
                        {
                            OpenControlEditor(item.control);
                        }
                        else if (item.sourceComponent != null)
                        {
                            // Highlight the component in hierarchy
                            Selection.activeGameObject = item.sourceComponent.gameObject;
                            EditorGUIUtility.PingObject(item.sourceComponent.gameObject);
                        }
                    }
                }
            }
            
            GUILayout.Space(5);
        }

        private void DrawControlEditor()
        {
            if (editingControl == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GetLocalizedText("コントロール編集", "Control Editor"), EditorStyles.boldLabel);
            
            // Control name
            EditorGUILayout.LabelField(GetLocalizedText("コントロール名", "Control Name"));
            editingControl.name = EditorGUILayout.TextField(editingControl.name);
            
            // Control type
            EditorGUILayout.LabelField(GetLocalizedText("コントロールタイプ", "Control Type"));
            editingControl.type = (VRCExpressionsMenu.Control.ControlType)EditorGUILayout.EnumPopup(editingControl.type);
            
            // Parameter field (for controls with parameters)
            if (editingControl.type == VRCExpressionsMenu.Control.ControlType.Button || 
                editingControl.type == VRCExpressionsMenu.Control.ControlType.Toggle)
            {
                EditorGUILayout.LabelField(GetLocalizedText("パラメータ名", "Parameter Name"));
                if (editingControl.parameter == null)
                {
                    editingControl.parameter = new VRCExpressionsMenu.Control.Parameter();
                }
                editingControl.parameter.name = EditorGUILayout.TextField(editingControl.parameter.name);
            }
            
            // Value field (for controls with numeric values)
            if (editingControl.type == VRCExpressionsMenu.Control.ControlType.Button)
            {
                EditorGUILayout.LabelField(GetLocalizedText("値", "Value"));
                editingControl.value = EditorGUILayout.Slider(editingControl.value, 0, 1);
            }
            
            // Icon field
            EditorGUILayout.LabelField(GetLocalizedText("アイコン", "Icon"));
            
            // アイコン選択フィールド
            var newIcon = (Texture2D)EditorGUILayout.ObjectField(editingControl.icon, typeof(Texture2D), false);
            if (newIcon != editingControl.icon)
            {
                editingControl.icon = newIcon;
            }
            
            // アイコンプレビュー表示
            if (editingControl.icon != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(GetLocalizedText("プレビュー:", "Preview:"), EditorStyles.miniBoldLabel);
                
                EditorGUILayout.BeginVertical("box");
                
                // 中サイズのアイコンプレビュー（64x64）
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                
                Rect previewRect = GUILayoutUtility.GetRect(64, 64, GUILayout.Width(64), GUILayout.Height(64));
                EditorGUI.DrawRect(previewRect, new Color(0.85f, 0.85f, 0.85f, 1f));
                GUI.DrawTexture(previewRect, editingControl.icon, ScaleMode.ScaleToFit, true);
                
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                
                // アイコン情報
                EditorGUILayout.LabelField(GetLocalizedText("ファイル名", "File Name"), editingControl.icon.name, EditorStyles.miniLabel);
                EditorGUILayout.LabelField(GetLocalizedText("サイズ", "Size"), $"{editingControl.icon.width} x {editingControl.icon.height}", EditorStyles.miniLabel);
                
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space();
            }
            
            // Sub menu (for controls that open submenus)
            if (editingControl.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
            {
                EditorGUILayout.LabelField(GetLocalizedText("サブメニュー", "Sub Menu"));
                editingControl.subMenu = (VRCExpressionsMenu)EditorGUILayout.ObjectField(editingControl.subMenu, typeof(VRCExpressionsMenu), false);
            }
            
            // Save button
            if (GUILayout.Button(GetLocalizedText("保存", "Save")))
            {
                isEditingControl = false;
                // TODO: Add any additional save logic here
            }
            
            EditorGUILayout.EndVertical();
        }

        private List<Component> GetModularAvatarMenuInstallers()
        {
            var installers = new List<Component>();
            
            if (selectedAvatar != null && IsModularAvatarAvailable())
            {
                var installerType = GetModularAvatarMenuInstallerType();
                if (installerType != null)
                {
                    var components = selectedAvatar.GetComponentsInChildren(installerType, true);
                    
                    // Filter out Editor Only components
                    foreach (var component in components)
                    {
                        if (!IsComponentOrParentEditorOnly(component))
                        {
                            installers.Add(component);
                        }
                    }
                }
            }
            
            return installers;
        }
        
        private List<Component> GetModularAvatarObjectToggles()
        {
            var toggles = new List<Component>();
            
            if (selectedAvatar != null && IsModularAvatarAvailable())
            {
                var toggleType = GetModularAvatarObjectToggleType();
                if (toggleType != null)
                {
                    var components = selectedAvatar.GetComponentsInChildren(toggleType, true);
                    
                    // Filter out Editor Only components
                    foreach (var component in components)
                    {
                        if (!IsComponentOrParentEditorOnly(component))
                        {
                            toggles.Add(component);
                        }
                    }
                }
            }
            
            return toggles;
        }
        
        private List<Component> GetModularAvatarMenuItems()
        {
            var menuItems = new List<Component>();
            
            if (selectedAvatar != null && IsModularAvatarAvailable())
            {
                var menuItemType = GetModularAvatarMenuItemType();
                if (menuItemType != null)
                {
                    var components = selectedAvatar.GetComponentsInChildren(menuItemType, true);
                    
                    // Filter out Editor Only components
                    foreach (var component in components)
                    {
                        if (!IsComponentOrParentEditorOnly(component))
                        {
                            menuItems.Add(component);
                        }
                    }
                }
            }
            
            return menuItems;
        }

        private VRCExpressionsMenu GetMainExpressionMenu()
        {
            if (selectedAvatar?.expressionsMenu != null)
            {
                return selectedAvatar.expressionsMenu;
            }
            return null;
        }

        private VRCExpressionsMenu GetMenuToAppendFromInstaller(Component installer)
        {
            if (installer == null) return null;
            
            try
            {
                // Get menuToAppend field using reflection
                var menuToAppendField = installer.GetType().GetField("menuToAppend");
                if (menuToAppendField != null)
                {
                    return menuToAppendField.GetValue(installer) as VRCExpressionsMenu;
                }
            }
            catch
            {
                // Ignore reflection errors
            }
            
            return null;
        }

        private VRCExpressionsMenu GetInstallTargetFromInstaller(Component installer)
        {
            if (installer == null) return null;
            
            try
            {
                // Get install target information using reflection
                var installTargetField = installer.GetType().GetField("installTargetMenu");
                if (installTargetField != null)
                {
                    return installTargetField.GetValue(installer) as VRCExpressionsMenu;
                }
            }
            catch
            {
                // Ignore reflection errors
            }
            
            return null;
        }
        
        private MergedMenuItem FindMenuItemByVRCMenu(MergedMenuItem rootItem, VRCExpressionsMenu targetMenu)
        {
            if (rootItem == null || targetMenu == null) return null;
            
            // Check if this item's submenu matches the target
            if (rootItem.subMenu == targetMenu)
            {
                return rootItem;
            }
            
            // Check if this item's control has a submenu that matches
            if (rootItem.control?.subMenu == targetMenu)
            {
                return rootItem;
            }
            
            // Recursively search in children
            foreach (var child in rootItem.children)
            {
                var found = FindMenuItemByVRCMenu(child, targetMenu);
                if (found != null)
                {
                    return found;
                }
            }
            
            return null;
        }

        private string GetMenuInstallPath(Component installer)
        {
            if (installer == null) return GetLocalizedText("不明", "Unknown");
            
            var installTarget = GetInstallTargetFromInstaller(installer);
            if (installTarget != null)
            {
                return installTarget.name;
            }
            
            // If no specific target, it will install to the root menu
            return GetLocalizedText("ルートメニュー", "Root Menu");
        }

        private void DrawObjectToggle(Component toggle)
        {
            if (toggle == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            // Toggle Header
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"🔄 {toggle.name}", EditorStyles.miniBoldLabel);
            
            // GameObject reference button
            if (GUILayout.Button("→", GUILayout.Width(25)))
            {
                Selection.activeGameObject = toggle.gameObject;
                EditorGUIUtility.PingObject(toggle.gameObject);
            }
            GUILayout.EndHorizontal();
            
            // Toggle Details
            EditorGUI.indentLevel++;
            
            try
            {
                // Get toggle objects using reflection
                var objectsField = toggle.GetType().GetField("Objects");
                if (objectsField != null)
                {
                    var objects = objectsField.GetValue(toggle) as System.Collections.IList;
                    if (objects != null && objects.Count > 0)
                    {
                        EditorGUILayout.LabelField(GetLocalizedText($"制御オブジェクト数: {objects.Count}", $"Controlled Objects: {objects.Count}"), parameterStyle);
                    }
                    else
                    {
                        EditorGUILayout.LabelField(GetLocalizedText("制御オブジェクト: (なし)", "Controlled Objects: (None)"), parameterStyle);
                    }
                }
            }
            catch
            {
                EditorGUILayout.LabelField(GetLocalizedText("詳細情報を取得できませんでした", "Could not retrieve details"), parameterStyle);
            }
            
            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }
        
        private void DrawMenuItem(Component item)
        {
            if (item == null) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            // Item Header
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"📋 {item.name}", EditorStyles.miniBoldLabel);
            
            // GameObject reference button
            if (GUILayout.Button("→", GUILayout.Width(25)))
            {
                Selection.activeGameObject = item.gameObject;
                EditorGUIUtility.PingObject(item.gameObject);
            }
            GUILayout.EndHorizontal();
            
            // Item Details
            EditorGUI.indentLevel++;
            
            try
            {
                // Get menu item properties using reflection
                var controlField = item.GetType().GetField("Control");
                if (controlField != null)
                {
                    var control = controlField.GetValue(item);
                    if (control != null)
                    {
                        // Get control name
                        var nameField = control.GetType().GetField("name");
                        if (nameField != null)
                        {
                            var controlName = nameField.GetValue(control) as string;
                            if (!string.IsNullOrEmpty(controlName))
                            {
                                EditorGUILayout.LabelField(GetLocalizedText($"コントロール名: {controlName}", $"Control Name: {controlName}"), parameterStyle);
                            }
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField(GetLocalizedText("コントロール: (設定なし)", "Control: (Not configured)"), parameterStyle);
                    }
                }
            }
            catch
            {
                EditorGUILayout.LabelField(GetLocalizedText("詳細情報を取得できませんでした", "Could not retrieve details"), parameterStyle);
            }
            
            EditorGUI.indentLevel--;
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private string GetControlIcon(VRCExpressionsMenu.Control.ControlType controlType)
        {
            switch (controlType)
            {
                case VRCExpressionsMenu.Control.ControlType.Button:
                    return "🔘";
                case VRCExpressionsMenu.Control.ControlType.Toggle:
                    return "🎚️";
                case VRCExpressionsMenu.Control.ControlType.SubMenu:
                    return "📂";
                case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                    return "🎮";
                case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                    return "🕹️";
                case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                    return "⭕";
                default:
                    return "⚪";
            }
        }

        private void OpenControlEditor(VRCExpressionsMenu.Control control)
        {
            editingControl = control;
            isEditingControl = true;
        }

        private string GetParameterType(VRCExpressionsMenu.Control.Parameter parameter)
        {
            if (parameter == null) return "";
            
            // Control.Parameter only contains the name, not full parameter info
            return GetLocalizedText("文字列参照", "String Reference");
        }

        private string GetMenuPath(VRCExpressionsMenu menu)
        {
            if (menu == null) return "";
            
            // メニューのパスを構築（簡易版）
            string assetPath = AssetDatabase.GetAssetPath(menu);
            string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            return fileName;
        }
        
        private void HandleEditModeInput(MergedMenuItem item, Rect itemRect, MergedMenuItem parentMenu, ref bool clicked)
        {
            Event evt = Event.current;
            
            switch (evt.type)
            {
                case EventType.MouseDown:
                    if (itemRect.Contains(evt.mousePosition) && evt.button == 0)
                    {
                        // Handle selection
                        if (evt.control || evt.command)
                        {
                            // Toggle selection
                            if (selectedItems.Contains(item))
                                selectedItems.Remove(item);
                            else
                                selectedItems.Add(item);
                        }
                        else if (evt.shift && lastClickedItem != null)
                        {
                            // Range selection (simplified)
                            selectedItems.Clear();
                            selectedItems.Add(item);
                        }
                        else
                        {
                            // Single selection
                            selectedItems.Clear();
                            selectedItems.Add(item);
                        }
                        
                        lastClickedItem = item;
                        draggedItem = item;
                        dragStartPosition = evt.mousePosition;
                        isDragging = false;
                        
                        evt.Use();
                    }
                    break;
                    
                case EventType.MouseDrag:
                    if (draggedItem == item && !isDragging)
                    {
                        // Start dragging if moved enough
                        if (Vector2.Distance(evt.mousePosition, dragStartPosition) > 5f)
                        {
                            isDragging = true;
                            Repaint();
                        }
                    }
                    else if (isDragging)
                    {
                        FindDropTarget(evt.mousePosition, parentMenu);
                        Repaint();
                    }
                    break;
                    
                case EventType.MouseUp:
                    if (draggedItem == item)
                    {
                        if (isDragging && dragTargetParent != null)
                        {
                            // Perform the move
                            MoveItemToNewLocation(draggedItem, dragTargetParent, dragTargetIndex);
                        }
                        else if (!isDragging && itemRect.Contains(evt.mousePosition))
                        {
                            // It was just a click
                            clicked = true;
                        }
                        
                        // Reset drag state
                        draggedItem = null;
                        isDragging = false;
                        dragTargetParent = null;
                        dragTargetIndex = -1;
                        Repaint();
                    }
                    break;
            }
        }
        
        private void DrawEditModeVisualFeedback(MergedMenuItem item, Rect itemRect)
        {
            // Selection border
            if (selectedItems.Contains(item))
            {
                Color selectionColor = new Color(0.2f, 0.6f, 1f, 1f); // Blue
                DrawBorder(itemRect, selectionColor, 3f);
            }
            
            // Drag feedback
            if (isDragging && draggedItem == item)
            {
                Color dragColor = new Color(1f, 0.8f, 0f, 0.7f); // Gold
                DrawBorder(itemRect, dragColor, 4f);
                
                // Draw drag preview at cursor
                Vector2 mousePos = Event.current.mousePosition;
                Rect dragPreviewRect = new Rect(mousePos.x - 55, mousePos.y - 50, 110, 100);
                
                Color prevColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.8f);
                EditorGUI.DrawRect(dragPreviewRect, new Color(0f, 0f, 0f, 0.3f));
                GUI.color = prevColor;
                
                GUI.Label(dragPreviewRect, item.name, EditorStyles.centeredGreyMiniLabel);
            }
            
            // Drop target feedback
            if (isDragging && dragTargetParent != null && IsValidDropTarget(item))
            {
                Color dropColor = new Color(0f, 1f, 0f, 0.8f); // Green
                DrawBorder(itemRect, dropColor, 2f);
            }
        }
        
        private void DrawBorder(Rect rect, Color color, float width)
        {
            // Top
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, width), color);
            // Bottom
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height - width, rect.width, width), color);
            // Left
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, width, rect.height), color);
            // Right
            EditorGUI.DrawRect(new Rect(rect.x + rect.width - width, rect.y, width, rect.height), color);
        }
        
        private void DrawInsertionPointFeedback(MergedMenuItem item, Rect itemRect)
        {
            if (dragTargetIndex == -1) return;
            
            int itemIndex = currentMenuItems.IndexOf(item);
            if (itemIndex == -1) return;
            
            Color insertionColor = new Color(0f, 1f, 0f, 0.9f); // Bright green
            float lineWidth = 4f;
            
            // Draw insertion line before this item
            if (dragTargetIndex == itemIndex)
            {
                // Draw vertical line at the left side of this item
                Rect insertionLine = new Rect(itemRect.x - 2, itemRect.y - 5, lineWidth, itemRect.height + 10);
                EditorGUI.DrawRect(insertionLine, insertionColor);
                
                // Draw small arrows to indicate insertion point
                DrawInsertionArrow(new Vector2(itemRect.x - 2, itemRect.y + itemRect.height / 2), insertionColor);
            }
            // Draw insertion line after this item
            else if (dragTargetIndex == itemIndex + 1)
            {
                // Draw vertical line at the right side of this item
                Rect insertionLine = new Rect(itemRect.x + itemRect.width - 2, itemRect.y - 5, lineWidth, itemRect.height + 10);
                EditorGUI.DrawRect(insertionLine, insertionColor);
                
                // Draw small arrows to indicate insertion point
                DrawInsertionArrow(new Vector2(itemRect.x + itemRect.width + 2, itemRect.y + itemRect.height / 2), insertionColor);
            }
        }
        
        private void DrawInsertionArrow(Vector2 position, Color color)
        {
            float arrowSize = 8f;
            
            // Draw simple arrow indicator
            Vector2[] points = {
                new Vector2(position.x - arrowSize/2, position.y - arrowSize/2),
                new Vector2(position.x + arrowSize/2, position.y),
                new Vector2(position.x - arrowSize/2, position.y + arrowSize/2)
            };
            
            // Simple implementation using rectangles to form arrow shape
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector2 start = points[i];
                Vector2 end = points[i + 1];
                
                // Draw line between points (simplified)
                float distance = Vector2.Distance(start, end);
                Vector2 direction = (end - start).normalized;
                
                for (float d = 0; d < distance; d += 1f)
                {
                    Vector2 point = start + direction * d;
                    EditorGUI.DrawRect(new Rect(point.x, point.y, 2, 2), color);
                }
            }
        }
        
        private void FindDropTarget(Vector2 mousePosition, MergedMenuItem currentParent)
        {
            // Reset drop target
            dragTargetParent = currentParent;
            dragTargetIndex = -1;
            dragTargetIsSubmenu = false;
            dragTargetIsParentLevel = false;
            dragTargetIsDeleteArea = false;
            
            // Check for delete area drop (highest priority)
            if (IsInDeleteArea(mousePosition))
            {
                dragTargetIsDeleteArea = true;
                return;
            }
            
            // Check for back navigation area drop (move up hierarchy)
            if (menuNavigationStack.Count > 0 && IsInBackNavigationArea(mousePosition))
            {
                dragTargetParent = GetParentMenu();
                dragTargetIndex = GetParentMenu()?.children?.Count ?? 0; // Add to end of parent
                dragTargetIsParentLevel = true;
                return;
            }
            
            if (currentMenuItems.Count == 0) return;
            
            // First pass: Check for submenu drops (dropping ON items with submenus)
            for (int i = 0; i < currentMenuItems.Count; i++)
            {
                var item = currentMenuItems[i];
                if (!itemRects.ContainsKey(item)) continue;
                if (item == draggedItem) continue;
                if (!HasSubmenu(item)) continue;
                
                var rect = itemRects[item];
                
                // Check if mouse is over the center area of a submenu item
                Rect centerArea = new Rect(rect.x + rect.width * 0.25f, rect.y + rect.height * 0.25f, 
                                         rect.width * 0.5f, rect.height * 0.5f);
                
                if (centerArea.Contains(mousePosition) && IsValidDropTarget(item))
                {
                    dragTargetParent = item;
                    dragTargetIndex = item.children?.Count ?? 0; // Add to end of submenu
                    dragTargetIsSubmenu = true;
                    hoveredSubmenu = item;
                    return;
                }
            }
            
            // Second pass: Find the best insertion point for reordering in current menu
            float bestDistance = float.MaxValue;
            int bestIndex = currentMenuItems.Count; // Default to end
            
            for (int i = 0; i < currentMenuItems.Count; i++)
            {
                var item = currentMenuItems[i];
                if (!itemRects.ContainsKey(item)) continue;
                if (item == draggedItem) continue;
                
                var rect = itemRects[item];
                
                // Calculate insertion points (left side, right side)
                Vector2[] insertionPoints = {
                    new Vector2(rect.x, rect.y + rect.height / 2), // Left side
                    new Vector2(rect.x + rect.width, rect.y + rect.height / 2) // Right side
                };
                
                // Check left insertion point (before this item)
                float distanceLeft = Vector2.Distance(mousePosition, insertionPoints[0]);
                if (distanceLeft < bestDistance)
                {
                    bestDistance = distanceLeft;
                    bestIndex = i;
                }
                
                // Check right insertion point (after this item)
                float distanceRight = Vector2.Distance(mousePosition, insertionPoints[1]);
                if (distanceRight < bestDistance)
                {
                    bestDistance = distanceRight;
                    bestIndex = i + 1;
                }
            }
            
            // Set the best insertion index for reordering
            dragTargetIndex = bestIndex;
            
            // Adjust for dragged item's current position to avoid unnecessary moves
            int draggedIndex = currentMenuItems.IndexOf(draggedItem);
            if (draggedIndex != -1 && dragTargetIndex > draggedIndex)
            {
                dragTargetIndex--; // Adjust because we'll remove the dragged item first
            }
        }
        
        private bool IsValidDropTarget(MergedMenuItem item)
        {
            // Prevent dropping item on itself or its children
            if (draggedItem == item) return false;
            if (IsDescendantOf(draggedItem, item)) return false;
            
            return true;
        }
        
        private bool IsDescendantOf(MergedMenuItem ancestor, MergedMenuItem item)
        {
            if (ancestor?.children == null) return false;
            
            foreach (var child in ancestor.children)
            {
                if (child == item) return true;
                if (IsDescendantOf(child, item)) return true;
            }
            
            return false;
        }
        
        private bool IsInBackNavigationArea(Vector2 mousePosition)
        {
            // Back navigation drop zone is in the breadcrumb area
            return backNavigationDropArea.Contains(mousePosition);
        }
        
        private MergedMenuItem GetParentMenu()
        {
            if (menuNavigationStack.Count <= 1)
            {
                // At root level, return the edited structure root
                return editedMenuStructure ?? BuildMergedMenuStructure();
            }
            
            // Return the parent menu (one level up)
            return menuNavigationStack[menuNavigationStack.Count - 2];
        }
        
        private bool HasSubmenu(MergedMenuItem item)
        {
            // Check if item has children (non-empty submenu)
            if (item.children != null && item.children.Count > 0)
                return true;
            
            // Check if item has a VRC submenu control (including empty ones)
            if (item.control?.subMenu != null)
                return true;
            
            // Check if item's control type is SubMenu (for empty submenus without subMenu reference)
            if (item.control?.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                return true;
                
            return false;
        }
        
        private bool IsInDeleteArea(Vector2 mousePosition)
        {
            return deleteDropArea.Contains(mousePosition);
        }
        
        private void InitializeEditedMenuStructure()
        {
            try
            {
                var originalStructure = BuildMergedMenuStructure();
                if (originalStructure != null)
                {
                    editedMenuStructure = CloneMenuStructure(originalStructure);
                    
                    // Reset navigation stack when entering edit mode
                    menuNavigationStack.Clear();
                    
                    Debug.Log("Edit mode initialized with menu structure clone");
                }
                else
                {
                    Debug.LogWarning("Failed to initialize edited menu structure - no original structure found");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error initializing edited menu structure: {e.Message}");
                editedMenuStructure = null;
            }
        }
        
        private void MoveItemToNewLocation(MergedMenuItem item, MergedMenuItem newParent, int newIndex)
        {
            // Handle different types of moves based on drag target state
            if (dragTargetIsDeleteArea)
            {
                DeleteItemWithConfirmation(item);
                return;
            }
            
            if (dragTargetIsSubmenu)
            {
                MoveItemToSubmenu(item, newParent, newIndex);
                return;
            }
            
            if (dragTargetIsParentLevel)
            {
                MoveItemToParentLevel(item, newParent, newIndex);
                return;
            }
            
            // For grid reordering within the same parent, use specialized method
            if (newParent != null && IsInCurrentMenu(item) && dragTargetIndex >= 0)
            {
                MoveItemWithinCurrentMenu(item, dragTargetIndex);
                return;
            }
            
            // Fallback: general cross-hierarchy move
            MoveItemToNewHierarchy(item, newParent, newIndex);
        }
        
        private void DeleteItemWithConfirmation(MergedMenuItem item)
        {
            if (item == null) return;
            
            string confirmationMessage = GetLocalizedText(
                $"本当に '{item.name}' を削除しますか？",
                $"Really delete '{item.name}'?"
            );
            
            string title = GetLocalizedText("削除確認", "Confirm Deletion");
            string okButton = GetLocalizedText("削除", "Delete");
            string cancelButton = GetLocalizedText("キャンセル", "Cancel");
            
            if (EditorUtility.DisplayDialog(title, confirmationMessage, okButton, cancelButton))
            {
                DeleteItem(item);
            }
        }
        
        private void DeleteItem(MergedMenuItem item)
        {
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // Find and remove the item from the edited structure
            var editedItem = FindItemInStructure(editedMenuStructure, item.name);
            if (editedItem != null)
            {
                RemoveItemFromParent(editedMenuStructure, editedItem);
                
                // Remove from selection if selected
                selectedItems.Remove(item);
                
                // Update current view
                UpdateCurrentMenuItems();
                
                Debug.Log(GetLocalizedText(
                    $"アイテム '{item.name}' を削除しました",
                    $"Deleted item '{item.name}'"
                ));
                
                Repaint();
            }
        }
        
        private void MoveItemToSubmenu(MergedMenuItem item, MergedMenuItem submenuParent, int newIndex)
        {
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // Find the item and submenu parent in the edited structure
            var editedItem = FindItemInStructure(editedMenuStructure, item.name);
            var editedSubmenuParent = FindItemInStructure(editedMenuStructure, submenuParent.name);
            
            if (editedItem != null && editedSubmenuParent != null)
            {
                // Remove from current location
                RemoveItemFromParent(editedMenuStructure, editedItem);
                
                // Ensure submenu parent has children list
                if (editedSubmenuParent.children == null)
                    editedSubmenuParent.children = new List<MergedMenuItem>();
                
                // Add to submenu
                editedSubmenuParent.children.Insert(Math.Min(newIndex, editedSubmenuParent.children.Count), editedItem);
                
                // Update current view
                UpdateCurrentMenuItems();
                
                Debug.Log(GetLocalizedText(
                    $"アイテム '{item.name}' をサブメニュー '{submenuParent.name}' に移動しました",
                    $"Moved item '{item.name}' into submenu '{submenuParent.name}'"
                ));
            }
        }
        
        private void MoveItemToParentLevel(MergedMenuItem item, MergedMenuItem parentMenu, int newIndex)
        {
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // Find the item in the edited structure
            var editedItem = FindItemInStructure(editedMenuStructure, item.name);
            var editedParentMenu = parentMenu != null ? FindItemInStructure(editedMenuStructure, parentMenu.name) : editedMenuStructure;
            
            if (editedItem != null && editedParentMenu != null)
            {
                // Remove from current location
                RemoveItemFromParent(editedMenuStructure, editedItem);
                
                // Ensure parent has children list
                if (editedParentMenu.children == null)
                    editedParentMenu.children = new List<MergedMenuItem>();
                
                // Add to parent level
                editedParentMenu.children.Insert(Math.Min(newIndex, editedParentMenu.children.Count), editedItem);
                
                // Navigate back to parent level if we moved item up
                if (menuNavigationStack.Count > 0)
                {
                    menuNavigationStack.RemoveAt(menuNavigationStack.Count - 1);
                    UpdateCurrentMenuItems();
                }
                
                Debug.Log(GetLocalizedText(
                    $"アイテム '{item.name}' を上位階層に移動しました",
                    $"Moved item '{item.name}' up to parent level"
                ));
            }
        }
        
        private void MoveItemToNewHierarchy(MergedMenuItem item, MergedMenuItem newParent, int newIndex)
        {
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // Find the item in the edited structure and move it
            var editedItem = FindItemInStructure(editedMenuStructure, item.name);
            if (editedItem != null)
            {
                // Remove from current parent
                RemoveItemFromParent(editedMenuStructure, editedItem);
                
                // Add to new parent
                var editedNewParent = newParent != null ? 
                    FindItemInStructure(editedMenuStructure, newParent.name) : editedMenuStructure;
                
                if (editedNewParent != null)
                {
                    if (editedNewParent.children == null)
                        editedNewParent.children = new List<MergedMenuItem>();
                    
                    editedNewParent.children.Insert(Math.Min(newIndex, editedNewParent.children.Count), editedItem);
                    UpdateCurrentMenuItems();
                }
            }
        }
        
        private bool IsInCurrentMenu(MergedMenuItem item)
        {
            return currentMenuItems.Contains(item);
        }
        
        private void MoveItemWithinCurrentMenu(MergedMenuItem item, int newIndex)
        {
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // Get the current menu context
            var currentMenu = GetCurrentEditedMenu();
            if (currentMenu?.children == null) return;
            
            // Find the item in the current menu
            var itemIndex = currentMenu.children.FindIndex(i => i.name == item.name);
            if (itemIndex == -1) return;
            
            // Remove item from current position
            var movingItem = currentMenu.children[itemIndex];
            currentMenu.children.RemoveAt(itemIndex);
            
            // Adjust insertion index if necessary
            if (newIndex > itemIndex)
                newIndex--;
            
            // Insert at new position
            newIndex = Math.Max(0, Math.Min(newIndex, currentMenu.children.Count));
            currentMenu.children.Insert(newIndex, movingItem);
            
            // Update the current menu items for immediate visual feedback
            UpdateCurrentMenuItems();
        }
        
        private MergedMenuItem GetCurrentEditedMenu()
        {
            if (editedMenuStructure == null) return null;
            
            // Navigate to the current menu based on navigation stack
            var currentMenu = editedMenuStructure;
            foreach (var navItem in menuNavigationStack)
            {
                var foundChild = currentMenu.children?.FirstOrDefault(c => c.name == navItem.name);
                if (foundChild != null)
                {
                    currentMenu = foundChild;
                }
                else
                {
                    break;
                }
            }
            
            return currentMenu;
        }
        
        private void UpdateCurrentMenuItems()
        {
            var currentMenu = GetCurrentEditedMenu();
            if (currentMenu?.children != null)
            {
                currentMenuItems.Clear();
                currentMenuItems.AddRange(currentMenu.children);
            }
        }
        
        private MergedMenuItem CloneMenuStructure(MergedMenuItem original)
        {
            if (original == null) return null;
            
            var clone = new MergedMenuItem
            {
                name = original.name,
                source = original.source,
                control = original.control,
                subMenu = original.subMenu,
                children = new List<MergedMenuItem>(),
                sourceComponent = original.sourceComponent,
                originalIndex = original.originalIndex
            };
            
            if (original.children != null)
            {
                foreach (var child in original.children)
                {
                    clone.children.Add(CloneMenuStructure(child));
                }
            }
            
            return clone;
        }
        
        private MergedMenuItem FindItemInStructure(MergedMenuItem root, string itemName)
        {
            if (root == null || itemName == null) return null;
            if (root.name == itemName) return root;
            
            if (root.children != null)
            {
                foreach (var child in root.children)
                {
                    var found = FindItemInStructure(child, itemName);
                    if (found != null) return found;
                }
            }
            
            return null;
        }
        
        private void RemoveItemFromParent(MergedMenuItem root, MergedMenuItem itemToRemove)
        {
            if (root?.children == null) return;
            
            if (root.children.Remove(itemToRemove)) return;
            
            foreach (var child in root.children)
            {
                RemoveItemFromParent(child, itemToRemove);
            }
        }
        
        private void SaveEditedMenuStructure()
        {
            if (selectedAvatar == null)
            {
                EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"), 
                    GetLocalizedText("アバターが選択されていません", "No avatar selected"), "OK");
                return;
            }
            
            try
            {
                var menuStructure = editedMenuStructure ?? BuildMergedMenuStructure();
                
                // Create folder for menu assets
                string folderName = $"{selectedAvatar.name}_EditedExpressionMenus";
                string folderPath = $"Assets/{folderName}";
                
                // Create folder if it doesn't exist
                if (!AssetDatabase.IsValidFolder(folderPath))
                {
                    string guid = AssetDatabase.CreateFolder("Assets", folderName);
                    if (string.IsNullOrEmpty(guid))
                    {
                        Debug.LogError("Failed to create folder for expression menus");
                        return;
                    }
                    Debug.Log($"Created folder: {folderPath}");
                }
                
                // Generate menu with all submenus saved as assets
                var newMenu = GenerateExpressionMenuFromMergedStructureWithAssets(menuStructure, folderPath);
                
                if (newMenu != null)
                {
                    // Create unique asset path for root menu
                    string rootAssetPath = AssetDatabase.GenerateUniqueAssetPath($"{folderPath}/Root.asset");
                    
                    // Create the root menu asset
                    AssetDatabase.CreateAsset(newMenu, rootAssetPath);
                    
                    // Assign to avatar
                    selectedAvatar.expressionsMenu = newMenu;
                    EditorUtility.SetDirty(selectedAvatar);
                    
                    // Update ModularAvatar Menu Installers to point to appropriate menus
                    UpdateModularAvatarInstallers(menuStructure, newMenu);
                    
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    
                    Debug.Log(GetLocalizedText(
                        $"新しいExpressionMenuを作成し、アバターに割り当てました: {rootAssetPath}",
                        $"Created new ExpressionMenu and assigned to avatar: {rootAssetPath}"
                    ));
                    
                    // Reset edit state
                    editedMenuStructure = null;
                    selectedItems.Clear();
                    
                    EditorUtility.DisplayDialog(GetLocalizedText("保存完了", "Save Complete"),
                        GetLocalizedText("メニューが正常に保存されました", "Menu saved successfully"), "OK");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError(GetLocalizedText(
                    $"メニューの保存中にエラーが発生しました: {e.Message}",
                    $"Error saving menu: {e.Message}"
                ));
                
                EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                    GetLocalizedText("保存中にエラーが発生しました", "Error occurred during save"), "OK");
            }
        }
        
        private VRCExpressionsMenu GenerateExpressionMenuFromMergedStructure(MergedMenuItem rootItem)
        {
            if (rootItem?.children == null || rootItem.children.Count == 0)
            {
                return null;
            }
            
            // Create new root menu
            var newMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            newMenu.controls = new List<VRCExpressionsMenu.Control>();
            
            // Convert merged structure to VRC controls
            foreach (var item in rootItem.children)
            {
                var control = ConvertMergedItemToControl(item);
                if (control != null)
                {
                    newMenu.controls.Add(control);
                }
            }
            
            return newMenu;
        }
        
        private void ResetEditedMenuStructure()
        {
            editedMenuStructure = null;
            selectedItems.Clear();
            draggedItem = null;
            isDragging = false;
            
            // Clear navigation stack to return to root
            menuNavigationStack.Clear();
            
            // Clear drag/drop tracking data
            itemRects.Clear();
            currentMenuItems.Clear();
            dragTargetParent = null;
            dragTargetIndex = -1;
            
            // Clear hierarchy change state
            dragTargetIsSubmenu = false;
            dragTargetIsParentLevel = false;
            dragTargetIsDeleteArea = false;
            hoveredSubmenu = null;
            backNavigationDropArea = Rect.zero;
            deleteDropArea = Rect.zero;
            
            Debug.Log("Edit mode reset - returned to original menu structure");
            Repaint();
        }
        
        private VRCExpressionsMenu.Control ConvertMergedItemToControl(MergedMenuItem item, string parentPath = "")
        {
            // Skip ModularAvatar items - they will be handled separately
            if (IsModularAvatarItem(item))
            {
                return null;
            }
            
            if (item.control != null)
            {
                // If it has an original control, clone it
                var control = new VRCExpressionsMenu.Control();
                control.name = item.control.name;
                control.icon = item.control.icon;
                control.type = item.control.type;
                control.parameter = item.control.parameter;
                control.value = item.control.value;
                control.style = item.control.style;
                control.subParameters = item.control.subParameters?.ToArray();
                control.labels = item.control.labels?.ToArray();
                
                // Handle submenu (filter out MA items from children)
                if (item.children != null && item.children.Count > 0)
                {
                    string currentPath = string.IsNullOrEmpty(parentPath) ? item.name : $"{parentPath}_{item.name}";
                    control.subMenu = CreateSubmenuFromChildren(item.children, currentPath, item.name);
                }
                else
                {
                    control.subMenu = item.control.subMenu;
                }
                
                return control;
            }
            else if (item.children != null && item.children.Count > 0)
            {
                // Create a submenu control for items that only have children (filter out MA items)
                var nonMAChildren = item.children.Where(c => !IsModularAvatarItem(c)).ToList();
                if (nonMAChildren.Count > 0)
                {
                    var control = new VRCExpressionsMenu.Control();
                    control.name = item.name;
                    control.type = VRCExpressionsMenu.Control.ControlType.SubMenu;
                    string currentPath = string.IsNullOrEmpty(parentPath) ? item.name : $"{parentPath}_{item.name}";
                    control.subMenu = CreateSubmenuFromChildren(item.children, currentPath, item.name);
                    
                    return control;
                }
            }
            
            return null;
        }
        
        private bool IsModularAvatarItem(MergedMenuItem item)
        {
            return item.source.StartsWith("MA_");
        }
        
        private VRCExpressionsMenu CreateSubmenuFromChildren(List<MergedMenuItem> children, string parentPath = "", string menuName = "Submenu")
        {
            var submenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            submenu.controls = new List<VRCExpressionsMenu.Control>();
            submenu.name = menuName;
            
            foreach (var child in children)
            {
                var control = ConvertMergedItemToControl(child, parentPath);
                if (control != null)
                {
                    submenu.controls.Add(control);
                }
            }
            
            return submenu;
        }
        
        private VRCExpressionsMenu GenerateExpressionMenuFromMergedStructureWithAssets(MergedMenuItem rootItem, string basePath)
        {
            // Create a dictionary to track created submenu assets
            var submenuAssets = new Dictionary<string, VRCExpressionsMenu>();
            
            // Generate the root menu
            var rootMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            rootMenu.controls = new List<VRCExpressionsMenu.Control>();
            rootMenu.name = selectedAvatar.name + "_EditedExpressionMenu";
            
            // Process all children recursively
            ProcessChildrenWithAssets(rootItem.children, rootMenu.controls, submenuAssets, basePath, "");
            
            return rootMenu;
        }
        
        private void ProcessChildrenWithAssets(List<MergedMenuItem> children, List<VRCExpressionsMenu.Control> targetControls, 
            Dictionary<string, VRCExpressionsMenu> submenuAssets, string basePath, string currentPath)
        {
            if (children == null) return;
            
            foreach (var child in children)
            {
                // Skip ModularAvatar items
                if (IsModularAvatarItem(child)) continue;
                
                var control = CreateControlWithAssets(child, submenuAssets, basePath, currentPath);
                if (control != null)
                {
                    targetControls.Add(control);
                }
            }
        }
        
        private VRCExpressionsMenu.Control CreateControlWithAssets(MergedMenuItem item, Dictionary<string, VRCExpressionsMenu> submenuAssets, 
            string basePath, string currentPath)
        {
            if (item.control != null)
            {
                // Clone original control
                var control = new VRCExpressionsMenu.Control();
                control.name = item.control.name;
                control.icon = item.control.icon;
                control.type = item.control.type;
                control.parameter = item.control.parameter;
                control.value = item.control.value;
                control.style = item.control.style;
                control.subParameters = item.control.subParameters?.ToArray();
                control.labels = item.control.labels?.ToArray();
                
                // Handle submenu with asset creation
                if (item.children != null && item.children.Count > 0 && item.children.Any(c => !IsModularAvatarItem(c)))
                {
                    string submenuPath = string.IsNullOrEmpty(currentPath) ? item.name : $"{currentPath}_{item.name}";
                    control.subMenu = CreateSubmenuAsset(item, submenuAssets, basePath, submenuPath);
                }
                else
                {
                    control.subMenu = item.control.subMenu;
                }
                
                return control;
            }
            else if (item.children != null && item.children.Count > 0)
            {
                // Create submenu control for items that only have children
                var nonMAChildren = item.children.Where(c => !IsModularAvatarItem(c)).ToList();
                if (nonMAChildren.Count > 0)
                {
                    var control = new VRCExpressionsMenu.Control();
                    control.name = item.name;
                    control.type = VRCExpressionsMenu.Control.ControlType.SubMenu;
                    
                    string submenuPath = string.IsNullOrEmpty(currentPath) ? item.name : $"{currentPath}_{item.name}";
                    control.subMenu = CreateSubmenuAsset(item, submenuAssets, basePath, submenuPath);
                    
                    return control;
                }
            }
            
            return null;
        }
        
        private VRCExpressionsMenu CreateSubmenuAsset(MergedMenuItem item, Dictionary<string, VRCExpressionsMenu> submenuAssets, 
            string basePath, string submenuPath)
        {
            // Check if we already created this submenu
            if (submenuAssets.ContainsKey(submenuPath))
            {
                return submenuAssets[submenuPath];
            }
            
            // Create the submenu
            var submenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            submenu.controls = new List<VRCExpressionsMenu.Control>();
            submenu.name = item.name;
            
            // Process children recursively
            ProcessChildrenWithAssets(item.children, submenu.controls, submenuAssets, basePath, submenuPath);
            
            // Save as asset in folder
            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{basePath}/{submenuPath}.asset");
            AssetDatabase.CreateAsset(submenu, assetPath);
            
            // Store in dictionary for future reference
            submenuAssets[submenuPath] = submenu;
            
            Debug.Log($"Created submenu asset: {assetPath}");
            
            return submenu;
        }
        
        private void UpdateModularAvatarInstallers(MergedMenuItem menuStructure, VRCExpressionsMenu newRootMenu)
        {
            if (!IsModularAvatarAvailable()) return;
            
            var menuInstallers = GetModularAvatarMenuInstallers();
            var processedInstallers = new HashSet<Component>();
            
            // Build a mapping of all reachable menus from the root
            var reachableMenus = BuildReachableMenuMap(newRootMenu);
            
            // Process MA items in the menu structure
            ProcessModularAvatarItemsRecursively(menuStructure, newRootMenu, menuInstallers, processedInstallers, reachableMenus);
            
            Debug.Log($"Updated {processedInstallers.Count} ModularAvatar Menu Installers");
        }
        
        private Dictionary<string, VRCExpressionsMenu> BuildReachableMenuMap(VRCExpressionsMenu rootMenu)
        {
            var reachableMenus = new Dictionary<string, VRCExpressionsMenu>();
            
            if (rootMenu != null)
            {
                reachableMenus[rootMenu.name] = rootMenu;
                BuildReachableMenuMapRecursive(rootMenu, reachableMenus);
            }
            
            return reachableMenus;
        }
        
        private void BuildReachableMenuMapRecursive(VRCExpressionsMenu menu, Dictionary<string, VRCExpressionsMenu> reachableMenus)
        {
            if (menu?.controls == null) return;
            
            foreach (var control in menu.controls)
            {
                if (control.subMenu != null && !reachableMenus.ContainsKey(control.subMenu.name))
                {
                    reachableMenus[control.subMenu.name] = control.subMenu;
                    BuildReachableMenuMapRecursive(control.subMenu, reachableMenus);
                }
            }
        }
        
        private void ProcessModularAvatarItemsRecursively(MergedMenuItem currentItem, VRCExpressionsMenu targetMenu, 
            List<Component> menuInstallers, HashSet<Component> processedInstallers, Dictionary<string, VRCExpressionsMenu> reachableMenus)
        {
            if (currentItem?.children == null) return;
            
            foreach (var child in currentItem.children)
            {
                if (IsModularAvatarItem(child))
                {
                    // Find the corresponding MA Menu Installer
                    var installer = FindCorrespondingInstaller(child, menuInstallers);
                    if (installer != null && !processedInstallers.Contains(installer))
                    {
                        // Find the best reachable menu for this installer
                        var bestTargetMenu = FindBestReachableMenu(child, targetMenu, reachableMenus);
                        UpdateInstallerTarget(installer, bestTargetMenu);
                        processedInstallers.Add(installer);
                        Debug.Log($"Updated MA Menu Installer '{installer.name}' to target '{bestTargetMenu.name}'");
                    }
                }
                else if (child.children != null && child.children.Count > 0)
                {
                    // For non-MA items with children, find the corresponding submenu
                    var correspondingControl = FindControlInMenu(targetMenu, child.name);
                    if (correspondingControl?.subMenu != null)
                    {
                        ProcessModularAvatarItemsRecursively(child, correspondingControl.subMenu, menuInstallers, processedInstallers, reachableMenus);
                    }
                }
            }
        }
        
        private VRCExpressionsMenu FindBestReachableMenu(MergedMenuItem maItem, VRCExpressionsMenu defaultMenu, Dictionary<string, VRCExpressionsMenu> reachableMenus)
        {
            // Try to find a menu with the same name first
            if (reachableMenus.ContainsKey(maItem.name))
            {
                return reachableMenus[maItem.name];
            }
            
            // If the default menu is reachable, use it
            if (reachableMenus.ContainsValue(defaultMenu))
            {
                return defaultMenu;
            }
            
            // Fall back to the root menu (first in reachableMenus)
            return reachableMenus.Values.FirstOrDefault() ?? defaultMenu;
        }
        
        private Component FindCorrespondingInstaller(MergedMenuItem maItem, List<Component> menuInstallers)
        {
            if (maItem.sourceComponent != null)
            {
                // If the item has a direct reference to the source component
                return menuInstallers.FirstOrDefault(installer => installer == maItem.sourceComponent);
            }
            
            // Try to find by name matching
            return menuInstallers.FirstOrDefault(installer => installer.name == maItem.name || 
                installer.gameObject.name == maItem.name);
        }
        
        private VRCExpressionsMenu.Control FindControlInMenu(VRCExpressionsMenu menu, string controlName)
        {
            if (menu?.controls == null) return null;
            
            return menu.controls.FirstOrDefault(control => control.name == controlName);
        }
        
        private void UpdateInstallerTarget(Component installer, VRCExpressionsMenu targetMenu)
        {
            try
            {
                var installTargetField = installer.GetType().GetField("installTargetMenu");
                if (installTargetField != null)
                {
                    installTargetField.SetValue(installer, targetMenu);
                    EditorUtility.SetDirty(installer);
                }
                else
                {
                    Debug.LogWarning($"Could not find installTargetMenu field on {installer.name}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error updating installer target for {installer.name}: {e.Message}");
            }
        }
    }
}