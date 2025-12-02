using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace VRCExpressionMenuOrganizer
{
    // Marker components are defined in a runtime-compiled file (ExprMenuVisualizerMarkers.cs)
    // so Unity can reliably deserialize them across domain reloads / restarts.

    public class VRCExpressionMenuOrganizerWindow : EditorWindow
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
        private bool showStats = false;
        private bool useEnglish = false; // デフォルトは日本語
        private bool useGridView = true; // デフォルトはグリッド表示
        private bool editMode = false; // グローバル編集モード
        private VRCAvatarDescriptor previousAvatar;
        private MergedMenuItem cachedMenuStructure;
        private bool menuStructureDirty = true;
        
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

        // Modular Avatar mapping state
        private readonly Dictionary<VRCExpressionsMenu.Control, Component> maControlMap =
            new Dictionary<VRCExpressionsMenu.Control, Component>(ReferenceEqualityComparer<VRCExpressionsMenu.Control>.Instance);

        private readonly Dictionary<Component, VRCExpressionsMenu.Control> maComponentToControl =
            new Dictionary<Component, VRCExpressionsMenu.Control>(ReferenceEqualityComparer<Component>.Instance);

        private readonly Dictionary<string, Queue<Component>> maMenuItemSignatureMap =
            new Dictionary<string, Queue<Component>>(StringComparer.Ordinal);

        // Color definitions for UI elements
        private static readonly Color COLOR_DELETE_AREA = new Color(0.992f, 0.380f, 0.396f, 1f);     // #fd6165
        private static readonly Color COLOR_BACK_NAVIGATION = new Color(0f, 0.741f, 1f, 1f);         // #00bdff
        private static readonly Color COLOR_SUBMENU = new Color(0f, 0.741f, 1f, 1f);                // #00bdff (same as back navigation)
        private static readonly Color COLOR_MA_ITEM = new Color(0.498f, 0.976f, 0.757f, 1f);        // #7ff9c1
        private static readonly Color COLOR_EMPTY_SUBMENU = new Color(0.976f, 0.973f, 0.443f, 1f);  // #f9f871
        private static readonly Color COLOR_EXCLUDED = new Color(0.922f, 0.576f, 0.157f, 1f);       // #eb9929
        // Insertion line color for drag-and-drop (green, semi-transparent)
        private static readonly Color COLOR_INSERT_LINE = new Color(0f, 1f, 0f, 0.5f);
        // Dark blue used to outline a submenu when hovering to drop into it (#0072BC)
        private static readonly Color COLOR_SUBMENU_HOVER_OUTLINE = new Color(0f, 114f/255f, 188f/255f, 1f);

        // Static language setting for cross-window localization
        private static bool staticUseEnglish = false;

        private readonly HashSet<string> activeConversionExcludedPaths = new HashSet<string>(StringComparer.Ordinal);
        private VRCExpressionsMenu activeConversionRootMenu;
        private readonly HashSet<string> configuredExclusionPaths = new HashSet<string>(StringComparer.Ordinal);
        private bool awaitingExclusionSelection;
        private ExclusionSelectionWindow activeExclusionSelectionWindow;

        private struct ParameterState
        {
            public bool saved;
            public bool synced;
        }

        private readonly Dictionary<string, ParameterState> expressionParameterStates =
            new Dictionary<string, ParameterState>(StringComparer.Ordinal);

        // Temporary lookup used during generation to reuse existing generated GameObjects
        // keyed by their ExprMenuVisualizerGeneratedMetadata.fullPath. Populated at the
        // start of GenerateModularAvatarMenuHierarchy and cleared at the end.
        private Dictionary<string, GameObject> tempGeneratedLookup = null;

        internal void RegisterModularAvatarMenuSignature(Component component, VRCExpressionsMenu.Control control)
        {
            if (component == null || control == null) return;

            var signature = BuildControlSignature(control);
            if (string.IsNullOrEmpty(signature)) return;

            if (!maMenuItemSignatureMap.TryGetValue(signature, out var queue))
            {
                queue = new Queue<Component>();
                maMenuItemSignatureMap[signature] = queue;
            }

            queue.Enqueue(component);
        }

        internal Component ResolveModularAvatarMenuComponent(VRCExpressionsMenu.Control control)
        {
            if (control == null) return null;

            var signature = BuildControlSignature(control);
            if (string.IsNullOrEmpty(signature)) return null;

            if (maMenuItemSignatureMap.TryGetValue(signature, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }

            return null;
        }

        internal void ConsumeModularAvatarMenuSignature(Component component, VRCExpressionsMenu.Control control)
        {
            if (component == null || control == null) return;

            var signature = BuildControlSignature(control);
            if (string.IsNullOrEmpty(signature)) return;

            if (!maMenuItemSignatureMap.TryGetValue(signature, out var queue) || queue.Count == 0)
            {
                return;
            }

            if (ReferenceEquals(queue.Peek(), component))
            {
                queue.Dequeue();
                return;
            }

            var tempQueue = new Queue<Component>(queue.Count);
            bool removed = false;
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                if (!removed && ReferenceEquals(item, component))
                {
                    removed = true;
                    continue;
                }

                tempQueue.Enqueue(item);
            }

            while (tempQueue.Count > 0)
            {
                queue.Enqueue(tempQueue.Dequeue());
            }
        }

        private static string BuildControlSignature(VRCExpressionsMenu.Control control)
        {
            if (control == null) return string.Empty;

            var builder = new StringBuilder();
            builder.Append(control.type);
            builder.Append('|');
            builder.Append(control.name ?? string.Empty);
            builder.Append('|');
            builder.Append(control.parameter?.name ?? string.Empty);
            builder.Append('|');
            builder.Append(control.value.ToString("G", CultureInfo.InvariantCulture));
            builder.Append('|');

            if (control.subParameters != null)
            {
                for (int i = 0; i < control.subParameters.Length; i++)
                {
                    var subParam = control.subParameters[i];
                    builder.Append(subParam != null ? subParam.name ?? string.Empty : string.Empty);
                    builder.Append('|');
                }
            }
            else
            {
                builder.Append('|');
            }

            if (control.labels != null)
            {
                for (int i = 0; i < control.labels.Length; i++)
                {
                    var label = control.labels[i];
                    builder.Append(label.name ?? string.Empty);
                    builder.Append('|');
                }
            }
            else
            {
                builder.Append('|');
            }

            builder.Append(control.style);
            builder.Append('|');
            builder.Append(control.subMenu != null ? control.subMenu.name ?? string.Empty : string.Empty);

            return builder.ToString();
        }

        // Normalize menu fullPath values so the leading root segment is consistent
        // across merged/menu-display roots and the generated scene root name.
        private static string CanonicalizeMenuFullPath(string originalPath)
        {
            if (string.IsNullOrEmpty(originalPath)) return originalPath;
            // Split into root and remainder
            var parts = originalPath.Split(new[] {'/'}, 2);
            if (parts.Length == 0) return originalPath;
            if (string.Equals(parts[0], GeneratedMenuRootName, StringComparison.Ordinal))
                return originalPath; // already canonical
            if (parts.Length == 1)
                return GeneratedMenuRootName + "/" + parts[0];
            return GeneratedMenuRootName + "/" + parts[1];
        }

        private readonly List<Component> modularAvatarInstallTargets = new List<Component>();

        // Editing context for Modular Avatar items
        private Component editingSourceComponent;

        // Hierarchy change state
        private bool dragTargetIsSubmenu = false;
        private bool dragTargetIsParentLevel = false;
        private bool dragTargetIsDeleteArea = false;
        private MergedMenuItem hoveredSubmenu = null;
        private Rect backNavigationDropArea;
        private Rect deleteDropArea;

        // Temporary storage for edited menu structure
        private MergedMenuItem editedMenuStructure = null;

        // --- Edit-mode immediate scene edits ---------------------------------
        // Starting from this change, edit mode operations (move/create/delete) are
        // reflected immediately in the Hierarchy under the "メニュー項目" root.
        // To allow rolling back a series of edit-mode user actions, we capture
        // an Undo group snapshot when edit mode is enabled and revert to it when
        // the Reset button is used.


        // Edit-mode Undo snapshot group id -- used to revert scene hierarchy changes when Reset is pressed
        private int editModeUndoGroup = -1;

        // MA item movement tracking
        private const string GeneratedMenuRootName = "メニュー項目";

        // MA Installer menu tracking - for detecting baked content
        private readonly HashSet<VRCExpressionsMenu> maInstallerMenus =
            new HashSet<VRCExpressionsMenu>(ReferenceEqualityComparer<VRCExpressionsMenu>.Instance);

        // Debug logging

        // ModularAvatar support - using reflection for safe type handling
        private static Type modularAvatarMenuInstallerType;
        private static Type modularAvatarObjectToggleType;
        private static Type modularAvatarMenuItemType;
        private static Type modularAvatarMenuInstallTargetType;
        private static Type modularAvatarMenuGroupType;
        private static bool checkedForModularAvatar = false;

        private static readonly Dictionary<char, char> forbiddenCharReplacements = new Dictionary<char, char>
        {
            { '"', '”' },
            { '<', '＜' },
            { '>', '＞' },
            { ':', '：' },
            { '/', '／' },
            { '\\', '＼' },
            { '|', '｜' },
            { '?', '？' },
            { '*', '＊' }
        };

        private static readonly HashSet<char> invalidFileNameChars = new HashSet<char>(Path.GetInvalidFileNameChars());
        
        private static Type GetModularAvatarMenuInstallerType()
        {
            if (!checkedForModularAvatar)
            {
                checkedForModularAvatar = true;
                modularAvatarMenuInstallerType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller, nadena.dev.modular-avatar.core");
                modularAvatarObjectToggleType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarObjectToggle, nadena.dev.modular-avatar.core");
                modularAvatarMenuItemType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuItem, nadena.dev.modular-avatar.core");
                modularAvatarMenuInstallTargetType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuInstallTarget, nadena.dev.modular-avatar.core");
                modularAvatarMenuGroupType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuGroup, nadena.dev.modular-avatar.core");
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

        private static Type GetModularAvatarMenuInstallTargetType()
        {
            GetModularAvatarMenuInstallerType();
            return modularAvatarMenuInstallTargetType;
        }

        private static Type GetModularAvatarMenuGroupType()
        {
            GetModularAvatarMenuInstallerType();
            return modularAvatarMenuGroupType;
        }
        
        private static bool IsModularAvatarAvailable()
        {
            return GetModularAvatarMenuInstallerType() != null;
        }

        /// <summary>
        /// Menu Install Target を持つ GameObject が、除外マーカー付きアイテムかを判定
        /// </summary>
        private bool IsExcludedItemByInstaller(GameObject gameObject)
        {
            if (gameObject == null)
                return false;

            var installTargetType = GetModularAvatarMenuInstallTargetType();
            if (installTargetType == null)
                return false;

            Component menuInstallTarget = null;
            try
            {
                menuInstallTarget = gameObject.GetComponent(installTargetType);
            }
            catch { }

            if (menuInstallTarget == null)
                return false;

            var installerField = installTargetType.GetField("installer");
            if (installerField == null)
                return false;

            var installer = installerField.GetValue(menuInstallTarget) as Component;
            if (installer == null)
                return false;

            // Consider it an excluded item if the installer references a generated/excluded marker
            try
            {
                if (installer.GetComponent<ExprMenuVisualizerExcluded>() != null) return true;
                if (installer.GetComponent<ExprMenuVisualizerGenerated>() != null) return true;
                if (installer.GetComponent<ExprMenuVisualizerGeneratedGuid>() != null) return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// selectedAvatar 配下から「メニュー項目」GameObject を検索
        /// </summary>
        private Transform FindMenuItemRoot(VRCAvatarDescriptor avatar)
        {
            if (avatar == null)
                return null;

            // Prefer an explicit marker component if present (user-placed root)
            try
            {
                var existing = avatar.GetComponentInChildren<ExprMenuVisualizerGeneratedRoot>(true);
                if (existing != null)
                    return existing.transform;
            }
            catch { }

            // Fallback to the legacy name-based search
            for (int i = 0; i < avatar.transform.childCount; i++)
            {
                var child = avatar.transform.GetChild(i);
                if (child != null && child.gameObject.name == GeneratedMenuRootName)
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>
        /// Menu Install Target から installer GameObject を取得
        /// </summary>
        private GameObject GetInstallerGameObjectFromMenuInstallTarget(GameObject gameObject)
        {
            if (gameObject == null)
                return null;

            var installTargetType = GetModularAvatarMenuInstallTargetType();
            Component menuInstallTarget = gameObject.GetComponent(installTargetType);
            if (menuInstallTarget == null)
                return null;

            var installerField = installTargetType.GetField("installer");
            if (installerField == null)
                return null;

            var installer = installerField.GetValue(menuInstallTarget) as Component;
            return installer?.gameObject;
        }

        /// <summary>
        /// Menu Install Target (gameObject) から Control 情報を取得する
        /// </summary>
        private VRCExpressionsMenu.Control GetControlInfoFromInstaller(GameObject gameObject)
        {
            if (gameObject == null) return null;

            var installTargetType = GetModularAvatarMenuInstallTargetType();
            if (installTargetType == null) return null;

            Component menuInstallTarget = null;
            try { menuInstallTarget = gameObject.GetComponent(installTargetType); } catch { }
            if (menuInstallTarget == null) return null;

            var installerField = installTargetType.GetField("installer");
            if (installerField == null) return null;

            var installer = installerField.GetValue(menuInstallTarget) as Component;
            if (installer == null) return null;

            // Try to read a Control field from the installer itself
            try
            {
                var installerType = installer.GetType();
                var controlField = installerType.GetField("Control") ?? installerType.GetField("control");
                if (controlField != null)
                {
                    return controlField.GetValue(installer) as VRCExpressionsMenu.Control;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// GameObject から表示用情報を構築
        /// </summary>
        private MenuItemDisplayInfo GetMenuItemDisplayInfo(GameObject gameObject)
        {
            var info = new MenuItemDisplayInfo();
            info.GameObjectNode = gameObject;

            // 除外項目判定
            bool isExcludedItem = IsExcludedItemByInstaller(gameObject);
            info.IsExcludedItem = isExcludedItem;

            if (isExcludedItem)
            {
                // 除外項目の場合：Menu Install Target → installer から Control 取得
                var control = GetControlInfoFromInstaller(gameObject);
                info.Control = control;
                info.DisplayName = control?.name ?? gameObject.name;
                info.Icon = control?.icon;
            }
            else
            {
                // 通常項目の場合：GameObject に付いた ModularAvatarMenuItem から Control 取得
                var menuItemType = GetModularAvatarMenuItemType();
                var menuItemComponent = gameObject.GetComponent(menuItemType);
                if (menuItemComponent != null)
                {
                    var controlField = menuItemType.GetField("Control");
                    if (controlField != null)
                    {
                        var control = controlField.GetValue(menuItemComponent) as VRCExpressionsMenu.Control;
                        info.Control = control;
                        info.DisplayName = control?.name ?? gameObject.name;
                        info.Icon = control?.icon;
                    }
                    else
                    {
                        // Control が無ければ GameObject 名を使用
                        info.DisplayName = gameObject.name;
                    }
                }
                else
                {
                    // Menu Item コンポーネントがなければ GameObject 名を使用
                    info.DisplayName = gameObject.name;
                }
            }

            return info;
        }

        /// <summary>
        /// 編集モード時に、「メニュー項目」直下のすべての GameObject を取得（Sibling 順）
        /// </summary>
        private List<GameObject> GetMenuItemsInEditMode()
        {
            var menuItems = new List<GameObject>();

            if (selectedAvatar == null)
                return menuItems;

            // 「メニュー項目」GameObject を検索
            Transform menuItemRoot = FindMenuItemRoot(selectedAvatar);
            if (menuItemRoot == null)
                return menuItems;

            // 直下のすべての GameObject を Sibling 順で追加
            for (int i = 0; i < menuItemRoot.childCount; i++)
            {
                var child = menuItemRoot.GetChild(i).gameObject;
                menuItems.Add(child);
            }

            return menuItems;
        }

        /// <summary>
        /// 非編集モード時：「メニュー項目」内の構造に従って、
        /// 除外マーカー付き元アイテムを取得（順序を保持）
        /// </summary>
        private List<GameObject> GetExcludedItemsInMenuItemOrder()
        {
            var excludedItems = new List<GameObject>();

            if (selectedAvatar == null)
                return excludedItems;

            // 「メニュー項目」GameObject を検索
            Transform menuItemRoot = FindMenuItemRoot(selectedAvatar);
            if (menuItemRoot == null)
                return excludedItems;

            // 「メニュー項目」直下のすべての GameObject をループ
            for (int i = 0; i < menuItemRoot.childCount; i++)
            {
                var menuItemChild = menuItemRoot.GetChild(i).gameObject;

                // このアイテムが Menu Install Target を持つか確認
                bool hasMenuInstallTarget =
                    menuItemChild.GetComponent(GetModularAvatarMenuInstallTargetType()) != null;

                if (hasMenuInstallTarget)
                {
                    // Menu Install Target の installer フィールドから元のアイテムを取得
                    var installerGO = GetInstallerGameObjectFromMenuInstallTarget(menuItemChild);

                    if (installerGO != null)
                    {
                        // Accept either the original generated marker or the new GUID marker
                        bool hasGen = installerGO.GetComponent<ExprMenuVisualizerGenerated>() != null;
                        bool hasGuid = installerGO.GetComponent<ExprMenuVisualizerGeneratedGuid>() != null;
                        if (hasGen || hasGuid)
                        {
                            // 除外マーカー付き元アイテムを追加
                            excludedItems.Add(installerGO);
                        }
                    }
                }
            }

            return excludedItems;
        }

        private string SanitizeForAssetPath(string source, string fallback = "Menu")
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return fallback;
            }

            var builder = new StringBuilder(source.Length);
            foreach (var character in source.Trim())
            {
                if (forbiddenCharReplacements.TryGetValue(character, out var replacement))
                {
                    builder.Append(replacement);
                }
                else if (invalidFileNameChars.Contains(character) || char.IsControl(character))
                {
                    // Remove characters that still violate Windows file name rules.
                    continue;
                }
                else
                {
                    builder.Append(character);
                }
            }

            var sanitized = builder.ToString();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = fallback;
            }

            return sanitized;
        }

        private string CombineSanitizedPath(string currentPath, string rawName, string fallback = "Menu")
        {
            string segment = SanitizeForAssetPath(rawName, fallback);
            return string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}_{segment}";
        }

        private void AssignMenuPaths(MergedMenuItem root)
        {
            if (root == null) return;
            AssignMenuPathsRecursive(root, null);
        }

        private void AssignMenuPathsRecursive(MergedMenuItem item, string parentPath)
        {
            if (item == null) return;

            item.fullPath = string.IsNullOrEmpty(parentPath) ? item.name : $"{parentPath}/{item.name}";

            if (item.children == null) return;
            foreach (var child in item.children)
            {
                AssignMenuPathsRecursive(child, item.fullPath);
            }
        }

        // Find MA menu components in avatar outside the MenuItem root and include them in the merged menu
        private void IncludeOrphanMAItems(MergedMenuItem rootItem)
        {
            if (rootItem == null || selectedAvatar == null) return;

            var menuItemType = GetModularAvatarMenuItemType();
            var installTargetType = GetModularAvatarMenuInstallTargetType();
            var menuItemRoot = FindMenuItemRoot(selectedAvatar);

            // Collect existing sourceComponents under the menu root to prevent duplicates
            var existing = new HashSet<Component>(ReferenceEqualityComparer<Component>.Instance);
            void collectExisting(MergedMenuItem node)
            {
                if (node == null) return;
                if (node.sourceComponent != null) existing.Add(node.sourceComponent);
                if (node.children == null) return;
                foreach (var c in node.children) collectExisting(c);
            }
            collectExisting(rootItem);

            var avatarTransforms = selectedAvatar.transform.GetComponentsInChildren<Transform>(true);
            foreach (var t in avatarTransforms)
            {
                if (t == null || t.gameObject == null) continue;
                var go = t.gameObject;

                // Skip objects with Included marker
                if (go.GetComponent<ExprMenuVisualizerIncluded>() != null) continue;

                // Skip objects already under the MenuItem root, EXCEPT for MA Menu Install Target
                // MA Menu Install Targetは除外項目として生成されたものなので、メニュー構造に含める必要がある
                bool isUnderMenuItemRoot = menuItemRoot != null && go.transform.IsChildOf(menuItemRoot);
                if (isUnderMenuItemRoot)
                {
                    // MA Menu Install Targetを持つ場合のみ処理を続行
                    bool hasInstallTarget = installTargetType != null && go.GetComponent(installTargetType) != null;
                    if (!hasInstallTarget) continue;
                }

                Component sourceComponent = null;
                string source = null;
                VRCExpressionsMenu.Control control = null;

                if (installTargetType != null)
                {
                    var menuInstallTarget = go.GetComponent(installTargetType);
                    if (menuInstallTarget != null)
                    {
                        control = GetControlInfoFromInstaller(go);
                        sourceComponent = menuInstallTarget;
                        source = "MA_InstallTarget";
                    }
                }

                if (control == null && menuItemType != null)
                {
                    var menuItemComponent = go.GetComponent(menuItemType);
                    if (menuItemComponent != null)
                    {
                        control = ExtractControlFromSourceComponent(menuItemComponent);
                        sourceComponent = menuItemComponent;
                        source = "MA_MenuItem";
                    }
                }

                if (control == null || sourceComponent == null) continue;
                if (existing.Contains(sourceComponent)) continue;

                var orphanItem = new MergedMenuItem
                {
                    name = string.IsNullOrEmpty(control.name) ? GetLocalizedText("無名コントロール", "Unnamed Control") : control.name,
                    source = source,
                    control = control,
                    subMenu = control.subMenu,
                    sourceComponent = sourceComponent,
                    children = new List<MergedMenuItem>(),
                    originalIndex = rootItem.children.Count,
                    fullPath = string.Empty
                };
                rootItem.children.Add(orphanItem);
                existing.Add(sourceComponent);
                LogDetail($"IncludeOrphanMAItems: Added orphan MA item '{orphanItem.name}' from GameObject '{go.name}'");
            }
        }

        private void UpdateReadOnlyFlags(MergedMenuItem root)
        {
            bool generatedRootExists = HasGeneratedMenuRoot();
            UpdateReadOnlyFlagsRecursive(root, generatedRootExists);
        }

        private void UpdateReadOnlyFlagsRecursive(MergedMenuItem item, bool generatedRootExists)
        {
            if (item == null) return;

            if (item.sourceComponent != null)
            {
                // 除外マーカーが付いている場合は常に読み取り専用
                if (item.sourceComponent.GetComponent<ExprMenuVisualizerExcluded>() != null)
                {
                    item.isReadOnly = true;
                }
                // 生成ルートが存在し、生成されたコンポーネントでない場合は読み取り専用
                else if (generatedRootExists && !IsGeneratedMenuComponent(item.sourceComponent))
                {
                    item.isReadOnly = true;
                }
                else
                {
                    item.isReadOnly = false;
                }
            }
            else
            {
                item.isReadOnly = false;
            }

            if (item.children == null) return;
            foreach (var child in item.children)
            {
                UpdateReadOnlyFlagsRecursive(child, generatedRootExists);
            }
        }

        private bool HasGeneratedMenuRoot()
        {
            if (selectedAvatar == null) return false;
            return selectedAvatar.GetComponentInChildren<ExprMenuVisualizerGeneratedRoot>(true) != null;
        }

        private bool IsGeneratedMenuComponent(Component component)
        {
            if (component == null) return false;
            // 生成マーカーコンポーネントがあるかチェック (従来のマーカー or GUIDマーカー)
            return component.GetComponent<ExprMenuVisualizerGenerated>() != null
                   || component.GetComponent<ExprMenuVisualizerGeneratedGuid>() != null;
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

        private void RefreshExpressionParameterStates()
        {
            expressionParameterStates.Clear();

            var parameters = selectedAvatar?.expressionParameters;
            if (parameters?.parameters == null)
            {
                return;
            }

            foreach (var parameter in parameters.parameters)
            {
                if (parameter == null || string.IsNullOrWhiteSpace(parameter.name))
                {
                    continue;
                }

                expressionParameterStates[parameter.name] = new ParameterState
                {
                    saved = parameter.saved,
                    synced = parameter.networkSynced
                };
            }
        }

        private ParameterState GetParameterState(string parameterName)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                return new ParameterState { saved = true, synced = true };
            }

            if (expressionParameterStates.TryGetValue(parameterName, out var state))
            {
                return state;
            }

            return new ParameterState { saved = true, synced = true };
        }

        private ParameterState DetermineControlParameterState(VRCExpressionsMenu.Control control)
        {
            if (control == null)
            {
                return new ParameterState { saved = true, synced = true };
            }

            bool anyParameter = false;
            bool saved = false;
            bool synced = false;

            void IncludeState(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                anyParameter = true;
                var state = GetParameterState(name);
                saved |= state.saved;
                synced |= state.synced;
            }

            IncludeState(control.parameter?.name);

            if (control.subParameters != null)
            {
                foreach (var sub in control.subParameters)
                {
                    IncludeState(sub?.name);
                }
            }

            if (!anyParameter)
            {
                saved = true;
                synced = true;
            }

            return new ParameterState { saved = saved, synced = synced };
        }

        private void ForceBooleanMember(Component component, string fieldName, string propertyName, bool value)
        {
            if (component == null) return;

            var type = component.GetType();
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(bool))
            {
                field.SetValue(component, value);
                return;
            }

            var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == typeof(bool) && property.CanWrite)
            {
                property.SetValue(component, value, null);
                return;
            }

            try
            {
                var serializedObject = new SerializedObject(component);
                var serializedProperty = serializedObject.FindProperty(fieldName) ?? serializedObject.FindProperty(propertyName);
                if (serializedProperty != null && serializedProperty.propertyType == SerializedPropertyType.Boolean)
                {
                    serializedProperty.boolValue = value;
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            catch
            {
                // Ignore serialization errors for unknown components
            }
        }
        
        // ローカライゼーション用ヘルパーメソッド
        private string GetLocalizedText(string japanese, string english)
        {
            staticUseEnglish = useEnglish;  // 言語設定を同期
            return GetLocalizedTextStatic(japanese, english);
        }

        // 静的ローカライゼーション用メソッド（他のウィンドウから呼び出し可能）
        public static string GetLocalizedTextStatic(string japanese, string english)
        {
            return staticUseEnglish ? english : japanese;
        }
        
        [MenuItem("メニュー整理ツール/最新版-beta")]
        public static void ShowWindow()
        {
            GetWindow<VRCExpressionMenuOrganizerWindow>("メニュー整理ツール");
        }

        private void OnGUI()
        {
            InitializeStyles();
            
            EditorGUILayout.Space();

            // Avatar Selection
            selectedAvatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                new GUIContent(GetLocalizedText("アバター", "Avatar"), GetLocalizedText("メニューを確認，編集したいアバターを選択してください。", "Select the avatar you want to check and edit the menu for.")),
                selectedAvatar,
                typeof(VRCAvatarDescriptor),
                true
            );

            if (selectedAvatar != previousAvatar)
            {
                OnSelectedAvatarChanged(previousAvatar, selectedAvatar);
                previousAvatar = selectedAvatar;
            }

            if (selectedAvatar == null)
            {
                EditorGUILayout.HelpBox(GetLocalizedText("Hierarchyからアバターをドラッグドロップしてください。", "Please drag an avatar from the Hierarchy."), MessageType.Info);
                EditorGUILayout.HelpBox(GetLocalizedText("アバターのメニュー構造を複製して作りなおします。必ずバックアップを取ってから実行してください。", "This will duplicate and rebuild the avatar's menu structure. Make sure to back up before proceeding."), MessageType.Warning);
                return;
            }
            
            if (awaitingExclusionSelection)
            {
                EditorGUILayout.HelpBox(GetLocalizedText(
                    "除外するメニュー項目の選択を待機しています。別ウィンドウで項目を選択してください。",
                    "Waiting for exclusion selection. Complete the selection in the separate window."), MessageType.Info);
                EditorGUILayout.LabelField(GetLocalizedText("選択待ち", "Awaiting selection"), EditorStyles.boldLabel);
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

            useEnglish = GUILayout.Toggle(useEnglish, new GUIContent(GetLocalizedText("English", "English"), GetLocalizedText("英語で表示", "Display in English")), GUILayout.Width(80));
            GUILayout.EndHorizontal();

            // Search bar
            // 検索バー削除

            // Statistics - Foldout化
            showStats = EditorGUILayout.Foldout(showStats, GetLocalizedText("統計情報", "Statistics"), true);
            if (showStats)
            {
                EditorGUI.indentLevel++;
                DrawStatistics();
                EditorGUI.indentLevel--;
            }

            // Edit Mode Toggle
            EditorGUILayout.Space();

            // Use different background color and text color when edit mode is enabled
            GUIStyle bgStyle;
            GUIStyle toggleStyle;
            if (editMode)
            {
                bgStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    normal = { background = MakeTex(2, 2, new Color(0.5f, 0.5f, 0.5f, 1f)) },
                    fixedHeight = 36
                };
                toggleStyle = new GUIStyle(EditorStyles.toggle)
                {
                    normal = { textColor = Color.white },
                    onNormal = { textColor = Color.white },
                    hover = { textColor = Color.white },
                    onHover = { textColor = Color.white },
                    active = { textColor = Color.white },
                    onActive = { textColor = Color.white },
                    focused = { textColor = Color.white },
                    onFocused = { textColor = Color.white }
                };
            }
            else
            {
                bgStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    fixedHeight = 36
                };
                toggleStyle = EditorStyles.toggle;
            }

            EditorGUILayout.BeginVertical(bgStyle);
            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();

            // Use custom toggle style for text color control
            string labelText = editMode
                ? GetLocalizedText("編集モードが有効です。項目をつかんで移動できます。", "Edit mode is active. You can drag and move items.")
                : GetLocalizedText("編集モード", "Edit Mode");
            string tooltipText = GetLocalizedText("メニュー構造を編集します", "Edit menu structure");

            bool newEditMode = EditorGUILayout.ToggleLeft(new GUIContent(labelText, tooltipText), editMode, toggleStyle);
            if (newEditMode != editMode)
            {
                HandleEditModeToggled(newEditMode);
            }
            editMode = newEditMode;

            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();

            // Edit Mode Buttons - Full width with larger size
            if (editMode)
            {
                EditorGUILayout.Space();
                EditorGUILayout.BeginVertical();

                // Selection info
                if (selectedItems.Count > 0)
                {
                    EditorGUILayout.LabelField(GetLocalizedText($"{selectedItems.Count}個選択中", $"{selectedItems.Count} selected"));
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space();

            // Legend
            DrawLegend();

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
            // Debug.Log("Refreshed edited menu structure from avatar");
            }
            
            // Clear edit mode tracking data
            selectedItems.Clear();
            draggedItem = null;
            isDragging = false;
            itemRects.Clear();
            currentMenuItems.Clear();
            dragTargetParent = null;
            dragTargetIndex = -1;
            editingSourceComponent = null;

            // Clear MA item tracking data
            maMenuItemSignatureMap.Clear();

            MarkMenuStructureDirty();

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

            if (IsModularAvatarAvailable() && stats.maInstallersCount > 0)
            {
                EditorGUILayout.LabelField(GetLocalizedText("ModularAvatarコンポーネント:", "ModularAvatar Components:"), EditorStyles.miniLabel);
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(GetLocalizedText($"インストーラー: {stats.maInstallersCount}", $"Installers: {stats.maInstallersCount}"), parameterStyle, GUILayout.Width(120));
                GUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawLegend()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("🎨", EditorStyles.miniBoldLabel);

            // 1行目：サブメニュー + Toggle項目
            GUILayout.BeginHorizontal();

            // Submenu color
            DrawColorLegendItem(COLOR_SUBMENU, GetLocalizedText("サブメニュー", "Submenu"));

            // Toggle items color
            DrawColorLegendItem(COLOR_MA_ITEM, GetLocalizedText("Toggle項目", "Toggle Controls"));

            GUILayout.EndHorizontal();

            // 2行目：空のサブメニュー + 除外項目
            GUILayout.BeginHorizontal();

            // Empty submenu color
            DrawColorLegendItem(COLOR_EMPTY_SUBMENU, GetLocalizedText("空のサブメニュー", "Empty Submenu"));

            // Excluded items color
            DrawColorLegendItem(COLOR_EXCLUDED, GetLocalizedText("除外項目", "Excluded Items"));

            GUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawColorLegendItem(Color color, string label)
        {
            GUILayout.BeginHorizontal();

            // Draw a small colored box
            Rect colorBox = GUILayoutUtility.GetRect(15, 15, GUILayout.Width(15));
            EditorGUI.DrawRect(colorBox, color);

            // Draw border for visibility
            DrawBorder(colorBox, Color.gray, 1f);

            // Draw label next to color box
            EditorGUILayout.LabelField(label, GUILayout.Width(110));

            GUILayout.EndHorizontal();
        }

        private void DrawEditActionsPanel()
        {
            var currentMenu = GetCurrentEditedMenu();
            bool readOnlyContext = currentMenu?.isReadOnly == true;
            GUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(readOnlyContext))
            {
                // Submenu creation button - 50% width with larger size
                if (GUILayout.Button(new GUIContent(GetLocalizedText("📁 サブメニュー生成", "📁 Create Submenu"),
                    GetLocalizedText("現在の階層に新しい空のサブメニューを追加します", "Add a new empty submenu to current level")),
                    GUILayout.Height(36)))
                {
                    CreateNewSubmenu();
                }
            }

            // Delete area - use button instead of label for proper event handling
            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = dragTargetIsDeleteArea ? COLOR_DELETE_AREA : new Color(COLOR_DELETE_AREA.r, COLOR_DELETE_AREA.g, COLOR_DELETE_AREA.b, 0.5f);

            string deleteText = GetLocalizedText("🗑️ 削除エリア", "🗑️ Delete Zone");
            if (isDragging)
            {
                deleteText += GetLocalizedText(" (ここにドロップ)", " (Drop Here)");
            }

            // Create delete button and get its rect immediately - 50% width with 36px height
            Rect deleteRect = GUILayoutUtility.GetRect(new GUIContent(deleteText), EditorStyles.helpBox,
                GUILayout.Height(36));

            // Draw the delete area
            GUI.Label(deleteRect, deleteText, EditorStyles.helpBox);

            // Always update the delete drop area during layout events
            deleteDropArea = deleteRect;

            GUI.backgroundColor = originalColor;

            GUILayout.EndHorizontal();

            if (readOnlyContext)
            {
                EditorGUILayout.HelpBox(GetLocalizedText(
                    "この階層は参照専用です。変換から除外されているため編集できません。",
                    "This level is read-only because it was excluded from conversion."), MessageType.Info);
            }
        }
        
        private void CreateNewSubmenu()
        {
            var currentMenu = GetCurrentEditedMenu();
            if (IsItemReadOnly(currentMenu))
            {
                ShowReadOnlyItemWarning();
                return;
            }
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // Get current menu context
            currentMenu = GetCurrentEditedMenu();
            if (currentMenu == null) return;
            
            // Ensure children list exists
            if (currentMenu.children == null)
                currentMenu.children = new List<MergedMenuItem>();
            
            // Show name input dialog
            string submenuName = ShowSubmenuNameInputDialog();
            if (string.IsNullOrEmpty(submenuName))
            {
                // User cancelled or entered empty name
                return;
            }
            
            // Generate unique name if needed
            string uniqueName = GenerateUniqueSubmenuName(currentMenu, submenuName);
            
            // Instead of creating a new VRCExpressionsMenu asset, create an
            // in-scene MA-style submenu represented by a child GameObject under
            // the "メニュー項目" root. This avoids creating new ExpressionMenu
            // assets and keeps submenus as GameObject containers.
            var newSubmenu = new MergedMenuItem
            {
                name = uniqueName,
                // Mark as a menu item backed by a GameObject (Modular Avatar style)
                source = "MA_MenuItem",
                children = new List<MergedMenuItem>(),
                // We intentionally do NOT create a VRCExpressionsMenu asset for the submenu
                subMenu = null,
                control = new VRCExpressionsMenu.Control
                {
                    name = uniqueName,
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    // No ExpressionMenu asset is generated or referenced here
                    subMenu = null,
                    // Provide an empty parameter object so MA components can decide to
                    // auto-generate parameter names when applicable
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = string.Empty }
                }
            };
            
            // Add to current menu
            currentMenu.children.Add(newSubmenu);
            
            // Note: Per configuration, we do NOT create new ExpressionMenu assets
            // here and we do not modify underlying VRCExpressionsMenu assets.
            // The submenu exists as an in-scene GameObject (created below in
            // edit-mode) and the merged structure is updated so the UI reflects
            // the new submenu container immediately.
            
            // Update current view
            UpdateCurrentMenuItems();

            // If in edit mode, create a child GameObject under the MenuItem root to
            // represent this submenu (no ExpressionMenu asset is created).
            if (editMode)
            {
                TryCreateMenuItemGameObjectForSubmenu(newSubmenu, parentMenu: currentMenu);
            }
            
            // Debug.Log(GetLocalizedText(
            //     $"新しいサブメニュー '{uniqueName}' を作成しました",
            //     $"Created new submenu '{uniqueName}'"
            // ));
            
            Repaint();
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
            string sanitizedMenuName = SanitizeForAssetPath(menuName, "Menu");
            string assetName = sanitizedMenuName.Replace(" ", "_");
            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{folderPath}/{assetName}.asset");
            
            // Create the asset
            AssetDatabase.CreateAsset(menu, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            // Debug.Log(GetLocalizedText(
            //     $"VRCExpressionsMenuアセットを作成しました: {assetPath}",
            //     $"Created VRCExpressionsMenu asset: {assetPath}"
            // ));
            
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
        
        private string ShowSubmenuNameInputDialog()
        {
            string defaultName = GetLocalizedText("新しいサブメニュー", "New Submenu");
            string dialogTitle = GetLocalizedText("サブメニュー名を入力", "Enter Submenu Name");
            string dialogMessage = GetLocalizedText("新しいサブメニューの名前を入力してください:", "Please enter the name for the new submenu:");
            
            // Create a popup window for text input
            return SubmenuNameInputWindow.ShowDialog(dialogTitle, dialogMessage, defaultName);
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
            public string fullPath;
            public bool isReadOnly;
        }

        /// <summary>
        /// メニュー項目（通常または除外）の表示用情報
        /// </summary>
        private class MenuItemDisplayInfo
        {
            public GameObject GameObjectNode { get; set; }              // GameObject
            public string DisplayName { get; set; }                      // 表示名
            public VRCExpressionsMenu.Control Control { get; set; }     // Control 情報
            public bool IsExcludedItem { get; set; }                     // 除外項目か
            public Texture2D Icon { get; set; }                          // アイコン
        }

        private class ExclusionSelectionWindow : EditorWindow
        {
            private VRCExpressionMenuOrganizerWindow owner;
            private string avatarName;
            private MergedMenuItem selectionRoot;
            private HashSet<string> workingSelection;
            private Vector2 scrollPosition;
            private bool suppressCallbacks;
            private bool selectionDispatched;
            private readonly Dictionary<string, bool> groupFoldouts = new Dictionary<string, bool>(StringComparer.Ordinal);
            private GUIStyle highlightedMessageStyle;

            public static ExclusionSelectionWindow ShowWindow(VRCExpressionMenuOrganizerWindow owner, string avatarName, MergedMenuItem root, HashSet<string> initialSelection)
            {
                var window = CreateInstance<ExclusionSelectionWindow>();
                window.owner = owner;
                window.avatarName = string.IsNullOrEmpty(avatarName) ? "Avatar" : avatarName;
                window.selectionRoot = root;
                window.workingSelection = initialSelection != null
                    ? new HashSet<string>(initialSelection, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                window.titleContent = new GUIContent("除外する項目を選択");
                window.minSize = new Vector2(420f, 520f);
                window.ShowUtility();
                return window;
            }

            public void ForceCloseWithoutDispatch()
            {
                // Ensure owner UI exits edit mode when forcing close without dispatch
                try
                {
                    if (owner != null)
                    {
                        owner.editMode = false;
                        try { owner.HandleEditModeToggled(false); } catch { }
                        // Clear awaiting state since ApplyExclusionSelection will not be called
                        try { owner.awaitingExclusionSelection = false; } catch { }
                        try { owner.activeExclusionSelectionWindow = null; } catch { }
                    }
                }
                catch { }

                suppressCallbacks = true;
                Close();
            }

            private void OnGUI()
            {
                // カスタムスタイルの初期化
                if (highlightedMessageStyle == null)
                {
                    highlightedMessageStyle = new GUIStyle(EditorStyles.helpBox);
                    highlightedMessageStyle.normal.background = MakeSolidColorTexture(new Color32(0xBB, 0x00, 0x00, 0xFF)); // #BB0000
                    highlightedMessageStyle.normal.textColor = Color.white;
                    highlightedMessageStyle.fontSize = Mathf.RoundToInt(EditorStyles.helpBox.fontSize * 1.2f);
                    // padding: 左右10, 上下はhelpBoxの1.25倍
                    int padL = 10, padR = 10;
                    int padT = Mathf.RoundToInt(EditorStyles.helpBox.padding.top * 1.25f);
                    int padB = Mathf.RoundToInt(EditorStyles.helpBox.padding.bottom * 1.25f);
                    highlightedMessageStyle.padding = new RectOffset(padL, padR, padT, padB);
                    highlightedMessageStyle.wordWrap = true;
                    highlightedMessageStyle.alignment = TextAnchor.MiddleLeft;
                }

                if (owner == null)
                {
                    EditorGUILayout.HelpBox(Localized(
                        "親ウィンドウが閉じられたため選択を完了できません。",
                        "The parent window closed before the selection was completed."), MessageType.Warning);
                    if (GUILayout.Button(Localized("閉じる", "Close")))
                    {
                        ForceCloseWithoutDispatch();
                    }
                    return;
                }

                EditorGUILayout.HelpBox(Localized(
                    "メニュー項目をすべてModular Avatarに転送します。必ずアバターのバックアップを取ってから実行してください。",
                    "All menu items will be transferred to Modular Avatar. Please make sure to back up your avatar before proceeding."), MessageType.Warning);
                GUILayout.Box(Localized(
                    "可愛いポーズツール、VirtualLensなど、一部ギミックはチェックを入れてください。",
                    "Please check certain gimmicks such as cute pose tools and VirtualLens."),
                    highlightedMessageStyle, GUILayout.ExpandWidth(true));

                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                bool renderedAny = DrawMenuGroup(selectionRoot, -1);
                if (!renderedAny)
                {
                    EditorGUILayout.HelpBox(Localized(
                    "ルート階層直下のModular Avatar項目が見つかりません。除外設定は不要です。",
                    "No root-level Modular Avatar items were found. There is nothing to exclude."), MessageType.Info);
                }
                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space();
                // 「後にする」ボタン（ウィンドウ幅・36px）
                if (GUILayout.Button(Localized("後にする", "Later"), GUILayout.Height(36), GUILayout.ExpandWidth(true)))
                {
                    DispatchSelectionAndClose(true);
                }
                // 「決定」ボタン（ウィンドウ幅・36px）
                if (GUILayout.Button(Localized("決定", "Confirm"), GUILayout.Height(36), GUILayout.ExpandWidth(true)))
                {
                    DispatchSelectionAndClose(false);
                }
            }

            private bool DrawMenuGroup(MergedMenuItem parent, int depth)
            {
                if (parent == null) return false;

                var selectableChildren = new List<MergedMenuItem>();
                var childGroups = new List<MergedMenuItem>();

                if (parent.children != null)
                {
                    foreach (var child in parent.children)
                    {
                        bool childSelectable = owner != null && owner.IsItemSelectableForExclusion(child);
                        bool childHasGroup = child.children != null && child.children.Count > 0 && owner != null && owner.HasSelectableExclusionItems(child);

                        if (childSelectable)
                        {
                            selectableChildren.Add(child);
                        }

                        if (childHasGroup)
                        {
                            childGroups.Add(child);
                        }
                    }
                }

                if (selectableChildren.Count == 0 && childGroups.Count == 0)
                {
                    return false;
                }

                string header = depth < 0
                    ? Localized("ルート階層", "Root Level")
                    : (string.IsNullOrEmpty(parent.name) ? Localized("(名称未設定)", "(No Name)") : parent.name);

                string key = $"{depth}:{parent.fullPath ?? header}";
                if (!groupFoldouts.TryGetValue(key, out bool foldoutState))
                {
                    foldoutState = true;
                }

                foldoutState = EditorGUILayout.Foldout(foldoutState, header, true);
                groupFoldouts[key] = foldoutState;

                if (foldoutState)
                {
                    EditorGUI.indentLevel++;
                    foreach (var item in selectableChildren)
                    {
                        DrawSelectableItem(item);
                    }

                    foreach (var group in childGroups)
                    {
                        DrawMenuGroup(group, depth + 1);
                    }
                    EditorGUI.indentLevel--;
                }

                return true;
            }

            private void DrawSelectableItem(MergedMenuItem item)
            {
                if (item == null) return;

                var path = item.fullPath;
                bool hasPath = !string.IsNullOrEmpty(path);
                bool current = hasPath && workingSelection.Contains(path);
                string label = string.IsNullOrEmpty(item.name)
                    ? Localized("(名称未設定)", "(No Name)")
                    : item.name;
                bool next = EditorGUILayout.ToggleLeft(label, current);

                if (!hasPath || next == current) return;

                if (next)
                {
                    workingSelection.Add(path);
                }
                else
                {
                    workingSelection.Remove(path);
                }
            }

            private void DispatchSelectionAndClose(bool deferConversion)
            {
                if (selectionDispatched || suppressCallbacks)
                {
                    Close();
                    return;
                }

                selectionDispatched = true;
                // If the user deferred conversion ("後にする"), exit edit mode immediately.
                // If the user confirmed (deferConversion == false), keep/enter edit mode so the menu editor remains active.
                try
                {
                    if (owner != null)
                    {
                        if (deferConversion)
                        {
                            owner.editMode = false;
                            try { owner.HandleEditModeToggled(false); } catch { }
                        }
                        else
                        {
                            owner.editMode = true;
                            try { owner.HandleEditModeToggled(true); } catch { }
                        }
                    }
                }
                catch { }

                owner?.ApplyExclusionSelection(new HashSet<string>(workingSelection, StringComparer.Ordinal), deferConversion);
                suppressCallbacks = true;
                Close();
            }

            private void OnDestroy()
            {
                if (suppressCallbacks || selectionDispatched)
                {
                    return;
                }

                if (owner != null)
                {
                    selectionDispatched = true;
                    // Exit edit mode immediately when the window is destroyed without explicit action
                    try
                    {
                        owner.editMode = false;
                        try { owner.HandleEditModeToggled(false); } catch { }
                    }
                    catch { }

                    owner?.ApplyExclusionSelection(new HashSet<string>(workingSelection, StringComparer.Ordinal), true);
                }
            }

            private Texture2D MakeSolidColorTexture(Color color)
            {
                var texture = new Texture2D(1, 1);
                texture.SetPixel(0, 0, color);
                texture.Apply();
                return texture;
            }

            private string Localized(string japanese, string english)
            {
                return owner != null ? owner.GetLocalizedText(japanese, english) : japanese;
            }
        }

        private MergedMenuItem BuildMergedMenuStructure()
        {
            if (selectedAvatar == null)
            {
                LogDetail("BuildMergedMenuStructure: selectedAvatar is null");
                cachedMenuStructure = null;
                return null;
            }

            if (!menuStructureDirty && cachedMenuStructure != null)
            {
                LogDetail("BuildMergedMenuStructure: Using cached structure");
                return cachedMenuStructure;
            }

            LogDetail("========== BuildMergedMenuStructure: START (Rebuild) ==========");
            LogDetail($"BuildMergedMenuStructure: Processing avatar '{selectedAvatar.name}'");
            maControlMap.Clear();
            maComponentToControl.Clear();
            modularAvatarInstallTargets.Clear();

            MergedMenuItem builtStructure = null;

            if (IsModularAvatarAvailable())
            {
                LogDetail("BuildMergedMenuStructure: Modular Avatar is available, using MA-aware build");
                var maStructure = BuildMergedMenuStructureWithModularAvatar();
                if (maStructure != null)
                {
                    LogDetail($"BuildMergedMenuStructure: MA structure built successfully with {maStructure.children?.Count ?? 0} root children");
                    RemoveEditorOnlyEntriesFromMenu(maStructure);
                    builtStructure = maStructure;
                }
                else
                {
                    LogDetail("BuildMergedMenuStructure: MA structure returned null, falling back to legacy");
                }
            }
            else
            {
                LogDetail("BuildMergedMenuStructure: Modular Avatar not available, using legacy build");
            }

            if (builtStructure == null)
            {
                var legacyStructure = BuildMergedMenuStructureLegacy();
                RemoveEditorOnlyEntriesFromMenu(legacyStructure);
                LogDetail($"BuildMergedMenuStructure: Legacy structure built with {legacyStructure?.children?.Count ?? 0} root children");
                builtStructure = legacyStructure;
            }

            AssignMenuPaths(builtStructure);
            UpdateReadOnlyFlags(builtStructure);

            cachedMenuStructure = builtStructure;
            menuStructureDirty = cachedMenuStructure == null;

            LogDetail("========== BuildMergedMenuStructure: END (Rebuild) ==========");
            return cachedMenuStructure;
        }

        private MergedMenuItem BuildMergedMenuStructureLegacy()
        {
            if (selectedAvatar == null) return null;

            var rootItem = new MergedMenuItem
            {
                name = GetLocalizedText("ルートメニュー", "Root Menu"),
                source = "VRC"
            };

            var mainMenu = GetMainExpressionMenu();
            if (mainMenu != null)
            {
                BuildMenuItemsFromVRCMenu(mainMenu, rootItem, "VRC");
            }

            if (IsModularAvatarAvailable())
            {
                IntegrateModularAvatarMenus(rootItem);
            }

            return rootItem;
        }

        private MergedMenuItem BuildMergedMenuStructureWithModularAvatar()
        {
            LogDetail("BuildMergedMenuStructureWithModularAvatar: Calling ModularAvatarMenuBridge.Build");
            try
            {
                var result = ModularAvatarMenuBridge.Build(this);
                if (result != null)
                {
                    LogDetail($"BuildMergedMenuStructureWithModularAvatar: Success, root has {result.children?.Count ?? 0} children");
                    try
                    {
                        IncludeOrphanMAItems(result);
                    }
                    catch { }
                }
                else
                {
                    LogDetail("BuildMergedMenuStructureWithModularAvatar: Bridge returned null");
                }
                return result;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                LogDetail($"BuildMergedMenuStructureWithModularAvatar: Exception - {inner.Message}");
                Debug.LogWarning($"Failed to build Modular Avatar merged menu: {inner.Message}");
                Debug.LogException(inner);
                return null;
            }
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
            LogDetail($"--- IntegrateDirectMenuInstaller: START for '{installer.gameObject.name}' ---");
            LogDetail($"    menuToInstall: {(menuToInstall != null ? menuToInstall.name : "null")}");

            // Get the install target menu
            var installTargetMenu = GetInstallTargetFromInstaller(installer);
            LogDetail($"    installTargetMenu: {(installTargetMenu != null ? installTargetMenu.name : "null")}");

            MergedMenuItem targetItem = null;

            if (installTargetMenu != null)
            {
                // Find the specific menu item that matches the install target
                targetItem = FindMenuItemByVRCMenu(rootItem, installTargetMenu);
                LogDetail($"    Found target item: {(targetItem != null ? targetItem.name : "null (using root)")}");
            }

            // If no specific target found, use root menu
            if (targetItem == null)
            {
                targetItem = rootItem;
                LogDetail("    Using root as target item");
            }

            // Add only the installer entry, not the detailed menu content
            // This prevents MA Menu Item details from being displayed
            // DO NOT set subMenu field to prevent MA menu content from being baked into VRC menu
            var installerItem = new MergedMenuItem
            {
                name = installer.gameObject.name,
                source = "MA_Installer",
                sourceComponent = installer
                // subMenu is intentionally not set to prevent double menu entries
            };

            LogDetail($"    Created installerItem: name='{installerItem.name}', source='{installerItem.source}', subMenu={(installerItem.subMenu != null ? installerItem.subMenu.name : "null (INTENTIONALLY NOT SET)")}");

            targetItem.children.Add(installerItem);
            LogDetail($"    Added installerItem to target. Target now has {targetItem.children.Count} children");
            LogDetail($"--- IntegrateDirectMenuInstaller: END for '{installer.gameObject.name}' ---");
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
                EditorGUILayout.LabelField(GetLocalizedText("メインメニュー", "Main Menu"), EditorStyles.boldLabel);
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

        /// <summary>
        /// 除外項目の表示（読み取り専用マーク付き）
        /// </summary>
        private void DrawExcludedMenuItemNode(GameObject gameObject, MenuItemDisplayInfo displayInfo, int depth)
        {
            GUILayout.BeginHorizontal("box");
            {
                // インデント
                GUILayout.Space(depth * 20);

                // 1. アイコン表示
                if (displayInfo.Icon != null)
                {
                    GUILayout.Label(
                        new GUIContent(displayInfo.Icon),
                        GUILayout.Width(20),
                        GUILayout.Height(20)
                    );
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(20), GUILayout.Height(20));
                }

                // 2. 項目名
                GUILayout.Label(displayInfo.DisplayName, GUILayout.ExpandWidth(true));

                // 3. 読 取り専用マーク
                GUILayout.Label("[読取専用]", GUILayout.Width(80));

                // 4. ボタン（編集モード時のみ）
                if (editMode)
                {
                    // 並び順変更ボタン
                    if (GUILayout.Button("▲", GUILayout.Width(30)))
                    {
                        MoveMenuItemExcluded(gameObject, -1);
                    }
                    if (GUILayout.Button("▼", GUILayout.Width(30)))
                    {
                        MoveMenuItemExcluded(gameObject, 1);
                    }
                }
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 通常項目の表示
        /// </summary>
        private void DrawNormalMenuItemNode(GameObject gameObject, MenuItemDisplayInfo displayInfo, int depth)
        {
            GUILayout.BeginHorizontal("box");
            {
                // インデント
                GUILayout.Space(depth * 20);

                // 1. アイコン表示
                if (displayInfo.Icon != null)
                {
                    GUILayout.Label(
                        new GUIContent(displayInfo.Icon),
                        GUILayout.Width(20),
                        GUILayout.Height(20)
                    );
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(20), GUILayout.Height(20));
                }

                // 2. 項目名
                GUILayout.Label(displayInfo.DisplayName, GUILayout.ExpandWidth(true));

                // 3. ボタン（編集モード時）
                if (editMode)
                {
                    // 削除ボタン
                    if (GUILayout.Button("削除", GUILayout.Width(60)))
                    {
                        // 削除処理
                        RemoveMenuItemFromGameObject(gameObject);
                    }

                    // 並び順変更ボタン
                    if (GUILayout.Button("▲", GUILayout.Width(30)))
                    {
                        MoveMenuItemExcluded(gameObject, -1);
                    }
                    if (GUILayout.Button("▼", GUILayout.Width(30)))
                    {
                        MoveMenuItemExcluded(gameObject, 1);
                    }
                }
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 除外項目 GameObject の並び順を変更するヘルパーメソッド
        /// </summary>
        private void MoveMenuItemExcluded(GameObject gameObject, int direction)
        {
            if (gameObject == null || selectedAvatar == null)
                return;

            Transform menuItemRoot = FindMenuItemRoot(selectedAvatar);
            if (menuItemRoot == null)
                return;

            int currentIndex = gameObject.transform.GetSiblingIndex();
            int newIndex = currentIndex + direction;

            // 範囲チェック
            if (newIndex < 0 || newIndex >= menuItemRoot.childCount)
                return;

            // Sibling Index を変更
            gameObject.transform.SetSiblingIndex(newIndex);
            EditorUtility.SetDirty(gameObject);
            EditorUtility.SetDirty(menuItemRoot.gameObject);
        }

        /// <summary>
        /// GameObject をメニューから削除
        /// </summary>
        private void RemoveMenuItemFromGameObject(GameObject gameObject)
        {
            if (gameObject == null)
                return;

            Undo.DestroyObjectImmediate(gameObject);
            EditorUtility.SetDirty(selectedAvatar.gameObject);
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
                    GetLocalizedText("メニュー (編集中)", "Menu (Editing)") :
                    GetLocalizedText("メニュー", "Menu");
                EditorGUILayout.LabelField(menuTitle, EditorStyles.boldLabel);
                DrawMenuGrid(menuStructureToDisplay);
            }
            else if (!editMode)
            {
                // Fallback for original structure when not in edit mode
                var mainMenu = GetMainExpressionMenu();
                if (mainMenu != null)
                {
                    EditorGUILayout.LabelField(GetLocalizedText("メニュー", "Menu"), EditorStyles.boldLabel);
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

            // 非編集モード時：除外マーカー付きアイテムを読み取り専用で表示
            if (!editMode && selectedAvatar != null)
            {
                DisplayExcludedItemsReadOnly();
            }
        }

        /// <summary>
        /// 非編集モード時：「メニュー項目」内の構造に従った順序で、
        /// 除外マーカー付き元アイテムを読み取り専用で表示
        /// </summary>
        private void DisplayExcludedItemsReadOnly()
        {
            var excludedItems = GetExcludedItemsInMenuItemOrder();

            if (excludedItems.Count == 0)
                return;

            GUILayout.Space(10);
            GUILayout.BeginVertical("box");
            {
                GUILayout.Label(GetLocalizedText("除外マーカー付きアイテム（読み取り専用）", "Excluded Items (Read-Only)"), EditorStyles.boldLabel);

                foreach (var excludedGO in excludedItems)
                {
                    // GameObjectから Menu Installer コンポーネントを探してControlを取得
                    var menuItemType = GetModularAvatarMenuItemType();
                    var menuInstallerComponent = excludedGO.GetComponent(menuItemType);
                    var control = ExtractControlFromSourceComponent(menuInstallerComponent);

                    GUILayout.BeginHorizontal("box");
                    {
                        // 1. アイコン表示
                        if (control?.icon != null)
                        {
                            GUILayout.Label(
                                new GUIContent(control.icon),
                                GUILayout.Width(20),
                                GUILayout.Height(20)
                            );
                        }
                        else
                        {
                            GUILayout.Label("", GUILayout.Width(20), GUILayout.Height(20));
                        }

                        // 2. 項目名
                        GUILayout.Label(
                            control?.name ?? excludedGO.name,
                            GUILayout.ExpandWidth(true)
                        );

                        // 3. 読 取り専用マーク
                        GUILayout.Label(GetLocalizedText("[読取専用]", "[Read-Only]"), GUILayout.Width(80));
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndVertical();
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

        /// <summary>
        /// MergedMenuItem ツリー内でアイテムを移動（除外/通常両対応）
        /// </summary>
        private void MoveMenuItemInTree(MergedMenuItem item, int direction)
        {
            if (item == null || editedMenuStructure == null)
                return;

            var parent = FindParentInMergedTree(editedMenuStructure, item);
            if (parent == null || parent.children == null)
                return;

            int currentIndex = parent.children.IndexOf(item);
            if (currentIndex < 0)
                return;

            int newIndex = currentIndex + direction;
            if (newIndex < 0 || newIndex >= parent.children.Count)
                return;

            // 入れ替え
            var temp = parent.children[currentIndex];
            parent.children[currentIndex] = parent.children[newIndex];
            parent.children[newIndex] = temp;

            // Update underlying GameObjects in edit mode
            if (editMode)
            {
                var parentGo = ResolveGameObjectForMergedItem(parent) ?? GetMenuItemRootTransform()?.gameObject;
                if (parentGo != null)
                {
                    var a = ResolveGameObjectForMergedItem(parent.children[newIndex]); // after the swap in-memory
                    var b = ResolveGameObjectForMergedItem(parent.children[currentIndex]);
                    if (a != null && b != null && a.transform.parent == b.transform.parent)
                    {
                        // perform sibling index swap
                        int aIndex = a.transform.GetSiblingIndex();
                        int bIndex = b.transform.GetSiblingIndex();
                        Undo.RecordObject(a.transform, "Swap siblings");
                        Undo.RecordObject(b.transform, "Swap siblings");
                        a.transform.SetSiblingIndex(bIndex);
                        b.transform.SetSiblingIndex(aIndex);
                        EditorUtility.SetDirty(a);
                        EditorUtility.SetDirty(b);
                    }
                }
            }
            EditorUtility.SetDirty(selectedAvatar.gameObject);
        }

        /// <summary>
        /// MergedMenuItem ツリー内で親を検索
        /// </summary>
        private MergedMenuItem FindParentInMergedTree(MergedMenuItem root, MergedMenuItem target)
        {
            if (root == null || target == null)
                return null;

            if (root.children != null && root.children.Contains(target))
                return root;

            foreach (var child in root.children ?? new List<MergedMenuItem>())
            {
                var found = FindParentInMergedTree(child, target);
                if (found != null)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// MergedMenuItem ツリーからアイテムを削除（通常項目のみ）
        /// </summary>
        private void RemoveMenuItemFromTree(MergedMenuItem item)
        {
            if (item == null || editedMenuStructure == null)
                return;

            // 除外項目は削除不可（念のため確認）
            if (item.sourceComponent != null &&
                IsExcludedItemByInstaller(item.sourceComponent.gameObject))
            {
                LogDetail("除外項目は削除できません");
                return;
            }

            var parent = FindParentInMergedTree(editedMenuStructure, item);
            if (parent == null || parent.children == null)
                return;

            parent.children.Remove(item);
            EditorUtility.SetDirty(selectedAvatar.gameObject);
        }

        private void DrawNavigationBreadcrumb()
        {
            // Back button above breadcrumb (if not at root)
            if (menuNavigationStack.Count > 0)
            {
                string backButtonText = "← " + GetLocalizedText("戻る", "Back");
                if (editMode)
                {
                    backButtonText += GetLocalizedText(" (ドロップエリア)", " (Drop Zone)");
                }

                // Get rect for back button (50% window width, 36px height)
                Rect backButtonRect = GUILayoutUtility.GetRect(new GUIContent(backButtonText), GUI.skin.button, GUILayout.Height(36), GUILayout.ExpandWidth(false));
                backButtonRect.width = EditorGUIUtility.currentViewWidth * 0.5f - 8; // 50% of window width minus padding

                if (GUI.Button(backButtonRect, backButtonText))
                {
                    menuNavigationStack.RemoveAt(menuNavigationStack.Count - 1);
                }

                // Update back navigation drop area for drag and drop
                if (editMode)
                {
                    backNavigationDropArea = backButtonRect;
                }
                else
                {
                    backNavigationDropArea = Rect.zero;
                }
            }
            else
            {
                backNavigationDropArea = Rect.zero;
            }

            // Breadcrumb trail
            EditorGUILayout.BeginHorizontal();
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
                    COLOR_DELETE_AREA : // Bright red when hovering
                    new Color(COLOR_DELETE_AREA.r, COLOR_DELETE_AREA.g, COLOR_DELETE_AREA.b, 0.5f);  // Light red otherwise

                EditorGUI.DrawRect(deleteDropArea, deleteColor);

                // Add border for better visibility
                DrawBorder(deleteDropArea, dragTargetIsDeleteArea ? COLOR_DELETE_AREA : new Color(COLOR_DELETE_AREA.r * 0.8f, COLOR_DELETE_AREA.g * 0.8f, COLOR_DELETE_AREA.b * 0.8f, 1f), 3f);
                
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
                    COLOR_BACK_NAVIGATION : // Bright blue when hovering
                    new Color(COLOR_BACK_NAVIGATION.r, COLOR_BACK_NAVIGATION.g, COLOR_BACK_NAVIGATION.b, 0.3f);  // Light blue otherwise

                EditorGUI.DrawRect(backNavigationDropArea, dropAreaColor);

                // Add border for better visibility
                DrawBorder(backNavigationDropArea, dragTargetIsParentLevel ? COLOR_BACK_NAVIGATION : new Color(COLOR_BACK_NAVIGATION.r * 0.7f, COLOR_BACK_NAVIGATION.g * 0.5f, COLOR_BACK_NAVIGATION.b, 1f), 2f);
                
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

            // 編集モード時、除外項目（読み取り専用項目）の判定
            // item.isReadOnlyは既にExprMenuVisualizerExcludedコンポーネントをチェックしている
            bool isExcludedItem = editMode && item.isReadOnly;

            // 除外項目の場合はサブメニューやトグルではなく単なるアイテムとして扱う
            bool hasSubMenu = !isExcludedItem && (item.children.Count > 0 || (item.control?.subMenu != null));
            bool isEmptySubmenu = !isExcludedItem && (item.control?.type == VRCExpressionsMenu.Control.ControlType.SubMenu) &&
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
                if (isExcludedItem)
                    overlayColor = new Color(COLOR_EXCLUDED.r, COLOR_EXCLUDED.g, COLOR_EXCLUDED.b, 0.45f); // Orange for excluded items
                else if (isEmptySubmenu)
                    overlayColor = new Color(COLOR_EMPTY_SUBMENU.r, COLOR_EMPTY_SUBMENU.g, COLOR_EMPTY_SUBMENU.b, 0.4f); // Yellow overlay for empty submenus
                else if (hasSubMenu)
                    overlayColor = new Color(COLOR_SUBMENU.r, COLOR_SUBMENU.g, COLOR_SUBMENU.b, 0.3f); // Blue overlay for regular submenus
                else
                    overlayColor = new Color(0, 0, 0, 0.2f); // Default overlay
                
                EditorGUI.DrawRect(itemRect, overlayColor);
            }
            else
            {
                // Draw attractive gradient background if no icon
                Color topColor, bottomColor;
                if (isExcludedItem)
                {
                    topColor = new Color(COLOR_EXCLUDED.r, COLOR_EXCLUDED.g + 0.2f, COLOR_EXCLUDED.b + 0.2f, 0.9f);
                    bottomColor = COLOR_EXCLUDED;
                }
                else if (isEmptySubmenu)
                {
                    topColor = new Color(COLOR_EMPTY_SUBMENU.r, COLOR_EMPTY_SUBMENU.g, COLOR_EMPTY_SUBMENU.b + 0.2f, 0.8f);
                    bottomColor = new Color(COLOR_EMPTY_SUBMENU.r - 0.1f, COLOR_EMPTY_SUBMENU.g - 0.1f, COLOR_EMPTY_SUBMENU.b - 0.2f, 0.9f);
                }
                else if (hasSubMenu)
                {
                    topColor = new Color(COLOR_SUBMENU.r + 0.4f, COLOR_SUBMENU.g - 0.1f, COLOR_SUBMENU.b, 0.8f);
                    bottomColor = new Color(COLOR_SUBMENU.r - 0.2f, COLOR_SUBMENU.g - 0.3f, COLOR_SUBMENU.b, 0.9f);
                }
                else
                {
                    // Different colors based on control type for better visual distinction
                    var controlType = item.control?.type ?? VRCExpressionsMenu.Control.ControlType.Button;
                    switch (controlType)
                    {
                        case VRCExpressionsMenu.Control.ControlType.Toggle:
                            topColor = new Color(COLOR_MA_ITEM.r - 0.2f, COLOR_MA_ITEM.g, COLOR_MA_ITEM.b - 0.2f, 0.8f);   // Light green variant
                            bottomColor = new Color(COLOR_MA_ITEM.r - 0.4f, COLOR_MA_ITEM.g - 0.2f, COLOR_MA_ITEM.b - 0.4f, 0.9f); // Dark green variant
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
                borderColor = COLOR_EMPTY_SUBMENU; // Yellow border for empty submenus
                borderWidth = 3f;
            }
            else if (hasSubMenu)
            {
                borderColor = COLOR_SUBMENU; // Blue border for regular submenus
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

                // 編集モード時の除外項目は詳細情報を非表示
                if (!isExcludedItem)
                {
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
                }
                
                GUILayout.EndVertical();
                GUILayout.EndArea();
            }
            else
            {
                // For non-texture items, only show source indicator at bottom
                GUILayout.BeginArea(itemRect);
                GUILayout.BeginVertical();

                GUILayout.FlexibleSpace();

                // 編集モード時の除外項目は詳細情報を非表示
                if (!isExcludedItem)
                {
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
                }

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
                        // 除外項目のサブメニューには遷移させない
                        if (item.isReadOnly)
                        {
                            EditorUtility.DisplayDialog(
                                GetLocalizedText("除外項目", "Excluded Item"),
                                GetLocalizedText(
                                    "この項目は除外されているため、編集モードでサブメニュー内容を変更できません。",
                                    "This item is excluded and its submenu contents cannot be edited in edit mode."
                                ),
                                GetLocalizedText("OK", "OK")
                            );
                        }
                        else
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

            bool isModularAvatarControl = false;
            var menuItemType = GetModularAvatarMenuItemType();
            if (editingSourceComponent != null && menuItemType != null)
            {
                isModularAvatarControl = menuItemType.IsInstanceOfType(editingSourceComponent);
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(GetLocalizedText("コントロール編集", "Control Editor"), EditorStyles.boldLabel);
            
            // Control name
            EditorGUILayout.LabelField(GetLocalizedText("コントロール名", "Control Name"));
            string newName = EditorGUILayout.TextField(editingControl.name);
            if (newName != editingControl.name)
            {
                if (isModularAvatarControl)
                {
                    Undo.RecordObject(editingSourceComponent, "Edit MA Control Name");
                    MarkComponentDirty(editingSourceComponent);
                }
                editingControl.name = newName;

                // GameObject名も変更
                if (editingSourceComponent != null && editingSourceComponent.gameObject != null)
                {
                    Undo.RecordObject(editingSourceComponent.gameObject, "Rename Menu GameObject");
                    editingSourceComponent.gameObject.name = newName;

                    // ExprMenuVisualizerGeneratedMetadataのfullPathも更新
                    var meta = editingSourceComponent.gameObject.GetComponent<VRCExpressionMenuVisualizer.ExprMenuVisualizerGeneratedMetadata>();
                    if (meta != null)
                    {
                        // 親のfullPathを取得
                        string parentPath = null;
                        var parent = editingSourceComponent.transform.parent;
                        if (parent != null)
                        {
                            var parentMeta = parent.GetComponent<VRCExpressionMenuVisualizer.ExprMenuVisualizerGeneratedMetadata>();
                            if (parentMeta != null && !string.IsNullOrEmpty(parentMeta.fullPath))
                                parentPath = parentMeta.fullPath;
                        }
                        // ルート名補完
                        if (string.IsNullOrEmpty(parentPath))
                        {
                            // ルートGameObject名を取得
                            var go = editingSourceComponent.gameObject;
                            var rootGo = go.transform.root.gameObject;
                            parentPath = rootGo != null ? rootGo.name : "";
                        }
                        meta.fullPath = string.IsNullOrEmpty(parentPath) ? newName : parentPath + "/" + newName;
                    }
                }
            }

            // Parameter field (for controls with parameters)
            if (editingControl.type == VRCExpressionsMenu.Control.ControlType.Button || 
                editingControl.type == VRCExpressionsMenu.Control.ControlType.Toggle)
            {
                EditorGUILayout.LabelField(GetLocalizedText("パラメータ名", "Parameter Name"));
                if (editingControl.parameter == null)
                {
                    editingControl.parameter = new VRCExpressionsMenu.Control.Parameter();
                }
                string newParamName = EditorGUILayout.TextField(editingControl.parameter.name);
                if (newParamName != editingControl.parameter.name)
                {
                    if (isModularAvatarControl)
                    {
                        Undo.RecordObject(editingSourceComponent, "Edit MA Parameter");
                    }
                    editingControl.parameter.name = newParamName;
                    if (isModularAvatarControl)
                    {
                        MarkComponentDirty(editingSourceComponent);
                    }
                }
            }
            
            // Value field (for controls with numeric values)
            if (editingControl.type == VRCExpressionsMenu.Control.ControlType.Button)
            {
                EditorGUILayout.LabelField(GetLocalizedText("値", "Value"));
                float newValue = EditorGUILayout.Slider(editingControl.value, 0, 1);
                if (!Mathf.Approximately(newValue, editingControl.value))
                {
                    if (isModularAvatarControl)
                    {
                        Undo.RecordObject(editingSourceComponent, "Edit MA Value");
                    }
                    editingControl.value = newValue;
                    if (isModularAvatarControl)
                    {
                        MarkComponentDirty(editingSourceComponent);
                    }
                }
            }
            
            // Icon field
            EditorGUILayout.LabelField(GetLocalizedText("アイコン", "Icon"));
            
            // アイコン選択フィールド
            var newIcon = (Texture2D)EditorGUILayout.ObjectField(editingControl.icon, typeof(Texture2D), false);
            if (newIcon != editingControl.icon)
            {
                if (isModularAvatarControl)
                {
                    Undo.RecordObject(editingSourceComponent, "Edit MA Icon");
                }
                editingControl.icon = newIcon;
                if (isModularAvatarControl)
                {
                    MarkComponentDirty(editingSourceComponent);
                }
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
            
            // サブメニューの表記・フィールドを削除
            
            if (isModularAvatarControl)
            {
                DrawModularAvatarControlExtras();
            }

            // Save button
            if (GUILayout.Button(GetLocalizedText("内容の保存", "Save Content")))
            {
                if (isModularAvatarControl)
                {
                    SetModularAvatarMenuItemLabel(editingSourceComponent, editingControl.name);
                    MarkComponentDirty(editingSourceComponent);
                }
                isEditingControl = false;
                editingSourceComponent = null;
                editingControl = null;

                // Refresh menu tree to reflect changes immediately
                RefreshMenuTree();
                Repaint();
                GUIUtility.keyboardControl = 0;
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

                    // Filter out Editor Only components and Included marker components
                    foreach (var component in components)
                    {
                        if (!IsComponentOrParentEditorOnly(component))
                        {
                            // Skip components from GameObjects with Included marker
                            if (component != null && component.gameObject != null &&
                                component.gameObject.GetComponent<ExprMenuVisualizerIncluded>() == null)
                            {
                                installers.Add(component);
                            }
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

                    // Filter out Editor Only components and Included marker components
                    foreach (var component in components)
                    {
                        if (!IsComponentOrParentEditorOnly(component))
                        {
                            // Skip components from GameObjects with Included marker
                            if (component != null && component.gameObject != null &&
                                component.gameObject.GetComponent<ExprMenuVisualizerIncluded>() == null)
                            {
                                menuItems.Add(component);
                            }
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
            editingSourceComponent = null;
            editingControl = control;

            if (control != null && maControlMap.TryGetValue(control, out var component))
            {
                editingSourceComponent = component;
                if (maComponentToControl.TryGetValue(component, out var mappedControl) && mappedControl != null)
                {
                    editingControl = mappedControl;
                }
            }

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
                    if (IsItemReadOnly(item))
                    {
                        if (!isDragging && draggedItem == item && Vector2.Distance(evt.mousePosition, dragStartPosition) > 5f)
                        {
                            ShowReadOnlyItemWarning();
                            draggedItem = null;
                        }
                        break;
                    }
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
                        if (IsItemReadOnly(item))
                        {
                            ShowReadOnlyItemWarning();
                            draggedItem = null;
                            isDragging = false;
                            dragTargetParent = null;
                            dragTargetIndex = -1;
                            Repaint();
                            evt.Use();
                            break;
                        }
                        if (isDragging && dragTargetParent != null)
                        {
                            // Perform the move when a drop target is available
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
            if (isDragging && dragTargetParent != null)
            {
                // If we're hovering over a submenu target, highlight it with a dark blue outline
                if (dragTargetIsSubmenu && dragTargetParent == item)
                {
                    // Increase outline thickness by 50% (was 4f -> now 6f)
                    DrawBorder(itemRect, COLOR_SUBMENU_HOVER_OUTLINE, 6f);
                }
                else if (IsValidDropTarget(item))
                {
                    Color dropColor = new Color(0f, 1f, 0f, 0.8f); // Green for generic drop targets
                    DrawBorder(itemRect, dropColor, 2f);
                }
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

            // If we're hovering a submenu target, do not draw the green insertion line
            // for the submenu item itself (we outline it in blue instead).
            if (dragTargetIsSubmenu && dragTargetParent == item)
                return;
            
            int itemIndex = currentMenuItems.IndexOf(item);
            if (itemIndex == -1) return;
            
            Color insertionColor = COLOR_INSERT_LINE; // Green at 50% opacity
            float lineWidth = 5f; // 25% thicker than previous 4f
            
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
            
            // If the current menu view is empty, allow dropping directly into the
            // current parent (this covers root / empty submenu cases).
            if (currentMenuItems.Count == 0)
            {
                dragTargetParent = currentParent;
                dragTargetIndex = 0; // insert as first child
                return;
            }
            
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

        }
        
        private bool IsValidDropTarget(MergedMenuItem item)
        {
            if (item == null) return false;
            if (IsItemReadOnly(item)) return false;
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
                // Debug.Log("[InitializeEditedMenuStructure] CALLED");

                // 通常のメニュー構造を取得
                var originalStructure = BuildMergedMenuStructure();
                if (originalStructure != null)
                {
                    // Debug.Log($"[InitializeEditedMenuStructure] Original structure has {originalStructure.children?.Count ?? 0} children");
                    // if (originalStructure.children != null)
                    // {
                    //     foreach (var child in originalStructure.children)
                    //     {
                    //         Debug.Log($"[InitializeEditedMenuStructure] Original child: name={child.name}, source={child.source}");
                    //     }
                    // }

                    // 「メニュー項目」配下のアイテムのみをフィルタリング
                    editedMenuStructure = FilterMenuStructureByMenuItemRoot(originalStructure);

                    // Debug.Log($"[InitializeEditedMenuStructure] Filtered structure has {editedMenuStructure?.children?.Count ?? 0} children");
                    // if (editedMenuStructure?.children != null)
                    // {
                    //     foreach (var child in editedMenuStructure.children)
                    //     {
                    //         Debug.Log($"[InitializeEditedMenuStructure] Filtered child: name={child.name}, source={child.source}");
                    //     }
                    // }

                    // Reset navigation stack when entering edit mode
                    menuNavigationStack.Clear();

                    // Debug.Log($"[InitializeEditedMenuStructure] editedMenuStructure assigned, value is {(editedMenuStructure == null ? "NULL" : "NOT NULL")}");
                    // Debug.Log("Edit mode initialized with filtered menu structure from MenuItemRoot");
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

        /// <summary>
        /// 通常のメニュー構造を「メニュー項目」配下のアイテムのみにフィルタリング
        /// </summary>
        private MergedMenuItem FilterMenuStructureByMenuItemRoot(MergedMenuItem originalStructure)
        {
            if (selectedAvatar == null || originalStructure == null)
                return null;

            // 「メニュー項目」GameObjectを探す
            var menuItemRoot = FindMenuItemRoot(selectedAvatar);
            if (menuItemRoot == null)
                return null;

            // メニュー項目配下の直接の子GameObjectの名前を収集
            var menuItemNames = new System.Collections.Generic.HashSet<string>();
            // Debug.Log($"[FilterMenuStructureByMenuItemRoot] MenuItemRoot children count: {menuItemRoot.transform.childCount}");
            foreach (Transform childTransform in menuItemRoot.transform)
            {
                menuItemNames.Add(childTransform.gameObject.name);
                // Debug.Log($"[FilterMenuStructureByMenuItemRoot] Found MenuItemRoot child: {childTransform.gameObject.name}");
            }

            // フィルタリング済みのルートを作成
            var filteredRoot = new MergedMenuItem
            {
                name = originalStructure.name,
                source = originalStructure.source,
                control = originalStructure.control,
                subMenu = originalStructure.subMenu
            };

            // 元の子を走査して、メニュー項目に含まれるものをコピー
            if (originalStructure.children != null)
            {
                // Debug.Log($"[FilterMenuStructureByMenuItemRoot] Original structure has {originalStructure.children.Count} children");
                foreach (var child in originalStructure.children)
                {
                    // Debug.Log($"[FilterMenuStructureByMenuItemRoot] Checking child: name={child.name}, source={child.source}, contained={menuItemNames.Contains(child.name)}");

                    if (menuItemNames.Contains(child.name))
                    {
                        // 深くコピーして、フィルタリング済み構造に追加
                        var copiedChild = CloneMenuStructure(child);
                        if (copiedChild != null)
                        {
                            // Debug.Log($"[FilterMenuStructureByMenuItemRoot] Filtered child: name={copiedChild.name}, source={copiedChild.source}");

                            // 除外項目（MA_InstallTarget）のサブメニューは削除
                            if (copiedChild.source == "MA_InstallTarget")
                            {
                                // Debug.Log($"[FilterMenuStructureByMenuItemRoot] Removing submenu from excluded item: {copiedChild.name}");
                                copiedChild.subMenu = null;
                                copiedChild.children.Clear();
                            }

                            filteredRoot.children.Add(copiedChild);
                        }
                    }
                }
            }

            // Debug.Log($"[FilterMenuStructureByMenuItemRoot] Final filtered structure has {filteredRoot.children.Count} children");
            return filteredRoot;
        }

        /// <summary>
        /// メニュー項目配下のサブメニュー構造を処理
        /// </summary>
        private void BuildMenuItemsFromMenuItemRoot(Transform menuItemRoot, VRCExpressionsMenu subMenu, MergedMenuItem parentItem)
        {
            if (subMenu == null || parentItem == null)
                return;

            // Debug.Log($"[BuildMenuItemsFromMenuItemRoot] Processing submenu for {parentItem.name}, controls count: {subMenu.controls?.Count ?? 0}");

            // 非編集モード時と同様に、VRCメニュー構造をそのまま処理
            BuildMenuItemsFromVRCMenu(subMenu, parentItem, "MA_MenuItem");

            // Debug.Log($"[BuildMenuItemsFromMenuItemRoot] After BuildMenuItemsFromVRCMenu, parentItem has {parentItem.children.Count} children");
        }

        /// <summary>
        /// Build merged menu children by scanning a container GameObject's direct children.
        /// This enables GameObject-backed submenus (no VRCExpressionsMenu asset required).
        /// </summary>
        private void BuildMenuItemsFromGameObjectChildren(Transform container, MergedMenuItem parentItem)
        {
            if (container == null || parentItem == null) return;

            var menuItemType = GetModularAvatarMenuItemType();
            var installTargetType = GetModularAvatarMenuInstallTargetType();

            foreach (Transform child in container)
            {
                if (child == null || child.gameObject == null) continue;
                var childGO = child.gameObject;

                VRCExpressionsMenu.Control control = null;
                Component sourceComponent = null;
                string source = string.Empty;

                // Installer / install-target based (excluded) menu items
                if (installTargetType != null)
                {
                    var menuInstallTarget = childGO.GetComponent(installTargetType);
                    if (menuInstallTarget != null)
                    {
                        var installerField = installTargetType.GetField("installer");
                        if (installerField != null)
                        {
                            var installer = installerField.GetValue(menuInstallTarget) as Component;
                            if (installer != null && menuItemType != null)
                            {
                                var menuItemComponent = installer.gameObject.GetComponent(menuItemType);
                                if (menuItemComponent != null)
                                {
                                    control = ExtractControlFromSourceComponent(menuItemComponent);
                                    sourceComponent = menuInstallTarget;
                                    source = "MA_InstallTarget";
                                }
                            }
                        }
                    }
                }

                // MA MenuItem on the GameObject itself
                if (control == null && menuItemType != null)
                {
                    var menuItemComponent = childGO.GetComponent(menuItemType);
                    if (menuItemComponent != null)
                    {
                        control = ExtractControlFromSourceComponent(menuItemComponent);
                        sourceComponent = menuItemComponent;
                        source = "MA_MenuItem";
                    }
                }

                // Skip if we couldn't extract any control information
                if (control == null)
                    continue;

                var menuItem = new MergedMenuItem
                {
                    name = string.IsNullOrEmpty(control.name) ? GetLocalizedText("無名コントロール", "Unnamed Control") : control.name,
                    source = source,
                    control = control,
                    subMenu = control.subMenu,
                    sourceComponent = sourceComponent,
                    children = new List<MergedMenuItem>()
                };

                parentItem.children.Add(menuItem);

                // If this control references a VRCExpressionsMenu asset, use that
                if (control.subMenu != null)
                {
                    if (source == "MA_MenuItem")
                        BuildMenuItemsFromMenuItemRoot(GetMenuItemRootTransform(), control.subMenu, menuItem);
                    else
                        BuildMenuItemsFromVRCMenu(control.subMenu, menuItem, source);
                }
                else if (child.childCount > 0)
                {
                    // Otherwise, if this GameObject itself has children, treat them as the submenu
                    BuildMenuItemsFromGameObjectChildren(child, menuItem);
                }
            }
        }

        /// <summary>
        /// 「メニュー項目」GameObject配下からのみメニュー構造を構築
        /// </summary>
        private MergedMenuItem BuildMenuStructureFromMenuItemRoot()
        {
            try
            {
                if (selectedAvatar == null)
                {
                    LogDetail("BuildMenuStructureFromMenuItemRoot: selectedAvatar is null");
                    // Debug.Log("[BuildMenuStructureFromMenuItemRoot] selectedAvatar is null");
                    return null;
                }

                // 「メニュー項目」Gameobjectを探す
                var menuItemRoot = FindMenuItemRoot(selectedAvatar);
                if (menuItemRoot == null)
                {
                    LogDetail("BuildMenuStructureFromMenuItemRoot: MenuItemRoot not found");
                    // Debug.Log("[BuildMenuStructureFromMenuItemRoot] MenuItemRoot not found");
                    return null;
                }

                // Debug.Log($"[BuildMenuStructureFromMenuItemRoot] MenuItemRoot found: {menuItemRoot.name}");

                // メニュー項目ルート用のMergedMenuItemを作成
                var rootItem = new MergedMenuItem
                {
                    name = GetLocalizedText("ルートメニュー", "Root Menu"),
                    source = "MenuItemRoot"
                };

                // メニュー項目配下の直接の子GameObjectを処理
                int childCount = 0;
                var menuItemType = GetModularAvatarMenuItemType();
                var installTargetType = GetModularAvatarMenuInstallTargetType();

                foreach (Transform childTransform in menuItemRoot.transform)
                {
                    childCount++;
                    var childGO = childTransform.gameObject;
                    // Debug.Log($"[BuildMenuStructureFromMenuItemRoot] Processing child {childCount}: {childGO.name}");

                    VRCExpressionsMenu.Control control = null;
                    Component sourceComponent = null;
                    string source = "";

                    // 1. Menu Install Targetを持つ場合（除外項目）
                    if (installTargetType != null)
                    {
                        var menuInstallTarget = childGO.GetComponent(installTargetType);
                        if (menuInstallTarget != null)
                        {
                            // Debug.Log($"[BuildMenuStructureFromMenuItemRoot] {childGO.name} has Menu Install Target");

                            // Menu Install TargetからinstallerフィールドでMenu Installerを取得
                            var installTargetTypeInstance = menuInstallTarget.GetType();
                            var installerField = installTargetTypeInstance.GetField("installer");
                            if (installerField != null)
                            {
                                var installer = installerField.GetValue(menuInstallTarget) as Component;
                                if (installer != null)
                                {
                                    // InstallerのGameObjectからMenu ItemコンポーネントでControl情報を取得
                                    var menuItemComponent = installer.gameObject.GetComponent(menuItemType);
                                    if (menuItemComponent != null)
                                    {
                                        control = ExtractControlFromSourceComponent(menuItemComponent);
                                        sourceComponent = menuInstallTarget;
                                        source = "MA_InstallTarget";
                                    }
                                }
                            }
                        }
                    }

                    // 2. Menu Itemコンポーネントを持つ場合（非除外項目）
                    if (control == null && menuItemType != null)
                    {
                        var menuItemComponent = childGO.GetComponent(menuItemType);
                        if (menuItemComponent != null)
                        {
                            // Debug.Log($"[BuildMenuStructureFromMenuItemRoot] {childGO.name} has Menu Item component");
                            control = ExtractControlFromSourceComponent(menuItemComponent);
                            sourceComponent = menuItemComponent;
                            source = "MA_MenuItem";
                        }
                    }

                    // 3. Control情報を取得できない場合はスキップ
                    if (control == null)
                    {
                        // Debug.Log($"[BuildMenuStructureFromMenuItemRoot] {childGO.name} - no control extracted");
                        continue;
                    }

                    // Debug.Log($"[BuildMenuStructureFromMenuItemRoot] Extracted control: {control.name}");

                    var menuItem = new MergedMenuItem
                    {
                        name = string.IsNullOrEmpty(control.name) ? GetLocalizedText("無名コントロール", "Unnamed Control") : control.name,
                        source = source,
                        control = control,
                        subMenu = control.subMenu,
                        sourceComponent = sourceComponent
                    };

                    rootItem.children.Add(menuItem);

                    // サブメニューを再帰的に処理
                    if (control.subMenu != null)
                    {
                        // If the control references an ExpressionMenu asset, behave as before
                        if (source == "MA_MenuItem")
                        {
                            BuildMenuItemsFromMenuItemRoot(menuItemRoot, control.subMenu, menuItem);
                        }
                        else
                        {
                            BuildMenuItemsFromVRCMenu(control.subMenu, menuItem, source);
                        }
                    }
                    else
                    {
                        // If this MenuItem GameObject has child GameObjects, treat them as
                        // the submenu contents (GameObject-backed submenu) so that submenus
                        // can be represented without ExpressionMenu assets.
                        if (childGO.transform.childCount > 0)
                        {
                            // Populate children by scanning this GameObject's direct children
                            BuildMenuItemsFromGameObjectChildren(childGO.transform, menuItem);
                        }
                        else
                        {
                            // Debug.Log($"[BuildMenuStructureFromMenuItemRoot] {menuItem.name} has NO submenu");
                        }
                    }
                }

                // Debug.Log($"[BuildMenuStructureFromMenuItemRoot] Built structure with {rootItem.children.Count} root children");
                LogDetail($"BuildMenuStructureFromMenuItemRoot: Built structure with {rootItem.children.Count} root children");

                // Ensure orphan MA items outside the MenuItem root are included
                IncludeOrphanMAItems(rootItem);
                return rootItem;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BuildMenuStructureFromMenuItemRoot] Exception: {e.Message}\n{e.StackTrace}");
                LogDetail($"BuildMenuStructureFromMenuItemRoot: Exception - {e.Message}");
                return null;
            }
        }
        
        private void MoveItemToNewLocation(MergedMenuItem item, MergedMenuItem newParent, int newIndex)
        {
            if (IsItemReadOnly(item) || (newParent != null && IsItemReadOnly(newParent)))
            {
                ShowReadOnlyItemWarning();
                return;
            }

            // 禁止ルール: 除外項目（ExprMenuVisualizerExcluded が付いた項目）の上へ移動することは禁止
            if (newParent != null && IsExcludedItem(newParent))
            {
                // ユーザーに即座に警告を表示し、操作を取り消す
                EditorUtility.DisplayDialog(
                    GetLocalizedText("移動禁止", "Move Not Allowed"),
                    GetLocalizedText("除外した項目の中には移動できません。", "Cannot move items into an excluded item's subtree."),
                    GetLocalizedText("OK", "OK")
                );
                return;
            }

            // 除外項目の移動制限：Menu Installerより下の階層には移動不可
            if (IsExcludedItem(item) && newParent != null && IsMenuInstallerSubmenu(newParent))
            {
                ShowNotification(new GUIContent(GetLocalizedText(
                    "除外項目はMenu Installerのサブメニュー内には移動できません。",
                    "Excluded items cannot be moved into Menu Installer submenus.")));
                return;
            }

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

            if (IsItemReadOnly(item))
            {
                ShowReadOnlyItemWarning();
                return;
            }
            
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

            // fullPath ベースで項目を検索（同名項目を正確に区別）
            var editedItem = FindItemByPath(editedMenuStructure, item.fullPath);
            if (editedItem != null)
            {
                RemoveItemFromParent(editedMenuStructure, editedItem);

                // Remove from selection if selected
                selectedItems.Remove(item);

                // Update current view
                UpdateCurrentMenuItems();

            // Debug.Log(GetLocalizedText(
            //                     $"アイテム '{item.name}' を削除しました",
            //                     $"Deleted item '{item.name}'"
            //                 ));

                Repaint();
            }
            // If we're in edit mode also remove corresponding GameObject in the Hierarchy immediately
            // NOTE: do NOT delete the original sourceComponent GameObject for excluded MA items.
            if (editMode && item != null)
            {
                var menuRoot = GetMenuItemRootTransform();
                GameObject goToDelete = null;

                // Prefer an existing Menu Install Target object under メニュー項目
                goToDelete = FindExistingMenuInstallTargetGameObject(item);

                // If not found, try to resolve a GameObject under the MenuItem root by name
                if (goToDelete == null && menuRoot != null)
                {
                    foreach (Transform t in menuRoot)
                    {
                        if (t == null || t.gameObject == null) continue;
                        if (string.Equals(t.gameObject.name, item.name, StringComparison.Ordinal))
                        {
                            goToDelete = t.gameObject;
                            break;
                        }
                    }
                }

                // Destroy only the found GameObject under メニュー項目 (do NOT touch original MA source objects)
                if (goToDelete != null)
                {
                    Undo.DestroyObjectImmediate(goToDelete);
                    EditorUtility.SetDirty(selectedAvatar.gameObject);
                }
            }
        }

        /// <summary>
        /// Try to resolve a GameObject that corresponds to a merged menu item inside the MenuItem root.
        /// Falls back to searching children by name when no sourceComponent is present.
        /// </summary>
        private GameObject ResolveGameObjectForMergedItem(MergedMenuItem item)
        {
            if (selectedAvatar == null || item == null) return null;

            if (item.sourceComponent != null)
            {
                // If the merged item represents an excluded source (installed via a Menu Install Target),
                // prefer the install-target GameObject that exists under the avatar's "メニュー項目" root
                // (we do not want to modify the original source GameObject during edit mode).
                try
                {
                    var possibleInstallTarget = FindExistingMenuInstallTargetGameObject(item);
                    if (possibleInstallTarget != null)
                        return possibleInstallTarget;
                }
                catch { /* ignore */ }

                // If we're in edit mode we must not modify the original avatar GameObject;
                // prefer the menu-root result above and only fall back to the original source when not editing.
                if (!editMode)
                {
                    try { return item.sourceComponent.gameObject; } catch { }
                }
            }

            // Fallback: try to find a child under メニュー項目 with matching name
            var root = FindMenuItemRoot(selectedAvatar);
            if (root == null) return null;

            // Search recursively under the MenuItem root (covers nested submenu GameObjects)
            var allTransforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                // skip the root container itself - we only want real menu item GameObjects
                if (t == root) continue;
                if (t == null || t.gameObject == null) continue;
                if (string.Equals(t.gameObject.name, item.name, StringComparison.Ordinal))
                    return t.gameObject;
            }

            return null;
        }

        private Transform GetMenuItemRootTransform()
        {
            return FindMenuItemRoot(selectedAvatar);
        }

        private void TryCreateMenuItemGameObjectForSubmenu(MergedMenuItem newSubmenu, MergedMenuItem parentMenu)
        {
            if (!editMode) return;
            var root = GetMenuItemRootTransform();
            if (root == null) return;

            // Determine target parent transform
            Transform parentTransform = root;
            if (parentMenu != null && parentMenu != editedMenuStructure)
            {
                var parentGO = ResolveGameObjectForMergedItem(parentMenu);
                if (parentGO != null) parentTransform = parentGO.transform;
            }

            // If the chosen parent is an install-target GameObject, do not parent under it; use the root instead.
            if (parentTransform != root && IsInstallTargetGameObject(parentTransform.gameObject))
            {
                parentTransform = root;
            }

            // Create a visible GameObject representing the submenu under the MenuItem root
            string goName = string.IsNullOrWhiteSpace(newSubmenu.name) ? "Submenu" : newSubmenu.name;
            var newGO = new GameObject(goName);
            EnsureAddComponent<ExprMenuVisualizerGenerated>(newGO);

            // Attach GeneratedMetadata so fullPath-based lookup can find this submenu
            try
            {
                var meta = EnsureAddComponent<ExprMenuVisualizerGeneratedMetadata>(newGO);
                if (meta != null)
                {
                    try { meta.fullPath = newSubmenu.fullPath ?? newSubmenu.name; } catch { }
                    EditorUtility.SetDirty(meta);
                }
            }
            catch { }

            // Attach GUID-based generated marker for robust identification across sessions
            try
            {
                var guidComp = EnsureAddComponent(newGO, typeof(VRCExpressionMenuVisualizer.ExprMenuVisualizerGeneratedGuid)) as Component;
                if (guidComp != null)
                {
                    var guidField = guidComp.GetType().GetField("generatedGuid");
                    var pathField = guidComp.GetType().GetField("originalMenuPath");
                    var installNameField = guidComp.GetType().GetField("installTargetName");
                    if (guidField != null && string.IsNullOrEmpty(guidField.GetValue(guidComp) as string))
                    {
                        guidField.SetValue(guidComp, System.Guid.NewGuid().ToString("N"));
                    }
                    if (pathField != null)
                    {
                        try { pathField.SetValue(guidComp, newSubmenu.fullPath ?? newSubmenu.name); } catch { }
                    }
                    if (installNameField != null)
                    {
                        try { installNameField.SetValue(guidComp, newSubmenu.name ?? string.Empty); } catch { }
                    }
                    EditorUtility.SetDirty(guidComp);
                }
            }
            catch { }

            // Add a MA menu item component if available and wire the control to submenu asset
            var menuItemType = GetModularAvatarMenuItemType();
            if (menuItemType != null)
            {
                var comp = EnsureAddComponent(newGO, menuItemType) as Component;
                if (comp != null)
                {
                    // Populate the Modular Avatar menu item Control with type=SubMenu
                    // and an empty parameter to allow auto-parameter behavior in MA.
                    try
                    {
                        var controlField = menuItemType.GetField("Control");
                        if (controlField != null)
                        {
                            var control = new VRCExpressionsMenu.Control
                            {
                                name = newSubmenu.name,
                                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                                subMenu = null,
                                parameter = new VRCExpressionsMenu.Control.Parameter { name = string.Empty }
                            };
                            controlField.SetValue(comp, control);
                        }

                        // Make sure the Menu Item component is configured to use children as
                        // the submenu source rather than referencing a VRCExpressionsMenu asset.
                        var menuSourceField = menuItemType.GetField("MenuSource");
                        if (menuSourceField != null)
                        {
                            try
                            {
                                var enumValue = Enum.Parse(menuSourceField.FieldType, "Children");
                                menuSourceField.SetValue(comp, enumValue);
                            }
                            catch
                            {
                                // Ignore if the enum doesn't exist in this MA implementation
                            }
                        }

                        // Clear any explicit "other object children" assignment so the
                        // component will rely on actual child GameObjects.
                        var otherChildrenField = menuItemType.GetField("menuSource_otherObjectChildren", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (otherChildrenField != null)
                        {
                            otherChildrenField.SetValue(comp, null);
                        }

                        // Also set a friendly label on the component if available
                        var labelField = menuItemType.GetField("label", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        labelField?.SetValue(comp, newSubmenu.name);

                        // Attempt to set any boolean "auto parameter" style field to true
                        // (Modular Avatar implementations vary; we search common-looking fields).
                        var fields = menuItemType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        foreach (var f in fields)
                        {
                            if ((f.FieldType == typeof(bool) || f.FieldType == typeof(Boolean)) &&
                                f.Name.IndexOf("auto", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                f.Name.IndexOf("param", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                f.SetValue(comp, true);
                            }
                        }
                        // Also check properties
                        var props = menuItemType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        foreach (var p in props)
                        {
                            if (!p.CanWrite) continue;
                            if ((p.PropertyType == typeof(bool) || p.PropertyType == typeof(Boolean)) &&
                                p.Name.IndexOf("auto", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                p.Name.IndexOf("param", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                p.SetValue(comp, true, null);
                            }
                        }
                    }
                    catch
                    {
                        // Non-critical: if reflection fails, just continue without auto-flagging.
                    }
                }
            }

            // Register creation in Undo so Reset (Undo.RevertAllDownToGroup) can return to snapshot
            Undo.RegisterCreatedObjectUndo(newGO, "Create submenu gameobject in edit mode");
            // If we added a menuItem component, set the merged menu item's sourceComponent for future mapping
            if (menuItemType != null)
            {
                newSubmenu.sourceComponent = newGO.GetComponent(menuItemType) as Component;
            }
            else
            {
                // Prefer the GUID marker component if present, otherwise fall back to the legacy generated marker
                var guidComp = newGO.GetComponent<ExprMenuVisualizerGeneratedGuid>();
                if (guidComp != null)
                {
                    newSubmenu.sourceComponent = guidComp;
                }
                else
                {
                    newSubmenu.sourceComponent = newGO.GetComponent<ExprMenuVisualizerGenerated>();
                }
            }
            Undo.SetTransformParent(newGO.transform, parentTransform, "Attach submenu gameobject");

            EditorUtility.SetDirty(newGO);

            // Mark the menu structure dirty so the next BuildMergedMenuStructure call
            // will rebuild from the current scene hierarchy. Without this, the
            // cached merged menu structure may be returned and the freshly created
            // GameObject won't be picked up until a later refresh — this is the
            // root cause of needing multiple reloads to see newly created submenus.
            MarkMenuStructureDirty();

            // Refresh the edited structure so the window reflects the newly created
            // GameObject submenu immediately. This rebuilds the editedMenuStructure
            // (which reads from the "メニュー項目" root), updates the current view
            // and repaints the editor window.
            try
            {
                InitializeEditedMenuStructure();
                UpdateCurrentMenuItems();
                Repaint();
            }
            catch
            {
                // Non-critical: if refresh fails, the UI will still be repainted above
            }
        }

        private void SetModularAvatarMenuItemLabel(Component component, string newLabel)
        {
            var menuItemType = GetModularAvatarMenuItemType();
            if (component == null || menuItemType == null || !menuItemType.IsInstanceOfType(component)) return;

            var labelField = menuItemType.GetField("label", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            labelField?.SetValue(component, newLabel ?? string.Empty);
        }

        private void MarkComponentDirty(Component component)
        {
            if (component == null) return;
            EditorUtility.SetDirty(component);
            if (component.gameObject != null)
            {
                EditorUtility.SetDirty(component.gameObject);
            }
        }

        // Safe AddComponent helper that attempts Undo.AddComponent and falls back to direct AddComponent
        // if necessary. Also returns an existing component if already present.
        private T EnsureAddComponent<T>(GameObject go) where T : Component
        {
            if (go == null) return null;
            var existing = go.GetComponent<T>();
            if (existing != null) return existing;
            T comp = null;
            try
            {
                comp = Undo.AddComponent<T>(go);
            }
            catch { }
            if (comp == null)
            {
                try
                {
                    comp = go.AddComponent<T>();
                    Debug.LogWarning($"Fallback: added component '{typeof(T).Name}' via AddComponent on '{go.name}' because Undo.AddComponent returned null.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Failed to add component '{typeof(T).Name}' to '{go.name}': {ex.Message}");
                }
            }
            return comp;
        }

        private Component EnsureAddComponent(GameObject go, System.Type type)
        {
            if (go == null || type == null) return null;
            var existing = go.GetComponent(type);
            if (existing != null) return existing as Component;
            Component comp = null;
            try
            {
                comp = Undo.AddComponent(go, type) as Component;
            }
            catch { }
            if (comp == null)
            {
                try
                {
                    comp = go.AddComponent(type) as Component;
                    Debug.LogWarning($"Fallback: added component '{type.Name}' via AddComponent on '{go.name}' because Undo.AddComponent returned null.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Failed to add component '{type.Name}' to '{go.name}': {ex.Message}");
                }
            }
            return comp;
        }

        // Return true if the given GameObject has a Modular Avatar 'Menu Install Target' component.
        private bool IsInstallTargetGameObject(GameObject go)
        {
            if (go == null) return false;
            var installTargetType = GetModularAvatarMenuInstallTargetType();
            if (installTargetType == null) return false;
            try
            {
                return go.GetComponent(installTargetType) != null;
            }
            catch { return false; }
        }

        private Component FindCurrentInstallTarget(Component component)
        {
            var installTargetType = GetModularAvatarMenuInstallTargetType();
            if (component == null || installTargetType == null) return null;

            var targets = component.GetComponentsInParent(installTargetType, true) as Component[];
            if (targets != null && targets.Length > 0)
            {
                return targets[0];
            }

            return null;
        }

        private void ShowInstallTargetSelectionMenu(Component menuItem, Component currentTarget)
        {
            if (menuItem == null) return;

            var menu = new GenericMenu();

            menu.AddItem(new GUIContent(GetLocalizedText("Install Targetなし (変更しない)", "No Install Target (keep current)")), currentTarget == null,
                () => { /* no-op */ });

            foreach (var target in modularAvatarInstallTargets)
            {
                if (target == null) continue;
                string label = target.gameObject != null ? target.gameObject.name : target.name;
                menu.AddItem(new GUIContent(label), target == currentTarget, () => AssignMenuItemToInstallTarget(menuItem, target));
            }

            menu.ShowAsContext();
        }

        private void AssignMenuItemToInstallTarget(Component menuItem, Component installTarget)
        {
            if (menuItem == null) return;

            var newParent = installTarget != null ? installTarget.transform : menuItem.transform.parent;
            if (newParent == null) return;

            Undo.RecordObject(menuItem.transform, "Change Install Target");
            menuItem.transform.SetParent(newParent, false);
            MarkComponentDirty(menuItem);

            // Debug.Log(GetLocalizedText(
            //                 $"'{menuItem.gameObject.name}' のInstall Targetを更新しました。",
            //                 $"Updated install target for '{menuItem.gameObject.name}'."
            //             ));

            Repaint();
        }

        private bool IsModularAvatarMenuItem(MergedMenuItem item)
        {
            var menuItemType = GetModularAvatarMenuItemType();
            return item?.sourceComponent != null && menuItemType != null && menuItemType.IsInstanceOfType(item.sourceComponent);
        }

        private bool IsItemReadOnly(MergedMenuItem item)
        {
            if (item == null)
                return true;

            // 編集モード時は、すべてのアイテムが編集可能（除外項目と非除外項目を同じ扱い）
            if (editMode)
            {
                return false;
            }

            // 非編集モード時は isReadOnly フラグに従う
            return item.isReadOnly;
        }

        /// <summary>
        /// アイテムが除外項目（ExprMenuVisualizerExcludedマーカー付き）かどうかを判定
        /// </summary>
        private bool IsExcludedItem(MergedMenuItem item)
        {
            if (item == null || item.sourceComponent == null)
                return false;

            return item.sourceComponent.GetComponent<ExprMenuVisualizerExcluded>() != null;
        }

        /// <summary>
        /// 指定されたアイテムがMenu Installerのサブメニュー内にあるかどうかを判定
        /// </summary>
        private bool IsMenuInstallerSubmenu(MergedMenuItem item)
        {
            if (item == null || editedMenuStructure == null)
                return false;

            // ツリーをたどって、item の親がMenu Installerのsource を持つかどうかを確認
            return IsMenuInstallerSubmenuRecursive(editedMenuStructure, item);
        }

        /// <summary>
        /// 再帰的にMenu Installerのサブメニュー判定を行う
        /// </summary>
        private bool IsMenuInstallerSubmenuRecursive(MergedMenuItem current, MergedMenuItem target)
        {
            if (current == null || current.children == null)
                return false;

            // 直接の子の場合、currentがMenu Installerかどうか確認
            if (current.children.Contains(target))
            {
                // currentがMenu Installerのsourceを持つ場合、targetはそのサブメニュー
                return current.source == "MA_Installer";
            }

            // 再帰的に子を検索
            foreach (var child in current.children)
            {
                if (IsMenuInstallerSubmenuRecursive(child, target))
                    return true;
            }

            return false;
        }

        private void ShowReadOnlyItemWarning()
        {
            // 編集モード時は警告を表示しない
            if (editMode)
                return;

            ShowNotification(new GUIContent(GetLocalizedText(
                "この項目は参照専用です。変換時に除外されているため編集できません。",
                "This entry is read-only because it was excluded during conversion.")));
        }

        private void DrawModularAvatarControlExtras()
        {
            if (editingSourceComponent == null) return;

            EditorGUILayout.Space();

            if (editingSourceComponent.gameObject != null)
            {
                if (GUILayout.Button(GetLocalizedText("関連GameObjectを選択", "Select Related GameObject")))
                {
                    Selection.activeGameObject = editingSourceComponent.gameObject;
                    EditorGUIUtility.PingObject(editingSourceComponent.gameObject);
                }
            }
        }
        
        private void MoveItemToSubmenu(MergedMenuItem item, MergedMenuItem submenuParent, int newIndex)
        {
            if (IsItemReadOnly(item) || (submenuParent != null && IsItemReadOnly(submenuParent)))
            {
                ShowReadOnlyItemWarning();
                return;
            }

            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }

            // fullPath ベースで項目を検索（同名項目を正確に区別）
            var editedItem = FindItemByPath(editedMenuStructure, item.fullPath);
            var editedSubmenuParent = FindItemByPath(editedMenuStructure, submenuParent.fullPath);

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

                // Also update actual scene Hierarchy in edit mode
                if (editMode)
                {
                    var go = ResolveGameObjectForMergedItem(item);
                    Transform targetParent = ResolveGameObjectForMergedItem(submenuParent)?.transform ?? GetMenuItemRootTransform();
                    if (go != null && targetParent != null)
                    {
                        Undo.SetTransformParent(go.transform, targetParent, "Move menu item");
                        Undo.RecordObject(go.transform, "Change sibling index");
                        go.transform.SetSiblingIndex(Mathf.Clamp(newIndex, 0, targetParent.childCount));
                        EditorUtility.SetDirty(go);
                    }
                }
            }

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
                
                // Recompute fullPath values in the edited structure and sync GeneratedMetadata
                try
                {
                    AssignMenuPaths(editedMenuStructure);
                    UpdateGeneratedMetadataForTree(editedMenuStructure);
                }
                catch { }
            // Debug.Log(GetLocalizedText(
            //                     $"アイテム '{item.name}' をサブメニュー '{submenuParent.name}' に移動しました",
            //                     $"Moved item '{item.name}' into submenu '{submenuParent.name}'"
            //                 ));
            }
        }
        
        private void MoveItemToParentLevel(MergedMenuItem item, MergedMenuItem parentMenu, int newIndex)
        {
            if (IsItemReadOnly(item) || (parentMenu != null && IsItemReadOnly(parentMenu)))
            {
                ShowReadOnlyItemWarning();
                return;
            }
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // fullPath ベースで項目を検索（同名項目を正確に区別）
            var editedItem = FindItemByPath(editedMenuStructure, item.fullPath);
            // If parentMenu represents the editedMenuStructure (root) or has no fullPath,
            // treat the editedMenuStructure as the destination. Otherwise try to resolve
            // by fullPath in the edited structure.
            MergedMenuItem editedParentMenu;
            if (parentMenu == null || editedMenuStructure == parentMenu || string.IsNullOrEmpty(parentMenu.fullPath))
            {
                editedParentMenu = editedMenuStructure;
            }
            else
            {
                editedParentMenu = FindItemByPath(editedMenuStructure, parentMenu.fullPath);
            }

            if (editedItem != null && editedParentMenu != null)
            {
                // Remove from current location
                RemoveItemFromParent(editedMenuStructure, editedItem);
                
                // Ensure parent has children list
                if (editedParentMenu.children == null)
                    editedParentMenu.children = new List<MergedMenuItem>();
                
                // Add to parent level
                editedParentMenu.children.Insert(Math.Min(newIndex, editedParentMenu.children.Count), editedItem);
                
                // Also update actual scene Hierarchy in edit mode (move GameObject up to parentMenu)
                if (editMode)
                {
                    var go = ResolveGameObjectForMergedItem(item);
                    Transform targetParent = ResolveGameObjectForMergedItem(parentMenu)?.transform ?? GetMenuItemRootTransform();

                    // Do not allow parenting under an install-target GameObject; fall back to the menu root
                    if (targetParent != null && targetParent != GetMenuItemRootTransform() && IsInstallTargetGameObject(targetParent.gameObject))
                    {
                        targetParent = GetMenuItemRootTransform();
                    }

                    // If resolve couldn't find the scene object for the item, try one more time
                    // by checking the sourceComponent first (if it's already under the menu root)
                    // and then searching the MenuItem root recursively (excluding the root container itself).
                    if (go == null && editMode)
                    {
                        var root = GetMenuItemRootTransform();
                        if (root != null)
                        {
                            // If the merged item has a sourceComponent whose GameObject is already
                            // under the MenuItem root, prefer that GameObject directly (handles
                            // the common case where a menu item exists as a child of the root).
                            try
                            {
                                if (item.sourceComponent != null)
                                {
                                    var srcGo = item.sourceComponent.gameObject;
                                    if (srcGo != null && srcGo.transform != null && srcGo.transform.IsChildOf(root))
                                    {
                                        go = srcGo;
                                    }
                                }
                            }
                            catch { }

                            var candidates = root.GetComponentsInChildren<Transform>(true);
                            foreach (var c in candidates)
                            {
                                if (c == null || c.gameObject == null) continue;
                                if (c == root) continue; // skip container
                                if (string.Equals(c.gameObject.name, item.name, StringComparison.Ordinal))
                                {
                                    go = c.gameObject;
                                    break;
                                }
                            }
                        }
                    }

                    if (go != null && targetParent != null)

                    // If moving to root but the "メニュー項目" root doesn't exist yet,
                    // create it on demand (in edit mode) so the GameObject can be parented there.
                    if (targetParent == null && editMode && selectedAvatar != null)
                    {
                        // Prefer an existing user-placed GeneratedRoot marker if available
                        GameObject rootObject = null;
                        try
                        {
                            var existingRootComp = selectedAvatar.GetComponentInChildren<ExprMenuVisualizerGeneratedRoot>(true);
                            if (existingRootComp != null)
                                rootObject = existingRootComp.gameObject;
                        }
                        catch { }

                        if (rootObject == null)
                        {
                            // Create a root container under the selected avatar so edit-mode operations
                            // have somewhere to place moved items immediately.
                            rootObject = new GameObject(GeneratedMenuRootName);
                            EnsureAddComponent<ExprMenuVisualizerGeneratedRoot>(rootObject);
                            EnsureAddComponent<ExprMenuVisualizerGenerated>(rootObject);
                            Undo.RegisterCreatedObjectUndo(rootObject, "Create MenuItem root for edit-mode move");
                            Undo.SetTransformParent(rootObject.transform, selectedAvatar.transform, "Attach MenuItem root");
                            rootObject.transform.localPosition = Vector3.zero;
                            rootObject.transform.localRotation = Quaternion.identity;
                            rootObject.transform.localScale = Vector3.one;
                            EditorUtility.SetDirty(rootObject);
                        }

                        targetParent = rootObject.transform;
                    }

                    if (go != null && targetParent != null)
                    {
                        Undo.SetTransformParent(go.transform, targetParent, "Move menu item");
                        Undo.RecordObject(go.transform, "Change sibling index");
                        go.transform.SetSiblingIndex(Mathf.Clamp(newIndex, 0, targetParent.childCount));
                        EditorUtility.SetDirty(go);
                    }
                }

                // Navigate back to parent level if we moved item up
                if (menuNavigationStack.Count > 0)
                {
                    menuNavigationStack.RemoveAt(menuNavigationStack.Count - 1);
                    UpdateCurrentMenuItems();
                }
                
            // Debug.Log(GetLocalizedText(
            //                     $"アイテム '{item.name}' を上位階層に移動しました",
            //                     $"Moved item '{item.name}' up to parent level"
            //                 ));
            }
        }
        
        private void MoveItemToNewHierarchy(MergedMenuItem item, MergedMenuItem newParent, int newIndex)
        {
            if (IsItemReadOnly(item) || (newParent != null && IsItemReadOnly(newParent)))
            {
                ShowReadOnlyItemWarning();
                return;
            }
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // fullPath ベースで項目を検索（同名項目を正確に区別）
            var editedItem = FindItemByPath(editedMenuStructure, item.fullPath);
            if (editedItem != null)
            {
                // Remove from current parent
                RemoveItemFromParent(editedMenuStructure, editedItem);

                // Add to new parent
                // Handle moving to root (editedMenuStructure) when newParent is null or
                // represents the editedMenuStructure. Don't rely solely on FindItemByPath
                // returning a match for the root because its fullPath may be empty.
                MergedMenuItem editedNewParent;
                if (newParent == null || editedMenuStructure == newParent || string.IsNullOrEmpty(newParent.fullPath))
                {
                    editedNewParent = editedMenuStructure;
                }
                else
                {
                    editedNewParent = FindItemByPath(editedMenuStructure, newParent.fullPath);
                }
                
                if (editedNewParent != null)
                {
                    if (editedNewParent.children == null)
                        editedNewParent.children = new List<MergedMenuItem>();
                    
                    editedNewParent.children.Insert(Math.Min(newIndex, editedNewParent.children.Count), editedItem);
                    UpdateCurrentMenuItems();

                    // If in edit mode, also move the corresponding GameObject in the scene
                    if (editMode)
                    {
                        var go = ResolveGameObjectForMergedItem(item);
                        Transform targetParent = ResolveGameObjectForMergedItem(newParent)?.transform ?? GetMenuItemRootTransform();

                        // Do not allow parenting under an install-target GameObject; fall back to the menu root
                        if (targetParent != null && targetParent != GetMenuItemRootTransform() && IsInstallTargetGameObject(targetParent.gameObject))
                        {
                            targetParent = GetMenuItemRootTransform();
                        }

                        // If targetParent is null and edit mode, create the root container on demand
                        if (targetParent == null && editMode && selectedAvatar != null)
                        {
                            GameObject rootObject = null;
                            try
                            {
                                var existingRootComp = selectedAvatar.GetComponentInChildren<ExprMenuVisualizerGeneratedRoot>(true);
                                if (existingRootComp != null)
                                    rootObject = existingRootComp.gameObject;
                            }
                            catch { }

                            if (rootObject == null)
                            {
                                rootObject = new GameObject(GeneratedMenuRootName);
                                EnsureAddComponent<ExprMenuVisualizerGeneratedRoot>(rootObject);
                                EnsureAddComponent<ExprMenuVisualizerGenerated>(rootObject);
                                Undo.RegisterCreatedObjectUndo(rootObject, "Create MenuItem root for edit-mode move");
                                Undo.SetTransformParent(rootObject.transform, selectedAvatar.transform, "Attach MenuItem root");
                                rootObject.transform.localPosition = Vector3.zero;
                                rootObject.transform.localRotation = Quaternion.identity;
                                rootObject.transform.localScale = Vector3.one;
                                EditorUtility.SetDirty(rootObject);
                            }

                            targetParent = rootObject.transform;
                        }

                        // Fallback search for GameObject if unresolved
                        if (go == null)
                        {
                            var root = GetMenuItemRootTransform();
                            if (root != null)
                            {
                                try
                                {
                                    if (item.sourceComponent != null)
                                    {
                                        var srcGo = item.sourceComponent.gameObject;
                                        if (srcGo != null && srcGo.transform != null && srcGo.transform.IsChildOf(root))
                                        {
                                            go = srcGo;
                                        }
                                    }
                                }
                                catch { }

                                if (go == null)
                                {
                                    var candidates = root.GetComponentsInChildren<Transform>(true);
                                    foreach (var c in candidates)
                                    {
                                        if (c == null || c.gameObject == null) continue;
                                        if (c == root) continue;
                                        if (string.Equals(c.gameObject.name, item.name, StringComparison.Ordinal))
                                        {
                                            go = c.gameObject;
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        if (go != null && targetParent != null)
                        {
                            Undo.SetTransformParent(go.transform, targetParent, "Move menu item");
                            Undo.RecordObject(go.transform, "Change sibling index");
                            go.transform.SetSiblingIndex(Mathf.Clamp(newIndex, 0, targetParent.childCount));
                            EditorUtility.SetDirty(go);
                        }
                    }
                    // Recompute fullPath values in the edited structure and sync GeneratedMetadata
                    try
                    {
                        AssignMenuPaths(editedMenuStructure);
                        UpdateGeneratedMetadataForTree(editedMenuStructure);
                    }
                    catch { }
                }

                // Recompute fullPath values in the edited structure and sync GeneratedMetadata
                try
                {
                    AssignMenuPaths(editedMenuStructure);
                    UpdateGeneratedMetadataForTree(editedMenuStructure);
                }
                catch { }
            }
        }
        
        private bool IsInCurrentMenu(MergedMenuItem item)
        {
            return currentMenuItems.Contains(item);
        }
        
        private void MoveItemWithinCurrentMenu(MergedMenuItem item, int newIndex)
        {
            if (IsItemReadOnly(item))
            {
                ShowReadOnlyItemWarning();
                return;
            }
            // Ensure we have an edited structure to work with
            if (editedMenuStructure == null)
            {
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }
            
            // Get the current menu context
            var currentMenu = GetCurrentEditedMenu();
            if (currentMenu?.children == null) return;

            // fullPath ベースで項目を検索（同名項目を正確に区別）
            var itemIndex = currentMenu.children.FindIndex(i => i.fullPath == item.fullPath);
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
            
            // If in edit mode, also reflect the reorder in the scene Hierarchy
            if (editMode)
            {
                var go = ResolveGameObjectForMergedItem(item);
                // Resolve target parent transform for the current menu
                Transform targetParent = ResolveGameObjectForMergedItem(currentMenu)?.transform ?? GetMenuItemRootTransform();

                // If we couldn't resolve the GameObject, try fallback search under the menu root
                if (go == null)
                {
                    var root = GetMenuItemRootTransform();
                    if (root != null)
                    {
                        try
                        {
                            if (item.sourceComponent != null)
                            {
                                var srcGo = item.sourceComponent.gameObject;
                                if (srcGo != null && srcGo.transform != null && srcGo.transform.IsChildOf(root))
                                {
                                    go = srcGo;
                                }
                            }
                        }
                        catch { }

                        if (go == null)
                        {
                            var candidates = root.GetComponentsInChildren<Transform>(true);
                            foreach (var c in candidates)
                            {
                                if (c == null || c.gameObject == null) continue;
                                if (c == root) continue; // skip container
                                if (string.Equals(c.gameObject.name, item.name, StringComparison.Ordinal))
                                {
                                    go = c.gameObject;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (go != null && targetParent != null)
                {
                    Undo.RecordObject(go.transform, "Change sibling index");
                    go.transform.SetSiblingIndex(Mathf.Clamp(newIndex, 0, targetParent.childCount));
                    EditorUtility.SetDirty(go);
                }
            }

            // Recompute fullPath values in the edited structure and sync GeneratedMetadata
            try
            {
                AssignMenuPaths(editedMenuStructure);
                UpdateGeneratedMetadataForTree(editedMenuStructure);
            }
            catch { }
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

        /// <summary>
        /// Walk the merged menu tree and update ExprMenuVisualizerGeneratedMetadata.fullPath
        /// for corresponding generated GameObjects so that metadata reflects the edited
        /// hierarchy after drag/drop moves.
        /// </summary>
        private void UpdateGeneratedMetadataForTree(MergedMenuItem root)
        {
            if (root == null) return;

            try
            {
                // Update this node
                var go = ResolveGameObjectForMergedItem(root);
                if (go != null)
                {
                    try
                    {
                        var meta = go.GetComponent<ExprMenuVisualizerGeneratedMetadata>() ?? EnsureAddComponent<ExprMenuVisualizerGeneratedMetadata>(go);
                        if (meta != null)
                        {
                            // Use Undo so edits are undo-able in the editor
                            Undo.RecordObject(meta, "Update GeneratedMetadata fullPath");
                            meta.fullPath = root.fullPath ?? string.Empty;
                            EditorUtility.SetDirty(meta);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (root.children == null) return;
            foreach (var child in root.children)
            {
                UpdateGeneratedMetadataForTree(child);
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
                originalIndex = original.originalIndex,
                fullPath = original.fullPath,
                isReadOnly = original.isReadOnly
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

        // fullPath（完全パス）ベースでメニュー項目を検索
        private MergedMenuItem FindItemByPath(MergedMenuItem root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path))
                return null;

            return FindItemByPathRecursive(root, path);
        }

        private MergedMenuItem FindItemByPathRecursive(MergedMenuItem current, string targetPath)
        {
            if (current == null)
                return null;

            // 完全パスが一致したら返す
            if (!string.IsNullOrEmpty(current.fullPath) && current.fullPath == targetPath)
                return current;

            // 子要素を再帰的に検索
            if (current.children != null)
            {
                foreach (var child in current.children)
                {
                    var found = FindItemByPathRecursive(child, targetPath);
                    if (found != null)
                        return found;
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
            LogDetail("########## SaveEditedMenuStructure: START ##########");
            if (selectedAvatar == null)
            {
                LogDetail("SaveEditedMenuStructure: No avatar selected");
                EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                    GetLocalizedText("アバターが選択されていません", "No avatar selected"), "OK");
                return;
            }

            if (awaitingExclusionSelection)
            {
                EditorUtility.DisplayDialog(GetLocalizedText("選択待ち", "Awaiting Selection"),
                    GetLocalizedText("除外するメニュー項目の選択が完了していません。別ウィンドウで選択を確定してください。",
                        "Exclusion selection is not finished yet. Please finish the selection in the other window."),
                    "OK");
                return;
            }

            LogDetail($"SaveEditedMenuStructure: Saving menu for avatar '{selectedAvatar.name}'");
            try
            {
                var menuStructure = editedMenuStructure ?? BuildMergedMenuStructure();
                LogDetail($"SaveEditedMenuStructure: Menu structure has {menuStructure?.children?.Count ?? 0} root children");
                if (!ConvertMenuForAvatarAssignment(menuStructure, configuredExclusionPaths, out var rootAssetPath))
                {
                    EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                        GetLocalizedText("メニューの保存中にエラーが発生しました", "Error occurred during save"), "OK");
                    LogDetail("########## SaveEditedMenuStructure: END (FAILED CONVERSION) ##########");
                    return;
                }

            // Debug.Log(GetLocalizedText(
            //                     $"ExpressionMenuをModular Avatar構造に変換し、空のルートメニューを割り当てました: {rootAssetPath}",
            //                     $"Converted ExpressionMenu to Modular Avatar hierarchy and assigned empty root menu: {rootAssetPath}"
            //                 ));

                // 第2段階：メニュー項目生成完了後に最新のメニュー構造を再構築
                // キャッシュを無効化して最新の構造を確実に取得
                cachedMenuStructure = null;
                menuStructureDirty = true;
                var updatedMenuStructure = BuildMergedMenuStructure();
                if (updatedMenuStructure != null)
                {
                    ApplyMenuInstallTargetsToExcludedItems(updatedMenuStructure, configuredExclusionPaths);
                }

                LogDetail("########## SaveEditedMenuStructure: END (SUCCESS) ##########");
                EditorUtility.DisplayDialog(GetLocalizedText("保存完了", "Save Complete"),
                    GetLocalizedText("メニューが正常に保存されました", "Menu saved successfully"), "OK");
            }
            catch (System.Exception e)
            {
                LogDetail($"SaveEditedMenuStructure: EXCEPTION - {e.Message}");
                LogDetail($"SaveEditedMenuStructure: Stack trace: {e.StackTrace}");
                LogDetail("########## SaveEditedMenuStructure: END (EXCEPTION) ##########");
                Debug.LogError(GetLocalizedText(
                    $"メニューの保存中にエラーが発生しました: {e.Message}",
                    $"Error saving menu: {e.Message}"
                ));

                EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                    GetLocalizedText("保存中にエラーが発生しました", "Error occurred during save"), "OK");
            }
        }

        /// <summary>
        /// アバター割り当て時のMA変換処理
        /// 空のルートメニューを作成してから階層を生成
        /// </summary>
        private bool ConvertMenuForAvatarAssignment(MergedMenuItem menuStructure, HashSet<string> excludedPaths, out string rootAssetPath)
        {
            LogDetail("########## ConvertMenuForAvatarAssignment: START ##########");
            rootAssetPath = null;

            if (selectedAvatar == null)
            {
                Debug.LogError("Cannot convert menu: no avatar selected");
                LogDetail("ConvertMenuForAvatarAssignment: No avatar selected");
                return false;
            }

            if (menuStructure == null)
            {
                Debug.LogError("Cannot convert menu: menu structure is null");
                LogDetail("ConvertMenuForAvatarAssignment: Menu structure is null");
                return false;
            }

            // Step 1: 共通の準備処理
            PrepareForMenuConversion(menuStructure, excludedPaths);

            // Step 2: 空のルートメニューアセットを作成
            string rawFolderName = $"{selectedAvatar.name}_EditedExpressionMenus";
            string sanitizedFolderName = SanitizeForAssetPath(rawFolderName, "EditedExpressionMenus");
            string folderPath = $"Assets/{rawFolderName}";
            LogDetail($"ConvertMenuForAvatarAssignment: Preparing folder path '{folderPath}'");

            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                folderPath = $"Assets/{sanitizedFolderName}";
                LogDetail($"ConvertMenuForAvatarAssignment: Folder not found, trying sanitized path '{folderPath}'");
            }

            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                LogDetail($"ConvertMenuForAvatarAssignment: Creating folder '{sanitizedFolderName}'");
                string guid = AssetDatabase.CreateFolder("Assets", sanitizedFolderName);
                if (string.IsNullOrEmpty(guid))
                {
                    LogDetail("ConvertMenuForAvatarAssignment: ERROR - Failed to create folder");
                    Debug.LogError("Failed to create folder for expression menus");
                    return false;
                }
                folderPath = $"Assets/{sanitizedFolderName}";
                LogDetail($"ConvertMenuForAvatarAssignment: Folder created successfully: {folderPath}");
            }
            else
            {
                LogDetail($"ConvertMenuForAvatarAssignment: Using existing folder '{folderPath}'");
            }

            var emptyMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            emptyMenu.controls = new List<VRCExpressionsMenu.Control>();

            rootAssetPath = AssetDatabase.GenerateUniqueAssetPath($"{folderPath}/Root.asset");
            LogDetail($"ConvertMenuForAvatarAssignment: Creating empty root menu asset at '{rootAssetPath}'");
            AssetDatabase.CreateAsset(emptyMenu, rootAssetPath);

            Undo.RecordObject(selectedAvatar, "Assign empty expressions menu");
            selectedAvatar.expressionsMenu = emptyMenu;
            EditorUtility.SetDirty(selectedAvatar);
            LogDetail("ConvertMenuForAvatarAssignment: Assigned empty menu to avatar");

            // Step 3: Modular Avatar階層を生成
            LogDetail("ConvertMenuForAvatarAssignment: Generating Modular Avatar hierarchy");
            activeConversionRootMenu = emptyMenu;
            GenerateModularAvatarMenuHierarchy(menuStructure);
            activeConversionRootMenu = null;

            // Step 4: アセット保存とクリーンアップ
            FinalizMenuConversion();

            LogDetail("########## ConvertMenuForAvatarAssignment: END (SUCCESS) ##########");
            return true;
        }

        /// <summary>
        /// 保存ボタン時のMA変換処理
        /// ルートメニューを作成せず、既存の階層を更新
        /// 非除外項目と除外項目の処理を完全に分離
        /// </summary>
        private bool ConvertMenuForSaveButton(MergedMenuItem menuStructure, HashSet<string> excludedPaths)
        {
            LogDetail("########## ConvertMenuForSaveButton: START ##########");

            if (selectedAvatar == null)
            {
                Debug.LogError("Cannot convert menu: no avatar selected");
                LogDetail("ConvertMenuForSaveButton: No avatar selected");
                return false;
            }

            if (menuStructure == null)
            {
                Debug.LogError("Cannot convert menu: menu structure is null");
                LogDetail("ConvertMenuForSaveButton: Menu structure is null");
                return false;
            }

            // Step 1: 共通の準備処理
            PrepareForMenuConversion(menuStructure, excludedPaths);

            // Step 2: 非除外項目のみを生成（Modular Avatar階層を生成）
            LogDetail("ConvertMenuForSaveButton: Generating Modular Avatar hierarchy for non-excluded items");
            // When saving, excluded items are repositioned separately later; skip them here
            GenerateModularAvatarMenuHierarchy(menuStructure, true);

            // Step 3: 除外項目を後処理で配置（Menu Install Target を保持）
            LogDetail("ConvertMenuForSaveButton: Repositioning excluded items");
            RepositionExcludedMenuItemsForSave(menuStructure);

            // Step 4: アセット保存とクリーンアップ
            FinalizMenuConversion();

            LogDetail("########## ConvertMenuForSaveButton: END (SUCCESS) ##########");
            return true;
        }

                    /// <summary>
        /// 除外項目を生成後に配置し直す処理
        /// Menu Install Target を保持したまま、適切な位置に配置
        /// </summary>
        private void RepositionExcludedMenuItemsForSave(MergedMenuItem menuStructure)
        {
            LogDetail("########## RepositionExcludedMenuItemsForSave: START ##########");
            
            if (selectedAvatar == null || menuStructure == null) return;

            var avatarTransform = selectedAvatar.transform;
            if (avatarTransform == null) return;

            // 生成されたメニュー root を探索
            Transform menuItemRoot = null;
            for (int i = 0; i < avatarTransform.childCount; i++)
            {
                var child = avatarTransform.GetChild(i);
                if (child != null && child.GetComponent<ExprMenuVisualizerGeneratedRoot>() != null)
                {
                    menuItemRoot = child;
                    break;
                }
            }

            if (menuItemRoot == null)
            {
                LogDetail("RepositionExcludedMenuItemsForSave: Menu item root not found");
                return;
            }

            RepositionExcludedItemsRecursive(menuStructure, menuItemRoot, 0);

            LogDetail("########## RepositionExcludedMenuItemsForSave: END ##########");
        }

        /// <summary>
        /// 再帰的に除外項目を処理する
        /// </summary>
        private void RepositionExcludedItemsRecursive(MergedMenuItem item, Transform parentTransform, int siblingIndex)
        {
            if (item == null) return;

            // 除外項目（sourceComponent がある）の場合
            if (item.sourceComponent != null)
            {
                var excludedGameObject = item.sourceComponent.gameObject;
                if (excludedGameObject != null)
                {
                    LogDetail($"Repositioning excluded item: {item.name}");
                    
                    // Menu Install Target を保持したまま配置し直す
                    Undo.SetTransformParent(excludedGameObject.transform, parentTransform, "Reposition excluded menu item");
                    excludedGameObject.transform.localPosition = Vector3.zero;
                    excludedGameObject.transform.localRotation = Quaternion.identity;
                    excludedGameObject.transform.localScale = Vector3.one;
                    excludedGameObject.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, parentTransform.childCount - 1));
                    
                    EditorUtility.SetDirty(excludedGameObject);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(excludedGameObject);
                    
                    // 除外項目の子要素も処理
                    if (item.children != null)
                    {
                        for (int i = 0; i < item.children.Count; i++)
                        {
                            RepositionExcludedItemsRecursive(item.children[i], excludedGameObject.transform, i);
                        }
                    }
                }
                return;
            }

            // 非除外項目の場合、その子の除外項目を処理
            if (item.children != null)
            {
                for (int i = 0; i < item.children.Count; i++)
                {
                    RepositionExcludedItemsRecursive(item.children[i], parentTransform, siblingIndex + i);
                }
            }
        }


        /// <summary>
        /// メニュー変換の共通準備処理
        /// </summary>
        private void PrepareForMenuConversion(MergedMenuItem menuStructure, HashSet<string> excludedPaths)
        {
            LogDetail("PrepareForMenuConversion: Starting common preparation");

            RefreshExpressionParameterStates();
            AssignMenuPaths(menuStructure);

            excludedPaths ??= new HashSet<string>(StringComparer.Ordinal);
            activeConversionExcludedPaths.Clear();
            foreach (var path in excludedPaths)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    activeConversionExcludedPaths.Add(path);
                }
            }

            LogDetail("PrepareForMenuConversion: Common preparation completed");
        }

        /// <summary>
        /// メニュー変換の共通終了処理
        /// </summary>
        private void FinalizMenuConversion()
        {
            LogDetail("FinalizMenuConversion: Starting finalization");

            maMenuItemSignatureMap.Clear();
            modularAvatarInstallTargets.Clear();
            MarkMenuStructureDirty();
            activeConversionExcludedPaths.Clear();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            LogDetail("FinalizMenuConversion: Assets saved and database refreshed");

            editedMenuStructure = null;
            selectedItems.Clear();
            LogDetail("FinalizMenuConversion: Edit state reset");
        }

        /// <summary>
        /// 編集モードの保存ボタン専用メソッド
        /// ConvertMenuForSaveButtonを使用して処理を統一
        /// </summary>
        private void SaveEditedMenuStructureSimple()
        {
            LogDetail("########## SaveEditedMenuStructureSimple: START ##########");
            if (selectedAvatar == null)
            {
                LogDetail("SaveEditedMenuStructureSimple: No avatar selected");
                EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                    GetLocalizedText("アバターが選択されていません", "No avatar selected"), "OK");
                return;
            }

            if (!IsModularAvatarAvailable())
            {
                EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                    GetLocalizedText("Modular Avatarが利用できません", "Modular Avatar is not available"), "OK");
                return;
            }

            LogDetail($"SaveEditedMenuStructureSimple: Saving menu for avatar '{selectedAvatar.name}'");
            try
            {
                var menuStructure = editedMenuStructure ?? BuildMergedMenuStructure();
                LogDetail($"SaveEditedMenuStructureSimple: Menu structure has {menuStructure?.children?.Count ?? 0} root children");

                if (menuStructure == null)
                {
                    EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                        GetLocalizedText("メニュー構造の取得に失敗しました", "Failed to build menu structure"), "OK");
                    LogDetail("########## SaveEditedMenuStructureSimple: END (FAILED - NULL STRUCTURE) ##########");
                    return;
                }

                // ConvertMenuForSaveButtonを使用して共通処理を実行
                if (!ConvertMenuForSaveButton(menuStructure, configuredExclusionPaths))
                {
                    EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                        GetLocalizedText("メニューの保存中にエラーが発生しました", "Error occurred during save"), "OK");
                    LogDetail("########## SaveEditedMenuStructureSimple: END (FAILED CONVERSION) ##########");
                    return;
                }

                // キャッシュを無効化して最新の構造を再構築
                cachedMenuStructure = null;
                menuStructureDirty = true;
                var updatedMenuStructure = BuildMergedMenuStructure();

                // 除外項目にMenu Install Targetを適用（サブメニュー生成ボタンで作成したメニューの保存に必要）
                if (updatedMenuStructure != null)
                {
                    ApplyMenuInstallTargetsToExcludedItems(updatedMenuStructure, configuredExclusionPaths);
                }

                LogDetail("########## SaveEditedMenuStructureSimple: END (SUCCESS) ##########");
                EditorUtility.DisplayDialog(GetLocalizedText("保存完了", "Save Complete"),
                    GetLocalizedText("メニュー構造が更新されました", "Menu structure updated successfully"), "OK");

                Repaint();
            }
            catch (System.Exception e)
            {
                LogDetail($"SaveEditedMenuStructureSimple: EXCEPTION - {e.Message}");
                LogDetail($"SaveEditedMenuStructureSimple: Stack trace: {e.StackTrace}");
                LogDetail("########## SaveEditedMenuStructureSimple: END (EXCEPTION) ##########");
                Debug.LogError(GetLocalizedText(
                    $"メニュー構造の更新中にエラーが発生しました: {e.Message}",
                    $"Error updating menu structure: {e.Message}"
                ));

                EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                    GetLocalizedText("保存中にエラーが発生しました", "Error occurred during save"), "OK");
            }
        }

        private void GenerateModularAvatarMenuHierarchy(MergedMenuItem menuStructure, bool isForSaveButton = false)
        {
            if (selectedAvatar == null)
            {
                Debug.LogError("Cannot generate Modular Avatar hierarchy: no avatar selected");
                return;
            }

            if (!IsModularAvatarAvailable())
            {
                Debug.LogError("Modular Avatar package is not available. Cannot generate MA menu hierarchy.");
                return;
            }

            var installerType = GetModularAvatarMenuInstallerType();
            var menuGroupType = GetModularAvatarMenuGroupType();
            var menuItemType = GetModularAvatarMenuItemType();

            if (installerType == null || menuGroupType == null || menuItemType == null)
            {
                Debug.LogError("Failed to resolve Modular Avatar menu types. Aborting generation.");
                return;
            }

            // Build a lookup of existing generated GameObjects (fullPath -> GameObject)
            // so we can reuse them when regenerating the hierarchy. This preserves
            // inspector state and avoids re-creating/destroying objects unnecessarily.
            try
            {
                tempGeneratedLookup = null;
                var menuRoot = FindMenuItemRoot(selectedAvatar);
                if (menuRoot != null)
                {
                    var metas = menuRoot.GetComponentsInChildren<ExprMenuVisualizerGeneratedMetadata>(true);
                    if (metas != null && metas.Length > 0)
                    {
                        tempGeneratedLookup = new Dictionary<string, GameObject>(StringComparer.Ordinal);
                        for (int i = 0; i < metas.Length; i++)
                        {
                            var m = metas[i];
                            if (m == null) continue;
                            try
                            {
                                var key = CanonicalizeMenuFullPath(m.fullPath);
                                if (!string.IsNullOrEmpty(key) && !tempGeneratedLookup.ContainsKey(key))
                                {
                                    tempGeneratedLookup[key] = m.gameObject;
                                }
                                else if (!string.IsNullOrEmpty(key))
                                {
                                    Debug.LogWarning($"Duplicate generated metadata fullPath found during regeneration: {key}");
                                }
                            }
                            catch { }
                        }
                        // Override or augment lookup with GUID-marked GameObjects so GUID-bearing
                        // generated objects are preferred when both exist for the same path.
                        try
                        {
                            var guidMarkers = menuRoot.GetComponentsInChildren<ExprMenuVisualizerGeneratedGuid>(true);
                            if (guidMarkers != null && guidMarkers.Length > 0)
                            {
                                for (int gi = 0; gi < guidMarkers.Length; gi++)
                                {
                                    var gm = guidMarkers[gi];
                                    if (gm == null) continue;
                                    try
                                    {
                                        if (!string.IsNullOrEmpty(gm.originalMenuPath))
                                        {
                                            // Prefer GUID-marked object for this fullPath (canonicalized)
                                            var gk = CanonicalizeMenuFullPath(gm.originalMenuPath);
                                            if (!string.IsNullOrEmpty(gk)) tempGeneratedLookup[gk] = gm.gameObject;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception) { tempGeneratedLookup = null; }

            RemoveExistingGeneratedMenuHierarchy();
            MarkExistingModularAvatarComponentsAsEditorOnly(menuStructure, activeConversionExcludedPaths);

            var avatarTransform = selectedAvatar.transform;
            if (avatarTransform == null)
            {
                Debug.LogError("Selected avatar does not have a transform. Cannot generate menu hierarchy.");
                return;
            }

            Undo.IncrementCurrentGroup();

            GameObject rootObject = null;

            // Try to find an existing explicit GeneratedRoot marker placed under the avatar.
            var existingRootComp = selectedAvatar.GetComponentInChildren<ExprMenuVisualizerGeneratedRoot>(true);
            if (existingRootComp != null)
            {
                rootObject = existingRootComp.gameObject;

                // Ensure the root is parented to the avatar (in case user placed it elsewhere)
                if (rootObject.transform.parent != avatarTransform)
                {
                    Undo.SetTransformParent(rootObject.transform, avatarTransform, "Attach existing Modular Avatar menu root");
                }

                // Clear any existing children under the root so we can regenerate cleanly
                for (int i = rootObject.transform.childCount - 1; i >= 0; i--)
                {
                    var c = rootObject.transform.GetChild(i)?.gameObject;
                    if (c != null)
                        Undo.DestroyObjectImmediate(c);
                }

                // Ensure the generated marker exists on the root as well
                EnsureAddComponent<ExprMenuVisualizerGenerated>(rootObject);
                Undo.RegisterCompleteObjectUndo(rootObject, "Reuse Modular Avatar menu root");
            }
            else
            {
                rootObject = new GameObject(GeneratedMenuRootName);
                EnsureAddComponent<ExprMenuVisualizerGeneratedRoot>(rootObject);
                EnsureAddComponent<ExprMenuVisualizerGenerated>(rootObject);
                Undo.RegisterCreatedObjectUndo(rootObject, "Generate Modular Avatar menu root");
                Undo.SetTransformParent(rootObject.transform, avatarTransform, "Attach Modular Avatar menu root");
                rootObject.transform.localPosition = Vector3.zero;
                rootObject.transform.localRotation = Quaternion.identity;
                rootObject.transform.localScale = Vector3.one;
            }

            var installerComponent = EnsureAddComponent(rootObject, installerType) as Component;
            var menuGroupComponent = EnsureAddComponent(rootObject, menuGroupType) as Component;

            if (installerComponent != null)
            {
                Undo.RecordObject(installerComponent, "Configure Modular Avatar installer");
                var menuToAppendField = installerType.GetField("menuToAppend");
                var installTargetField = installerType.GetField("installTargetMenu");
                menuToAppendField?.SetValue(installerComponent, null);
                installTargetField?.SetValue(installerComponent, null);
                EditorUtility.SetDirty(installerComponent);
            }

            if (menuGroupComponent != null)
            {
                Undo.RecordObject(menuGroupComponent, "Configure Modular Avatar menu group");
                var targetObjectField = menuGroupType.GetField("targetObject");
                targetObjectField?.SetValue(menuGroupComponent, null);
                EditorUtility.SetDirty(menuGroupComponent);
            }

            EditorUtility.SetDirty(rootObject);

            var children = menuStructure?.children;
            if (children != null)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    CreateModularAvatarMenuItemRecursive(children[i], rootObject.transform, i, children[i].fullPath, isForSaveButton);
                }
            }

            PrefabUtility.RecordPrefabInstancePropertyModifications(rootObject);
            PrefabUtility.RecordPrefabInstancePropertyModifications(selectedAvatar.gameObject);
            EditorUtility.SetDirty(selectedAvatar.gameObject);

            // Destroy any leftover generated GameObjects that were not reused during regeneration
            try
            {
                if (tempGeneratedLookup != null && tempGeneratedLookup.Count > 0)
                {
                    foreach (var kv in tempGeneratedLookup)
                    {
                        try
                        {
                            var leftover = kv.Value;
                            if (leftover != null)
                            {
                                Undo.DestroyObjectImmediate(leftover);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                tempGeneratedLookup = null;
            }
        }

        private void RemoveExistingGeneratedMenuHierarchy()
        {
            if (selectedAvatar == null) return;

            var avatarTransform = selectedAvatar.transform;
            if (avatarTransform == null) return;

            // We want to preserve an explicit "Generated Root" marker if the user has placed
            // one in the avatar. If a root marker exists, clear its children instead of
            // destroying the root GameObject itself. Otherwise, remove any generated-only
            // objects under the avatar.
            var toRemove = new List<GameObject>();

            for (int i = 0; i < avatarTransform.childCount; i++)
            {
                var child = avatarTransform.GetChild(i);
                if (child == null) continue;

                bool isRoot = child.GetComponent<ExprMenuVisualizerGeneratedRoot>() != null;
                bool isGenerated = child.GetComponent<ExprMenuVisualizerGenerated>() != null
                                   || child.GetComponent<ExprMenuVisualizerGeneratedGuid>() != null;

                if (isRoot)
                {
                    // preserve the root GameObject itself but queue its children for removal
                    for (int j = child.childCount - 1; j >= 0; j--)
                    {
                        var sub = child.GetChild(j)?.gameObject;
                        if (sub == null) continue;

                        // If we have a tempGeneratedLookup and this child is present in it (by metadata.fullPath),
                        // skip removal to allow reuse later.
                        if (tempGeneratedLookup != null)
                        {
                            try
                            {
                                var meta = sub.GetComponent<ExprMenuVisualizerGeneratedMetadata>();
                                if (meta != null && !string.IsNullOrEmpty(meta.fullPath) &&
                                    tempGeneratedLookup.TryGetValue(CanonicalizeMenuFullPath(meta.fullPath), out var mapped) && ReferenceEquals(mapped, sub))
                                {
                                    // keep this one for reuse
                                    continue;
                                }
                            }
                            catch { }
                        }

                        toRemove.Add(sub);
                    }
                }
                else if (isGenerated)
                {
                    // remove generated-only top-level objects unless they are in the reuse map
                    bool skip = false;
                    if (tempGeneratedLookup != null)
                    {
                        try
                        {
                            var meta = child.gameObject.GetComponent<ExprMenuVisualizerGeneratedMetadata>();
                            if (meta != null && !string.IsNullOrEmpty(meta.fullPath) &&
                                tempGeneratedLookup.TryGetValue(CanonicalizeMenuFullPath(meta.fullPath), out var mapped) && ReferenceEquals(mapped, child.gameObject))
                            {
                                skip = true;
                            }
                        }
                        catch { }
                    }

                    if (!skip)
                        toRemove.Add(child.gameObject);
                }
            }

            foreach (var go in toRemove)
            {
                Undo.DestroyObjectImmediate(go);
            }
        }

        private void MarkExistingModularAvatarComponentsAsEditorOnly(MergedMenuItem root, HashSet<string> excludedPaths)
        {
            if (root == null) return;

            var componentsToMark = new HashSet<Component>(ReferenceEqualityComparer<Component>.Instance);
            CollectModularAvatarComponents(root, componentsToMark, excludedPaths);

            var installerType = GetModularAvatarMenuInstallerType();

            foreach (var component in componentsToMark)
            {
                if (component == null) continue;
                var go = component.gameObject;
                if (go == null) continue;

                // Skip if already marked as Excluded
                if (go.GetComponent<ExprMenuVisualizerExcluded>() != null)
                {
                    continue;
                }

                Undo.RecordObject(go, "Process non-excluded Modular Avatar component");

                // Remove MA Menu Installer component if present
                if (installerType != null)
                {
                    var installer = go.GetComponent(installerType);
                    if (installer != null)
                    {
                        // Before removing the installer, check for a ModularAvatarParameters
                        // component on the same GameObject and disable any "auto-rename"
                        // (internalParameter) flags so parameters won't be auto-renamed
                        // after we remove the installer.
                        try
                        {
                            var paramsType = Type.GetType("nadena.dev.modular_avatar.core.ModularAvatarParameters, nadena.dev.modular-avatar.core");
                            if (paramsType != null)
                            {
                                var paramsComp = go.GetComponent(paramsType);
                                if (paramsComp != null)
                                {
                                    var parametersField = paramsType.GetField("parameters");
                                    if (parametersField != null)
                                    {
                                        var listObj = parametersField.GetValue(paramsComp) as System.Collections.IList;
                                        if (listObj != null)
                                        {
                                            bool changed = false;
                                            for (int i = 0; i < listObj.Count; i++)
                                            {
                                                var entry = listObj[i];
                                                if (entry == null) continue;
                                                var entryType = entry.GetType();
                                                var internalField = entryType.GetField("internalParameter");
                                                if (internalField != null && internalField.FieldType == typeof(bool))
                                                {
                                                    var val = (bool)internalField.GetValue(entry);
                                                    if (val)
                                                    {
                                                        internalField.SetValue(entry, false);
                                                        // write the modified struct back into the list
                                                        listObj[i] = entry;
                                                        changed = true;
                                                    }
                                                }
                                            }

                                            if (changed)
                                            {
                                                try
                                                {
                                                    Undo.RecordObject(paramsComp as UnityEngine.Object, "Disable MA Parameters Auto Rename");
                                                }
                                                catch { }
                                                EditorUtility.SetDirty(paramsComp as UnityEngine.Object);
                                                try { PrefabUtility.RecordPrefabInstancePropertyModifications(paramsComp as UnityEngine.Object); } catch { }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }

                        Undo.DestroyObjectImmediate(installer);
                    }
                }

                // Add Included marker
                var includedMarker = go.GetComponent<ExprMenuVisualizerIncluded>();
                if (includedMarker == null)
                {
                    includedMarker = Undo.AddComponent<ExprMenuVisualizerIncluded>(go);
                }

                EditorUtility.SetDirty(go);
                PrefabUtility.RecordPrefabInstancePropertyModifications(go);
            }
        }

        private void CollectModularAvatarComponents(MergedMenuItem node, HashSet<Component> results, HashSet<string> excludedPaths)
        {
            if (node == null) return;

            var fullPath = node.fullPath;
            if (IsMenuPathExcluded(fullPath, excludedPaths))
            {
                return;
            }

            if (node.sourceComponent != null)
            {
                results.Add(node.sourceComponent);
            }

            if (node.children != null)
            {
                foreach (var child in node.children)
                {
                    CollectModularAvatarComponents(child, results, excludedPaths);
                }
            }
        }

        private bool IsMenuPathExcluded(string path, HashSet<string> excludedPaths)
        {
            if (string.IsNullOrEmpty(path) || excludedPaths == null || excludedPaths.Count == 0)
            {
                return false;
            }

            if (excludedPaths.Contains(path))
            {
                return true;
            }

            foreach (var excludedPath in excludedPaths)
            {
                if (string.IsNullOrEmpty(excludedPath)) continue;
                if (path.Length > excludedPath.Length &&
                    path.StartsWith(excludedPath, StringComparison.Ordinal) &&
                    path[excludedPath.Length] == '/')
                {
                    return true;
                }
            }

            return false;
        }

        private VRCExpressionsMenu.Control ExtractControlFromSourceComponent(Component component)
        {
            if (component == null) return null;

            var menuItemType = GetModularAvatarMenuItemType();
            if (menuItemType != null && menuItemType.IsInstanceOfType(component))
            {
                var controlField = menuItemType.GetField("Control");
                var controlValue = controlField?.GetValue(component) as VRCExpressionsMenu.Control;
                return controlValue;
            }

            return null;
        }

        private void RemoveEditorOnlyEntriesFromMenu(MergedMenuItem root)
        {
            if (root == null || root.children == null) return;

            for (int i = root.children.Count - 1; i >= 0; i--)
            {
                var child = root.children[i];
                if (ShouldHideMergedMenuItem(child))
                {
                    root.children.RemoveAt(i);
                    continue;
                }

                RemoveEditorOnlyEntriesFromMenu(child);
            }
        }

        private bool ShouldHideMergedMenuItem(MergedMenuItem item)
        {
            if (item == null) return false;

            // Hide items with Included marker (always, regardless of edit mode)
            if (item.sourceComponent != null)
            {
                var go = item.sourceComponent.gameObject;
                if (go != null && go.GetComponent<ExprMenuVisualizerIncluded>() != null)
                {
                    return true;
                }
            }

            if (item.sourceComponent != null && IsEditorOnlyObject(item.sourceComponent.gameObject))
            {
                return true;
            }

            return false;
        }

        private bool IsEditorOnlyObject(GameObject obj)
        {
            if (obj == null) return false;

            var current = obj.transform;
            while (current != null)
            {
                var currentTag = current.gameObject.tag;
                if (!string.IsNullOrEmpty(currentTag) && string.Equals(currentTag, "EditorOnly", StringComparison.Ordinal))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        /// <summary>
        /// 除外項目のソースComponentから既存のMA Menu Install TargetコンポーネントがあるGameObjectを探す
        /// </summary>
        private GameObject FindExistingMenuInstallTargetGameObject(MergedMenuItem item)
        {
            if (item?.sourceComponent == null)
                return null;

            var installTargetType = GetModularAvatarMenuInstallTargetType();
            if (installTargetType == null)
                return null;

            var sourceGameObject = item.sourceComponent.gameObject;
            if (sourceGameObject == null)
                return null;

            // sourceComponent自体にMA Menu Install Targetがある場合
            if (sourceGameObject.GetComponent(installTargetType) != null)
            {
                return sourceGameObject;
            }

            // Otherwise try to find a matching install target under the avatar's "メニュー項目" root.
            // Search recursively so items that were moved within the MenuItem hierarchy are found.
            // We consider an existing install-target object a match if its installer field refers to
            // the same installer/component as item.sourceComponent OR refers to the same GameObject.
            var root = FindMenuItemRoot(selectedAvatar);
            if (root != null)
            {
                // First, prefer GUID-based matching: if a generated GUID marker exists with an
                // originalMenuPath that equals this item's fullPath, prefer that GameObject.
                try
                {
                    var guidMarkers = root.GetComponentsInChildren<ExprMenuVisualizerGeneratedGuid>(true);
                    if (guidMarkers != null)
                    {
                        foreach (var gm in guidMarkers)
                        {
                            if (gm == null) continue;
                            try
                            {
                                if (!string.IsNullOrEmpty(gm.originalMenuPath) &&
                                    string.Equals(CanonicalizeMenuFullPath(gm.originalMenuPath), CanonicalizeMenuFullPath(item.fullPath), StringComparison.Ordinal))
                                {
                                    return gm.gameObject;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Otherwise try to find a matching install target under the avatar's "メニュー項目" root.
                // Search recursively so items that were moved within the MenuItem hierarchy are found.
                var allTransforms = root.GetComponentsInChildren<Transform>(true);
                foreach (var t in allTransforms)
                {
                    if (t == null || t.gameObject == null) continue;
                    var child = t.gameObject;

                    var installTarget = child.GetComponent(installTargetType);
                    if (installTarget == null) continue;

                    // Try to inspect the "installer" field on the installTarget
                    try
                    {
                        var installerField = installTargetType.GetField("installer");
                        if (installerField != null)
                        {
                            var installerObj = installerField.GetValue(installTarget) as Component;
                            if (installerObj != null)
                            {
                                // If the installer component itself == item.sourceComponent, it's a match
                                if (ReferenceEquals(installerObj, item.sourceComponent))
                                    return child;

                                // If installerObj.gameObject == item.sourceComponent.gameObject, also a match
                                if (item.sourceComponent != null && installerObj.gameObject == item.sourceComponent.gameObject)
                                    return child;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore reflection failures and continue searching
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 非除外項目のMenuItem生成処理
        /// </summary>
        private void GenerateNonExcludedMenuItems(MergedMenuItem item, Transform parent, int siblingIndex, string itemFullPath = null)
        {
            if (item == null || parent == null) return;

            // Prevent adding children under a Menu Install Target object in edit/save generation.
            if (IsInstallTargetGameObject(parent.gameObject))
            {
                parent = GetMenuItemRootTransform();
                if (parent == null) return;
            }

            string effectivePath = string.IsNullOrEmpty(itemFullPath) ? item.fullPath : itemFullPath;
            string canonicalPath = CanonicalizeMenuFullPath(effectivePath);

            var controlSource = item.control ?? ExtractControlFromSourceComponent(item.sourceComponent);

            // Skip items without actual controls (e.g., installer placeholders)
            if (controlSource == null)
            {
                if (item.children != null)
                {
                    for (int i = 0; i < item.children.Count; i++)
                    {
                        GenerateNonExcludedMenuItems(item.children[i], parent, i, item.children[i].fullPath);
                    }
                }
                return;
            }

            var menuItemType = GetModularAvatarMenuItemType();
            if (menuItemType == null)
            {
                Debug.LogError("ModularAvatarMenuItem type was not resolved. Cannot create menu item.");
                return;
            }

            var controlClone = CloneControlForMenuItem(controlSource, item.name);
            if (controlClone == null)
            {
                return;
            }

            string controlName = string.IsNullOrWhiteSpace(controlClone.name) ? "Menu Item" : controlClone.name;

            GameObject menuItemObject = null;
            bool reusedExisting = false;

            // Attempt to reuse an existing generated GameObject that matches this fullPath
            if (tempGeneratedLookup != null && !string.IsNullOrEmpty(canonicalPath))
            {
                try
                {
                    if (tempGeneratedLookup.TryGetValue(canonicalPath, out var existing) && existing != null)
                    {
                        menuItemObject = existing;
                        reusedExisting = true;
                        // Ensure markers and metadata are present and up-to-date
                        EnsureAddComponent<ExprMenuVisualizerGenerated>(menuItemObject);
                        try
                        {
                            var meta = EnsureAddComponent<ExprMenuVisualizerGeneratedMetadata>(menuItemObject);
                            if (meta != null)
                            {
                                meta.fullPath = canonicalPath;
                                EditorUtility.SetDirty(meta);
                            }
                        }
                        catch { }

                        // Remove from lookup so leftover entries reflect truly-unused objects
                        tempGeneratedLookup.Remove(canonicalPath);
                    }
                }
                catch { }
            }

            if (menuItemObject == null)
            {
                menuItemObject = new GameObject(controlName);
                EnsureAddComponent<ExprMenuVisualizerGenerated>(menuItemObject);
                // Attach metadata so we can robustly identify generated GameObjects by fullPath later
                try
                {
                    var meta = EnsureAddComponent<ExprMenuVisualizerGeneratedMetadata>(menuItemObject);
                    if (meta != null)
                    {
                        meta.fullPath = canonicalPath;
                    }
                }
                catch { }
                // Ensure GUID marker exists on generated menu items so they are stable across sessions
                try
                {
                    var guidComp = menuItemObject.GetComponent<ExprMenuVisualizerGeneratedGuid>() ?? EnsureAddComponent<ExprMenuVisualizerGeneratedGuid>(menuItemObject);
                    if (guidComp != null)
                    {
                        if (string.IsNullOrEmpty(guidComp.generatedGuid))
                            guidComp.generatedGuid = System.Guid.NewGuid().ToString("N");
                        try { guidComp.originalMenuPath = canonicalPath ?? string.Empty; } catch { }
                        try { guidComp.installTargetName = item.name ?? string.Empty; } catch { }
                        EditorUtility.SetDirty(guidComp);
                        
                    }
                }
                catch { }

                Undo.RegisterCreatedObjectUndo(menuItemObject, "Create Modular Avatar menu item");
            }

            // 親とSiblingIndexの設定
            if (reusedExisting)
            {
                Undo.SetTransformParent(menuItemObject.transform, parent, "Reparent existing generated menu item");
            }
            else
            {
                Undo.SetTransformParent(menuItemObject.transform, parent, "Set menu item parent");
            }
            menuItemObject.transform.localPosition = Vector3.zero;
            menuItemObject.transform.localRotation = Quaternion.identity;
            menuItemObject.transform.localScale = Vector3.one;
            menuItemObject.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, parent.childCount - 1));

            // ModularAvatarMenuItemコンポーネントを追加
            var menuItemComponent = EnsureAddComponent(menuItemObject, menuItemType) as Component;
            if (menuItemComponent != null)
            {
                Undo.RecordObject(menuItemComponent, "Configure Modular Avatar menu item");

                var controlField = menuItemType.GetField("Control");
                controlField?.SetValue(menuItemComponent, controlClone);

                var labelField = menuItemType.GetField("label");
                labelField?.SetValue(menuItemComponent, controlClone.name);

                var parameterState = DetermineControlParameterState(controlClone);

                ForceBooleanMember(menuItemComponent, "useGameObjectName", "UseGameObjectName", true);

                menuItemType.GetField("isSynced")?.SetValue(menuItemComponent, parameterState.synced);
                menuItemType.GetField("isSaved")?.SetValue(menuItemComponent, parameterState.saved);
                menuItemType.GetField("automaticValue")?.SetValue(menuItemComponent, true);
                menuItemType.GetField("isDefault")?.SetValue(menuItemComponent, false);

                if (controlClone.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    var menuSourceField = menuItemType.GetField("MenuSource");
                    if (menuSourceField != null)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(menuSourceField.FieldType, "Children");
                            menuSourceField.SetValue(menuItemComponent, enumValue);
                        }
                        catch
                        {
                            // Ignore enum assignment issues
                        }
                    }

                    var otherChildrenField = menuItemType.GetField("menuSource_otherObjectChildren", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    otherChildrenField?.SetValue(menuItemComponent, null);
                }

                EditorUtility.SetDirty(menuItemComponent);
                PrefabUtility.RecordPrefabInstancePropertyModifications(menuItemComponent);
            }

            CopyAdditionalComponentsFromSource(item, menuItemObject);

            PrefabUtility.RecordPrefabInstancePropertyModifications(menuItemObject);

            // 子要素を再帰的に生成
            if (item.children != null && item.children.Count > 0)
            {
                for (int i = 0; i < item.children.Count; i++)
                {
                    GenerateNonExcludedMenuItems(item.children[i], menuItemObject.transform, i, item.children[i].fullPath);
                }
            }
        }

        /// <summary>
        /// 除外項目のMenuItem生成処理
        /// 既存のMA Menu Install TargetGameObjectを再利用のみ
        /// </summary>
        private void GenerateExcludedMenuItems(MergedMenuItem item, Transform parent, int siblingIndex, string itemFullPath = null)
        {
            if (item == null || parent == null) return;

            string effectivePath = string.IsNullOrEmpty(itemFullPath) ? item.fullPath : itemFullPath;
            string canonicalPath = CanonicalizeMenuFullPath(effectivePath);

            var controlSource = item.control ?? ExtractControlFromSourceComponent(item.sourceComponent);

            // Skip items without actual controls (e.g., installer placeholders)
            if (controlSource == null)
            {
                if (item.children != null)
                {
                    for (int i = 0; i < item.children.Count; i++)
                    {
                        GenerateExcludedMenuItems(item.children[i], parent, i, item.children[i].fullPath);
                    }
                }
                return;
            }

            var controlClone = CloneControlForMenuItem(controlSource, item.name);
            if (controlClone == null)
            {
                return;
            }

            // 除外項目の場合、既存のMA Menu Install Targetを持つGameObjectを再利用
            GameObject menuItemObject = null;
            bool isReusedExistingObject = false;

            menuItemObject = FindExistingMenuInstallTargetGameObject(item);
            if (menuItemObject != null)
            {
                isReusedExistingObject = true;
                LogDetail($"Reusing existing MA Menu Install Target GameObject: {menuItemObject.name}");
            }

            // 既存のGameObjectが見つらない場合は新規作成
            if (menuItemObject == null)
            {
                string controlName = string.IsNullOrWhiteSpace(controlClone.name) ? "Menu Item" : controlClone.name;
                menuItemObject = new GameObject(controlName);

                // Mark as a generated item so metadata-based matching can find it later
                try
                {
                    EnsureAddComponent<ExprMenuVisualizerGenerated>(menuItemObject);
                }
                catch { }

                // Attach GeneratedMetadata and set fullPath so excluded items are discoverable
                try
                {
                    var meta = EnsureAddComponent<ExprMenuVisualizerGeneratedMetadata>(menuItemObject);
                    if (meta != null)
                    {
                        meta.fullPath = canonicalPath;
                        EditorUtility.SetDirty(meta);
                    }
                }
                catch { }

                Undo.RegisterCreatedObjectUndo(menuItemObject, "Create excluded Modular Avatar menu item");
            }

            // Ensure GUID marker exists on excluded menu items (newly created or existing)
            try
            {
                var guidComp = menuItemObject.GetComponent<ExprMenuVisualizerGeneratedGuid>() ?? EnsureAddComponent(menuItemObject, typeof(ExprMenuVisualizerGeneratedGuid)) as ExprMenuVisualizerGeneratedGuid;
                    if (guidComp != null)
                    {
                        if (string.IsNullOrEmpty(guidComp.generatedGuid))
                            guidComp.generatedGuid = System.Guid.NewGuid().ToString("N");
                        guidComp.originalMenuPath = canonicalPath;
                        guidComp.installTargetName = item.name ?? string.Empty;
                        EditorUtility.SetDirty(guidComp);
                    }
            }
            catch { }

            // 親とSiblingIndexの設定（既存オブジェクトの場合も更新）
            Undo.SetTransformParent(menuItemObject.transform, parent, "Set menu item parent");
            menuItemObject.transform.localPosition = Vector3.zero;
            menuItemObject.transform.localRotation = Quaternion.identity;
            menuItemObject.transform.localScale = Vector3.one;
            menuItemObject.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, parent.childCount - 1));

            // 既存オブジェクト再利用の場合はコンポーネントの変更をスキップ
            if (isReusedExistingObject)
            {
                LogDetail($"Skipping component modification for reused object: {menuItemObject.name}");
                // 既存のMA Menu Install Targetオブジェクトはそのまま維持
                // 子GameObjectの生成もスキップ（既存の構造を維持）
            }
            else
            {
                // 新規作成の除外項目は ModularAvatarMenuItem を追加しない
                // （Menu Install Target コンポーネントは ApplyMenuInstallTargetsToExcludedItems で後処理）
                PrefabUtility.RecordPrefabInstancePropertyModifications(menuItemObject);
            }
        }


        /// <summary>
        /// 非除外項目のみを処理する
        /// 除外項目（sourceComponent != null）は完全にスキップ
        /// 除外項目の配置は後処理フェーズで別途実施
        /// </summary>
        private void CreateModularAvatarMenuItemRecursive(MergedMenuItem item, Transform parent, int siblingIndex, string itemFullPath = null, bool isForSaveOnly = false)
        {
            if (item == null || parent == null) return;

            // sourceComponent があれば除外項目（元々のMA Menu Item）
            // Treat an item as excluded only if it has a Modular Avatar source component AND the
            // menu path is part of the active conversion excluded paths. This lets non-excluded
            // MA-origin items (i.e. not selected for exclusion) be handled as normal items
            // (they will be marked EditorOnly earlier, and we will generate menu entries for them
            // beneath the generated "メニュー項目" root to reproduce the menu structure).
            bool isExcluded = item.sourceComponent != null && IsMenuPathExcluded(item.fullPath, activeConversionExcludedPaths);

            // 保存時のみ除外項目をスキップ
            if (isExcluded && isForSaveOnly)
            {
                LogDetail($"Skipping excluded item during generation: {item.name} (will be repositioned later)");
                return;
            }

            // アバター割り当て時は従来通り処理
            if (isExcluded && !isForSaveOnly)
            {
                GenerateExcludedMenuItems(item, parent, siblingIndex, itemFullPath);
            }
            else if (!isExcluded)
            {
                GenerateNonExcludedMenuItems(item, parent, siblingIndex, itemFullPath);
            }
        }



        private void CopyAdditionalComponentsFromSource(MergedMenuItem item, GameObject destination)
        {
            if (item?.sourceComponent == null || destination == null)
            {
                return;
            }

            var sourceObject = item.sourceComponent.gameObject;
            if (sourceObject == null || sourceObject == destination)
            {
                return;
            }

            var menuItemType = GetModularAvatarMenuItemType();
            var installerType = GetModularAvatarMenuInstallerType();

            var components = sourceObject.GetComponents<Component>();
            foreach (var component in components)
            {
                if (component == null || component is Transform)
                {
                    continue;
                }

                var componentType = component.GetType();
                if (menuItemType != null && menuItemType.IsAssignableFrom(componentType))
                {
                    continue;
                }

                if (installerType != null && installerType.IsAssignableFrom(componentType))
                {
                    continue;
                }

                // Skip internal marker components
                if (component is ExprMenuVisualizerIncluded ||
                    component is ExprMenuVisualizerExcluded ||
                    component is ExprMenuVisualizerGenerated ||
                    component is ExprMenuVisualizerGeneratedRoot ||
                    component is ExprMenuVisualizerGeneratedMetadata)
                {
                    continue;
                }

                try
                {
                    if (ComponentUtility.CopyComponent(component))
                    {
                        Undo.RecordObject(destination, "Copy Modular Avatar component");
                        if (ComponentUtility.PasteComponentAsNew(destination))
                        {
                            var pastedComponents = destination.GetComponents(componentType);
                            if (pastedComponents != null && pastedComponents.Length > 0)
                            {
                                EditorUtility.SetDirty(pastedComponents[pastedComponents.Length - 1]);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to copy component '{componentType?.Name}' from '{sourceObject.name}': {ex.Message}");
                }
            }
        }

        private VRCExpressionsMenu.Control CloneControlForMenuItem(VRCExpressionsMenu.Control source, string fallbackName)
        {
            if (source == null) return null;

            var clone = new VRCExpressionsMenu.Control
            {
            name = string.IsNullOrWhiteSpace(source.name) ? (string.IsNullOrWhiteSpace(fallbackName) ? "Menu Item" : fallbackName) : source.name,
                icon = source.icon,
                type = source.type,
                parameter = source.parameter != null
                    ? new VRCExpressionsMenu.Control.Parameter { name = source.parameter.name }
                    : null,
                value = source.value,
                subMenu = null,
                style = source.style
            };

            if (source.subParameters != null)
            {
                clone.subParameters = source.subParameters
                    .Select(p => p != null
                        ? new VRCExpressionsMenu.Control.Parameter { name = p.name }
                        : new VRCExpressionsMenu.Control.Parameter())
                    .ToArray();
            }
            else
            {
                clone.subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>();
            }

            if (source.labels != null)
            {
                clone.labels = source.labels
                    .Select(l => new VRCExpressionsMenu.Control.Label { name = l.name, icon = l.icon })
                    .ToArray();
            }
            else
            {
                clone.labels = Array.Empty<VRCExpressionsMenu.Control.Label>();
            }

            return clone;
        }

        /// <summary>
        /// For avatars that were already converted to ModularAvatar structure,
        /// ensure that any MA-origin menu items that are missing generated GameObjects
        /// under the `メニュー項目` root are created so they are visible and can
        /// receive Menu Install Target components.
        /// </summary>
        private void EnsureGeneratedMenuItemsForConvertedAvatar(MergedMenuItem root, HashSet<string> excludedPaths)
        {
            if (root == null || selectedAvatar == null) return;

            var rootTransform = FindMenuItemRoot(selectedAvatar);
            if (rootTransform == null) return;

            var menuItemType = GetModularAvatarMenuItemType();

            // Recursively process children and create missing generated GameObjects
            void Recurse(MergedMenuItem node)
            {
                if (node == null) return;

                // Only consider MA-origin items that are not excluded
                if (IsModularAvatarItem(node) && !IsMenuPathExcluded(node.fullPath, excludedPaths))
                {
                    // Try to locate an existing generated GameObject for this path
                    GameObject existing = null;
                    try
                    {
                        // Prefer GUID-based markers first (if present and carrying an originalMenuPath)
                        try
                        {
                            var guidMarkers = rootTransform.GetComponentsInChildren<ExprMenuVisualizerGeneratedGuid>(true);
                            if (guidMarkers != null)
                            {
                                foreach (var gm in guidMarkers)
                                {
                                    if (gm == null) continue;
                                    try
                                    {
                                        if (!string.IsNullOrEmpty(gm.originalMenuPath) &&
                                            string.Equals(CanonicalizeMenuFullPath(gm.originalMenuPath), CanonicalizeMenuFullPath(node.fullPath), StringComparison.Ordinal))
                                        {
                                            existing = gm.gameObject;
                                            break;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }

                        // Fallback to metadata.fullPath matching
                        if (existing == null)
                        {
                            var candidateMetas = rootTransform.GetComponentsInChildren<ExprMenuVisualizerGeneratedMetadata>(true);
                            foreach (var meta in candidateMetas)
                            {
                                if (meta == null) continue;
                                if (string.Equals(CanonicalizeMenuFullPath(meta.fullPath), CanonicalizeMenuFullPath(node.fullPath), StringComparison.Ordinal))
                                {
                                    existing = meta.gameObject;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    if (existing == null)
                    {
                        // Create a new generated GameObject to represent this MA item
                        var controlSource = node.control ?? ExtractControlFromSourceComponent(node.sourceComponent);
                        if (controlSource != null)
                        {
                            string controlName = string.IsNullOrWhiteSpace(controlSource.name) ? (string.IsNullOrWhiteSpace(node.name) ? "Menu Item" : node.name) : controlSource.name;
                            var go = new GameObject(controlName);
                            EnsureAddComponent<ExprMenuVisualizerGenerated>(go);
                            try
                            {
                                var meta = EnsureAddComponent<ExprMenuVisualizerGeneratedMetadata>(go);
                                    if (meta != null)
                                    {
                                        meta.fullPath = CanonicalizeMenuFullPath(node.fullPath);
                                        EditorUtility.SetDirty(meta);
                                    }
                            }
                            catch { }

                            // Ensure GUID marker exists on generated items created during EnsureGeneratedMenuItems
                            try
                            {
                                var guidComp = go.GetComponent<ExprMenuVisualizerGeneratedGuid>() ?? EnsureAddComponent<ExprMenuVisualizerGeneratedGuid>(go);
                                if (guidComp != null)
                                {
                                    if (string.IsNullOrEmpty(guidComp.generatedGuid))
                                        guidComp.generatedGuid = System.Guid.NewGuid().ToString("N");
                                    try { guidComp.originalMenuPath = CanonicalizeMenuFullPath(node.fullPath) ?? string.Empty; } catch { }
                                    try { guidComp.installTargetName = node.name ?? string.Empty; } catch { }
                                    EditorUtility.SetDirty(guidComp);
                                }
                            }
                            catch { }

                            Undo.RegisterCreatedObjectUndo(go, "Create generated MA menu item");

                            // Determine proper parent for this generated item. Prefer the generated
                            // GameObject that corresponds to this node's parent path. If not found,
                            // fall back to the menu root.
                            Transform parentForGo = rootTransform;
                            try
                            {
                                if (!string.IsNullOrEmpty(node.fullPath) && rootTransform != null)
                                {
                                    int lastSlash = node.fullPath.LastIndexOf('/');
                                    if (lastSlash > 0)
                                    {
                                        var parentFullPath = node.fullPath.Substring(0, lastSlash);
                                        var parentCandidateMetas = rootTransform.GetComponentsInChildren<ExprMenuVisualizerGeneratedMetadata>(true);
                                        for (int pmi = 0; pmi < parentCandidateMetas.Length; pmi++)
                                        {
                                            var pm = parentCandidateMetas[pmi];
                                            if (pm == null) continue;
                                            if (string.Equals(CanonicalizeMenuFullPath(pm.fullPath), CanonicalizeMenuFullPath(parentFullPath), StringComparison.Ordinal))
                                            {
                                                parentForGo = pm.gameObject.transform;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }

                            // Parent the new object under the selected parent and reset local transform
                            Undo.SetTransformParent(go.transform, parentForGo, "Attach generated MA menu item");
                            go.transform.localPosition = Vector3.zero;
                            go.transform.localRotation = Quaternion.identity;
                            go.transform.localScale = Vector3.one;

                            // Add a ModularAvatarMenuItem component if available and configure it
                            if (menuItemType != null)
                            {
                                var comp = EnsureAddComponent(go, menuItemType) as Component;
                                if (comp != null)
                                {
                                    try
                                    {
                                        var controlField = menuItemType.GetField("Control");
                                        var labelField = menuItemType.GetField("label");

                                        var controlClone = CloneControlForMenuItem(controlSource, node.name);
                                        if (controlField != null && controlClone != null)
                                            controlField.SetValue(comp, controlClone);

                                        if (labelField != null && controlClone != null)
                                            labelField.SetValue(comp, controlClone.name);

                                        // Set common flags similar to generation path
                                        ForceBooleanMember(comp, "useGameObjectName", "UseGameObjectName", true);
                                        menuItemType.GetField("isSynced")?.SetValue(comp, DetermineControlParameterState(controlClone).synced);
                                        menuItemType.GetField("isSaved")?.SetValue(comp, DetermineControlParameterState(controlClone).saved);
                                        menuItemType.GetField("automaticValue")?.SetValue(comp, true);
                                        menuItemType.GetField("isDefault")?.SetValue(comp, false);

                                        if (controlClone.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                                        {
                                            var menuSourceField = menuItemType.GetField("MenuSource");
                                            if (menuSourceField != null)
                                            {
                                                try
                                                {
                                                    var enumValue = Enum.Parse(menuSourceField.FieldType, "Children");
                                                    menuSourceField.SetValue(comp, enumValue);
                                                }
                                                catch { }
                                            }
                                        }

                                        EditorUtility.SetDirty(comp);
                                        PrefabUtility.RecordPrefabInstancePropertyModifications(comp);
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.LogWarning($"EnsureGeneratedMenuItems: failed to configure MA MenuItem component: {ex.Message}");
                                    }
                                }
                            }

                            // Copy additional components from source if appropriate
                            try
                            {
                                CopyAdditionalComponentsFromSource(node, go);
                            }
                            catch { }

                            PrefabUtility.RecordPrefabInstancePropertyModifications(go);
                            EditorUtility.SetDirty(go);
                        }
                    }
                }

                if (node.children != null)
                {
                    foreach (var c in node.children)
                        Recurse(c);
                }
            }

            Recurse(root);
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
            
            // If we captured an edit-mode undo snapshot group, revert scene changes
            if (editModeUndoGroup >= 0)
            {
                try
                {
                    // Revert all Undo groups that were created after the saved snapshot group
                    Undo.RevertAllDownToGroup(editModeUndoGroup);
                }
                catch
                {
                    // Reverting may fail in unusual states; ensure we clear the group to avoid stale values
                }
                editModeUndoGroup = -1;
            }

            // Debug.Log("Edit mode reset - returned to original menu structure");
            Repaint();

            // Re-initialize edited structure from actual hierarchy after revert
            if (editMode && selectedAvatar != null)
            {
                InitializeEditedMenuStructure();
                UpdateCurrentMenuItems();
            }
        }

        private void HandleEditModeToggled(bool newState)
        {
            if (!newState)
            {
                // When disabling edit mode, keep the changes and just clear the edit state
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

                // Clear the undo group without reverting
                editModeUndoGroup = -1;

                MarkMenuStructureDirty();
                UpdateCurrentMenuItems();
                Repaint();
            }
            else
            {
                // Capture the current Undo group as a snapshot point so Reset can revert changes
                try
                {
                    editModeUndoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("EditModeSnapshot");
                    // Start a new group so subsequent edits are recorded after our snapshot
                    Undo.IncrementCurrentGroup();
                }
                catch
                {
                    editModeUndoGroup = -1;
                }
            }
        }

        private void MarkMenuStructureDirty()
        {
            menuStructureDirty = true;
            cachedMenuStructure = null;
        }

        private void BeginExclusionSelectionWorkflow(VRCAvatarDescriptor avatar)
        {
            if (avatar == null)
            {
                awaitingExclusionSelection = false;
                return;
            }

            try
            {
                var structure = BuildMergedMenuStructure();
                if (structure == null)
                {
                    awaitingExclusionSelection = false;
                    Debug.LogWarning("Failed to build menu structure for exclusion selection.");
                    return;
                }

                var selectionRoot = CloneMenuStructure(structure);
                AssignMenuPaths(selectionRoot);

                // Restore exclusion paths from existing Excluded markers
                RestoreExclusionPathsFromMarkers(selectionRoot);

                 if (!HasSelectableExclusionItems(selectionRoot))
                 {
                     awaitingExclusionSelection = false;
                     // Don't clear configuredExclusionPaths here - we want to preserve restored paths
            // Debug.Log("No Modular Avatar generated items found for exclusion. Skipping selection window.");
                     TryConvertMenuOnAvatarLoad(avatar);
                     return;
                 }

                awaitingExclusionSelection = true;
                activeExclusionSelectionWindow = ExclusionSelectionWindow.ShowWindow(this,
                    avatar.name,
                    selectionRoot,
                    configuredExclusionPaths);
            }
            catch (Exception e)
            {
                awaitingExclusionSelection = false;
                Debug.LogError($"Failed to start exclusion selection workflow: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                Repaint();
            }
        }

        private void RestoreExclusionPathsFromMarkers(MergedMenuItem root)
        {
            if (root == null || selectedAvatar == null) return;

            configuredExclusionPaths.Clear();

            // Method 1: Scan the menu structure for Excluded markers
            CollectExclusionPathsFromMarkers(root);

            // Method 2: Directly scan avatar for Excluded markers (more reliable)
            // This catches any Excluded items that might not be in the menu structure
            if (IsModularAvatarAvailable())
            {
                var menuItemType = GetModularAvatarMenuItemType();
                var installerType = GetModularAvatarMenuInstallerType();

                if (menuItemType != null || installerType != null)
                {
                    var allTransforms = selectedAvatar.GetComponentsInChildren<Transform>(true);
                    foreach (var t in allTransforms)
                    {
                        if (t == null || t.gameObject == null) continue;
                        var go = t.gameObject;

                        // Check if this GameObject has an Excluded marker
                        if (go.GetComponent<ExprMenuVisualizerExcluded>() == null) continue;

                        // Check if this GameObject has a relevant MA component
                        Component sourceComponent = null;
                        if (menuItemType != null)
                        {
                            sourceComponent = go.GetComponent(menuItemType);
                        }
                        if (sourceComponent == null && installerType != null)
                        {
                            sourceComponent = go.GetComponent(installerType);
                        }

                        if (sourceComponent != null)
                        {
                            // Try to find the path for this item in the menu structure
                            var path = FindPathForComponent(root, sourceComponent);
                            if (!string.IsNullOrEmpty(path))
                            {
                                configuredExclusionPaths.Add(path);
                            }
                        }
                    }
                }
            }
        }

        private string FindPathForComponent(MergedMenuItem item, Component component)
        {
            if (item == null || component == null) return null;

            if (ReferenceEquals(item.sourceComponent, component))
            {
                return item.fullPath;
            }

            if (item.children != null)
            {
                foreach (var child in item.children)
                {
                    var path = FindPathForComponent(child, component);
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
            }

            return null;
        }

        private void CollectExclusionPathsFromMarkers(MergedMenuItem item)
        {
            if (item == null) return;

            // Check if this item has an Excluded marker
            if (item.sourceComponent != null)
            {
                var go = item.sourceComponent.gameObject;
                if (go != null && go.GetComponent<ExprMenuVisualizerExcluded>() != null)
                {
                    if (!string.IsNullOrEmpty(item.fullPath))
                    {
                        configuredExclusionPaths.Add(item.fullPath);
                    }
                }
            }

            // Recursively check children
            if (item.children != null)
            {
                foreach (var child in item.children)
                {
                    CollectExclusionPathsFromMarkers(child);
                }
            }
        }

        private void CloseActiveExclusionSelectionWindow()
        {
            if (activeExclusionSelectionWindow == null) return;
            activeExclusionSelectionWindow.ForceCloseWithoutDispatch();
            activeExclusionSelectionWindow = null;
        }

        private bool HasSelectableExclusionItems(MergedMenuItem root)
        {
            if (root == null) return false;
            if (IsItemSelectableForExclusion(root)) return true;
            if (root.children == null) return false;
            foreach (var child in root.children)
            {
                if (HasSelectableExclusionItems(child))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsItemSelectableForExclusion(MergedMenuItem item)
        {
            if (item == null || !IsModularAvatarItem(item))
            {
                return false;
            }

            if (!IsWithinAllowedDepth(item, 1))
            {
                return false;
            }

            if (item.sourceComponent == null)
            {
                return false;  // sourceComponentがない項目は選択不可
            }

            // 生成されたコンポーネントは除外
            if (IsGeneratedMenuComponent(item.sourceComponent))
            {
                return false;
            }

            // 既に除外マーカーが付いている項目は表示しない
            if (item.sourceComponent.GetComponent<ExprMenuVisualizerExcluded>() != null)
            {
                return false;
            }

            // 既にIncludedマーカーが付いている項目は表示しない
            if (item.sourceComponent.GetComponent<ExprMenuVisualizerIncluded>() != null)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 除外項目がMenu Install Targetを追加可能かを判定
        /// 親にMenu Installerがある、または除外項目自身がMenu Installerである場合
        /// </summary>
        private bool CanExcludedItemBeRepositioned(MergedMenuItem item)
        {
            if (item == null || !item.isReadOnly)
            {
                return false;  // 除外項目でない
            }

            if (item.sourceComponent == null)
            {
                return false;  // sourceComponentがない
            }

            // 生成されたコンポーネントは位置変更不可
            if (IsGeneratedMenuComponent(item.sourceComponent))
            {
                return false;
            }

            var installerType = GetModularAvatarMenuInstallerType();
            if (installerType == null)
            {
                return false;  // Modular Avatarが利用不可
            }

            // 除外項目自身がInstallerである場合
            if (item.sourceComponent.GetComponent(installerType) != null)
            {
                return true;
            }

            // 親にInstallerがあるか確認
            var parentItem = FindParentMenuItem(item, editedMenuStructure ?? BuildMergedMenuStructure());
            if (parentItem == null || parentItem.sourceComponent == null)
            {
                return false;
            }

            return parentItem.sourceComponent.GetComponent(installerType) != null;
        }

        /// <summary>
        /// 指定のアイテムの親を検索（ExclusionSelectionWindowと同様のロジック）
        /// </summary>
        private MergedMenuItem FindParentMenuItem(MergedMenuItem target, MergedMenuItem root)
        {
            if (root == null || target == null)
                return null;

            return FindParentMenuItemRecursive(root, target);
        }

        private MergedMenuItem FindParentMenuItemRecursive(MergedMenuItem current, MergedMenuItem target)
        {
            if (current == null)
                return null;

            if (current.children != null)
            {
                foreach (var child in current.children)
                {
                    if (child == target)
                        return current;

                    var found = FindParentMenuItemRecursive(child, target);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }

        private bool IsWithinAllowedDepth(MergedMenuItem item, int maxDepth)
        {
            if (item == null)
            {
                return false;
            }

            var path = item.fullPath;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            int depth = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/')
                {
                    depth++;
                }
            }

            return depth <= maxDepth;
        }

        internal void ApplyExclusionSelection(HashSet<string> selectedPaths, bool deferConversion)
        {
            configuredExclusionPaths.Clear();
            if (selectedPaths != null)
            {
                foreach (var path in selectedPaths)
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        configuredExclusionPaths.Add(path);
                    }
                }
            }

            // Diagnostic: log selected exclusion paths
            try
            {
                if (configuredExclusionPaths.Count > 0)
                {
                    Debug.LogWarning($"ApplyExclusionSelection: {configuredExclusionPaths.Count} paths selected: {string.Join(", ", configuredExclusionPaths)}");
                }
                else
                {
                    Debug.LogWarning("ApplyExclusionSelection: No exclusion paths selected");
                }
                // deferConversion flag logged for debugging previously; removed in final
            }
            catch { }

            // 除外された項目にマーカーを付与
            MarkExcludedItemsWithComponent(selectedPaths);

            awaitingExclusionSelection = false;
            activeExclusionSelectionWindow = null;

            MarkMenuStructureDirty();
            if (!editMode)
            {
                UpdateCurrentMenuItems();
            }
            else
            {
                // 編集モード中の場合は、編集用構造を再生成して除外マーカーを反映
                editedMenuStructure = CloneMenuStructure(BuildMergedMenuStructure());
            }

            // If the avatar already has a generated "メニュー項目" root (i.e. previously converted),
            // we need to apply Menu Install Targets for newly excluded items immediately.
            // TryConvertMenuOnAvatarLoad will skip conversion when already converted, so ensure
            // excluded items are post-processed here so Menu Install Target components get added.
            try
            {
                if (IsAlreadyConvertedToModularAvatar(selectedAvatar))
                {
                    // Rebuild merged structure to pick up the newly-applied Excluded markers
                    cachedMenuStructure = null;
                    menuStructureDirty = true;
                    var updatedMenuStructure = BuildMergedMenuStructure();
                    if (updatedMenuStructure != null)
                    {
                        // First, process non-excluded (included) MA components on the avatar:
                        // add Included markers, remove installers, and disable auto-rename on MA parameters.
                        try
                        {
                            MarkExistingModularAvatarComponentsAsEditorOnly(updatedMenuStructure, configuredExclusionPaths);
                        }
                        catch (Exception exMark)
                        {
                            Debug.LogWarning($"ApplyExclusionSelection: MarkExistingModularAvatarComponents failed: {exMark.Message}");
                        }

                        // Then apply MenuInstallTarget to excluded items so they keep their installer-based placement.
                        ApplyMenuInstallTargetsToExcludedItems(updatedMenuStructure, configuredExclusionPaths);

                        // Also ensure missing generated GameObjects for newly-added MA items are created
                        try
                        {
                            EnsureGeneratedMenuItemsForConvertedAvatar(updatedMenuStructure, configuredExclusionPaths);
                        }
                        catch (Exception ex2)
                        {
                            Debug.LogWarning($"ApplyExclusionSelection: EnsureGeneratedMenuItems failed: {ex2.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ApplyExclusionSelection: post-process failed: {ex.Message}");
            }

            if (!deferConversion)
            {
                try
                {
                    TryConvertMenuOnAvatarLoad(selectedAvatar);
                }
                catch (Exception) { }
            }
            Repaint();
        }

        private void MarkExcludedItemsWithComponent(HashSet<string> excludedPaths)
        {
            if (excludedPaths == null || excludedPaths.Count == 0 || selectedAvatar == null)
            {
                return;
            }

            // メニュー構造を再構築して、除外パスに該当する項目を探す
            var menuStructure = BuildMergedMenuStructure();
            if (menuStructure == null) return;

            MarkExcludedItemsRecursive(menuStructure, excludedPaths);
        }

        private void MarkExcludedItemsRecursive(MergedMenuItem item, HashSet<string> excludedPaths)
        {
            if (item == null) return;

            // このアイテムが除外パスに含まれている場合
            if (!string.IsNullOrEmpty(item.fullPath) && excludedPaths.Contains(item.fullPath))
            {
                // sourceComponentがあり、まだマーカーが付いていない場合
                if (item.sourceComponent != null &&
                    item.sourceComponent.GetComponent<ExprMenuVisualizerExcluded>() == null)
                {
                    Undo.RecordObject(item.sourceComponent.gameObject, "Mark as excluded");
                    EnsureAddComponent<ExprMenuVisualizerExcluded>(item.sourceComponent.gameObject);
                    EditorUtility.SetDirty(item.sourceComponent.gameObject);
                    // marked as excluded (no debug log in final)
                }
                else if (item.sourceComponent == null)
                {
                    // sourceComponent is null; debug helper was removed per request
                }
            }

            // 子要素を再帰的に処理
            if (item.children != null)
            {
                foreach (var child in item.children)
                {
                    MarkExcludedItemsRecursive(child, excludedPaths);
                }
            }
        }

        // デバッグ: 除外パスに該当する候補をツリーやシーンから列挙して表示する
        // Debug helper removed in final build

        /// <summary>
        /// 除外項目にMenu Install Targetを自動追加する処理を実行
        /// </summary>
        private void ApplyMenuInstallTargetsToExcludedItems(MergedMenuItem root, HashSet<string> excludedPaths)
        {
            if (root == null || excludedPaths == null)
                return;

            // Debug.Log($"[ApplyMenuInstallTargets] Processing with {excludedPaths.Count} excluded paths");
            ApplyMenuInstallTargetsRecursive(root, root, excludedPaths);
            // Debug.Log($"[ApplyMenuInstallTargets] Complete");
        }

        private void ApplyMenuInstallTargetsRecursive(MergedMenuItem current, MergedMenuItem root, HashSet<string> excludedPaths)
        {
            if (current == null) return;

            // 現在のアイテムが除外パスに含まれている場合
            if (!string.IsNullOrEmpty(current.fullPath) && excludedPaths.Contains(current.fullPath))
            {
                // Debug.Log($"[ApplyMenuInstallTargets] Found excluded item: {current.fullPath}");

                // 除外項目がRepositionedできるかを確認
                bool canReposition = CanExcludedItemBeRepositioned(current);
                // Debug.Log($"[ApplyMenuInstallTargets] CanExcludedItemBeRepositioned('{current.fullPath}'): {canReposition}, sourceComponent: {current.sourceComponent != null}");

                if (canReposition)
                {
                    // 親アイテムを探す
                    var parentItem = FindParentMenuItem(current, root);
                    // Debug.Log($"[ApplyMenuInstallTargets] Parent: {parentItem?.name}, has sourceComponent: {parentItem?.sourceComponent != null}");

                    // 親アイテムが見つからない場合のみスキップ
                    if (parentItem == null)
                    {
                        // Debug.Log($"[ApplyMenuInstallTargets] Skipping '{current.fullPath}': parent item not found");
                    }
                    else
                    {
                        // 除外項目自体がModular Avatarメニュー項目なら処理を続行
                        // (AddMenuInstallTargetToExcludedItem内でexcludedItem.sourceComponentをチェック)
                        AddMenuInstallTargetToExcludedItem(current, parentItem);
                    }
                }
                else
                {
                    // Debug.Log($"[ApplyMenuInstallTargets] Skipped '{current.fullPath}': CanExcludedItemBeRepositioned returned false");
                }
            }

            // 子要素を再帰的に処理
            if (current.children != null)
            {
                foreach (var child in current.children)
                {
                    ApplyMenuInstallTargetsRecursive(child, root, excludedPaths);
                }
            }
        }

        private void AddMenuInstallTargetToExcludedItem(MergedMenuItem excludedItem, MergedMenuItem parentItem)
        {
            
            if (excludedItem == null || excludedItem.sourceComponent == null)
                return;

            // 親項目が存在しない場合のみスキップ（親のsourceComponentは不要）
            if (parentItem == null)
            {
                LogDetail($"Skipping Menu Install Target for '{excludedItem.name}': parent item not found");
                return;
            }

            var installTargetType = GetModularAvatarMenuInstallTargetType();
            if (installTargetType == null)
                return;

            var installerType = GetModularAvatarMenuInstallerType();
            if (installerType == null)
                return;

            // 親のInstallerを優先的に取得
            Component installer = null;
            if (parentItem.sourceComponent != null)
            {
                installer = parentItem.sourceComponent.GetComponent(installerType);
            }

            // 親にInstallerがない場合、除外項目自身のInstallerを使用
            if (installer == null)
            {
                installer = excludedItem.sourceComponent.GetComponent(installerType);
            }

            // Installerが見つからない場合のみリターン
            
            if (installer == null)
            {
                LogDetail($"Skipping Menu Install Target for '{excludedItem.name}': no installer found");
                
                return;
            }

            try
            {
                // 除外項目の GameObject を「メニュー項目」の直下から検索して見つける
                Transform rootTransform = null;
                GameObject excludedItemGameObject = null;

                // 「メニュー項目」コンテナを探す
                var selectedAvatarTransform = selectedAvatar.transform;
                    if (selectedAvatarTransform != null)
                    {
                        // Prefer GUID-based markers first for robust matching across sessions
                        rootTransform = FindMenuItemRoot(selectedAvatar);
                        if (rootTransform != null)
                        {
                            try
                            {
                                var guidMarkers = rootTransform.GetComponentsInChildren<ExprMenuVisualizerGeneratedGuid>(true);
                                if (guidMarkers != null && guidMarkers.Length > 0)
                                {
                                    // Exact originalMenuPath match
                                    foreach (var gm in guidMarkers)
                                    {
                                        if (gm == null) continue;
                                        try
                                        {
                                            if (!string.IsNullOrEmpty(gm.originalMenuPath) &&
                                                string.Equals(CanonicalizeMenuFullPath(gm.originalMenuPath), CanonicalizeMenuFullPath(excludedItem.fullPath), StringComparison.Ordinal))
                                            {
                                                excludedItemGameObject = gm.gameObject;
                                                break;
                                            }
                                        }
                                        catch { }
                                    }

                                    // Ends-with fallback on originalMenuPath
                                    if (excludedItemGameObject == null)
                                    {
                                        foreach (var gm in guidMarkers)
                                        {
                                            if (gm == null || string.IsNullOrEmpty(gm.originalMenuPath)) continue;
                                                var gOriginal = CanonicalizeMenuFullPath(gm.originalMenuPath);
                                                if (gOriginal.EndsWith("/" + excludedItem.name, StringComparison.Ordinal) ||
                                                    gOriginal.EndsWith(excludedItem.name, StringComparison.Ordinal))
                                                {
                                                    excludedItemGameObject = gm.gameObject;
                                                    break;
                                                }
                                        }
                                    }
                                }
                                    
                            }
                            catch { }

                            // Fallback to metadata.fullPath matching if GUID markers didn't help
                            if (excludedItemGameObject == null)
                            {
                                try
                                {
                                    var candidateMetas = rootTransform.GetComponentsInChildren<ExprMenuVisualizerGeneratedMetadata>(true);
                                        foreach (var meta in candidateMetas)
                                    {
                                        if (meta == null) continue;
                                        if (string.Equals(CanonicalizeMenuFullPath(meta.fullPath), CanonicalizeMenuFullPath(excludedItem.fullPath), StringComparison.Ordinal))
                                        {
                                            excludedItemGameObject = meta.gameObject;
                                            break;
                                        }
                                    }

                                    // Fallback: try ends-with match on fullPath to handle different root name prefixes
                                    if (excludedItemGameObject == null)
                                    {
                                        foreach (var meta in candidateMetas)
                                        {
                                            if (meta == null || string.IsNullOrEmpty(meta.fullPath)) continue;
                                            var mFull = CanonicalizeMenuFullPath(meta.fullPath);
                                            if (mFull.EndsWith("/" + excludedItem.name, StringComparison.Ordinal) ||
                                                mFull.EndsWith(excludedItem.name, StringComparison.Ordinal))
                                            {
                                                excludedItemGameObject = meta.gameObject;
                                                // fallback matched meta.fullPath
                                                break;
                                            }
                                        }
                                    }
                                }
                                catch { }

                                // Fallback: legacy name-based search under direct children
                                if (excludedItemGameObject == null)
                                {
                                    for (int i = 0; i < rootTransform.childCount; i++)
                                    {
                                        var menuItemChild = rootTransform.GetChild(i);
                                        if (menuItemChild != null && menuItemChild.gameObject.name == excludedItem.name)
                                        {
                                            excludedItemGameObject = menuItemChild.gameObject;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                if (excludedItemGameObject == null)
                {
                    LogDetail($"Skipping: Cannot find generated GameObject for excluded item '{excludedItem.name}'");
                    

                    // Attempt fallback: create a generated menu item under the MenuItem root
                    if (rootTransform != null)
                    {
                        try
                        {
                            
                            var newGO = new GameObject(excludedItem.name);
                            Undo.RegisterCreatedObjectUndo(newGO, "Create Generated Menu Item");

                            // Determine parent transform: prefer the generated GameObject that corresponds
                            // to the parentItem.fullPath. If not found, fallback to the rootTransform.
                            Transform parentForNew = rootTransform;
                            try
                            {
                                if (parentItem != null && rootTransform != null)
                                {
                                    // Prefer GUID markers for parent matching first
                                    try
                                    {
                                        var parentGuidMarkers = rootTransform.GetComponentsInChildren<ExprMenuVisualizerGeneratedGuid>(true);
                                        if (parentGuidMarkers != null)
                                        {
                                            foreach (var pg in parentGuidMarkers)
                                            {
                                                if (pg == null) continue;
                                                try
                                                {
                                                    if (!string.IsNullOrEmpty(pg.originalMenuPath) &&
                                                        string.Equals(CanonicalizeMenuFullPath(pg.originalMenuPath), CanonicalizeMenuFullPath(parentItem.fullPath), StringComparison.Ordinal))
                                                    {
                                                        parentForNew = pg.gameObject.transform;
                                                        break;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                    }
                                    catch { }

                                    // Fallback to metadata.fullPath matching
                                    if (parentForNew == rootTransform)
                                    {
                                        try
                                        {
                                            var parentCandidateMetas = rootTransform.GetComponentsInChildren<ExprMenuVisualizerGeneratedMetadata>(true);
                                            for (int pi = 0; pi < parentCandidateMetas.Length; pi++)
                                            {
                                                var pm = parentCandidateMetas[pi];
                                                if (pm == null) continue;
                                                if (string.Equals(CanonicalizeMenuFullPath(pm.fullPath), CanonicalizeMenuFullPath(parentItem.fullPath), StringComparison.Ordinal))
                                                {
                                                    parentForNew = pm.gameObject.transform;
                                                    break;
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }

                            // Attach under the chosen parent and reset local transform
                            Undo.SetTransformParent(newGO.transform, parentForNew, "Attach generated menu item");
                            newGO.transform.localPosition = Vector3.zero;
                            newGO.transform.localRotation = Quaternion.identity;
                            newGO.transform.localScale = Vector3.one;
                            newGO.SetActive(true);

                            // Add Generated marker
                            try { newGO.AddComponent<ExprMenuVisualizerGenerated>(); } catch { }

                            // Add GeneratedMetadata and set fullPath
                            ExprMenuVisualizerGeneratedMetadata meta = null;
                            try
                            {
                                meta = newGO.AddComponent<ExprMenuVisualizerGeneratedMetadata>();
                                try { meta.fullPath = CanonicalizeMenuFullPath(excludedItem.fullPath); } catch { }
                            }
                            catch { }

                            // Add Menu Install Target component and set installer if possible
                            Component createdInstallTarget = null;
                            if (installTargetType != null)
                            {
                                try
                                {
                                    createdInstallTarget = newGO.AddComponent(installTargetType) as Component;
                                    if (createdInstallTarget != null && installer != null)
                                    {
                                        var installerFieldLocal = installTargetType.GetField("installer");
                                        if (installerFieldLocal != null)
                                        {
                                            try
                                            {
                                                installerFieldLocal.SetValue(createdInstallTarget, installer);
                                                EditorUtility.SetDirty(newGO);
                                            }
                                            catch (Exception ex)
                                            {
                                                
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            // Attach GeneratedGuid marker (best-effort)
                            try { newGO.AddComponent<ExprMenuVisualizerGeneratedGuid>(); } catch { }

                            

                            // Use the newly created GO as the excludedItemGameObject for the rest of the method
                            excludedItemGameObject = newGO;
                        }
                        catch (Exception ex)
                        {
                        }
                    }
                    else
                    {
                        
                    }

                    if (excludedItemGameObject == null)
                    {
                        return;
                    }
                }

                // 除外項目の GameObject 自体に Menu Install Target コンポーネントを追加
                Component newInstallTarget = excludedItemGameObject.GetComponent(installTargetType);
                if (newInstallTarget == null)
                {
                    
                    Undo.RecordObject(excludedItemGameObject, "Add Menu Install Target Component");
                    newInstallTarget = excludedItemGameObject.AddComponent(installTargetType);
                }
                else
                {
                    
                    Undo.RecordObject(excludedItemGameObject, "Update Menu Install Target");
                }

                // Installerへの参照を自動設定
                var installerField = installTargetType.GetField("installer");
                if (installerField != null)
                {
                    try
                    {
                        installerField.SetValue(newInstallTarget, installer);
                        EditorUtility.SetDirty(excludedItemGameObject);
                        
                    }
                    catch (Exception ex)
                    {
                        
                    }

                    // Menu InstallerのinstallTargetMenuをnullに設定（Menu Install Target経由での配置に切り替え）
                    var installTargetMenuField = installerType.GetField("installTargetMenu");
                    if (installTargetMenuField != null)
                    {
                        try
                        {
                            installTargetMenuField.SetValue(installer, null);
                            EditorUtility.SetDirty(installer as UnityEngine.Object);
                            
                        }
                        catch (Exception ex)
                        {
                            
                        }
                    }
                    else
                    {
                        
                    }

            // Debug.Log($"Added/Updated Menu Install Target component to '{excludedItem.name}'");
                }
                else
                {
                    Debug.LogWarning($"Failed to find 'installer' field on ModularAvatarMenuInstallTarget ({installTargetType?.FullName})");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to add Menu Install Target for '{excludedItem.name}': {e.Message}\n{e.StackTrace}");
            }
        }

        internal void HandleExclusionSelectionWindowClosedWithoutSelection()
        {
            ApplyExclusionSelection(new HashSet<string>(StringComparer.Ordinal), false);
        }

        private void OnSelectedAvatarChanged(VRCAvatarDescriptor oldAvatar, VRCAvatarDescriptor newAvatar)
        {
            MarkMenuStructureDirty();
            editedMenuStructure = null;
            menuNavigationStack.Clear();
            selectedItems.Clear();
            draggedItem = null;
            isDragging = false;
            itemRects.Clear();
            currentMenuItems.Clear();
            dragTargetParent = null;
            dragTargetIndex = -1;
            maMenuItemSignatureMap.Clear();
            modularAvatarInstallTargets.Clear();
            RefreshExpressionParameterStates();
            awaitingExclusionSelection = false;
            configuredExclusionPaths.Clear();
            CloseActiveExclusionSelectionWindow();

            if (newAvatar != null)
            {
                // 新しいアバターが読み込まれた場合、編集モードを自動的にオン
                editMode = true;

                BeginExclusionSelectionWorkflow(newAvatar);
                // Diagnostic: log any unclassified Modular Avatar Menu Installer components
                try
                {
                    LogUnclassifiedMenuInstallers(newAvatar);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"LogUnclassifiedMenuInstallers failed: {e.Message}");
                }
            }

            if (!editMode && !awaitingExclusionSelection)
            {
                UpdateCurrentMenuItems();
            }
        }

        private bool MenuHasAnyControls(VRCExpressionsMenu menu)
        {
            if (menu?.controls == null || menu.controls.Count == 0)
            {
                return false;
            }

            foreach (var control in menu.controls)
            {
                if (control != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// アバターが既にModular Avatar構造に変換済みかを判定
        /// </summary>
        private bool IsAlreadyConvertedToModularAvatar(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return false;

            // 「メニュー項目」GameObjectが存在するか確認
            var menuItemRoot = FindMenuItemRoot(avatar);
            if (menuItemRoot == null) return false;

            // ModularAvatarMenuGroupコンポーネントが付いているか確認
            var menuGroupType = GetModularAvatarMenuGroupType();
            if (menuGroupType == null) return false;

            var menuGroupComponent = menuItemRoot.GetComponent(menuGroupType);
            if (menuGroupComponent == null) return false;

            // expressionsMenuが空のメニューに設定されているか確認
            var menu = avatar.expressionsMenu;
            if (menu != null && (menu.controls == null || menu.controls.Count == 0))
            {
                // 既に変換済み
                return true;
            }

            return false;
        }

        private void TryConvertMenuOnAvatarLoad(VRCAvatarDescriptor avatar)
        {
            // Invocation logging removed in final
            if (avatar == null)
            {
                return;
            }

            // Skip if already converted to Modular Avatar structure
            // 既にMA構造に変換済みの場合はスキップ（除外項目のMA Menu Install Targetを保護）
            if (IsAlreadyConvertedToModularAvatar(avatar))
            {
                LogDetail("TryConvertMenuOnAvatarLoad: Already converted to MA structure, skipping");
                return;
            }

            if (awaitingExclusionSelection)
            {
                return;
            }

            if (!IsModularAvatarAvailable())
            {
                return;
            }

            var mainMenu = avatar.expressionsMenu;
            // diagnostics removed: mainMenu and control count checks logged during debugging
            var hasControls = MenuHasAnyControls(mainMenu);

            // Fallback: if the avatar's assigned expressions menu appears empty, but our
            // built merged menu structure contains items (e.g., Modular Avatar items),
            // allow conversion to proceed. This covers cases where avatar.expressionsMenu
            // is an auto-assigned empty menu but there are MA-derived items to convert.
            if (!hasControls)
            {
                var merged = BuildMergedMenuStructure();
                bool mergedHasChildren = merged != null && merged.children != null && merged.children.Count > 0;
                if (!mergedHasChildren)
                {
                    // abort because both menus are empty
                    return;
                }
            }

            try
            {
                var menuStructure = BuildMergedMenuStructure();
                if (menuStructure == null)
                {
                    EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                        GetLocalizedText("メニュー構造の取得に失敗しました。", "Failed to build menu structure."), "OK");
                    return;
                }

                // ConvertMenuForAvatarAssignmentを使用してアバター割り当て時の処理を実行
                if (ConvertMenuForAvatarAssignment(menuStructure, configuredExclusionPaths, out var rootAssetPath))
                {
            // Debug.Log(GetLocalizedText(
            //                         $"ExpressionMenuをModular Avatar構造に変換し、空のルートメニューを割り当てました: {rootAssetPath}",
            //                         $"Converted ExpressionMenu to Modular Avatar hierarchy and assigned empty root menu: {rootAssetPath}"
            //                     ));

                    // 第2段階：メニュー項目生成完了後に最新のメニュー構造を再構築
                    // キャッシュを無効化して最新の構造を確実に取得
                    cachedMenuStructure = null;
                    menuStructureDirty = true;
                    var updatedMenuStructure = BuildMergedMenuStructure();
                    if (updatedMenuStructure != null)
                    {
                        ApplyMenuInstallTargetsToExcludedItems(updatedMenuStructure, configuredExclusionPaths);
                    }

                    Repaint();
                }
                else
                {
                    EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                        GetLocalizedText("Modular Avatarへの変換に失敗しました。コンソールのログを確認してください。",
                            "Failed to convert to Modular Avatar. Check the console for details."), "OK");
                }
            }
            catch (Exception e)
            {
                Debug.LogError(GetLocalizedText(
                    $"Modular Avatarへの変換中にエラーが発生しました: {e.Message}",
                    $"An error occurred while converting to Modular Avatar: {e.Message}"
                ));
                EditorUtility.DisplayDialog(GetLocalizedText("エラー", "Error"),
                    GetLocalizedText("Modular Avatarへの変換中にエラーが発生しました。", "An error occurred while converting to Modular Avatar."), "OK");
            }
        }
        
        private VRCExpressionsMenu.Control ConvertMergedItemToControl(MergedMenuItem item, string parentPath = "")
        {
            LogDetail($"    > ConvertMergedItemToControl: item='{item.name}', source='{item.source}', hasControl={item.control != null}, hasChildren={item.children?.Count ?? 0}");

            // Skip ModularAvatar items - they will be handled separately
            if (IsModularAvatarItem(item))
            {
                LogDetail($"    > ConvertMergedItemToControl: SKIPPING MA item '{item.name}' (source={item.source})");
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
                    LogDetail($"    > ConvertMergedItemToControl: Created submenu from {item.children.Count} children, submenu={(control.subMenu != null ? "created" : "null")}");
                }
                else
                {
                    control.subMenu = item.control.subMenu;
                    LogDetail($"    > ConvertMergedItemToControl: Using original control.subMenu={(control.subMenu != null ? control.subMenu.name : "null")}");
                }

                LogDetail($"    > ConvertMergedItemToControl: Created control '{control.name}', type={control.type}, hasSubMenu={control.subMenu != null}");
                return control;
            }
            else if (item.children != null && item.children.Count > 0)
            {
                // Create a submenu control for items that only have children (filter out MA items)
                var nonMAChildren = item.children.Where(c => !IsModularAvatarItem(c)).ToList();
                LogDetail($"    > ConvertMergedItemToControl: Item has {item.children.Count} children, {nonMAChildren.Count} non-MA children");
                if (nonMAChildren.Count > 0)
                {
                    var control = new VRCExpressionsMenu.Control();
                    control.name = item.name;
                    control.type = VRCExpressionsMenu.Control.ControlType.SubMenu;
                    string currentPath = string.IsNullOrEmpty(parentPath) ? item.name : $"{parentPath}_{item.name}";
                    control.subMenu = CreateSubmenuFromChildren(item.children, currentPath, item.name);

                    LogDetail($"    > ConvertMergedItemToControl: Created submenu control '{control.name}', hasSubMenu={control.subMenu != null}");
                    return control;
                }
            }

            LogDetail($"    > ConvertMergedItemToControl: Returning null for item '{item.name}'");
            return null;
        }
        
        private bool IsModularAvatarItem(MergedMenuItem item)
        {
            return item.source.StartsWith("MA_");
        }

        private void LogDetail(string message)
        {
            // 詳細ログ機能は削除済みのため、現在は何もしない
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

        /// <summary>
        /// Diagnostic helper: log Menu Installer components that appear to be "unclassified".
        /// Condition: the avatar has a MenuItem root (GeneratedMenuRootName) AND there exist
        /// installer components under the avatar whose GameObject is not under the MenuItem root
        /// and which do not have Generated/Excluded markers.
        /// </summary>
        private void LogUnclassifiedMenuInstallers(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return;

            // Find the menu item root (marker-preferred)
            var root = FindMenuItemRoot(avatar);
            if (root == null)
            {
                // no menu item root -> nothing to check
                return;
            }

            var installerType = GetModularAvatarMenuInstallerType();
            if (installerType == null)
            {
                // Modular Avatar package not available or type not resolved
                return;
            }

            var avatarGO = avatar.gameObject;
            if (avatarGO == null) return;

            var comps = avatarGO.GetComponentsInChildren(installerType, true) as Component[];
            if (comps == null || comps.Length == 0) return;

            var unclassified = new List<string>();

            foreach (var c in comps)
            {
                if (c == null) continue;
                var go = c.gameObject;
                if (go == null) continue;

                // If the installer GameObject is under the menu root, consider it classified
                try
                {
                    if (go.transform.IsChildOf(root))
                        continue;
                }
                catch { }

                // If it already has Generated (legacy) or GUID marker, Excluded, or Included markers, or is EditorOnly, consider it classified
                if (go.GetComponent<ExprMenuVisualizerGenerated>() != null) continue;
                if (go.GetComponent<ExprMenuVisualizerGeneratedGuid>() != null) continue;
                if (go.GetComponent<ExprMenuVisualizerExcluded>() != null) continue;
                if (go.GetComponent<ExprMenuVisualizerIncluded>() != null) continue;
                if (go.GetComponent<ExprMenuVisualizerGeneratedRoot>() != null) continue;
                // Also skip objects that are marked EditorOnly (they were processed previously)
                try
                {
                    if (IsEditorOnlyObject(go)) continue;
                }
                catch { }

                // Otherwise, consider it unclassified — report its path relative to the avatar
                string path = GetTransformPathRelativeTo(go.transform, avatar.transform);
                unclassified.Add(string.IsNullOrEmpty(path) ? go.name : path);
            }

            if (unclassified.Count > 0)
            {
                var msg = new StringBuilder();
                msg.AppendLine($"[VRCExpressionMenuVisualizer] Found {unclassified.Count} unclassified Menu Installer(s) while MenuItem root exists:");
                foreach (var p in unclassified)
                {
                    msg.AppendLine($"  - {p}");
                }
                Debug.LogWarning(msg.ToString());
            }
        }

        private string GetTransformPathRelativeTo(Transform t, Transform root)
        {
            if (t == null) return string.Empty;
            if (root == null) return t.name;
            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != root.parent)
            {
                parts.Add(cur.name);
                if (cur == root) break;
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
        
        private VRCExpressionsMenu GenerateExpressionMenuFromMergedStructureWithAssets(MergedMenuItem rootItem, string basePath)
        {
            LogDetail("===== GenerateExpressionMenuFromMergedStructureWithAssets: START =====");
            LogDetail($"    Root item has {rootItem?.children?.Count ?? 0} children");
            LogDetail($"    Base path: {basePath}");

            // Create a dictionary to track created submenu assets
            var submenuAssets = new Dictionary<string, VRCExpressionsMenu>();

            // Generate the root menu
            var rootMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            rootMenu.controls = new List<VRCExpressionsMenu.Control>();
            rootMenu.name = selectedAvatar.name + "_EditedExpressionMenu";
            LogDetail($"    Created root menu: {rootMenu.name}");

            // Process all children recursively
            LogDetail("    Processing root children...");
            ProcessChildrenWithAssets(rootItem.children, rootMenu.controls, submenuAssets, basePath, "");

            LogDetail($"    Root menu generated with {rootMenu.controls.Count} controls");
            LogDetail("===== GenerateExpressionMenuFromMergedStructureWithAssets: END =====");
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
            LogDetail($"      >> CreateControlWithAssets: item='{item.name}', source='{item.source}', hasControl={item.control != null}, childCount={item.children?.Count ?? 0}");

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
                    string submenuPath = CombineSanitizedPath(currentPath, item.name, "Submenu");
                    LogDetail($"      >> CreateControlWithAssets: Creating submenu asset for '{item.name}' with {item.children.Count} children");
                    control.subMenu = CreateSubmenuAsset(item, submenuAssets, basePath, submenuPath);
                    LogDetail($"      >> CreateControlWithAssets: Submenu asset created: {(control.subMenu != null ? control.subMenu.name : "null")}");
                }
                else
                {
                    control.subMenu = item.control.subMenu;
                    LogDetail($"      >> CreateControlWithAssets: Using original control.subMenu={(control.subMenu != null ? control.subMenu.name : "null")}");
                }

                LogDetail($"      >> CreateControlWithAssets: Created control '{control.name}', type={control.type}, hasSubMenu={control.subMenu != null}");
                return control;
            }
            else if (item.children != null && item.children.Count > 0)
            {
                // Create submenu control for items that only have children
                var nonMAChildren = item.children.Where(c => !IsModularAvatarItem(c)).ToList();
                LogDetail($"      >> CreateControlWithAssets: Item has {item.children.Count} children, {nonMAChildren.Count} non-MA children");
                if (nonMAChildren.Count > 0)
                {
                    var control = new VRCExpressionsMenu.Control();
                    control.name = item.name;
                    control.type = VRCExpressionsMenu.Control.ControlType.SubMenu;

                    string submenuPath = CombineSanitizedPath(currentPath, item.name, "Submenu");
                    LogDetail($"      >> CreateControlWithAssets: Creating submenu asset for '{item.name}'");
                    control.subMenu = CreateSubmenuAsset(item, submenuAssets, basePath, submenuPath);

                    LogDetail($"      >> CreateControlWithAssets: Created submenu control '{control.name}', hasSubMenu={control.subMenu != null}");
                    return control;
                }
            }

            LogDetail($"      >> CreateControlWithAssets: Returning null for item '{item.name}'");
            return null;
        }
        
        private VRCExpressionsMenu CreateSubmenuAsset(MergedMenuItem item, Dictionary<string, VRCExpressionsMenu> submenuAssets, 
            string basePath, string submenuPath)
        {
            string sanitizedPath = SanitizeForAssetPath(submenuPath, "Submenu");
            
            // Check if we already created this submenu
            if (submenuAssets.ContainsKey(sanitizedPath))
            {
                return submenuAssets[sanitizedPath];
            }
            
            // Create the submenu
            var submenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            submenu.controls = new List<VRCExpressionsMenu.Control>();
            submenu.name = item.name;
            
            // Process children recursively
            ProcessChildrenWithAssets(item.children, submenu.controls, submenuAssets, basePath, sanitizedPath);
            
            // Save as asset in folder
            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{basePath}/{sanitizedPath}.asset");
            AssetDatabase.CreateAsset(submenu, assetPath);
            
            // Store in dictionary for future reference
            submenuAssets[sanitizedPath] = submenu;
            
            // Debug.Log($"Created submenu asset: {assetPath}");
            
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

            // Debug.Log($"Updated {processedInstallers.Count} ModularAvatar Menu Installers");
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
            // Debug.Log($"Updated MA Menu Installer '{installer.name}' to target '{bestTargetMenu.name}'");
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
                var installerType = installer.GetType();
                var menuToAppendField = installerType.GetField("menuToAppend");
                var installTargetField = installerType.GetField("installTargetMenu");

                if (installTargetField != null)
                {
                    // Preserve original menuToAppend before updating target
                    VRCExpressionsMenu originalMenuToAppend = menuToAppendField?.GetValue(installer) as VRCExpressionsMenu;

                    installTargetField.SetValue(installer, targetMenu);

                    // Restore original menuToAppend if it was set
                    if (menuToAppendField != null && originalMenuToAppend != null)
                    {
                        menuToAppendField.SetValue(installer, originalMenuToAppend);
                    }

                    EditorUtility.SetDirty(installer);
            // Debug.Log($"Updated MA Installer '{installer.name}' - target: {targetMenu?.name}, preserved menuToAppend: {originalMenuToAppend?.name}");
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

        private static class ModularAvatarMenuBridge
        {
            private static bool reflectionInitialized;
            private static bool reflectionAvailable;

            private static Type virtualMenuType;
            private static Type virtualMenuNodeType;
            private static Type buildContextType;
            private static FieldInfo virtualMenuNodeControlsField;
            private static FieldInfo virtualMenuNodeKeyField;
            private static FieldInfo virtualControlSubmenuField;
            private static PropertyInfo rootMenuNodeProperty;
            private static MethodInfo forAvatarMethod;
            private static ConstructorInfo buildContextCtor;
            private static FieldInfo buildContextPostProcessField;
            private static FieldInfo menuItemControlField;
            private static FieldInfo menuItemLabelField;
            private static Type menuNodesUnderType;
            private static FieldInfo menuNodesUnderRootField;
            private static Type menuSourceType;

            private static bool EnsureReflection()
            {
                if (reflectionInitialized) return reflectionAvailable;
                reflectionInitialized = true;

                try
                {
                    const string runtimeAssembly = "nadena.dev.modular-avatar.core";
                    const string editorAssembly = "nadena.dev.modular-avatar.core.editor";

                    virtualMenuType = Type.GetType($"nadena.dev.modular_avatar.core.editor.menu.VirtualMenu, {editorAssembly}");
                    virtualMenuNodeType = Type.GetType($"nadena.dev.modular_avatar.core.menu.VirtualMenuNode, {runtimeAssembly}");
                    buildContextType = Type.GetType($"nadena.dev.modular_avatar.core.editor.BuildContext, {editorAssembly}");
                    menuNodesUnderType = Type.GetType($"nadena.dev.modular_avatar.core.menu.MenuNodesUnder, {runtimeAssembly}");
                    menuSourceType = Type.GetType($"nadena.dev.modular_avatar.core.menu.MenuSource, {runtimeAssembly}");

                    if (virtualMenuType == null || virtualMenuNodeType == null || buildContextType == null)
                    {
                        throw new InvalidOperationException("Modular Avatar editor types not found.");
                    }

                    virtualMenuNodeControlsField = virtualMenuNodeType.GetField("Controls", BindingFlags.Instance | BindingFlags.Public);
                    virtualMenuNodeKeyField = virtualMenuNodeType.GetField("NodeKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var virtualControlType = Type.GetType($"nadena.dev.modular_avatar.core.menu.VirtualControl, {runtimeAssembly}");
                    virtualControlSubmenuField = virtualControlType?.GetField("SubmenuNode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    menuNodesUnderRootField = menuNodesUnderType?.GetField("root", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    rootMenuNodeProperty = virtualMenuType.GetProperty("RootMenuNode", BindingFlags.Instance | BindingFlags.Public);
                    forAvatarMethod = virtualMenuType.GetMethod("ForAvatar", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    buildContextCtor = buildContextType.GetConstructor(new[] { typeof(VRCAvatarDescriptor) })
                        ?? buildContextType.GetConstructor(new[] { typeof(GameObject) });
                    buildContextPostProcessField = buildContextType.GetField("PostProcessControls", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    var menuItemType = VRCExpressionMenuOrganizerWindow.GetModularAvatarMenuItemType();
                    if (menuItemType != null)
                    {
                        menuItemControlField = menuItemType.GetField("Control", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        menuItemLabelField = menuItemType.GetField("label", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }

                    if (virtualMenuNodeControlsField == null || virtualControlSubmenuField == null || rootMenuNodeProperty == null || forAvatarMethod == null)
                    {
                        throw new InvalidOperationException("Required Modular Avatar members not found.");
                    }

                    reflectionAvailable = true;
                }
                catch (Exception ex)
                {
                    reflectionAvailable = false;
                    Debug.LogWarning($"[ExpressionMenuVisualizer] Modular Avatar reflection unavailable: {ex.Message}");
                }

                return reflectionAvailable;
            }

            internal static MergedMenuItem Build(VRCExpressionMenuOrganizerWindow owner)
            {
                if (!EnsureReflection()) return null;
                if (owner?.selectedAvatar == null) return null;

                GameObject clonedAvatarRoot = null;
                VRCAvatarDescriptor clonedDescriptor = null;
                List<(Component original, Component clone)> componentPairs = null;

                try
                {
                    clonedAvatarRoot = CreateAvatarWorkingCopy(owner.selectedAvatar);
                    if (clonedAvatarRoot == null)
                    {
                        return null;
                    }

                    clonedDescriptor = clonedAvatarRoot.GetComponent<VRCAvatarDescriptor>();
                    if (clonedDescriptor == null)
                    {
                        Debug.LogWarning("[ExpressionMenuVisualizer] Temporary MA clone is missing VRCAvatarDescriptor.");
                        return null;
                    }

                    componentPairs = EnumerateComponentPairs(owner.selectedAvatar.gameObject, clonedAvatarRoot).ToList();

                    var menuItemType = VRCExpressionMenuOrganizerWindow.GetModularAvatarMenuItemType();
                    var menuInstallerType = VRCExpressionMenuOrganizerWindow.GetModularAvatarMenuInstallerType();
                    var installTargetType = VRCExpressionMenuOrganizerWindow.GetModularAvatarMenuInstallTargetType();

                    owner.maMenuItemSignatureMap.Clear();

                    if (componentPairs != null)
                    {
                        foreach (var pair in componentPairs)
                        {
                            if (pair.original == null) continue;
                            if (menuItemType != null && menuItemType.IsInstanceOfType(pair.original))
                            {
                                var originalControl = menuItemControlField?.GetValue(pair.original) as VRCExpressionsMenu.Control;
                                if (originalControl == null) continue;

                                var signatureControl = CloneControlForDisplay(originalControl);

                                var labelValue = menuItemLabelField?.GetValue(pair.original) as string;
                                signatureControl.name = string.IsNullOrEmpty(labelValue)
                                    ? pair.original.gameObject.name
                                    : labelValue;

                                owner.RegisterModularAvatarMenuSignature(pair.original, signatureControl);
                            }
                        }
                    }

                    var cloneToOriginalComponent = componentPairs != null
                        ? componentPairs
                            .Where(pair => pair.clone != null && pair.original != null)
                            .ToDictionary(pair => pair.clone, pair => pair.original, ReferenceEqualityComparer<Component>.Instance)
                        : new Dictionary<Component, Component>(ReferenceEqualityComparer<Component>.Instance);

                    object buildContext = null;
                    if (buildContextCtor != null)
                    {
                        var parameters = buildContextCtor.GetParameters();
                        object ctorArg = clonedDescriptor;
                        if (parameters.Length == 1)
                        {
                            if (parameters[0].ParameterType == typeof(VRCAvatarDescriptor))
                            {
                                ctorArg = clonedDescriptor;
                            }
                            else if (parameters[0].ParameterType == typeof(GameObject))
                            {
                                ctorArg = clonedAvatarRoot;
                            }
                        }

                        buildContext = buildContextCtor.Invoke(new[] { ctorArg });
                    }

                    var postProcessMap = buildContext != null && buildContextPostProcessField != null
                        ? buildContextPostProcessField.GetValue(buildContext) as IDictionary
                        : null;

                    var controlSources = new Dictionary<VRCExpressionsMenu.Control, Component>(ReferenceEqualityComparer<VRCExpressionsMenu.Control>.Instance);

                    if (postProcessMap != null && componentPairs != null)
                    {
                        foreach (var pair in componentPairs)
                        {
                            var cloneComponent = pair.clone;
                            var originalComponent = pair.original;

                            if (cloneComponent == null || originalComponent == null) continue;

                            // Track both MA Menu Items AND MA Menu Installers
                            bool isMenuItem = menuItemType != null && menuItemType.IsInstanceOfType(cloneComponent);
                            bool isMenuInstaller = menuInstallerType != null && menuInstallerType.IsInstanceOfType(cloneComponent);

                            if (!isMenuItem && !isMenuInstaller) continue;

                            Action<VRCExpressionsMenu.Control> existing = null;
                            if (postProcessMap.Contains(cloneComponent))
                            {
                                existing = postProcessMap[cloneComponent] as Action<VRCExpressionsMenu.Control>;
                            }

                            var capturedOriginal = originalComponent;
                            var capturedExisting = existing;

                            Action<VRCExpressionsMenu.Control> composite = ctrl =>
                            {
                                capturedExisting?.Invoke(ctrl);
                                if (ctrl != null)
                                {
                                    owner.LogDetail($"        [Bridge] Registering control '{ctrl.name}' from component '{capturedOriginal.gameObject.name}' (type: {(isMenuInstaller ? "MA_Installer" : "MA_MenuItem")})");
                                    controlSources[ctrl] = capturedOriginal;
                                }
                            };

                            postProcessMap[cloneComponent] = composite;
                        }
                    }

                    owner.modularAvatarInstallTargets.Clear();
                    if (installTargetType != null)
                    {
                        var targets = clonedAvatarRoot.GetComponentsInChildren(installTargetType, true);
                        foreach (var target in targets)
                        {
                            if (target is Component comp)
                            {
                                owner.modularAvatarInstallTargets.Add(comp);
                            }
                        }
                    }

                    // Collect MA Menu Installer menus to detect baked content later
                    owner.maInstallerMenus.Clear();
                    owner.LogDetail("    [Bridge] Collecting MA Menu Installer menus...");
                    owner.LogDetail($"    [Bridge] menuInstallerType is null: {menuInstallerType == null}");
                    if (menuInstallerType != null)
                    {
                        owner.LogDetail($"    [Bridge] menuInstallerType: {menuInstallerType.FullName}");
                        owner.LogDetail($"    [Bridge] Searching in clonedAvatarRoot: '{clonedAvatarRoot.name}'");

                        var installers = clonedAvatarRoot.GetComponentsInChildren(menuInstallerType, true);
                        owner.LogDetail($"    [Bridge] Found {installers.Length} MA Menu Installer components (cloned)");

                        // Get menuToAppend field
                        var menuToAppendField = menuInstallerType.GetField("menuToAppend",
                            BindingFlags.Public | BindingFlags.Instance);
                        owner.LogDetail($"    [Bridge] menuToAppendField is null: {menuToAppendField == null}");

                        if (menuToAppendField != null && componentPairs != null)
                        {
                            foreach (var installer in installers)
                            {
                                if (installer is Component clonedInstallerComp)
                                {
                                    owner.LogDetail($"    [Bridge] Processing cloned installer on GameObject: '{clonedInstallerComp.gameObject.name}'");

                                    // Find the original installer from componentPairs
                                    Component originalInstallerComp = null;
                                    foreach (var pair in componentPairs)
                                    {
                                        if (pair.clone == clonedInstallerComp)
                                        {
                                            originalInstallerComp = pair.original;
                                            break;
                                        }
                                    }

                                    if (originalInstallerComp != null)
                                    {
                                        owner.LogDetail($"    [Bridge] Found original installer on GameObject: '{originalInstallerComp.gameObject.name}'");

                                        // Get menuToAppend from the ORIGINAL installer (not the clone)
                                        var menuToAppend = menuToAppendField.GetValue(originalInstallerComp) as VRCExpressionsMenu;
                                        owner.LogDetail($"    [Bridge] menuToAppend value is null: {menuToAppend == null}");

                                        if (menuToAppend != null)
                                        {
                                            owner.maInstallerMenus.Add(menuToAppend);
                                            owner.LogDetail($"    [Bridge] Registered MA Installer menu: '{menuToAppend.name}' from '{originalInstallerComp.gameObject.name}'");
                                        }
                                    }
                                    else
                                    {
                                        owner.LogDetail($"    [Bridge] WARNING: Could not find original installer for cloned component on '{clonedInstallerComp.gameObject.name}'");
                                    }
                                }
                                else
                                {
                                    owner.LogDetail($"    [Bridge] Installer is not a Component: {installer?.GetType()?.FullName}");
                                }
                            }
                        }
                    }
                    owner.LogDetail($"    [Bridge] Total MA Installer menus: {owner.maInstallerMenus.Count}");

                    object virtualMenu;
                    try
                    {
                        virtualMenu = forAvatarMethod.Invoke(null, new[] { (object)clonedDescriptor, buildContext });
                    }
                    catch (TargetInvocationException tie)
                    {
                        throw tie.InnerException ?? tie;
                    }

                    if (virtualMenu == null) return null;

                    var rootNode = rootMenuNodeProperty.GetValue(virtualMenu);
                    if (rootNode == null) return null;

                    var visited = new HashSet<object>(ReferenceEqualityComparer<object>.Instance);
                    var rootItem = new MergedMenuItem
                    {
                        name = owner.GetLocalizedText("ルートメニュー", "Root Menu"),
                        source = "VRC"
                    };

                    owner.maControlMap.Clear();
                    owner.maComponentToControl.Clear();

                    PopulateChildren(owner, rootNode, rootItem, controlSources, visited, 0, cloneToOriginalComponent, menuItemType, menuInstallerType);

                    return rootItem;
                }
                finally
                {
                    if (clonedAvatarRoot != null)
                    {
                        UnityEngine.Object.DestroyImmediate(clonedAvatarRoot);
                    }
                }
            }

            private static void PopulateChildren(
                VRCExpressionMenuOrganizerWindow owner,
                object virtualNode,
                MergedMenuItem parent,
                Dictionary<VRCExpressionsMenu.Control, Component> controlSources,
                HashSet<object> visitedNodes,
                int depth,
                Dictionary<Component, Component> cloneToOriginalComponent,
                Type menuItemType,
                Type menuInstallerType)
            {
                if (virtualNode == null || parent == null) return;

                var nodeKey = virtualMenuNodeKeyField?.GetValue(virtualNode);
                if (nodeKey != null)
                {
                    if (visitedNodes.Contains(nodeKey))
                    {
                        return;
                    }
                    visitedNodes.Add(nodeKey);
                }

                if (!(virtualMenuNodeControlsField?.GetValue(virtualNode) is IEnumerable controls)) return;

                var maComponentQueue = BuildComponentQueueForNode(virtualNode, cloneToOriginalComponent, menuItemType, menuInstallerType);

                int index = 0;
                foreach (var ctrlObj in controls)
                {
                    if (!(ctrlObj is VRCExpressionsMenu.Control control))
                    {
                        continue;
                    }

                    if ((controlSources == null || !controlSources.ContainsKey(control)) && maComponentQueue != null && maComponentQueue.Count > 0)
                    {
                        var candidate = maComponentQueue.Dequeue();
                        if (candidate != null)
                        {
                            controlSources[control] = candidate;
                            owner.ConsumeModularAvatarMenuSignature(candidate, control);
                        }
                    }

                    var child = CreateMergedItem(owner, control, ctrlObj, controlSources, index++);
                    parent.children.Add(child);

                    var submenuNode = virtualControlSubmenuField?.GetValue(ctrlObj);
                    if (submenuNode != null)
                    {
                        // Check if this submenu is from an MA Installer - if so, skip processing children
                        bool shouldSkipChildren = false;
                        if (control?.subMenu != null && owner.maInstallerMenus.Contains(control.subMenu))
                        {
                            shouldSkipChildren = true;
                            owner.LogDetail($"      >> PopulateChildren: Skipping children for MA Installer menu '{control.subMenu.name}'");
                        }

                        if (!shouldSkipChildren)
                        {
                            PopulateChildren(owner, submenuNode, child, controlSources,
                                new HashSet<object>(visitedNodes, ReferenceEqualityComparer<object>.Instance), depth + 1,
                                cloneToOriginalComponent, menuItemType, menuInstallerType);
                        }
                    }
                }
            }

            private static Queue<Component> BuildComponentQueueForNode(
                object virtualNode,
                Dictionary<Component, Component> cloneToOriginalComponent,
                Type menuItemType,
                Type menuInstallerType)
            {
                if (virtualNode == null) return null;

                var nodeKey = virtualMenuNodeKeyField?.GetValue(virtualNode);
                if (nodeKey == null) return null;

                if (nodeKey is Component componentKey)
                {
                    if (cloneToOriginalComponent != null && cloneToOriginalComponent.TryGetValue(componentKey, out var originalComponent) &&
                        IsModularAvatarComponent(originalComponent, menuItemType, menuInstallerType))
                    {
                        var queue = new Queue<Component>();
                        queue.Enqueue(originalComponent);
                        return queue;
                    }
                }

                if (menuNodesUnderType != null && menuNodesUnderType.IsInstanceOfType(nodeKey))
                {
                    var root = menuNodesUnderRootField?.GetValue(nodeKey) as GameObject;
                    if (root == null) return null;

                    var queue = new Queue<Component>();
                    foreach (Transform child in root.transform)
                    {
                        if (child == null) continue;

                        Component menuSourceComponent = null;
                        var components = child.GetComponents<Component>();
                        foreach (var comp in components)
                        {
                            if (comp == null) continue;
                            if (menuSourceType != null && menuSourceType.IsInstanceOfType(comp))
                            {
                                menuSourceComponent = comp;
                                break;
                            }
                        }

                        if (menuSourceComponent == null && menuSourceType == null)
                        {
                            foreach (var comp in components)
                            {
                                if (comp == null) continue;
                                if (IsModularAvatarComponent(comp, menuItemType, menuInstallerType))
                                {
                                    menuSourceComponent = comp;
                                    break;
                                }
                            }
                        }

                        if (menuSourceComponent == null) continue;

                        if (!IsModularAvatarComponent(menuSourceComponent, menuItemType, menuInstallerType))
                        {
                            continue;
                        }

                        if (cloneToOriginalComponent == null || !cloneToOriginalComponent.TryGetValue(menuSourceComponent, out var originalComp) || originalComp == null)
                        {
                            continue;
                        }

                        queue.Enqueue(originalComp);
                    }

                    return queue.Count > 0 ? queue : null;
                }

                return null;
            }

            private static bool IsModularAvatarComponent(object componentObj, Type menuItemType, Type menuInstallerType)
            {
                if (!(componentObj is Component component)) return false;

                if (menuItemType != null && menuItemType.IsInstanceOfType(component))
                {
                    return true;
                }

                if (menuInstallerType != null && menuInstallerType.IsInstanceOfType(component))
                {
                    return true;
                }

                return false;
            }

            private static MergedMenuItem CreateMergedItem(
                VRCExpressionMenuOrganizerWindow owner,
                VRCExpressionsMenu.Control virtualControl,
                object virtualControlObject,
                Dictionary<VRCExpressionsMenu.Control, Component> controlSources,
                int index)
            {
                Component sourceComponent = null;
                var source = "VRC";
                VRCExpressionsMenu.Control displayControl;
                bool isMAInstaller = false;

                Component resolvedComponent = null;
                if (controlSources != null && controlSources.TryGetValue(virtualControl, out var mappedComponent) && mappedComponent != null)
                {
                    resolvedComponent = mappedComponent;
                }
                else
                {
                    var signatureComponent = owner.ResolveModularAvatarMenuComponent(virtualControl);
                    if (signatureComponent != null)
                    {
                        resolvedComponent = signatureComponent;
                        if (controlSources != null)
                        {
                            controlSources[virtualControl] = signatureComponent;
                        }
                    }
                }

                if (resolvedComponent != null)
                {
                    sourceComponent = resolvedComponent;
                    var menuItemTypeLocal = VRCExpressionMenuOrganizerWindow.GetModularAvatarMenuItemType();
                    var menuInstallerTypeLocal = VRCExpressionMenuOrganizerWindow.GetModularAvatarMenuInstallerType();

                    if (menuInstallerTypeLocal != null && menuInstallerTypeLocal.IsInstanceOfType(resolvedComponent))
                    {
                        source = "MA_Installer";
                        isMAInstaller = true;
                        displayControl = CloneControlForDisplay(virtualControl);
                    }
                    else if (menuItemTypeLocal != null && menuItemTypeLocal.IsInstanceOfType(resolvedComponent))
                    {
                        source = "MA_MenuItem";
                        displayControl = EnsureMenuItemControl(resolvedComponent) ?? CloneControlForDisplay(virtualControl);
                    }
                    else
                    {
                        source = "MA_MenuItem";
                        displayControl = CloneControlForDisplay(virtualControl);
                    }

                    if (displayControl != null)
                    {
                        owner.maControlMap[displayControl] = resolvedComponent;
                        owner.maComponentToControl[resolvedComponent] = displayControl;
                    }
                }
                else
                {
                    displayControl = CloneControlForDisplay(virtualControl);
                }

                // Check if this control's subMenu is from an MA Menu Installer
                if (displayControl?.subMenu != null && owner.maInstallerMenus.Contains(displayControl.subMenu))
                {
                    source = "MA_Installer";
                    isMAInstaller = true;
                    owner.LogDetail($"      >> CreateMergedItem: Detected MA_Installer via subMenu '{displayControl.subMenu.name}' in control '{displayControl.name}'");
                }

                // For MA Menu Installer, do NOT include subMenu to prevent baking MA content into VRC menu
                var submenuToUse = (isMAInstaller) ? null : displayControl?.subMenu;

                if (isMAInstaller)
                {
                    owner.LogDetail($"      >> CreateMergedItem: MA_Installer '{displayControl?.name}' - subMenu intentionally set to null (was: {(displayControl?.subMenu != null ? displayControl.subMenu.name : "null")})");
                }

                var child = new MergedMenuItem
                {
                    name = string.IsNullOrEmpty(displayControl?.name)
                        ? owner.GetLocalizedText("無名コントロール", "Unnamed Control")
                        : displayControl.name,
                    source = source,
                    control = displayControl,
                    subMenu = submenuToUse,
                    sourceComponent = sourceComponent,
                    originalIndex = index,
                    children = new List<MergedMenuItem>()
                };

                return child;
            }

            private static VRCExpressionsMenu.Control CloneControlForDisplay(VRCExpressionsMenu.Control source)
            {
                if (source == null) return null;

                var clone = new VRCExpressionsMenu.Control
                {
                    name = source.name,
                    icon = source.icon,
                    type = source.type,
                    value = source.value,
                    style = source.style,
                    subMenu = source.subMenu,
                    parameter = source.parameter != null
                        ? new VRCExpressionsMenu.Control.Parameter { name = source.parameter.name }
                        : null,
                    subParameters = source.subParameters?.Select(p => new VRCExpressionsMenu.Control.Parameter
                    {
                        name = p?.name
                    }).ToArray(),
                    labels = source.labels?.Select(l => new VRCExpressionsMenu.Control.Label
                    {
                        name = l.name,
                        icon = l.icon
                    }).ToArray()
                };

                return clone;
            }

            private static VRCExpressionsMenu.Control EnsureMenuItemControl(Component component)
            {
                if (component == null || menuItemControlField == null) return null;

                var control = menuItemControlField.GetValue(component) as VRCExpressionsMenu.Control;
                if (control == null)
                {
                    control = new VRCExpressionsMenu.Control();
                    menuItemControlField.SetValue(component, control);
                }

                if (menuItemLabelField != null && control != null)
                {
                    var label = menuItemLabelField.GetValue(component) as string;
                    if (!string.IsNullOrEmpty(label))
                    {
                        control.name = label;
                    }
                }

                return control;
            }

            private static GameObject CreateAvatarWorkingCopy(VRCAvatarDescriptor avatarDescriptor)
            {
                if (avatarDescriptor == null) return null;

                var source = avatarDescriptor.gameObject;
                if (source == null) return null;

                var clone = UnityEngine.Object.Instantiate(source);
                clone.name = source.name + " (MA Visualizer Clone)";
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.transform.SetParent(null, false);
                clone.transform.position = source.transform.position;
                clone.transform.rotation = source.transform.rotation;
                clone.transform.localScale = source.transform.localScale;

                SetHideFlagsRecursively(clone, HideFlags.HideAndDontSave);
                UnpackAllPrefabInstances(clone);

                // Disable Excluded marker GameObjects to prevent them from being included in VirtualMenu
                // Excludedマーカーは「除外項目として処理済み」を示すため、これらのGameObjectは
                // VirtualMenu構築時に含めないように無効化する
                var excludedMarkers = clone.GetComponentsInChildren<ExprMenuVisualizerExcluded>(true);
                foreach (var marker in excludedMarkers)
                {
                    if (marker == null) continue;
                    // Excludedマーカーが付いているGameObjectは全て無効化
                    marker.gameObject.SetActive(false);
                }

                return clone;
            }

            private static void SetHideFlagsRecursively(GameObject root, HideFlags flags)
            {
                if (root == null) return;

                root.hideFlags = flags;
                foreach (Transform child in root.transform)
                {
                    if (child != null)
                    {
                        SetHideFlagsRecursively(child.gameObject, flags);
                    }
                }
            }

            private static void UnpackAllPrefabInstances(GameObject root)
            {
                if (root == null) return;

                var stack = new Stack<GameObject>();
                stack.Push(root);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (current == null) continue;

                    if (PrefabUtility.IsAnyPrefabInstanceRoot(current))
                    {
                        PrefabUtility.UnpackPrefabInstance(current, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    }

                    foreach (Transform child in current.transform)
                    {
                        if (child != null)
                        {
                            stack.Push(child.gameObject);
                        }
                    }
                }
            }

            private static IEnumerable<(Component original, Component clone)> EnumerateComponentPairs(GameObject originalRoot, GameObject cloneRoot)
            {
                if (originalRoot == null || cloneRoot == null) yield break;

                var originalTransforms = originalRoot.GetComponentsInChildren<Transform>(true);
                var cloneTransforms = cloneRoot.GetComponentsInChildren<Transform>(true);

                var count = Math.Min(originalTransforms.Length, cloneTransforms.Length);
                for (int i = 0; i < count; i++)
                {
                    var originalTransform = originalTransforms[i];
                    if (originalTransform == null) continue;

                    // Skip GameObjects with Included marker
                    if (originalTransform.GetComponent<ExprMenuVisualizerIncluded>() != null)
                    {
                        continue;
                    }

                    var originalComponents = originalTransform.GetComponents<Component>() ?? Array.Empty<Component>();
                    var cloneComponents = cloneTransforms[i]?.GetComponents<Component>() ?? Array.Empty<Component>();

                    var componentCount = Math.Min(originalComponents.Length, cloneComponents.Length);
                    for (int j = 0; j < componentCount; j++)
                    {
                        var originalComponent = originalComponents[j];
                        var cloneComponent = cloneComponents[j];

                        if (originalComponent == null || cloneComponent == null) continue;

                        yield return (originalComponent, cloneComponent);
                    }
                }
            }
        }

        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

            public bool Equals(T x, T y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                if (obj == null) return 0;
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        // Helper method to create a solid color texture
        private static Texture2D MakeTex(int width, int height, Color color)
        {
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            Texture2D texture = new Texture2D(width, height);
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }
    
    // Helper class for submenu name input dialog
    public class SubmenuNameInputWindow : EditorWindow
    {
        private string inputText = "";
        private string dialogTitle = "";
        private string dialogMessage = "";
        private bool confirmed = false;
        private bool cancelled = false;
        
        private static string result = null;
        private static bool dialogClosed = false;
        
        public static string ShowDialog(string title, string message, string defaultText)
        {
            result = null;
            dialogClosed = false;
            
            var window = CreateInstance<SubmenuNameInputWindow>();
            window.titleContent = new GUIContent(title);
            window.dialogTitle = title;
            window.dialogMessage = message;
            window.inputText = defaultText;
            window.confirmed = false;
            window.cancelled = false;
            
            window.position = new Rect(
                (Screen.width - 400) / 2,
                (Screen.height - 150) / 2,
                400,
                150
            );
            
            window.minSize = new Vector2(400, 150);
            window.maxSize = new Vector2(400, 150);
            
            window.ShowModal();
            
            // Use Unity's editor update to wait for dialog closure
            while (!dialogClosed && window != null)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                System.Threading.Thread.Sleep(10);
            }
            
            return result;
        }
        
        private void OnGUI()
        {
            GUILayout.Space(10);
            
            GUILayout.Label(dialogMessage, EditorStyles.wordWrappedLabel);
            
            GUILayout.Space(10);
            
            GUI.SetNextControlName("InputField");
            inputText = EditorGUILayout.TextField("Name:", inputText);
            
            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                {
                    ConfirmDialog();
                    Event.current.Use();
                }
                else if (Event.current.keyCode == KeyCode.Escape)
                {
                    CancelDialog();
                    Event.current.Use();
                }
            }
            
            GUILayout.Space(10);
            
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            
            if (GUILayout.Button(VRCExpressionMenuOrganizerWindow.GetLocalizedTextStatic("作成", "Create"), GUILayout.Width(80)))
            {
                ConfirmDialog();
            }

            GUILayout.Space(10);

            if (GUILayout.Button(VRCExpressionMenuOrganizerWindow.GetLocalizedTextStatic("キャンセル", "Cancel"), GUILayout.Width(80)))
            {
                CancelDialog();
            }
            
            EditorGUILayout.EndHorizontal();
            
            GUILayout.Space(10);
            
            // Focus on input field when dialog opens
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.FocusTextInControl("InputField");
            }
        }
        
        private void ConfirmDialog()
        {
            if (!string.IsNullOrEmpty(inputText.Trim()))
            {
                result = inputText.Trim();
                confirmed = true;
                CloseDialog();
            }
        }
        
        private void CancelDialog()
        {
            result = null;
            cancelled = true;
            CloseDialog();
        }
        
        private void CloseDialog()
        {
            dialogClosed = true;
            Close();
        }
        
        private void OnDestroy()
        {
            if (!confirmed && !cancelled)
            {
                result = null;
            }
            dialogClosed = true;
        }
    }
}
