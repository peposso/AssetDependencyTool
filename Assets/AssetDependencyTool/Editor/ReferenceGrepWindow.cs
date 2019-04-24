using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Linq;
using System;
using System.Text;

namespace AssetDependencyTool
{
    public class ReferenceGrepWindow : EditorWindow
    {
        class PathAsset
        {
            internal string Path;
            internal UnityEngine.Object Object;
        }

        #if UNITY_EDITOR_WIN
        const string Bin = "rg.exe";
        #elif UNITY_EDITOR_OSX
        const string Bin = "rg-darwin";
        #else
        const string Bin = "rg";
        #endif

        const string CommonIgnorePatterns = "-g \"!*.meta\" -g \"!*.png\" -g \"!*.fbx\" -g \"!*.dll\"";

        bool isAutoSelect = true;
        bool isFindPath = false;
        public UnityEngine.Object Target = null;
        UnityEngine.Object prevTarget = null;
        string guid = "";
        string targetPath;
        List<PathAsset> items = null;
        List<PathAsset> itemCaches = null;
        Vector2 scroll = Vector2.zero;
        string scriptDirectory;
        System.Diagnostics.Process process;
        object sync = new object();
        bool isRepainting = false;
        List<string> argumentQueue = new List<string>();


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

        void OnEnable()
        {
            var cls = this.GetType();
            var monoScript = Resources.FindObjectsOfTypeAll<MonoScript>().FirstOrDefault(m => m.GetClass() == cls);
            scriptDirectory = Path.Combine(Directory.GetCurrentDirectory(), Path.GetDirectoryName(AssetDatabase.GetAssetPath(monoScript)));
            titleContent.text = "Reference Grep";
        }

        void OnDisable()
        {
            AssetDependencyDatabase.Close();
        }

        void OnGUI()
        {
            isAutoSelect = EditorGUILayout.Toggle("auto select", isAutoSelect);
            if (isAutoSelect)
            {
                if (Selection.objects != null && Selection.objects.Length > 0 && Selection.objects[0] != Target)
                {
                    Target = Selection.objects[0];
                }
            }

            if (Target == null && !string.IsNullOrEmpty(guid) && guid.Length == 32)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    Target = AssetDatabase.LoadAssetAtPath(assetPath, typeof(UnityEngine.Object));
                    targetPath = assetPath;
                }
            }
            Target = EditorGUILayout.ObjectField(Target, typeof(UnityEngine.Object), false);
            if (Target == null)
            {
                targetPath = null;
            }
            var newGUID = EditorGUILayout.TextField("GUID", guid ?? "");
            if (newGUID != guid)
            {
                guid = newGUID;
                isAutoSelect = false;
                Target = null;
                targetPath = null;
            }
            EditorGUILayout.TextField("Path", targetPath ?? "");

            var isFindPathNew = EditorGUILayout.Toggle("find path", isFindPath);
            if (isFindPathNew != isFindPath)
            {
                isFindPath = isFindPathNew;
                prevTarget = null;
            }

            if (Target != null && Target != prevTarget)
            {
                prevTarget = Target;
                Grep(Target);
            }

            if (items == null)
            {
                EditorGUILayout.LabelField("no result");
                return;
            }
            EditorGUILayout.LabelField(string.Format("references: {0} {1}", items.Count, process == null ? "" : "..."));

            if (!isRepainting)
            {
                itemCaches = items;
            }
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var item in itemCaches.ToArray())
            {
                item.Object = item.Object ?? AssetDatabase.LoadAssetAtPath(item.Path, typeof(UnityEngine.Object));
                if (item.Object == null)
                {
                    itemCaches.Remove(item);
                    items.Remove(item);
                    continue;
                }
                EditorGUILayout.ObjectField(item.Object, typeof(UnityEngine.Object), false);
            }
            GUILayout.EndScrollView();
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

        void Grep(UnityEngine.Object target)
        {
            targetPath = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(targetPath)) return;

            guid = AssetDatabase.AssetPathToGUID(targetPath);
            var searchPath = "";
            if (isFindPath)
            {
                var ext = Path.GetExtension(targetPath);
                searchPath = targetPath.Substring(0, targetPath.Length - ext.Length);
                var i = searchPath.IndexOf("/Resources/");
                if (i > -1)
                {
                    searchPath = searchPath.Substring(i + "/Resources/".Length);
                }
            }

            var crumbs = Path.GetDirectoryName(targetPath).Split(new[] {'/', '\\'});
            var sb = new StringBuilder();
            var dirs = new List<string>();
            for (var n = crumbs.Length; n > 0; --n)
            {
                sb.Length = 0;
                for (var j = 0; j < n; ++j)
                {
                    if (j > 0) sb.Append('/');
                    sb.Append(crumbs[j]);
                }
                dirs.Add(sb.ToString());
            }
            dirs.Add("ProjectSettings");

            lock (sync)
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }

                argumentQueue.Clear();

                for (var i = 0; i < dirs.Count; ++i)
                {
                    var dir = dirs[i];
                    var ignore = CommonIgnorePatterns;
                    if (!isFindPath)
                    {
                        // maybe guid is not in *.cs ...
                        ignore += " -g \"!*.cs\"";
                    }
                    if (i > 0)
                    {
                        ignore += string.Format(" -g \"!{0}/\"", dirs[i - 1]);
                    }
                    var pat = "";
                    if (searchPath != "")
                    {
                        pat = string.Format("\"\\b({0}|{1})\\b\"", guid, searchPath);
                    }
                    else
                    {
                        pat = guid;
                    }
                    argumentQueue.Add(string.Format("-l {0} {1} \"{2}\"", ignore, pat, dir));
                }

                StartProcess();

                items = new List<PathAsset>();
                scroll = Vector2.zero;

                var refPaths = AssetDependencyDatabase.GetReferences(targetPath);
                foreach (var refPath in refPaths)
                {
                    items.Add(new PathAsset { Path = refPath });
                }
            }
        }

        void StartProcess()
        {
            if (argumentQueue.Count == 0)
            {
                return;
            }
            var arg = argumentQueue[0];
            argumentQueue.RemoveAt(0);
            // Debug.Log(arg);
            process = new System.Diagnostics.Process();
            process.StartInfo.FileName = Path.Combine(scriptDirectory, Bin);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.OutputDataReceived += new System.Diagnostics.DataReceivedEventHandler(OnStdout);
            process.StartInfo.RedirectStandardError = true;
            process.ErrorDataReceived += new System.Diagnostics.DataReceivedEventHandler(OnStderr);
            process.StartInfo.RedirectStandardInput = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.Arguments = arg;
            process.EnableRaisingEvents = true;
            process.Exited += new System.EventHandler(OnProcessExit);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        void OnStdout(object sender, System.Diagnostics.DataReceivedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.Data)) return;
            // Debug.Log(args.Data);
            foreach (var line in args.Data.Split('\n'))
            {
                var path = line.Trim().Replace('\\', '/');
                if (!items.Any(i => i.Path == path))
                {
                    items.Add(new PathAsset { Path = path });
                    if (!isFindPath)
                    {
                        AssetDependencyDatabase.InsertDependencyPath(path, targetPath);
                    }
                }
            }
        }

        void OnStderr(object sender, System.Diagnostics.DataReceivedEventArgs args)
        {
            var err = args.Data;
            if (string.IsNullOrEmpty(err) || err.StartsWith("No files were searched")) return;
            Debug.LogWarning(err);
        }

        void OnProcessExit(object sender, System.EventArgs e)
        {
            lock (sync)
            {
                process = null;
                StartProcess();
            }
        }

    }
}
