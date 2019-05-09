using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System;
using UnityObject = UnityEngine.Object;

namespace AssetDependencyTool
{
    public class ScrollableObjectList
    {
        class Item
        {
            internal string Path;
            internal UnityObject Object;
        }
        List<string> paths;
        public int Count
        {
            get
            {
                lock (sync)
                {
                    return paths.Count;
                }
            }
        }

        public Action<ScrollableObjectList, string> OnPreDraw { get; set; }
        public Action<ScrollableObjectList, string> OnPostDraw { get; set; }
        public Action<string, UnityObject> OnAssetDrop { get; set; }

        List<Item> items = null;
        Vector2 scroll = Vector2.zero;
        bool isScroll;
        object sync = new object();
        Color? originalBackgroundColor;

        public ScrollableObjectList(bool isScroll = true)
        {
            this.isScroll = isScroll;
            paths = new List<string>();
            items = new List<Item>();
        }

        public List<string>.Enumerator GetEnumerator()
        {
            lock (sync)
            {
                return paths.GetEnumerator();
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                paths.Clear();
            }
        }

        public void Sort()
        {
            lock (sync)
            {
                paths.Sort();
            }
        }

        public void Add(string item)
        {
            lock (sync)
            {
                paths.Add(item);
            }
        }

        public bool Contains(string item)
        {
            lock (sync)
            {
                foreach (var s in paths)
                {
                    if (s == item)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public void AddRange(IEnumerable<string> items)
        {
            lock (sync)
            {
                paths.AddRange(items);
            }
        }

        public void SetBackgroundColor(Color color)
        {
            if (!originalBackgroundColor.HasValue)
            {
                originalBackgroundColor = GUI.backgroundColor;
            }
            GUI.backgroundColor = color;
        }

        public void Draw()
        {
            var e = Event.current;
            if (e.type == EventType.Layout)
            {
                lock (sync)
                {
                    if (!items.Select(i => i.Path).SequenceEqual(paths))
                    {
                        items.Clear();
                        foreach (var path in paths)
                        {
                            items.Add(new Item() { Path = path });
                        }
                    }
                }
                for (var i = 0; i < items.Count; ++i)
                {
                    var item = items[i];
                    item.Object = item.Object ?? AssetDatabase.LoadAssetAtPath(item.Path, typeof(UnityObject));
                }
                for (var i = 0; i < items.Count; ++i)
                {
                    var item = items[i];
                    if (item.Object == null)
                    {
                        items.Remove(item);
                        lock (sync)
                        {
                            paths.Remove(item.Path);
                        }
                        continue;
                    }
                }
            }
            if (isScroll)
            {
                scroll = GUILayout.BeginScrollView(scroll, GUI.skin.box);
            }
            for (var i = 0; i < items.Count; ++i)
            {
                var item = items[i];
                EditorGUILayout.BeginHorizontal();
                if (OnPreDraw != null)
                {
                    OnPreDraw(this, item.Path);
                }
                var o = EditorGUILayout.ObjectField(item.Object, typeof(UnityObject), false);
                if (originalBackgroundColor.HasValue)
                {
                    GUI.backgroundColor = originalBackgroundColor.Value;
                    originalBackgroundColor = null;
                }
                if (OnPostDraw != null)
                {
                    OnPostDraw(this, item.Path);
                }
                EditorGUILayout.EndHorizontal();
                if (o != item.Object && OnAssetDrop != null)
                {
                    OnAssetDrop(item.Path, o);
                }
            }
            if (isScroll)
            {
                GUILayout.EndScrollView();
            }
        }
    }
}
