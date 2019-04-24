using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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

        static SQLiteConnection sharedConnection;

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

        public static void InsertDependencyPath(string targetPath, string dependencyPath)
        {
            var fileInfo = new FileInfo(targetPath);
            if (!fileInfo.Exists || fileInfo.Length > int.MaxValue)
            {
                return;
            }
            var targetGUID = GetGUIDFromPath(targetPath);
            var depGUID = GetGUIDFromPath(dependencyPath);
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
            db.Insert(dep);
        }

        public static List<string> GetReferences(string path)
        {
            var guid = GetGUIDFromPath(path, true);
            if (guid == null)
            {
                UnityEngine.Debug.Log("error!");
                return null;
            }
            var db = Open();
            var rows = db.Query<Dependency>("select * from Dependency where DependencyGUID = ?", guid);
            var result = new List<string>();
            foreach (var r in rows)
            {
                var refGUID = r.TargetGUID;
                var refPath = GetPathFromGUID(refGUID, true);
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

        public static string GetGUIDFromPath(string path, bool validation = false)
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

        public static string GetPathFromGUID(string guid, bool validation = false)
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
            if (!File.Exists(path) || !File.Exists(metaPath))
            {
                return null;
            }
            var content = File.ReadAllText(metaPath);
            var i = content.IndexOf("\nguid: ");
            if (i == -1)
            {
                return null;
            }
            var j = content.IndexOf("\n", i + 1);
            if (j == -1)
            {
                return null;
            }
            var begin = i + "\nguid: ".Length;
            var guid = content.Substring(begin, j - begin).Trim();
            if (guid.Length != 32)
            {
                return null;
            }
            return guid;
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
    }
}
