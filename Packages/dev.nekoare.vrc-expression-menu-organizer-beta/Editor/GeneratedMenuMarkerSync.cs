using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class GeneratedMenuMarkerSync
{
    static GeneratedMenuMarkerSync()
    {
        // Register hierarchy changed handler to keep marker.originalMenuPath in sync
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }

    private static void OnHierarchyChanged()
    {
        // Find all marker components in the scene
        var markers = Object.FindObjectsOfType<VRCExpressionMenuVisualizer.ExprMenuVisualizerGeneratedGuid>();
        if (markers == null || markers.Length == 0) return;

        foreach (var m in markers)
        {
            if (m == null || m.gameObject == null) continue;

            string newPath = ComputeRelativeMenuPath(m.gameObject.transform);
            if (newPath == null) newPath = string.Empty;
            if (m.originalMenuPath != newPath)
            {
                try
                {
                    Undo.RecordObject(m, "Update GeneratedMenuMarker Path");
                    m.originalMenuPath = newPath;
                    EditorUtility.SetDirty(m);
                }
                catch { }
            }
        }
        
        // Also update GeneratedMetadata.fullPath for generated items so metadata
        // reflects the current hierarchy after moves (covers drag/drop into root etc.)
        try
        {
            var metas = Object.FindObjectsOfType<VRCExpressionMenuVisualizer.ExprMenuVisualizerGeneratedMetadata>();
            if (metas != null && metas.Length > 0)
            {
                foreach (var meta in metas)
                {
                    if (meta == null || meta.gameObject == null) continue;
                    try
                    {
                        string rel = ComputeRelativeMenuPath(meta.gameObject.transform);
                        if (rel == null) rel = string.Empty;

                        // Determine root name to prefix so fullPath matches merged structure
                        var root = FindMenuItemRootAncestor(meta.gameObject.transform);
                        string full = string.Empty;
                        if (root != null)
                        {
                            if (string.IsNullOrEmpty(rel)) full = root.name;
                            else full = root.name + "/" + rel;
                        }
                        else
                        {
                            // fallback to using the object's name
                            full = string.IsNullOrEmpty(rel) ? meta.gameObject.name : rel;
                        }

                        if (meta.fullPath != full)
                        {
                            try
                            {
                                Undo.RecordObject(meta, "Update GeneratedMetadata fullPath");
                                meta.fullPath = full;
                                EditorUtility.SetDirty(meta);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static string ComputeRelativeMenuPath(Transform t)
    {
        if (t == null) return null;

        // Walk up to find nearest ancestor that is a MenuItem root
        Transform root = FindMenuItemRootAncestor(t);
        if (root == null) return null;

        var parts = new List<string>();
        Transform cur = t;
        while (cur != null && cur != root)
        {
            parts.Insert(0, cur.name);
            cur = cur.parent;
        }

        return string.Join("/", parts.ToArray());
    }

    private static Transform FindMenuItemRootAncestor(Transform t)
    {
        if (t == null) return null;
        Transform cur = t.parent;
        while (cur != null)
        {
            // Prefer explicit root marker component if present
            var marker = cur.GetComponent("ExprMenuVisualizerGeneratedRoot");
            if (marker != null) return cur;

            if (cur.name == "メニュー項目") return cur;
            cur = cur.parent;
        }
        return null;
    }
}
