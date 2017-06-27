using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace AssetDependencyTool
{
    public class DependencyListWindow : EditorWindow
    {
        class Item
        {
            internal string Path;
            internal UnityEngine.Object Object;
            internal bool Checked;
        }

        bool isAutoSelect = true;
        bool isRecursive = false;
        public UnityEngine.Object Target = null;
        UnityEngine.Object prevTarget = null;
        string guid;
        string targetPath;
        List<Item> items = null;
        List<Item> itemCaches = null;

        Vector2 scroll = Vector2.zero;

        bool isRepainting = false;


        [MenuItem("App/Dependency Tool/Dependency List")]
        public static void OpenEditor()
        {
            EditorWindow.GetWindow<DependencyListWindow>();
        }

        [MenuItem(@"Assets/List Dependencies")]
        private static void Menu()
        {
            var win = EditorWindow.GetWindow<DependencyListWindow>();
            win.Target = Selection.objects[0];
        }


        [MenuItem(@"Assets/List Dependencies", true)]
        public static bool MenuValidation()
        {
            return Selection.objects != null && Selection.objects.Length == 1;
        }

        void OnEnable()
        {
            titleContent.text = "Dependency List";
        }

        void OnGUI()
        {
            isAutoSelect = EditorGUILayout.Toggle("auto select", isAutoSelect);

            Target = EditorGUILayout.ObjectField(Target, typeof(UnityEngine.Object), false);

            if (isAutoSelect)
            {
                if (Selection.objects != null && Selection.objects.Length > 0 && Selection.objects[0] != Target)
                {
                    Target = Selection.objects[0];
                }
            }

            if (Target == null)
            {
                guid = null;
                targetPath = null;
            }
            EditorGUILayout.TextField("GUID", guid ?? "");
            EditorGUILayout.TextField("Path", targetPath ?? "");

            var isRecursiveNew = EditorGUILayout.Toggle("recursive", isRecursive);
            if (isRecursiveNew != isRecursive)
            {
                isRecursive = isRecursiveNew;
                prevTarget = null;
            }

            if (Target != null && Target != prevTarget)
            {
                prevTarget = Target;
                ListDependencies(Target);
            }

            if (items == null)
            {
                EditorGUILayout.LabelField("--");
                return;
            }
            EditorGUILayout.LabelField(string.Format("dependencies: {0}", items.Count));

            scroll = GUILayout.BeginScrollView(scroll);
            if (!isRepainting)
            {
                itemCaches = items;
            }
            foreach (var item in itemCaches.ToArray())
            {
                item.Object = item.Object ?? AssetDatabase.LoadAssetAtPath(item.Path, typeof(UnityEngine.Object));
                if (item.Object == null)
                {
                    itemCaches.Remove(item);
                    items.Remove(item);
                    continue;
                }
                EditorGUILayout.BeginHorizontal();
                item.Checked = EditorGUILayout.ToggleLeft("", item.Checked, GUILayout.Width(15));
                EditorGUILayout.ObjectField(item.Object, typeof(UnityEngine.Object), false);
                EditorGUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            if (targetPath == null) return;
            if (GUILayout.Button("Duplicate!"))
            {
                Duplicate(targetPath, itemCaches);
            }
        }

        void OnInspectorUpdate()
        {
            try
            {
                isRepainting = true;
                Repaint();
            }
            finally
            {
                isRepainting = false;
            }
        }

        void ListDependencies(UnityEngine.Object target)
        {
            targetPath = AssetDatabase.GetAssetPath(target.GetInstanceID());
            items = new List<Item>();
            if (string.IsNullOrEmpty(targetPath)) return;
            guid = AssetDatabase.AssetPathToGUID(targetPath);
            var deps = AssetDatabase.GetDependencies(targetPath, isRecursive);
            foreach (var dep in deps.OrderBy(x => Path.GetExtension(x)).ThenBy(x => Path.GetFileName(x)))
            {
                if (dep == targetPath) continue;
                var ext = Path.GetExtension(dep);
                items.Add(new Item { Path = dep, Checked = ext != ".cs" && ext != ".shader" });
            }
        }

        void Duplicate(string path, List<Item> items)
        {
            var dir = Path.GetDirectoryName(path);
            var origGUIDs = new List<string>();
            var newFiles = new List<string>();
            foreach (var item in items)
            {
                if (!item.Checked) continue;
                var src = item.Path;
                var dest = UniqueFile(dir + "/" + Path.GetFileName(src));
                File.Copy(src, dest);
                origGUIDs.Add(AssetDatabase.AssetPathToGUID(src));
                newFiles.Add(dest);
            }

            if (newFiles.Count == 0)
            {
                Debug.Log("no copying");
                return;
            }
            AssetDatabase.Refresh();
            var fromTo = new Dictionary<string, string>();
            for (var i = 0; i < newFiles.Count; ++i)
            {
                var origGUID = origGUIDs[i];
                var newGUID = AssetDatabase.AssetPathToGUID(newFiles[i]);
                fromTo[origGUID] = newGUID;
            }

            foreach (var file in new [] { path }.Concat(newFiles))
            {
                var head = ReadHead(file);
                if (head.IndexOf("%TAG !u! tag:unity3d.com,2011:") == -1)
                {
                    continue;
                }
                var body = File.ReadAllText(file);
                foreach (var kv in fromTo)
                {
                    body = body.Replace(kv.Key, kv.Value);
                }
                File.WriteAllText(file, body);
                Debug.LogFormat("write {0}", file);
            }
            AssetDatabase.Refresh();
            prevTarget = null;
            Debug.Log("done");
        }

        static string UniqueFile(string file)
        {
            var ext = Path.GetExtension(file);
            while (File.Exists(file))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var m = Regex.Match(name, @"__(\d+)$");
                if (m.Length == 0)
                {
                    file = Path.GetDirectoryName(file) + "/" + name + "__1" + ext;
                }
                else
                {
                    var n = int.Parse(m.Groups[1].Value) + 1;
                    name = name.Substring(0, name.Length - m.Value.Length) + "__" + n;
                    file = Path.GetDirectoryName(file) + "/" + name + "__1" + ext;
                }
            }
            return file;
        }

        string ReadHead(string path)
        {
            var bytes = new byte [64];
            using (var file = File.Open(path, FileMode.Open))
            {
                file.Read(bytes, 0, bytes.Length);
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

    }

}
