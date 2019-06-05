using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SQLite;
using System.Linq.Expressions;
#if !NET_2_0 && !NET_2_0_SUBSET
using System.Collections.Concurrent;
#endif

namespace AssetDependencyTool
{
    public static class AssetDependencyDatabase
    {
        public class AssetInfo
        {
            [PrimaryKey]
            public string GUID { get; set; }
            [Unique]
            public string Path { get; set; }
            public long CreateAt { get; set; }
            public int FileSize { get; set; }
            public long ModifiedAt { get; set; }
            public long ScanAt { get; set; }
        }

        public class Dependency
        {
            [PrimaryKey, AutoIncrement]
            public int ID { get; set; }
            [Indexed]
            public string TargetGUID { get; set; }
            [Indexed]
            public string DependencyGUID { get; set; }
            public long UpdateAt { get; set; }
            public int FileSize { get; set; }
            public long ModifiedAt { get; set; }
        }

        public static readonly string[] IgnoreExtensions = new[]
        {
            ".meta", ".png", ".fbx", ".dll",
        };

        public static Action<string> Trace { get; set; }
        static SQLiteConnection sharedConnection;
        static Dictionary<string, string> builtinGUIDs;
        static object builtinGUIDsSync = new object();

        static SQLiteConnection Open()
        {
            if (sharedConnection != null)
            {
                return sharedConnection;
            }

            var path = Path.Combine(Directory.GetCurrentDirectory(), "Library/AssetDependencyCache.db");
            var conns = new SQLiteConnectionString(path, false);
            var db = new SQLiteConnection(conns);
            db.CreateTable<AssetInfo>();
            db.CreateTable<Dependency>();
            db.CreateIndex<Dependency>(d => d.TargetGUID, d => d.DependencyGUID, true);
            sharedConnection = db;
            return db;
        }

        public static void Close()
        {
            if (sharedConnection == null)
            {
                return;
            }

            sharedConnection.Close();
            sharedConnection = null;
        }

        static void Log(string s)
        {
            if (Trace != null) Trace(s);
        }

        public static void Truncate()
        {
            var db = Open();
            db.DeleteAll<AssetInfo>();
            db.DeleteAll<Dependency>();
        }

        public static void InsertDependencyPath(string targetPath, string dependencyPath)
        {
            var fileInfo = new FileInfo(targetPath);
            if (!fileInfo.Exists || fileInfo.Length > int.MaxValue)
            {
                return;
            }
            var targetGUID = PathToGUID(targetPath);
            var depGUID = PathToGUID(dependencyPath);
            if (targetGUID == null || depGUID == null)
            {
                return;
            }
            var db = Open();
            var dep = new Dependency
            {
                TargetGUID = targetGUID,
                DependencyGUID = depGUID,
                ModifiedAt = ToUnixTime(fileInfo.LastWriteTimeUtc),
                FileSize = (int)fileInfo.Length,
                UpdateAt = Now()
            };
            try
            {
                db.Insert(dep);
            }
            catch (SQLiteException e)
            {
                System.Console.WriteLine(e);
            }
        }

        public static List<string> GetReferences(string path)
        {
            var guid = PathToGUID(path, true);
            if (guid == null)
            {
                Log(string.Format("error! PathToGUID {0}", path));
                return null;
            }
            var db = Open();
            var rows = db.Query<Dependency>("select * from Dependency where DependencyGUID = ?", guid);
            var result = new List<string>();
            foreach (var r in rows)
            {
                var refGUID = r.TargetGUID;
                var refPath = GUIDToPath(refGUID, true);
                if (refPath == null) continue;
                var fi = new FileInfo(refPath);
                var modAt = ToUnixTime(fi.LastWriteTimeUtc);
                if (fi.Exists && r.ModifiedAt == modAt && r.FileSize == fi.Length)
                {
                    result.Add(refPath);
                }
                else
                {
                    db.Execute("delete from Dependency where ID = ?", r.ID);
                }
            }
            return result;
        }

        static List<string> GetReferenceGUIDs(string guid)
        {
            // without validation!!
            var db = Open();
            var rows = db.QueryEqual<Dependency>(d => d.DependencyGUID, guid);
            var result = new List<string>();
            foreach (var r in rows)
            {
                result.Add(r.TargetGUID);
            }
            return result;
        }

        public static AssetInfo GetAssetInfo(string path)
        {
            var db = Open();
            return db.FindEqual<AssetInfo>(ai => ai.Path, path);
        }

        public static string PathToGUID(string path, bool validation = false)
        {
            var assetInfo = GetAssetInfo(path);
            FileInfo fileInfo = null;
            if (assetInfo != null)
            {
                if (!validation)
                {
                    return assetInfo.GUID;
                }
                fileInfo = new FileInfo(path);
                var modAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
                if (fileInfo.Exists && assetInfo.ModifiedAt == modAt && assetInfo.FileSize == fileInfo.Length)
                {
                    return assetInfo.GUID;
                }
                else
                {
                    Open().Execute("delete from AssetInfo where GUID = ?", assetInfo.GUID);
                }
            }

            var guid = ReadGUIDFromMeta(path);
            if (guid == null)
            {
                return null;
            }
            InsertAssetInfo(path, guid, fileInfo);
            return guid;
        }

        public static string GUIDToPath(string guid, bool validation = false)
        {
            var db = Open();
            var assetInfo = db.FindEqual<AssetInfo>(a => a.GUID, guid);
            string path = null;
            FileInfo fileInfo = null;
            if (assetInfo != null)
            {
                if (!validation)
                {
                    return assetInfo.Path;
                }
                path = assetInfo.Path;
                fileInfo = new FileInfo(path);
                var modAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
                if (fileInfo.Exists && assetInfo.ModifiedAt == modAt && assetInfo.FileSize == fileInfo.Length)
                {
                    return path;
                }
                else
                {
                    db.Execute("delete from AssetInfo where GUID = ?", guid);
                }
                if (!fileInfo.Exists) path = null;
            }

            if (path == null)
            {
                path = ReadPathFromLibrary(guid);
                if (path == null)
                {
                    return null;
                }
            }

            InsertAssetInfo(guid, path, fileInfo);
            return path;
        }

        static void InsertAssetInfo(string path, string guid, FileInfo fileInfo = null)
        {
            fileInfo = fileInfo ?? new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length > int.MaxValue)
            {
                return;
            }
            var info = new AssetInfo();
            info.Path = path;
            info.GUID = guid;
            info.CreateAt = Now();
            info.ScanAt = 0;
            info.ModifiedAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
            info.FileSize = (int)fileInfo.Length;
            var db = Open();
            db.InsertOrReplace(info);
        }

        public static long ToUnixTime(DateTime datetime)
        {
            return (long)(datetime - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        public static long Now()
        {
            return ToUnixTime(DateTime.UtcNow);
        }

        static string ReadGUIDFromMeta(string path)
        {
            var metaPath = path + ".meta";
            if (!File.Exists(metaPath))
            {
                if (path.StartsWith("ProjectSettings/"))
                {
                    return ReadBuiltinGUID(path);
                }
                Log("file doesnot exist: " + metaPath);
                return null;
            }
            var content = File.ReadAllText(metaPath);
            var i = content.IndexOf("\nguid: ");
            if (i == -1)
            {
                Log("no guid");
                return null;
            }
            var j = content.IndexOf("\n", i + 1);
            if (j == -1)
            {
                Log("no newline");
                return null;
            }
            var begin = i + "\nguid: ".Length;
            var guid = content.Substring(begin, j - begin).Trim();
            if (guid.Length != 32)
            {
                Log("invalid guid");
                return null;
            }
            return guid;
        }

        static string ReadBuiltinGUID(string path)
        {
            if (builtinGUIDs == null)
            {
                var scan = false;
                lock (builtinGUIDsSync)
                {
                    if (builtinGUIDs == null)
                    {
                        var dict = new Dictionary<string, string>();
                        foreach (var file in Directory.GetFiles("Library/metadata/00", "*00000000000000.info"))
                        {
                            var name = Path.GetFileNameWithoutExtension(file);
                            if (name.Length != 32) continue;
                            var infoPath = ReadPathFromLibrary(name);
                            if (!string.IsNullOrEmpty(infoPath))
                            {
                                dict[infoPath] = name;
                            }
                        }
                        builtinGUIDs = dict;
                        scan = true;
                    }
                }
                if (scan)
                {
                    foreach (var pair in builtinGUIDs)
                    {
                        InsertAssetInfo(pair.Key, pair.Value);
                    }
                }
            }
            string guid;
            if (builtinGUIDs.TryGetValue(path, out guid))
            {
                return guid;
            }
            return null;
        }

        static string ReadPathFromLibrary(string guid)
        {
            var metadataPath = string.Format("Library/metadata/{0}/{1}.info", guid.Substring(0, 2), guid);
            var dataPath = Path.Combine(Directory.GetCurrentDirectory(), metadataPath);
            var content = File.ReadAllText(dataPath);
            var i = content.IndexOf("\npath: ");
            if (i == -1)
            {
                return null;
            }
            var j = content.IndexOf("\n", i + 1);
            if (j == -1)
            {
                return null;
            }
            var begin = i + "\npath: ".Length;
            var path = content.Substring(begin, j - begin).Trim();
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            return path;
        }

        static Thread scanWorker;
        static ConcurrentQueue<string> targetQueue = new ConcurrentQueue<string>();
        static bool isScanPaused = false;

        public static void SetScanPaused(bool paused)
        {
            if (isScanPaused && !paused)
            {
                isScanPaused = paused;
                EnqueueScanDependency(null);
            }
            isScanPaused = paused;
        }

        public static void EnqueueScanDependency(string path)
        {
            if (path != null) targetQueue.Enqueue(path);
            if ((scanWorker == null || !scanWorker.IsAlive) && !isScanPaused)
            {
                scanWorker = new Thread(ScanDependencies);
                scanWorker.Start();
            }
        }

        static void ScanDependencies()
        {
            byte[] buffer = null;
            List<string> guids = null;
            try
            {
                while (true)
                {
                    while (isScanPaused)
                    {
                        Thread.Sleep(500);
                    }
                    var path = "";
                    if (!targetQueue.TryDequeue(out path))
                    {
                        return;
                    }
                    // Log(path);
                    FileInfo fileInfo;
                    AssetInfo assetInfo;
                    if (!NeedsScan(path, out fileInfo, out assetInfo))
                    {
                        continue;
                    }
                    var modAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
                    var len = (int)fileInfo.Length;
                    var targetGUID = assetInfo.GUID;
                    if (targetGUID == null)
                    {
                        continue;
                    }
                    // Log($"read.. {path} {targetGUID}");
                    if (!ReadBuffer(path, ref buffer, len))
                    {
                        continue;
                    }
                    // Log($"collect.. {path} {targetGUID}");
                    guids = CollectGUIDs(buffer, len, guids);
                    if (guids.Count == 0)
                    {
                        continue;
                    }
                    // Log($"insert.. {path} {targetGUID}");
                    // db.Execute("delete from Dependency where TargetGUID = ?", targetGUID);
                    var db = Open();
                    db.BeginTransaction();
                    var dep = new Dependency();
                    foreach (var guid in guids)
                    {
                        // Log($"{path} {targetGUID} -> {guid}");
                        dep.TargetGUID = targetGUID;
                        dep.DependencyGUID = guid;
                        dep.ModifiedAt = modAt;
                        dep.FileSize = len;
                        dep.UpdateAt = Now();
                        try
                        {
                            db.Insert(dep);
                        }
                        catch (SQLiteException)
                        {
                            var dup = db.FindEqual<Dependency>(d => d.TargetGUID, targetGUID, d => d.DependencyGUID, guid);
                            if (dup != null)
                            {
                                dep.ID = dup.ID;
                                db.InsertOrReplace(dep);
                            }
                        }
                    }
                    assetInfo = assetInfo ?? new AssetInfo();
                    assetInfo.GUID = targetGUID;
                    assetInfo.Path = path;
                    assetInfo.CreateAt = Now();
                    assetInfo.FileSize = len;
                    assetInfo.ModifiedAt = modAt;
                    assetInfo.ScanAt = Now();
                    db.InsertOrReplace(assetInfo);
                    db.Commit();
                }
            }
            // catch (ThreadAbortException)
            // {
            // }
            catch (Exception e)
            {
                Log(e.ToString());
            }
            finally
            {
                scanWorker = null;
            }
        }

        static bool NeedsScan(string path, out FileInfo fileInfo, out AssetInfo assetInfo)
        {
            assetInfo = null;
            fileInfo = new FileInfo(path);
            var ext = Path.GetExtension(path);
            if (!fileInfo.Exists || fileInfo.Length > int.MaxValue || fileInfo.Length < 40)
            {
                return false;
            }
            if (IgnoreExtensions.Contains(ext))
            {
                return false;
            }
            var modAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
            var len = (int)fileInfo.Length;
            var db = Open();
            var r = db.Query<AssetInfo>("select * from AssetInfo where Path = ?", path);
            assetInfo = r.Count == 1 ? r[0] : null;
            string targetGUID = null;
            if (assetInfo != null)
            {
                if (assetInfo.FileSize == len &&
                    assetInfo.ModifiedAt == modAt &&
                    assetInfo.ScanAt > assetInfo.ModifiedAt)
                {
                    assetInfo.Path = path;
                    assetInfo.FileSize = len;
                    assetInfo.ModifiedAt = modAt;
                    return false;
                }
                targetGUID = assetInfo.GUID;
            }
            targetGUID = targetGUID ?? ReadGUIDFromMeta(path);
            if (targetGUID == null)
            {
                return false;
            }
            if (assetInfo == null)
            {
                assetInfo = new AssetInfo();
                assetInfo.Path = path;
                assetInfo.FileSize = len;
                assetInfo.ModifiedAt = modAt;
            }
            assetInfo.GUID = targetGUID;
            return true;
        }

        static bool ReadBuffer(string path, ref byte[] buffer, int len)
        {
            if (buffer == null)
            {
                buffer = new byte[16];
            }
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(buffer, 0, 16) != 16)
                {
                    return false;
                }
                var i = 0;
                for (; i < 16; ++i)
                {
                    var b = buffer[i];
                    if (b != 0xef && b != 0xbb && b != 0xbf) break;
                }
                var head = "%YAML 1.1";
                var j = 0;
                for (; i < 16 && j < head.Length; ++i)
                {
                    var b = buffer[i];
                    if (b != (byte)head[j++]) return false;
                }
                if (buffer.Length < len)
                {
                    buffer = new byte[len];
                }
                else if (buffer.Length > len * 2)
                {
                    Array.Resize(ref buffer, buffer.Length / 2);
                }
                fs.Seek(0, SeekOrigin.Begin);
                var count = 0;
                var offset = 0;
                while ((count = fs.Read(buffer, offset, len - offset < 1024 ? len - offset : 1024)) > 0)
                {
                    offset += count;
                    if (offset >= len) break;
                }
            }
            return true;
        }

        static List<string> CollectGUIDs(byte[] bytes, int len, List<string> guids)
        {
            guids = guids ?? new List<string>();
            guids.Clear();
            var i = 31;
            while (i < len)
            {
                var b = bytes[i];
                if (('0' <= b && b <= '9') || ('a' <= b && b <= 'f'))
                {
                    var j1 = i + 1;
                    for (; j1 < len && j1 - i < 34 && IsHexChar(bytes[j1]); ++j1) { }
                    if (j1 - i > 32)
                    {
                        i += j1 + 32;
                        continue;
                    }
                    --j1;
                    var j2 = i - 1;
                    for (; j2 > 0 && j1 - j2 < 34 && IsHexChar(bytes[j2]); --j2) { }
                    ++j2;
                    var n = j1 - j2 + 1;
                    if (n == 32)
                    {
                        var guid = Encoding.UTF8.GetString(bytes, j2, 32);
                        if (!guids.Contains(guid)) guids.Add(guid);
                    }
                    i = j1 + 33;
                }
                else
                {
                    i += 32;
                }
            }
            return guids;

        }

        static bool IsHexChar(byte b)
        {
            return ('0' <= b && b <= '9') || ('a' <= b && b <= 'f');
        }

        static Thread fullScanWorker;

        public static bool IsFullScanRunning()
        {
            return fullScanWorker != null && fullScanWorker.IsAlive;
        }

        public static int GetScanQueueCount()
        {
            return targetQueue.Count;
        }

        public static void StartFullScanWorker()
        {
            if (fullScanWorker == null || !fullScanWorker.IsAlive)
            {
                fullScanWorker = new Thread(ScanAllAssets);
                fullScanWorker.Start();
            }
        }

        static void ScanAllAssets()
        {
            var rootInfo = GetAssetInfo("Assets");
            if (rootInfo == null)
            {
                rootInfo = new AssetInfo();
                rootInfo.Path = "Assets";
                rootInfo.GUID = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
                rootInfo.ScanAt = 0;
            }
            var lastScanAt = rootInfo.ScanAt;
            var startAt = Now();
            var dirs = new List<string>() { "Assets" };

            for (var i = 0; i < dirs.Count; ++i)
            {
                var dir = dirs[i];
                var di = new DirectoryInfo(dir);
                var modAt = ToUnixTime(di.LastWriteTimeUtc);
                if (modAt < lastScanAt)
                {
                    dirs.RemoveAt(i--);
                    continue;
                }
                foreach (var child in Directory.GetDirectories(dir))
                {
                    dirs.Add(child);
                }
            }

            for (var i = 0; i < dirs.Count; ++i)
            {
                var dir = dirs[i];
                var files = Directory.GetFiles(dir);
                foreach (var name in files)
                {
                    if (name[0] == '.') continue;
                    var ext = Path.GetExtension(name).ToLower();
                    if (ext == ".meta") continue;
                    if (!files.Contains(name + ".meta")) continue;
                    var file = name.Replace('\\', '/');
                    FileInfo fileInfo;
                    AssetInfo assetInfo;
                    if (NeedsScan(file, out fileInfo, out assetInfo))
                    {
                        // Log(file);
                        EnqueueScanDependency(file);
                    }
                }
            }
            while (targetQueue.Count > 0)
            {
                EnqueueScanDependency(null);
                Thread.Sleep(3000);
            }
            rootInfo.ScanAt = startAt;
            // Log("Ends ScanAll.");
            Open().InsertOrReplace(rootInfo);
            fullScanWorker = null;
        }
    }

    static class SQLiteExtension
    {
        public static TTable FindEqual<TTable>(this SQLiteConnection db, Expression<Func<TTable, object>> prop, object value)
            where TTable : class, new()
        {
            var r = db.QueryEqual<TTable>(prop, value);
            return r.Count == 1 ? r[0] : null;
        }

        public static List<T> QueryEqual<T>(
            this SQLiteConnection db,
            Expression<Func<T, object>> prop,
            object value)
                where T : class, new()
        {
            var map = db.GetMapping<T>();
            var col = map.FindColumn<T>(prop);
            var q = string.Format("select * from {0} where {1} = ?", map.TableName, col.Name);
            return db.Query<T>(q, value);
        }

        public static TTable FindEqual<TTable>(
            this SQLiteConnection db,
            Expression<Func<TTable, object>> prop1, object value1,
            Expression<Func<TTable, object>> prop2, object value2
        ) where TTable : class, new()
        {
            var map = db.GetMapping<TTable>();
            var col1 = map.FindColumn<TTable>(prop1);
            var col2 = map.FindColumn<TTable>(prop2);
            var q = string.Format("select * from {0} where {1} = ? and {2} = ?", map.TableName, col1.Name, col2.Name);
            var r = db.Query<TTable>(q, value1, value2);
            return r.Count == 1 ? r[0] : null;
        }


    }

    #if  NET_2_0 || NET_2_0_SUBSET
    class ConcurrentQueue<T>
    {
        Queue<T> queue  = new Queue<T>();
        public int Count
        {
            get
            {
                lock (queue)
                {
                    return queue.Count;
                }
            }
        }
        public void Enqueue(T item)
        {
            lock (queue)
            {
                queue.Enqueue(item);
            }
        }
        public bool TryDequeue(out T item)
        {
            lock (queue)
            {
                if (queue.Count == 0)
                {
                    item = default(T);
                    return false;
                }
                item = queue.Dequeue();
                return true;
            }
        }
    }
    #endif

}
