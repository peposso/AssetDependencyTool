using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using UnityObject = UnityEngine.Object;
using System.Reflection;
using UnityEditorInternal;
using System.Collections.Generic;

namespace AssetDependencyTool
{
    public static class AssetMenu
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

        static string ToMenuItemPath(string path)
        {
            if (path.IndexOf("/") > -1)
            {
                path = path.Replace('\\', '/');
            }
            if (path.StartsWith("Assets/"))
            {
                path = path.Substring("Assets/".Length);
            }
            return path.Replace("/", "\u200A\u2215\u200A");
        }

        static readonly Queue<Action> actionQueue = new Queue<Action>();

        public static void DequeueContextAction()
        {
            if (actionQueue.Count > 0)
            {
                actionQueue.Dequeue()();
            }
        }

        static Func<int, int> getLocalIdentifierInFile = null;

        public static void PopupObjectHelperMenu(UnityObject o)
        {
            var path = AssetDatabase.GetAssetPath(o);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var iid = o.GetInstanceID();

            if (getLocalIdentifierInFile == null)
            {
                var method = typeof(Unsupported).GetMethod(
                    "GetLocalIdentifierInFile",
                    BindingFlags.Static | BindingFlags.Public);
                if (method != null)
                {
                    getLocalIdentifierInFile = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), method);
                }
                if (getLocalIdentifierInFile == null)
                {
                    getLocalIdentifierInFile = _ => 0;
                }
            }
            var localId = getLocalIdentifierInFile(iid);

            var info = new FileInfo(path);
            var name = Path.GetFileName(path);
            var itemPath = ToMenuItemPath(path);

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(name + "   " + info.Length.ToString("N0") + " B   " + info.LastWriteTime.ToString( "yyyy-MM-dd HH:mm:ss")), false, null);
            menu.AddSeparator("");
            var deps = AssetDatabase.GetDependencies(path, false);
            System.Array.Sort(deps);
            var limit = 30;
            foreach (var depPath in deps)
            {
                var target = AssetDatabase.LoadAssetAtPath(depPath, typeof(UnityObject));
                menu.AddItem(new GUIContent("Dependencies/" + ToMenuItemPath(depPath)), false, t => PopupObjectHelperMenu(t as UnityObject), target);
                if (--limit < 0) break;
            }
            menu.AddItem(new GUIContent("Dependencies/..."), false, DependencyListWindow.Open, o);

            var refs = AssetDependencyDatabase.GetReferences(path);
            refs.Sort();
            limit = 30;
            foreach (var refPath in refs)
            {
                var target = AssetDatabase.LoadAssetAtPath(refPath, typeof(UnityObject));
                if (target == null) continue;
                menu.AddItem(new GUIContent("References/" + ToMenuItemPath(refPath)), false, t => PopupObjectHelperMenu(t as UnityObject), target);
                if (--limit < 0) break;
            }
            menu.AddItem(new GUIContent("References/..."), false, ReferenceGrepWindow.Open, o);

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy " + itemPath), false, CopyToClipboard, path);
            menu.AddItem(new GUIContent("Copy GUID " + guid), false, CopyToClipboard, guid);
            menu.AddItem(new GUIContent("Copy InstanceID " + iid), false, CopyToClipboard, iid);
            if (localId != 0)
            {
                menu.AddItem(new GUIContent("Copy LocalID " + localId), false, CopyToClipboard, localId);
            }
            menu.AddItem(new GUIContent("Open In Finder"), false, OpenInFinder, path);
            menu.AddItem(new GUIContent("Open In Editor"), false, OpenInEditor, path);
            menu.AddItem(new GUIContent("Find References In Scene"), false, FindReferencesInScean, o);
            if (Event.current == null)
            {
                EditorGUIUtility.PingObject(o);
                Selection.activeObject = o;
                actionQueue.Enqueue(() =>
                {
                    menu.ShowAsContext();
                });
            }
            else
            {
                menu.ShowAsContext();
            }
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

        static void FindReferencesInScean(object o)
        {
            var iid = (o as UnityObject).GetInstanceID();
            FindInScene("ref:" + iid + ":");
        }
    }
}
