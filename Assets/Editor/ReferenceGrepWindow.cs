using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityObject = UnityEngine.Object;

namespace AssetDependencyTool
{
    public class ReferenceGrepWindow : EditorWindow, IHasCustomMenu
    {
        ReferenceSearchEngine engine;
        bool isRecursive = false;
        public UnityObject Target = null;
        UnityObject beforeTarget = null;
        string guid = "";
        string targetPath;
        ScrollableObjectList objectList = null;

        [MenuItem("Tools/Asset Reference Grep")]
        public static void OpenEditor()
        {
            EditorWindow.GetWindow<ReferenceGrepWindow>();
        }

        [MenuItem(@"Assets/Grep References")]
        private static void Menu()
        {
            var win = EditorWindow.GetWindow<ReferenceGrepWindow>();
            win.Target = Selection.objects[0];
        }

        public static void Open(object o)
        {
            var uo = o as UnityObject;
            var win = GetWindow<ReferenceGrepWindow>();
            win.Target = uo;
            win.beforeTarget = null;
        }

        void OnEnable()
        {
            titleContent.text = "References";

            objectList = new ScrollableObjectList();
            objectList.OnPostDraw = OnPostDrawResultItem;
            beforeTarget = null;
            if (engine != null)
            {
                engine.Cancel();
            }
            var root = Path.GetFullPath(Application.dataPath + "/..");
            engine = new ReferenceSearchEngine(root);

            ReferenceSearchEngine.Tracer = s => Debug.Log(s);
            AssetDependencyDatabase.Trace = s => Debug.Log(s);
        }

        void OnDisable()
        {
            AssetDependencyDatabase.Close();
        }

        void OnGUI()
        {
            OnKeyEvent();
            var e = Event.current;

            EditorGUIUtility.labelWidth = 100f;

            if (Target == null && !string.IsNullOrEmpty(guid) && guid.Length == 32)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    Target = AssetDatabase.LoadAssetAtPath(assetPath, typeof(UnityObject));
                    targetPath = assetPath;
                }
            }
            Target = EditorGUILayout.ObjectField(Target, typeof(UnityObject), false);
            if (Target != null && EditorWindowSupport.IsMouseDownInLastRect())
            {
                EditorWindowSupport.PopupObjectHelperMenu(Target);
            }
            if (Target == null)
            {
                targetPath = null;
            }
            else
            {
                targetPath = AssetDatabase.GetAssetPath(Target);
                guid = AssetDatabase.AssetPathToGUID(targetPath);
            }
            var newGUID = EditorGUILayout.TextField("GUID", guid ?? "");
            if (newGUID != guid)
            {
                guid = newGUID;
                Target = null;
                targetPath = null;
            }
            EditorGUILayout.TextField("Path", targetPath ?? "");

            if (Target != null && Target != beforeTarget)
            {
                beforeTarget = Target;
                objectList.Clear();
                engine.Start(targetPath, isRecursive, AddObjectList);
            }

            if (!engine.IsInitialized)
            {
                EditorGUILayout.LabelField("initialize ...");
            }
            else
            {
                EditorGUILayout.LabelField(string.Format("references: {0} {1}", objectList.Count, engine.IsSearching ? "..." : ""));
            }

            objectList.Draw();
        }

        void AddObjectList(string path)
        {
            objectList.Add(path);
        }

        void OnKeyEvent()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;

            if (e.shift)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Q:
                        isLocked = !isLocked;
                        beforeTarget = null;
                        e.Use();
                        return;
                    case KeyCode.R:
                        isRecursive = !isRecursive;
                        beforeTarget = null;
                        e.Use();
                        return;
                }
            }
        }

        int beforeSelectedID = 0;

        void OnInspectorUpdate()
        {
            if (!isLocked)
            {
                UnityObject selected = null;
                if (Selection.objects != null && Selection.objects.Length > 0 && Selection.objects[0] != Target)
                {
                    selected = Selection.objects[0];
                    var iid = selected.GetInstanceID();
                    var path = AssetDatabase.GetAssetPath(selected);
                    if (iid != beforeSelectedID)
                    {
                        Target = string.IsNullOrEmpty(path) ? null : selected;
                        beforeSelectedID = iid;
                    }
                }
                else
                {
                    if (beforeSelectedID != 0)
                    {
                        Target = null;
                        beforeSelectedID = 0;
                    }
                }
            }
            Repaint();
        }

        #region IHasCustomMenu
        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Lock #q"), isLocked, () =>
            {
                isLocked = !isLocked;
                beforeTarget = null;
            });
            menu.AddItem(new GUIContent("Recursive #r"), isRecursive, () =>
            {
                isRecursive = !isRecursive;
            });
            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Copy Result"), false, () =>
            {
                var sb = new StringBuilder();
                foreach (var file in objectList)
                {
                    sb.Append(file);
                    sb.Append('\n');
                }
                EditorGUIUtility.systemCopyBuffer = sb.ToString();
            });
            menu.AddItem(new GUIContent("Sort Result"), false, () =>
            {
                objectList.Sort();
            });
            menu.AddItem(new GUIContent("Clear Cache"), false, () =>
            {
                AssetDependencyDatabase.Truncate();
            });
        }
        #endregion

        #region Button beside context menu
        GUIStyle lockButton = null;
        GUIStyle recursiveButton = null;
        bool isLocked = false;

        void ShowButton(Rect rect)
        {
            lockButton = lockButton ?? new GUIStyle("IN LockButton");
            var label = new GUIContent(isLocked ? "　 Unlock" : "　 Lock");
            rect.x += rect.width;
            rect.width = GUI.skin.box.CalcSize(label).x;
            rect.x -= rect.width;
            EditorGUI.BeginChangeCheck();
            var newLock = GUI.Toggle(rect, isLocked, label, lockButton);
            if (EditorGUI.EndChangeCheck())
            {
                isLocked = newLock;
                beforeTarget = null;
            }

            if (recursiveButton ==null)
            {
                var icon = new GUIStyle("ListToggle").normal.background;
                recursiveButton = new GUIStyle("IN LockButton");
                recursiveButton.normal.background = icon;
                recursiveButton.active.background = icon;
                recursiveButton.focused.background = icon;
                recursiveButton.hover.background = icon;
                recursiveButton.onNormal.background = icon;
                recursiveButton.onActive.background = icon;
                recursiveButton.onFocused.background = icon;
                recursiveButton.onHover.background = icon;
            }
            label = new GUIContent(isRecursive ? "　 NonRecursive" : "　 Recursive");
            rect.width = GUI.skin.box.CalcSize(label).x;
            rect.x -= rect.width;
            EditorGUI.BeginChangeCheck();
            var newRec = GUI.Toggle(rect, isRecursive, label, recursiveButton);
            if (EditorGUI.EndChangeCheck())
            {
                isRecursive = newRec;
                beforeTarget = null;
            }
        }
        #endregion

        void OnPostDrawResultItem(ScrollableObjectList list, string path)
        {
            if (EditorWindowSupport.IsMouseDownInLastRect())
            {
                var o = AssetDatabase.LoadAssetAtPath(path, typeof(UnityObject));
                EditorWindowSupport.PopupObjectHelperMenu(o);
            }
        }
    }
}
