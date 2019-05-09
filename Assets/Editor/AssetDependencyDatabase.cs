using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SQLite;

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
            var dep = new Dependency();
            dep.TargetGUID = targetGUID;
            dep.DependencyGUID = depGUID;
            dep.ModifiedAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
            dep.FileSize = (int)fileInfo.Length;
            dep.UpdateAt = Now();
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
            var list = db.Query<Dependency>("select * from Dependency where DependencyGUID = ?", guid);
            var result = new List<string>();
            foreach (var r in list)
            {
                result.Add(r.TargetGUID);
            }
            return result;
        }

        public static string PathToGUID(string path, bool validation = false)
        {
            var db = Open();
            var r = db.Query<AssetInfo>("select * from AssetInfo where Path = ?", path);
            FileInfo fileInfo = null;
            if (r.Count == 1)
            {
                var info = r[0];
                if (!validation)
                {
                    return info.GUID;
                }
                fileInfo = new FileInfo(path);
                var modAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
                if (fileInfo.Exists && info.ModifiedAt == modAt && info.FileSize == fileInfo.Length)
                {
                    return info.GUID;
                }
                else
                {
                    db.Execute("delete from AssetInfo where GUID = ?", info.GUID);
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
            var r = db.Query<AssetInfo>("select * from AssetInfo where GUID = ?", guid);
            string path = null;
            FileInfo fileInfo = null;
            if (r.Count == 1)
            {
                var info = r[0];
                if (!validation)
                {
                    return info.Path;
                }
                path = info.Path;
                fileInfo = new FileInfo(path);
                var modAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
                if (info.ModifiedAt == modAt && info.FileSize == fileInfo.Length)
                {
                    return path;
                }
                else
                {
                    db.Execute("delete from AssetInfo where GUID = ?", guid);
                }
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
            return guid;
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
                    var ext = Path.GetExtension(path);
                    if (IgnoreExtensions.Contains(ext))
                    {
                        continue;
                    }
                    var fileInfo = new FileInfo(path);
                    if (!fileInfo.Exists || fileInfo.Length > int.MaxValue || fileInfo.Length < 40)
                    {
                        continue;
                    }
                    var modAt = ToUnixTime(fileInfo.LastWriteTimeUtc);
                    var len = (int)fileInfo.Length;
                    var db = Open();
                    var r = db.Query<AssetInfo>("select * from AssetInfo where Path = ?", path);
                    var assetInfo = r.Count == 1 ? r[0] : null;
                    string targetGUID = null;
                    if (assetInfo != null)
                    {
                        if (assetInfo.FileSize == len &&
                            assetInfo.ModifiedAt == modAt &&
                            assetInfo.ScanAt > assetInfo.ModifiedAt)
                        {
                            continue;
                        }
                        targetGUID = assetInfo.GUID;
                    }
                    targetGUID = targetGUID ?? ReadGUIDFromMeta(path);
                    if (targetGUID == null)
                    {
                        continue;
                    }
                    // Log($"scan.. {path} {targetGUID}");
                    if (!ReadBuffer(path, ref buffer, len))
                    {
                        continue;
                    }
                    guids = CollectGUIDs(buffer, len, guids);
                    if (guids.Count == 0)
                    {
                        continue;
                    }
                    db = Open();
                    var dep = new Dependency();
                    foreach (var guid in guids)
                    {
                        dep.TargetGUID = targetGUID;
                        dep.DependencyGUID = guid;
                        dep.ModifiedAt = modAt;
                        dep.FileSize = len;
                        dep.UpdateAt = Now();
                        try
                        {
                            db.Insert(dep);
                        }
                        catch (SQLiteException e)
                        {
                            System.Console.WriteLine(e);
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
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }
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
                if (IsHexChar(bytes[i]))
                {
                    var j1 = i - 1;
                    for (; j1 > 0 && IsHexChar(bytes[j1]); --j1) { }
                    ++j1;
                    var j2 = i + 1;
                    for (; j2 < len && j2 - j1 < 33 && IsHexChar(bytes[j2]); ++j2) { }
                    --j2;
                    var n = j2 - j1 + 1;
                    if (n == 32)
                    {
                        var guid = Encoding.UTF8.GetString(bytes, j1, 32);
                        if (!guids.Contains(guid)) guids.Add(guid);
                    }
                    i = j2 + 33;
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
    }

    #if  NET_2_0 || NET_2_0_SUBSET
    class ConcurrentQueue<T>
    {
        Queue<T> queue  = new Queue<T>();
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
