using UnityEngine;
using UnityEditor;
using System.IO;
using UnityObject = UnityEngine.Object;
using System.Reflection;
using UnityEditorInternal;

namespace AssetDependencyTool
{
    public static class EditorWindowSupport
    {
        public static bool IsMouseDownInLastRect()
        {
            var e = Event.current;
            var lastRect = GUILayoutUtility.GetLastRect();
            return e.type == EventType.MouseDown && lastRect.Contains(e.mousePosition);
        }

        public static void FindInScene(string searchFilter)
        {
            var searchableWindows = Resources.FindObjectsOfTypeAll<SearchableEditorWindow>();

            foreach (var sw in searchableWindows)
            {
                var type = sw.GetType();
                var hierarchyTypeField = type.GetField( "m_HierarchyType", BindingFlags.Instance | BindingFlags.NonPublic );
                var hierarchyType = (HierarchyType)hierarchyTypeField.GetValue(sw);

                if (hierarchyType != HierarchyType.GameObjects) continue;

                var setSearchFilterMethod = type.GetMethod("SetSearchFilter", BindingFlags.Instance | BindingFlags.NonPublic);
                object[] args;
                if (setSearchFilterMethod.GetParameters().Length == 3)
                {
                    args = new object[] { searchFilter, SearchableEditorWindow.SearchMode.All, false };
                }
                else
                {
                    args = new object[] { searchFilter, SearchableEditorWindow.SearchMode.All, false, false };
                }
                setSearchFilterMethod.Invoke(sw, args);

                sw.Repaint();
            }
        }

        public static void PopupObjectHelperMenu(UnityObject o)
        {
            var path = AssetDatabase.GetAssetPath(o);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var info = new FileInfo(path);
            var name = Path.GetFileName(path);
            var itemPath = path;
            if (itemPath.StartsWith("Assets/"))
            {
                itemPath = itemPath.Substring("Assets/".Length);
            }
            itemPath = itemPath.Replace("/", "\u200A\u2215\u200A");

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(name + "   " + info.Length.ToString("N0") + " B   " + info.LastWriteTime.ToString( "yyyy-MM-dd HH:mm:ss")), false, null);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Dependencies"), false, DependencyListWindow.Open, o);
            menu.AddItem(new GUIContent("References"), false, ReferenceGrepWindow.Open, o);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy " + itemPath), false, CopyToClipboard, path);
            menu.AddItem(new GUIContent("Copy " + guid), false, CopyToClipboard, guid);
            menu.AddItem(new GUIContent("Open In Finder"), false, OpenInFinder, path);
            menu.AddItem(new GUIContent("Open In Editor"), false, OpenInEditor, path);
            menu.AddItem(new GUIContent("Find References In Scene"), false, FindRerefencesInScean, o);
            menu.ShowAsContext();
        }

        static void CopyToClipboard(object o)
        {
            GUIUtility.systemCopyBuffer = o as string;
        }

        static void OpenInFinder(object o)
        {
            EditorUtility.RevealInFinder(o as string);
        }

        static void OpenInEditor(object o)
        {
            InternalEditorUtility.OpenFileAtLineExternal(o as string, -1);
        }

        static void FindRerefencesInScean(object o)
        {
            var iid = (o as UnityObject).GetInstanceID();
            FindInScene("ref:" + iid + ":");
        }
    }
}
