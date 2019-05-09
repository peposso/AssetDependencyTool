using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace AssetDependencyTool
{
    public class DependencyInfo
    {
        public bool modifyOnly = false;
        public string ext = "";
        public string beforePath = "";
        public string beforeGUID = "";
        public string beforePrefix = "";
        public string afterPath = "";
        public string afterGUID = "";
        public string afterPrefix = "";
        public Dictionary<int, int> fileIDMap = null;
    }

    public class AssetDuplicator
    {

        public static void Duplicate(IEnumerable<string> targets, System.Func<string, bool> func)
        {
            var dir = Path.GetDirectoryName(targets.First()).Replace('\\', '/');
            var items = targets.SelectMany(target => AssetDatabase.GetDependencies(target, true)).Distinct().ToList();
            var list = new List<DependencyInfo>();
            foreach (var item in items)
            {
                var src = item.Replace('\\', '/');
                if (targets.Contains(src))
                {
                    continue;
                }
                var dest = UniqueFileName(dir + "/" + Path.GetFileName(src));
                if (src == dest)
                {
                    continue;
                }
                var modifyOnly = false;
                if (!func(src))
                {
                    modifyOnly = true;
                }
                var info = new DependencyInfo()
                {
                    modifyOnly = modifyOnly,
                    ext = Path.GetExtension(src).ToLower(),
                    beforePath = src,
                    beforeGUID = AssetDatabase.AssetPathToGUID(src),
                    beforePrefix = Path.GetDirectoryName(src),
                    afterPath = dest,
                    afterPrefix = dir,
                };
                list.Add(info);
            }

            if (list.Count == 0)
            {
                Debug.Log("no copying");
                return;
            }
            // DuplicatorAssetPostprocessor.infoList = list;
            foreach (var info in list)
            {
                if (info.modifyOnly) continue;
                if (File.Exists(info.afterPath))
                {
                    if (IsSameContent(info.beforePath, info.afterPath))
                    {
                        continue;
                    }
                    info.afterPath = UniqueFileName(info.afterPath);
                }
                // File.Copy(info.beforePath, info.afterPath);
                AssetDatabase.CopyAsset(info.beforePath, info.afterPath);
            }
            AssetDatabase.Refresh();
            // DuplicatorAssetPostprocessor.infoList = null;

            foreach (var info in list)
            {
                if (info.modifyOnly) continue;
                info.afterGUID = AssetDatabase.AssetPathToGUID(info.afterPath);
                if (string.IsNullOrEmpty(info.afterGUID))
                {
                    throw new System.Exception(string.Format("unknown guid path:{0}", info.afterPath));
                }
            }
            foreach (var info in list)
            {
                if (info.modifyOnly) continue;
                if (info.ext == ".fbx")
                {
                    info.fileIDMap = CreateFBXFileIDMap(info.beforePath, info.afterPath);
                }
            }

            foreach (var target in targets)
            {
                ReplaceGUIDs(target, list);
            }
            foreach (var info in list)
            {
                ReplaceGUIDs(info.modifyOnly ? info.beforePath : info.afterPath, list);
            }
            AssetDatabase.Refresh();
            Debug.Log("done");
        }

        static Dictionary<int, int> CreateFBXFileIDMap(string src, string dest)
        {
            var srcMap = MiniYamlReader.Read(src + ".meta").Get("ModelImporter").Get("fileIDToRecycleName").CastValues<string>();
            var destMap = MiniYamlReader.Read(dest + ".meta").Get("ModelImporter").Get("fileIDToRecycleName").CastValues<string>();
            if (srcMap == null || destMap == null)
            {
                return null;
            }
            var map = new Dictionary<int, int>();
            foreach (var p1 in srcMap)
            {
                if (p1.Value == "//RootNode")
                {
                    continue;
                }
                foreach (var p2 in destMap)
                {
                    if (p2.Value == p1.Value)
                    {
                        var fileID1 = System.Convert.ToInt32(p1.Key);
                        var fileID2 = System.Convert.ToInt32(p2.Key);
                        if (fileID1 != fileID2)
                        {
                            map[fileID1] = fileID2;
                        }
                    }
                }
            }
            if (map.Count == 0)
            {
                return null;
            }
            var pairs = string.Join(", ", map.Select(p => string.Format("{0}: {1}", p.Key, p.Value)).ToArray());
            Debug.Log("fileID mapping: {" + pairs + "}");
            return map;
        }

        public static void ReplaceDependencyGUIDs(IEnumerable<string> targets, string beforeGUID, string afterGUID)
        {
            var relations = new List<string>();
            relations.AddRange(targets);
            foreach (var target in targets)
            {
                relations.AddRange(AssetDatabase.GetDependencies(target, true));
            }
            relations = relations.Distinct().ToList();
            var list = new List<DependencyInfo>();
            list.Add(new DependencyInfo { beforeGUID = beforeGUID, afterGUID = afterGUID });
            foreach (var path in relations)
            {
                ReplaceGUIDs(path, list);
            }
            AssetDatabase.Refresh();
        }

        static void ReplaceGUIDs(string target, IEnumerable<DependencyInfo> infoList)
        {
            ProcessYAML(target, orig =>
            {
                var body = orig.ToString();
                foreach (var info in infoList)
                {
                    if (string.IsNullOrEmpty(info.afterGUID)) continue;
                    if (info.fileIDMap != null)
                    {
                        var re = new Regex(@"{fileID: (\d+), guid: " + info.beforeGUID + @", type: (\d+)}");
                        body = re.Replace(body, m =>
                        {
                            var fileID = System.Convert.ToInt32(m.Groups[1].Value);
                            var type = System.Convert.ToInt32(m.Groups[2].Value);
                            var destFileID = 0;
                            if (info.fileIDMap.TryGetValue(fileID, out destFileID))
                            {
                                Debug.LogFormat("FileID Mapping {0} -> {1}", fileID, destFileID);
                                fileID = destFileID;
                            }
                            return string.Format("{{fileID: {0}, guid: {1}, type: {2}}}", fileID, info.afterGUID, type);
                        });
                    }
                    body = body.Replace(info.beforeGUID, info.afterGUID);
                }
                return body;
            }, false);
        }

        public static void ProcessYAML(string target, System.Func<string, string> process, bool isRefresh = true)
        {
            var ext = Path.GetExtension(target).ToLower();
            if (ext == ".fbx" || ext == ".png" || ext == ".cs" || ext == ".shader")
            {
                return;
            }
            var head = ReadHead(target);
            if (head.IndexOf("%TAG !u! tag:unity3d.com,2011:") == -1)
            {
                return;
            }
            var orig = File.ReadAllText(target);
            var body = process(orig);
            if (body != orig)
            {
                File.WriteAllText(target, body);
                if (isRefresh) AssetDatabase.Refresh();
            }
        }

        public static void RemoveGUID(string path, string guid)
        {
            AssetDuplicator.ProcessYAML(path, source =>
            {
                while (true)
                {
                    var i = source.IndexOf(guid);
                    if (i == -1) break;
                    var left = source.LastIndexOf("{", i);
                    var right = source.IndexOf("}", i);
                    if (left == -1 || right == -1) break;
                    source = source.Substring(0, left) + "{fileID: 0}" + source.Substring(right + 1);
                }
                return source;
            });
        }

        static string UniqueFileName(string file)
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
                    file = Path.GetDirectoryName(file) + "/" + name + ext;
                }
            }
            return file;
        }

        static string ReadHead(string path)
        {
            var bytes = new byte [64];
            using (var file = File.Open(path, FileMode.Open))
            {
                file.Read(bytes, 0, bytes.Length);
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        static bool IsSameContent(string file1, string file2)
        {
            using (var s1 = File.OpenRead(file1))
                using (var s2 = File.OpenRead(file2))
                {
                    var buf1 = new byte[16 * 1024];
                    var buf2 = new byte[16 * 1024];
                    var bytesRead = -1;
                    while ((bytesRead = s1.Read(buf1, 0, buf1.Length)) > 0)
                    {
                        if (bytesRead != s2.Read(buf2, 0, bytesRead))
                        {
                            return false;
                        }
                        if (!buf1.Take(bytesRead).SequenceEqual(buf2.Take(bytesRead)))
                        {
                            return false;
                        }
                    }
                }
            return true;
        }
    }

    public static class MiniYamlReader
    {
        static readonly Regex HeadSpaces = new Regex("^ *");

        static public Dictionary<string, object> Read(string filePath)
        {
            return ParseAll(File.ReadAllLines(filePath));
        }

        static public Dictionary<string, object> Get(this Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object value;
            if (dict.TryGetValue(key, out value))
            {
                return value as Dictionary<string, object>;
            }
            return null;
        }

        static public T GetValue<T>(this Dictionary<string, object> dict, string key)
        {
            if (dict == null) return default(T);
            object value;
            if (dict.TryGetValue(key, out value))
            {
                return (T)System.Convert.ChangeType(value, typeof(T));
            }
            return default(T);
        }

        static public Dictionary<string, T> CastValues<T>(this Dictionary<string, object> dict)
        {
            if (dict == null) return null;
            var result = new Dictionary<string, T>();
            foreach (var pair in dict)
            {
                result[pair.Key] = (T)System.Convert.ChangeType(pair.Value, typeof(T));
            }
            return result;
        }

        static public Dictionary<string, object> ParseAll(string[] lines, int startIndex = 0, int startIndent = 0)
        {
            var result = new Dictionary<string, object>();
            var index = startIndex;
            var pair = default(KeyValuePair<string, object>);
            while (Parse(lines, ref index, startIndent, out pair))
            {
                if (!string.IsNullOrEmpty(pair.Key))
                {
                    result[pair.Key] = pair.Value;
                }
            }
            return result;
        }

        static bool Parse(string[] lines, ref int index, int indent, out KeyValuePair<string, object> pair)
        {
            pair = default(KeyValuePair<string, object>);
            if (lines.Length <= index)
            {
                return false;
            }
            var line = lines[index];
            if (line.Trim().Length == 0)
            {
                ++index;
                return true;
            }
            var m = HeadSpaces.Match(line);
            var currentIndent = m.Value.Length;
            if (currentIndent < indent)
            {
                return false;
            }
            var i = line.IndexOf(": ");
            if (i == -1)
            {
                if (line[line.Length - 1] != ':')
                {
                    ++index;
                    return true;
                }
                i = line.Length - 1;
            }
            var key = line.Substring(currentIndent, i - currentIndent);
            var val = i + 2 < line.Length ? line.Substring(i + 2).Trim() : "";
            ++index;
            if (val == "")
            {
                var child = new Dictionary<string, object>();
                var childPair = default(KeyValuePair<string, object>);
                while (Parse(lines, ref index, currentIndent + 1, out childPair))
                {
                    if (!string.IsNullOrEmpty(childPair.Key))
                    {
                        child[childPair.Key] = childPair.Value;
                    }
                }
                pair = new KeyValuePair<string, object>(key, child.Count > 0 ? child : null);
            }
            else if (val[0] == '{' && val[val.Length - 1] == '}')
            {
                var pairs = val.Substring(1, val.Length - 2).Trim().Split(',');
                for (var j = 0; j < pairs.Length; ++j)
                {
                    pairs[j] = HeadSpaces.Replace(pairs[j], "");
                }
                pair = new KeyValuePair<string, object>(key, ParseAll(pairs));
            }
            else
            {
                pair = new KeyValuePair<string, object>(key, val);
            }
            return true;
        }
    }

    // public class DuplicatorAssetPostprocessor : AssetPostprocessor
    // {
    //     public static List<DependencyInfo> infoList;

    //     void OnPreprocessModel()
    //     {
    //         if (infoList == null) { return; }
    //         var info = infoList.FirstOrDefault(i => i.afterPath == assetPath);
    //         if (info == null) { return; }
    //         var imp2 = assetImporter as ModelImporter;
    //         var imp1 = AssetImporter.GetAtPath(info.beforePath) as ModelImporter;

    //         if (imp2.importMaterials != imp1.importMaterials)
    //         {
    //             imp2.importMaterials = imp1.importMaterials;
    //         }
    //     }

    //     void OnPreprocessTexture()
    //     {
    //         if (infoList == null) { return; }
    //         var info = infoList.FirstOrDefault(i => i.afterPath == assetPath);
    //         if (info == null) { return; }
    //         var imp2 = assetImporter as TextureImporter;
    //         var imp1 = AssetImporter.GetAtPath(info.beforePath) as TextureImporter;

    //         if (imp2.alphaIsTransparency != imp1.alphaIsTransparency)
    //         {
    //             imp2.alphaIsTransparency = imp1.alphaIsTransparency;
    //         }
    //         if (imp2.mipmapEnabled != imp1.mipmapEnabled)
    //         {
    //             imp2.mipmapEnabled = imp1.mipmapEnabled;
    //         }
    //         if (imp2.wrapMode != imp1.wrapMode)
    //         {
    //             imp2.wrapMode = imp1.wrapMode;
    //         }
    //         if (imp2.npotScale != imp1.npotScale)
    //         {
    //             imp2.npotScale = imp1.npotScale;
    //         }
    //     }
    // }
}
