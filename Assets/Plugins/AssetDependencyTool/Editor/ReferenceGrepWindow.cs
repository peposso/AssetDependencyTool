using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Linq;
using System;

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

        bool isAutoSelect = true;
        bool isFindPath = true;
        public UnityEngine.Object Target = null;
        UnityEngine.Object prevTarget = null;
        string guid;
        string targetPath;
        List<PathAsset> items = null;
        List<PathAsset> itemCaches = null;
        Vector2 scroll = Vector2.zero;
        string scriptDirectory;
        System.Diagnostics.Process process;
        object sync = new object();
        bool isRepainting = false;


        [MenuItem("App/Dependency Tool/Reference Grep")]
        public static void OpenEditor()
        {
            EditorWindow.GetWindow<ReferenceGrepWindow>();
        }

        [MenuItem(@"Assets/Grep References")]
        static void Menu()
        {
            var win = EditorWindow.GetWindow<ReferenceGrepWindow>();
            win.Target = Selection.objects[0];
        }

        void OnEnable()
        {
            var cls = GetType();
            var monoScript = Resources.FindObjectsOfTypeAll<MonoScript>().FirstOrDefault(m => m.GetClass() == cls);
            scriptDirectory = Path.GetDirectoryName(AssetDatabase.GetAssetPath(monoScript));
            titleContent.text = "Reference Grep";

            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/EditorSettings.asset")[0];
            var so = new SerializedObject(asset);
            var mode = so.FindProperty("m_SerializationMode");
            Debug.Assert(mode.intValue == 2, "SerializationMode is NOT ForceText");
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

            lock (sync)
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
                process = new System.Diagnostics.Process();
                process.StartInfo.WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Assets");
                process.StartInfo.FileName = Path.Combine(scriptDirectory, Bin);
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.OutputDataReceived += OnStdout;
                process.StartInfo.RedirectStandardError = true;
                process.ErrorDataReceived += OnStderr;
                process.StartInfo.RedirectStandardInput = false;
                process.StartInfo.CreateNoWindow = true;
                if (isFindPath)
                {
                    var ext = Path.GetExtension(targetPath);
                    var path = targetPath.Substring(0, targetPath.Length - ext.Length);
                    var i = path.IndexOf("/Resources/");
                    if (i > -1)
                    {
                        path = path.Substring(i + "/Resources/".Length);
                    }
                    process.StartInfo.Arguments = string.Format("-l -g \"!*.meta\" \"\\b({0}|{1})\\b\"", guid, path);
                }
                else
                {
                    process.StartInfo.Arguments = string.Format("-l -g \"!*.meta\" {0}", guid);
                }

                process.EnableRaisingEvents = true;
                process.Exited += OnProcessExit;

                items = new List<PathAsset>();
                scroll = Vector2.zero;

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
        }

        void OnStdout(object sender, System.Diagnostics.DataReceivedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.Data)) return;
            foreach (var line in args.Data.Split('\n'))
            {
                var path = Path.Combine("Assets", line.Trim()).Replace('\\', '/');
                items.Add(new PathAsset { Path = path });
            }
        }

        void OnStderr(object sender, System.Diagnostics.DataReceivedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.Data)) return;
            Debug.LogWarning(args.Data);
        }

        void OnProcessExit(object sender, System.EventArgs e)
        {
            lock (sync)
            {
                process = null;
            }
        }
    
    }
}
