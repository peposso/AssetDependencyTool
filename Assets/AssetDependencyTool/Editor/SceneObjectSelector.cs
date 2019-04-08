using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;
using UnityEngine.SceneManagement;
using System.IO;
using System.Reflection;

namespace AssetDependencyTool
{
    public static class SceneObjectSelector
    {

        public static int GetFileID(UnityEngine.Object o)
        {
            if (o == null)
            {
                return 0;
            }
            var inspectorModeInfo = typeof(SerializedObject).GetProperty("inspectorMode", BindingFlags.NonPublic | BindingFlags.Instance);
            var so = new SerializedObject(o);
            inspectorModeInfo.SetValue(so, InspectorMode.Debug, null);
            var prop = so.FindProperty("m_LocalIdentfierInFile");
            var id = prop.intValue;
            if (id <= 0)
            {
                if (PrefabUtility.GetPrefabType(o) != PrefabType.None)
                {
                    var prefab = PrefabUtility.GetPrefabObject(o);
                    id = GetFileID(prefab);
                }
            }
            return id;
        }

        public static void SelectReferencesInScene(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            var scene = SceneManager.GetActiveScene();
            var gameObjects = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject);
            var components = gameObjects.SelectMany(go => go.GetComponents<Component>());
            var target = null as UnityEngine.Object;
            if (Path.GetExtension(path) == ".fbx")
            {
                target = AssetDatabase.LoadAssetAtPath(path, typeof(Mesh));
            }
            else
            {
                target = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
            }
            var selected = new List<UnityEngine.Object>();
            foreach (var c in components)
            {
                if (HasReference(new SerializedObject(c), target))
                {
                    selected.Add(c.gameObject);
                }
            }
            if (target is GameObject)
            {
                foreach (var go in gameObjects)
                {
                    if (PrefabUtility.GetPrefabObject(go) == target)
                    {
                        selected.Add(go);
                    }
                    // else if (PrefabUtility.GetCorrespondingObjectFromSource(go) == target)
                    // {
                    //     selected.Add(go);
                    // }
                }
            }
            Selection.objects = selected.Distinct().ToArray();
        }

        static bool HasReference(SerializedObject so, UnityEngine.Object target)
        {
            foreach (var p in Iterate(so))
            {
                if (p.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }
                var o = p.objectReferenceValue;
                if (o == null) continue;
                if (o == target) return true;
                if (o is Material)
                {
                    if (HasReference(new SerializedObject(o), target))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        static IEnumerable<SerializedProperty> Iterate(SerializedObject so)
        {
            var property = so.GetIterator();
            while (property.Next(true))
            {
                yield return property;
                if (property.isArray)
                {
                    var size = property.arraySize;
                    for (var i = 0; i < size; ++i)
                    {
                        yield return property.GetArrayElementAtIndex(i);
                    }
                }
            }
        }
    }
}
