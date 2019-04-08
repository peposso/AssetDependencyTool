using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;

namespace AssetDependencyTool
{
    public class ScrollableObjectList
    {
        class Item
        {
            internal string Path;
            internal UnityEngine.Object Object;
        }
        public List<string> List;
        public int Count { get { return List.Count; } }

        List<Item> items = null;
        List<Item> itemCaches = null;
        Vector2 scroll = Vector2.zero;
        bool isScroll;

        public ScrollableObjectList(bool isScroll = true)
        {
            this.isScroll = isScroll;
            List = new List<string>();
        }

        public void Clear()
        {
            List.Clear();
        }

        public void Add(string item)
        {
            List.Add(item);
        }

        public void AddRange(IEnumerable<string> items)
        {
            List.AddRange(items);
        }

        public void Draw(System.Action<string> preDraw = null, System.Action<string> postDraw = null, System.Action<string, UnityEngine.Object> onDrop = null)
        {
            List = List ?? new List<string>();
            items = items ?? new List<Item>();
            if (!items.Select(i => i.Path).SequenceEqual(List))
            {
                items.Clear();
                foreach (var path in List)
                {
                    items.Add(new Item() { Path = path });
                }
            }

            for (var i = 0; i < items.Count; ++i)
            {
                var item = items[i];
                item.Object = item.Object ?? AssetDatabase.LoadAssetAtPath(item.Path, typeof(UnityEngine.Object));
            }
            if (Event.current.type != EventType.Repaint)
            {
                for (var i = 0; i < items.Count; ++i)
                {
                    var item = items[i];
                    if (item.Object == null)
                    {
                        items.Remove(item);
                        List.Remove(item.Path);
                        continue;
                    }
                }
                itemCaches = items.ToList();
            }
            if (isScroll)
            {
                scroll = GUILayout.BeginScrollView(scroll, GUI.skin.box);
            }
            for (var i = 0; i < itemCaches.Count; ++i)
            {
                var item = itemCaches[i];
                EditorGUILayout.BeginHorizontal();
                if (preDraw != null) preDraw(item.Path);
                var o = EditorGUILayout.ObjectField(item.Object, typeof(UnityEngine.Object), false);
                if (postDraw != null) postDraw(item.Path);
                EditorGUILayout.EndHorizontal();
                if (o != item.Object && onDrop != null)
                {
                    onDrop(item.Path, o);
                }
            }
            if (isScroll)
            {
                GUILayout.EndScrollView();
            }
        }
    }

    public class ScrollableObjectListWindow : EditorWindow
    {
        ScrollableObjectList list = new ScrollableObjectList();

        public static void Open(string title, IEnumerable<string> paths)
        {
            var w = EditorWindow.GetWindow<ScrollableObjectListWindow>(false);
            w.titleContent.text = title;
            w.list.AddRange(paths);
        }

        void OnGUI()
        {
            list.Draw();
        }
    }
}
