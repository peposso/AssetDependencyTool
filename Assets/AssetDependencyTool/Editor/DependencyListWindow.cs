using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using System.Reflection;
using UnityEngine.SceneManagement;
using UnityObject = UnityEngine.Object;

namespace AssetDependencyTool
{
    public class DependencyListWindow : EditorWindow, IHasCustomMenu
    {
        class DependencyInfo
        {
            public bool modifyOnly = false;
            public string ext = "";
            public string beforePath = "";
            public string beforeGUID = "";
            public string beforePrefix = "";
            public string afterPath = "";
            public string afterGUID = "";
            public string afterPrefix = "";
        }

        enum SortType
        {
            Extension,
            FileName,
            FilePath,
        }

        static List<UnityObject> history = new List<UnityObject>();

        SortType sortType = SortType.Extension;
        bool isAutoSelect = true;
        bool isRecursive = true;
        bool isShowDirectory = false;
        public List<UnityObject> Targets;
        ScrollableObjectList targetList;
        List<UnityObject> prevTargets = null;
        string guid;

        Dictionary<string, bool> checks = new Dictionary<string, bool>();
        ScrollableObjectList objectList;
        Dictionary<string, List<string>> references = new Dictionary<string, List<string>>();
        Dictionary<string, List<string>> dependencies = new Dictionary<string, List<string>>();
        string focusedObjectPath;

        [MenuItem("Tools/Asset Dependency List")]
        public static void OpenEditor()
        {
            EditorWindow.GetWindow<DependencyListWindow>();
        }

        [MenuItem(@"Assets/List Dependencies")]
        private static void Menu()
        {
            var win = EditorWindow.GetWindow<DependencyListWindow>();
            win.Targets = Selection.objects.ToList();
        }


        [MenuItem(@"Assets/List Dependencies", true)]
        public static bool MenuValidation()
        {
            return Selection.objects.Length > 0;
        }

        void OnEnable()
        {
            titleContent.text = "Dependency List";
            targetList =  new ScrollableObjectList(false);
            objectList =  new ScrollableObjectList();
            Targets = null;
            prevTargets = null;
            isAutoSelect = EditorPrefs.GetBool("AssetDependencyTool.DependencyListWindow.isAutoSelect", isAutoSelect);
            isRecursive = EditorPrefs.GetBool("AssetDependencyTool.DependencyListWindow.isRecursive", isRecursive);
            isShowDirectory = EditorPrefs.GetBool("AssetDependencyTool.DependencyListWindow.isShowDirectory", isShowDirectory);
            sortType = (SortType)EditorPrefs.GetInt("AssetDependencyTool.DependencyListWindow.sortType", (int)sortType);
            history = HistoryJson.Read();
        }

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.EndHorizontal();

            // var focused = EditorGUILayout.TextField("focus", GUI.GetNameOfFocusedControl());
            var focused = GUI.GetNameOfFocusedControl();
            var refs = null as List<string>;
            var deps = null as List<string>;
            if (!string.IsNullOrEmpty(focused))
            {
                var chunks = focused.Split(',');
                if (chunks.Length == 2)
                {
                    focusedObjectPath = chunks[1];
                    if (chunks[0] == "Result")
                    {
                        refs = GetOrNull(references, focusedObjectPath);
                        deps = GetOrNull(dependencies, focusedObjectPath);
                    }
                    else if (chunks[0] == "Target")
                    {
                        deps = GetOrNull(dependencies, focusedObjectPath);
                    }
                }
            }

            Targets = Targets ?? new List<UnityObject>();
            if (Targets.Count == 0)
            {
                Targets.Add(null);
            }
            targetList.Clear();
            targetList.AddRange(Targets.Select(AssetDatabase.GetAssetPath));
            var backgroundColor = GUI.backgroundColor;
            targetList.Draw(path =>
            {
                GUI.SetNextControlName("Target,"+path);
                if (path == focusedObjectPath)
                {
                    GUI.backgroundColor = new Color(0.8f, 1f, 0.7f);
                }
                else if (refs != null && refs.Contains(path))
                {
                    GUI.backgroundColor = new Color(1f, 0.8f, 0.5f);
                }
                else if (deps != null && deps.Contains(path))
                {
                    GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
                }
            }, path =>
            {
                GUI.backgroundColor = backgroundColor;
            });

            if (isAutoSelect)
            {
                if (Selection.objects != null && Selection.objects.Length > 0 && !Selection.objects.SequenceEqual(Targets))
                {
                    var o = Selection.objects[0];
                    if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o)))
                    {
                        Targets = Selection.objects.ToList();
                    }
                }
            }

            if (Targets[0] == null)
            {
                guid = null;
            }
            EditorGUILayout.TextField("GUIDs", guid ?? "");

            if (Targets[0] != null && (prevTargets == null || !Targets.SequenceEqual(prevTargets)))
            {
                prevTargets = Targets;
                ListDependencies(Targets);
            }

            EditorGUILayout.LabelField(string.Format("dependencies: {0}", objectList.List.Count));

            objectList.Draw(path =>
            {
                var check = checks.ContainsKey(path) && checks[path];
                checks[path] = EditorGUILayout.ToggleLeft("", check, GUILayout.Width(15));
                GUI.SetNextControlName("Result,"+path);
                if (path == focusedObjectPath)
                {
                    GUI.backgroundColor = new Color(0.8f, 1f, 0.7f);
                }
                else if (refs != null && refs.Contains(path))
                {
                    GUI.backgroundColor = new Color(1f, 0.8f, 0.5f);
                }
                else if (deps != null && deps.Contains(path))
                {
                    GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
                }
            }, path =>
            {
                GUI.backgroundColor = backgroundColor;
                if (targetList.List.Count == 1)
                {
                    EditorGUILayout.LabelField("", GUILayout.Width(1));
                }
                else
                {
                    var paths = GetOrNull(references, path);
                    var count = paths == null ? 0 : paths.Count(targetList.List.Contains);
                    EditorGUILayout.LabelField(count.ToString(), GUILayout.Width(15));
                }
                if (!isShowDirectory)
                {
                    return;
                }
                var i = path.IndexOf("Resources/");
                if (i > -1)
                {
                    path = path.Substring(i + "Resources/".Length);
                }
                EditorGUILayout.TextField(Path.GetDirectoryName(path));
            }, (path, o) =>
            {
                var destPath = AssetDatabase.GetAssetPath(o);
                if (Path.GetExtension(path) != Path.GetExtension(path))
                {
                    Debug.LogErrorFormat("extension unmatch");
                    return;
                }
                var before = AssetDatabase.AssetPathToGUID(path);
                var after = AssetDatabase.AssetPathToGUID(destPath);
                AssetDuplicator.ReplaceDependencyGUIDs(Targets.Select(AssetDatabase.GetAssetPath), before, after);
                prevTargets.Clear();
            });

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("SelectAll"))
            {
                SelectAll();
            }
            if (GUILayout.Button("DeselectAll"))
            {
                DeselectAll();
            }
            if (GUILayout.Button("SelectInScene"))
            {
                SelectInScene();
            }
            if (GUILayout.Button("History"))
            {
                ShowHistory();
            }
            if (GUILayout.Button("DuplicateDeps"))
            {
                Duplicate();
            }
            EditorGUILayout.EndHorizontal();

            var ev = Event.current;
            if (ev.type == EventType.KeyDown)
            {
                OnKeyDown(ev.keyCode);
            }
        }

        void OnInspectorUpdate()
        {
            if (isAutoSelect)
            {
                Repaint();
            }
        }

        void SelectAll()
        {
            foreach (var path in objectList.List)
            {
                var ext = Path.GetExtension(path);
                if (ext == ".cs" || ext == ".shader") continue;
                checks[path] = true;
            }
        }

        void DeselectAll()
        {
            foreach (var path in objectList.List)
            {
                var ext = Path.GetExtension(path);
                if (ext == ".cs" || ext == ".shader") continue;
                checks[path] = false;
            }
        }

        void ShowHistory()
        {
            var menu = new GenericMenu();
            foreach (var o in history.AsEnumerable().Reverse().Skip(1))
            {
                var name = AssetDatabase.GetAssetPath(o.GetInstanceID()).Replace("/", "\u2215");
                menu.AddItem(new GUIContent(name), false, OnHistorySelected, o);
            }
            menu.ShowAsContext();
        }

        string MoveFocus(int diff)
        {
            var focused = GUI.GetNameOfFocusedControl();
            if (string.IsNullOrEmpty(focused))
            {
                return null;
            }
            var all = new List<string>();
            foreach (var path in targetList.List)
            {
                all.Add("Target," + path);
            }
            foreach (var path in objectList.List)
            {
                all.Add("Result," + path);
            }
            var i = all.IndexOf(focused);
            if (i == -1)
            {
                return null;
            }
            i += diff;
            if (i < 0 && all.Count <= i)
            {
                return null;
            }
            var nextFocus = all[i];
            GUI.FocusControl(nextFocus);
            return nextFocus.Split(',')[1];
        }

        bool ToggleCheckFocused()
        {
            var focused = GUI.GetNameOfFocusedControl();
            if (!string.IsNullOrEmpty(focused))
            {
                var chunks = focused.Split(',');
                if (chunks.Length == 2)
                {
                    var path = chunks[1];
                    checks[path] = !(checks.ContainsKey(path) && checks[path]);
                }
                return true;
            }
            return false;
        }

        void OnKeyDown(KeyCode keyCode)
        {
            if (keyCode == KeyCode.DownArrow || keyCode == KeyCode.UpArrow)
            {
                var diff = keyCode == KeyCode.DownArrow ? 1 : -1;
                if (diff == 1 && Event.current.shift)
                {
                    ToggleCheckFocused();
                }
                var next = MoveFocus(diff);
                if (diff == -1 && Event.current.shift)
                {
                    ToggleCheckFocused();
                }
                if (next != null)
                {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath(next, typeof(Object)));
                }
            }
            else if (keyCode == KeyCode.S && !Event.current.alt)
            {
                ToggleCheckFocused();
            }
            if (Event.current.alt)
            {
                if (keyCode == KeyCode.D)
                {
                    Duplicate();
                }
                else if (keyCode == KeyCode.S)
                {
                    SelectInScene();
                }
                else if (keyCode == KeyCode.R)
                {
                    RemoveReference();
                }
            }
        }

        void ListDependencies(List<UnityObject> targets)
        {
            objectList = objectList ?? new ScrollableObjectList();
            objectList.Clear();
            checks.Clear();
            if (targets.Count == 1)
            {
                var target = targets.First();
                var targetPath = AssetDatabase.GetAssetPath(target.GetInstanceID());
                // instanceID = target.GetInstanceID();
                // fileID = GetFileID(target);
                if (string.IsNullOrEmpty(targetPath)) return;
                history.Remove(target);
                history.Add(target);
                if (history.Count > 50)
                {
                    history.RemoveAt(0);
                }
                HistoryJson.Write(history);
            }
            guid = string.Join(",", Targets.Select(t => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t.GetInstanceID()))).ToArray());
            var targetPaths = new List<string>();
            var deps = new List<string>();
            foreach (var o in targets)
            {
                var path = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(path)) continue;
                targetPaths.Add(path);
                var assets = AssetDatabase.GetDependencies(path, isRecursive);
                deps.AddRange(assets.Select(AssetDatabase.AssetPathToGUID));
            }
            deps = deps.Distinct()
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(path => !targetPaths.Contains(path)).ToList();
            if (sortType == SortType.Extension)
            {
                deps = deps.OrderBy(x => Path.GetExtension(x)).ThenBy(x => Path.GetFileName(x)).ToList();
            }
            else if (sortType == SortType.FileName)
            {
                deps = deps.OrderBy(x => Path.GetFileName(x)).ThenBy(x => Path.GetExtension(x)).ToList();
            }
            else if (sortType == SortType.FilePath)
            {
                deps = deps.OrderBy(x => x).ToList();
            }

            foreach (var dep in deps)
            {
                var ext = Path.GetExtension(dep);
                objectList.Add(dep);
                checks[dep] = ext != ".cs" && ext != ".shader";
            }

            var all = targetPaths.Concat(deps).ToArray();
            references.Clear();
            dependencies.Clear();
            foreach (var path in all)
            {
                dependencies.Add(path, AssetDatabase.GetDependencies(path, true).ToList());
                references.Add(path, new List<string>());
            }
            foreach (var pair in dependencies)
            {
                foreach (var path in pair.Value)
                {
                    references[path].Add(pair.Key);
                }
            }
        }

        void OnHistorySelected(object o)
        {
            var uo = (UnityObject)o;
            Selection.activeObject = uo;
            Targets = new List<UnityObject> { uo };
            EditorGUIUtility.PingObject(uo);
        }

        Tv GetOrNull<Tk, Tv>(Dictionary<Tk, Tv> dict, Tk key)
        {
            Tv v;
            if (dict.TryGetValue(key, out v))
            {
                return v;
            }
            return default(Tv);
        }

        int GetPrefixLength(IEnumerable<string> names)
        {
            if (names == null) return 0;
            var prefixLength = 0;
            var first = names.FirstOrDefault();
            if (first == null) return 0;
            var rest = names.Skip(1).ToArray();
            for (var i = 0; i < first.Length; ++i)
            {
                var c = first[i];
                foreach (var name in rest)
                {
                    if (i >= name.Length || name[i] != c)
                    {
                        return prefixLength;
                    }
                }
                ++prefixLength;
            }
            return prefixLength;
        }

        #region IHasCustomMenu
        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("auto select"), isAutoSelect, () =>
            {
                isAutoSelect = !isAutoSelect;
                EditorPrefs.SetBool("AssetDependencyTool.DependencyListWindow.isAutoSelect", isAutoSelect);
            });
            menu.AddItem(new GUIContent("recursive"), isRecursive, () =>
            {
                isRecursive = !isRecursive;
                prevTargets = null;
                EditorPrefs.SetBool("AssetDependencyTool.DependencyListWindow.isRecursive", isRecursive);
            });
            menu.AddItem(new GUIContent("show directory"), isShowDirectory, () =>
            {
                isShowDirectory = !isShowDirectory;
                EditorPrefs.SetBool("AssetDependencyTool.DependencyListWindow.isShowDirectory", isShowDirectory);
            });
            menu.AddItem(new GUIContent("sort/extension"), sortType == SortType.Extension, () =>
            {
                sortType = SortType.Extension;
                EditorPrefs.SetInt("AssetDependencyTool.DependencyListWindow.sortType", (int)sortType);
                prevTargets = null;
            });
            menu.AddItem(new GUIContent("sort/file name"), sortType == SortType.FileName, () =>
            {
                sortType = SortType.FileName;
                EditorPrefs.SetInt("AssetDependencyTool.DependencyListWindow.sortType", (int)sortType);
                prevTargets = null;
            });
            menu.AddItem(new GUIContent("sort/file path"), sortType == SortType.FilePath, () =>
            {
                sortType = SortType.FilePath;
                EditorPrefs.SetInt("AssetDependencyTool.DependencyListWindow.sortType", (int)sortType);
                prevTargets = null;
            });
            foreach (var o in history.AsEnumerable().Reverse().Skip(1))
            {
                var name = AssetDatabase.GetAssetPath(o.GetInstanceID()).Replace("/", "\u2215");
                menu.AddItem(new GUIContent("history/" + name), false, OnHistorySelected, o);
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("select in scene &s"), false, SelectInScene);
            menu.AddItem(new GUIContent("duplicate dependencies &d"), false, Duplicate);
            menu.AddItem(new GUIContent("remove reference &r"), false, RemoveReference);
        }
        #endregion

        void Duplicate()
        {
            var targetPaths = Targets.Select(o => AssetDatabase.GetAssetPath(o));
            AssetDuplicator.Duplicate(targetPaths, path => checks.ContainsKey(path) && checks[path]);
        }

        void SelectInScene()
        {
            if (string.IsNullOrEmpty(focusedObjectPath))
            {
                return;
            }
            SceneObjectSelector.SelectReferencesInScene(focusedObjectPath);
        }

        void RemoveReference()
        {
            if (string.IsNullOrEmpty(focusedObjectPath))
            {
                return;
            }
            var guid = AssetDatabase.AssetPathToGUID(focusedObjectPath);
            var refs = GetOrNull(references, focusedObjectPath);

            foreach (var path in refs)
            {
                AssetDuplicator.RemoveGUID(path, guid);
            }
            prevTargets = null;
        }
    }

    [System.Serializable]
    public class HistoryJson
    {
        static HistoryJson instance = new HistoryJson();
        static string JsonPath { get { return Application.dataPath + "/../Library/dependencyListHistory.json"; } }

        [SerializeField] public List<string> history = new List<string>();

        public static void Write(List<UnityObject> objs)
        {
            instance.WriteImpl(objs);
        }

        public static List<UnityObject> Read()
        {
            return instance.ReadImpl();
        }

        void WriteImpl(List<UnityObject> objs)
        {
            history.Clear();
            foreach (var o in objs)
            {
                history.Add(AssetDatabase.GetAssetPath(o.GetInstanceID()));
            }
            File.WriteAllText(JsonPath, JsonUtility.ToJson(this));
        }

        List<UnityObject> ReadImpl()
        {
            var objs = new List<UnityObject>();
            var path = JsonPath;
            if (!File.Exists(path))
            {
                return objs;
            }
            var text = File.ReadAllText(path);
            try
            {
                JsonUtility.FromJsonOverwrite(text, this);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(e);
                return objs;
            }

            history = history ?? new List<string>();
            foreach (var assetPath in history)
            {
                if (!File.Exists(assetPath)) continue;
                var o = AssetDatabase.LoadAssetAtPath(assetPath, typeof(UnityObject));
                if (o != null)
                {
                    objs.Add(o);
                }
            }
            if (history.Count != objs.Count)
            {
                WriteImpl(objs);
            }
            return objs;
        }
    }
}
