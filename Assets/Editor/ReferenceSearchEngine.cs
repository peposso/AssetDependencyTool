using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;

namespace AssetDependencyTool
{
    public class ReferenceSearchEngine : ReferenceSearchEngineProcess.IReferenceReport
    {
        public delegate void ResultCallback(string path);

        #if UNITY_EDITOR_WIN
        const string RipGrepReleaseUrl = "https://github.com/BurntSushi/ripgrep/releases/download/11.0.1/ripgrep-11.0.1-x86_64-pc-windows-msvc.zip";
        #elif UNITY_EDITOR_OSX
        const string RipGrepReleaseUrl = "https://github.com/BurntSushi/ripgrep/releases/download/11.0.1/ripgrep-11.0.1-x86_64-apple-darwin.tar.gz";
        #else
        const string RipGrepReleaseUrl = "";
        #endif

        public static Action<string> Tracer { get; set; }
        public bool IsSearching { get; private set; }
        public bool IsInitialized { get; private set; }

        internal static string RipGrepPath { get; private set; }
        internal static string Root { get; private set; }
        internal static string Prefix { get; private set; }

        string commonIgnorePatterns;
        object sync = new object();
        List<ReferenceSearchEngineProcess> processQueue = new List<ReferenceSearchEngineProcess>();
        ReferenceSearchEngineProcess currentProcess;
        int mainThreadId;
        Thread worker;
        int workerId;

        public ReferenceSearchEngine(string rootDirectory)
        {
            Root = rootDirectory;
            Prefix = Root.Replace('\\', '/') + "/";

            if (RipGrepPath == null)
            {
                #if UNITY_EDITOR_LINUX
                RipGrepPath = "rg";
                #else
                RipGrepPath = Path.Combine(Root, "Library/ripgrep/rg");
                #if UNITY_EDITOR_WIN
                RipGrepPath += ".exe";
                #endif
                #endif
            }

            mainThreadId = Thread.CurrentThread.ManagedThreadId;

            var sb = new StringBuilder();
            foreach (var ext in AssetDependencyDatabase.IgnoreExtensions)
            {
                sb.Append(" -g ");
                sb.Append(Quote("!*" + ext));
            }
            commonIgnorePatterns = sb.ToString();

            if (File.Exists(RipGrepPath))
            {
                IsInitialized = true;
            }
            else
            {
                DownloadRipGrep();
            }
        }

        void DownloadRipGrep()
        {
            new Thread(() =>
            {
                try
                {
                    var outDir = Prefix + "Library/ripgrep";
                    var ext = Path.GetExtension(RipGrepReleaseUrl);
                    var ext2 = Path.GetExtension(Path.GetFileNameWithoutExtension(RipGrepReleaseUrl));
                    if (!string.IsNullOrEmpty(ext2) && ext2.Length < 5)
                    {
                        ext = ext2 + ext;
                    }
                    var arcPath = Prefix + "Temp/ripgrep" + ext;
                    if (!File.Exists(arcPath))
                    {
                        #if UNITY_2017
                        var isDone = false;
                        string error = null;
                        UnityEditor.EditorApplication.delayCall += () =>
                        {
                            var req = new UnityEngine.Networking.UnityWebRequest(RipGrepReleaseUrl, "GET");
                            req.downloadHandler = new UnityEngine.Networking.DownloadHandlerFile(arcPath);
                            req.SendWebRequest();
                            UnityEditor.EditorApplication.CallbackFunction callback = null;
                            callback = () =>
                            {
                                if (!req.isDone && !req.isHttpError && !req.isNetworkError)
                                {
                                    UnityEditor.EditorApplication.delayCall = callback;
                                    return;
                                }
                                if (!string.IsNullOrEmpty(req.error))
                                {
                                    error = req.error;
                                }
                                isDone = true;
                            };
                            callback();
                        };
                        while (!isDone)
                        {
                            Thread.Sleep(500);
                        }
                        if (!string.IsNullOrEmpty(error))
                        {
                            throw new Exception(error);
                        }
                        #else
                        using (var client = new WebClient())
                        {
                            Log("download.. " + RipGrepReleaseUrl);
                            client.DownloadFile(RipGrepReleaseUrl, arcPath);
                        }
                        #endif
                    }
                    var fi = new FileInfo(arcPath);
                    if (!fi.Exists || fi.Length < 1000)
                    {
                        if (fi.Exists) File.Delete(arcPath);
                        throw new Exception("download failed");
                    }
                    if (!Directory.Exists(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                    }
                    Log("extract.. " + arcPath);
                    if (ext == ".zip")
                    {
                        using (var zip = ZipStorer.Open(arcPath, FileAccess.Read))
                        {
                            foreach (var e in zip.ReadCentralDir())
                            {
                                var path = Path.Combine(outDir, e.FilenameInZip);
                                Log(path);
                                zip.ExtractFile(e, path);
                            }
                        }
                    }
                    else if (ext == ".tar.gz")
                    {
                        ExecProcess("/usr/bin/tar", "zxvfk '" + arcPath + "' -C '" + outDir + "'");
                    }
                    else
                    {
                        throw new Exception("unknown: " + ext);
                    }
                    if (!File.Exists(RipGrepPath))
                    {
                        foreach (var f in Directory.GetFiles(outDir, "*.*", SearchOption.AllDirectories))
                        {
                            var rel = f.Substring(outDir.Length + 1);
                            Log(rel);
                            var dir = Path.GetDirectoryName(rel).Replace("\\", "/");
                            if (!string.IsNullOrEmpty(dir))
                            {
                                var destDir = string.Join("/", dir.Split('/').Skip(1).ToArray());
                                if (destDir == "")
                                {
                                    File.Move(f, outDir + "/" + Path.GetFileName(f));
                                }
                                else
                                {
                                    if (!Directory.Exists(outDir + "/" + destDir))
                                    {
                                        Directory.CreateDirectory(outDir + "/" + destDir);
                                    }
                                    File.Move(f, outDir + "/" + destDir + "/" + Path.GetFileName(f));
                                }
                            }
                        }
                    }
                    #if UNITY_EDITOR_OSX
                    ExecProcess("/bin/chmod", "+x Library/ripgrep/rg");
                    #endif
                    if (!File.Exists(RipGrepPath))
                    {
                        throw new Exception("no rg");
                    }
                    Log("done");
                    IsInitialized = true;
                }
                catch (Exception e)
                {
                    Log(e.ToString());
                }
            }).Start();
        }

        public static void Log(string s)
        {
            if (Tracer != null) Tracer.Invoke(s);
        }

        public static string ExecProcess(string bin, string args)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using (var p = new System.Diagnostics.Process())
            {
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.FileName = bin;
                p.StartInfo.Arguments = args;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                p.OutputDataReceived += (o, e) => { if (e.Data != null) stdout.Append(e.Data); };
                p.ErrorDataReceived += (o, e) => { if (e.Data != null) stderr.Append(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    Log("ExitCode=" + p.ExitCode);
                    Log("stdout>>\n" + stdout.ToString());
                    Log("stderr>>\n" + stderr.ToString());
                }
            }
            return stdout.ToString();
        }

        public void Start(string targetPath, bool isRecursive, ResultCallback callback)
        {
            if (!IsInitialized) return;
            if (string.IsNullOrEmpty(targetPath)) return;
            if (targetPath.StartsWith("Packages/")) return;

            // var callbackTarget = callback.Target as UnityObject;
            // if (callbackTarget == null)
            // {
            //     throw new Exception("callback.Target is not UnityObject");
            // }
            lock (sync)
            {
                CancelImpl();
                EnqueueProcess(targetPath, isRecursive, callback);
                StartWorker();
            }
            AssetDependencyDatabase.SetScanPaused(true);
            AssetDependencyDatabase.EnqueueScanDependency(targetPath);
        }

        public void Cancel()
        {
            lock (sync)
            {
                CancelImpl();
            }
        }

        public void CancelImpl()
        {
            if (currentProcess != null)
            {
                currentProcess.Cancel();
                currentProcess = null;
            }
            if (worker != null && !worker.IsAlive)
            {
                worker.Abort();
                worker = null;
                workerId = 0;
                IsSearching = false;
            }

            processQueue.Clear();
        }

        void EnqueueProcess(string path, bool isRecursive, ResultCallback callback, HashSet<string> results = null)
        {
            results = results ?? new HashSet<string>();
            var guid = AssetDependencyDatabase.PathToGUID(path);

            var crumbs = Path.GetDirectoryName(path).Split(new[] {'/', '\\'});
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

            ReferenceSearchEngineProcess process = null;

            for (var i = 0; i < dirs.Count; ++i)
            {
                var dir = dirs[i];
                var ignore = i > 0 ? dirs[i - 1] + "/" : null;
                if (dir == "ProjectSettings")
                {
                    ignore = null;
                }
                var merged = false;
                foreach (var q in processQueue)
                {
                    if (q.SearchDirectory == dir && q.IgnoreDirectory == ignore)
                    {
                        q.TargetPaths.Add(path);
                        q.TargetGUIDs.Add(guid);
                        merged = true;
                        process = q;
                        break;
                    }
                }
                if (!merged)
                {
                    process = new ReferenceSearchEngineProcess()
                    {
                        Argument = string.Format("--json {0} ", commonIgnorePatterns),
                        SearchDirectory = Prefix + dir,
                        IgnoreDirectory = ignore == null ? null : Prefix + ignore,
                        TargetPaths = new List<string>() {path},
                        TargetGUIDs = new List<string>() {guid},
                        Callback = callback,
                        IsRecursive = isRecursive,
                        Results = results,
                        Reporter = this,
                    };
                    processQueue.Add(process);
                }
            }
            var refPaths = AssetDependencyDatabase.GetReferences(path);
            if (refPaths == null)
            {
                return;
            }
            foreach (var refPath in refPaths)
            {
                lock (results)
                {
                    if (results.Contains(refPath))
                    {
                        continue;
                    }
                    results.Add(refPath);
                }
                FoundReference(process, refPath, path);
            }
        }

        static internal string Quote(string s)
        {
            #if UNITY_EDITOR_WIN
            return "\"" + s.Replace("\"", "\\\"") + "\"";
            #else
            return "'" + s.Replace("'", "\\'") + "'";
            #endif
        }

        void StartWorker()
        {
            if (worker == null || !worker.IsAlive)
            {
                worker = new Thread(ReadProcessOutput);
                workerId = worker.ManagedThreadId;
                worker.Start();
            }
        }

        void ReadProcessOutput()
        {
            try
            {
                IsSearching = true;
                while (true)
                {
                    if (Thread.CurrentThread.ManagedThreadId != workerId)
                    {
                        return;
                    }
                    ReferenceSearchEngineProcess p;
                    lock (sync)
                    {
                        if (processQueue.Count == 0)
                        {
                            AssetDependencyDatabase.SetScanPaused(false);
                            return;
                        }
                        p = processQueue[0];
                        processQueue.RemoveAt(0);
                        currentProcess = p;
                        p.StartProcess();
                    }
                    if (Thread.CurrentThread.ManagedThreadId != workerId)
                    {
                        return;
                    }
                    p.WaitForComplete();
                    if (Thread.CurrentThread.ManagedThreadId != workerId)
                    {
                        return;
                    }
                    lock (sync)
                    {
                        currentProcess = null;
                    }
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (System.Exception e)
            {
                Log(e.ToString());
            }
            finally
            {
                IsSearching = false;
            }
        }

        public void FoundReference(ReferenceSearchEngineProcess process, string objectPath, string targetPath)
        {
            // var o = process.Callback.Target as UnityObject;
            // if (o == null)
            // {
            //     return;
            // }
            if (objectPath.StartsWith(Root))
            {
                objectPath = objectPath.Substring(objectPath.Length + 1);
            }
            process.Callback(objectPath);
            if (process.IsRecursive && !targetPath.StartsWith("ProjectSettings/"))
            {
                EnqueueProcess(objectPath, process.IsRecursive, process.Callback, process.Results);
            }
            if (process.Results.Count > 5)
            {
                AssetDependencyDatabase.SetScanPaused(false);
            }
            AssetDependencyDatabase.EnqueueScanDependency(objectPath);
        }
    }

    public class ReferenceSearchEngineProcess
    {
        public interface IReferenceReport
        {
            void FoundReference(ReferenceSearchEngineProcess p, string objectPath, string targetPath);
        }

        public string Argument;
        public string SearchDirectory;
        public string IgnoreDirectory;
        public List<string> TargetPaths;
        public List<string> TargetGUIDs;
        public ReferenceSearchEngine.ResultCallback Callback;
        public bool IsRecursive;
        public HashSet<string> Results;
        public IReferenceReport Reporter;

        System.Diagnostics.Process process;

        internal void StartProcess()
        {
            process = new System.Diagnostics.Process();
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.StartInfo.FileName = ReferenceSearchEngine.RipGrepPath;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            var args = Argument;
            if (IgnoreDirectory != null)
            {
                args += string.Format(" -g {0} ", ReferenceSearchEngine.Quote("!" + IgnoreDirectory));
            }
            if (TargetGUIDs.Count == 1)
            {
                args += TargetGUIDs[0];
            }
            else
            {
                args += ReferenceSearchEngine.Quote(string.Join("|", TargetGUIDs.ToArray()));
            }
            args += string.Format(" {0}", ReferenceSearchEngine.Quote(SearchDirectory));
            // ReferenceSearchEngine.Log(args);
            process.StartInfo.Arguments = args;
            // process.EnableRaisingEvents = true;
            // process.OutputDataReceived += OnStdout;
            process.ErrorDataReceived += OnStderr;

            process.Start();
            // process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        internal void Cancel()
        {
            if (process != null && !process.HasExited)
            {
                process.Kill();
            }
            process = null;
        }

        internal void WaitForComplete()
        {
            string line;
            while (process != null && !process.HasExited && process.StandardOutput.Peek() > -1 && (line = process.StandardOutput.ReadLine()) != null)
            {
                OnStdoutLine(line);
            }
            // try
            // {
            //     Debug.Log("stderr..");
            //     while (process != null && (line = process.StandardError.ReadLine()) != null)
            //     {
            //         OnStderr(line);
            //     }
            // }
            // catch (System.InvalidOperationException)
            // {
            //     // cause by process.Kill
            //     return;
            // }
            if (process != null)
            {
                process.WaitForExit();
            }
            if (process != null)
            {
                process.Dispose();
            }
            process = null;
        }

        // void OnStdout(object sender, System.Diagnostics.DataReceivedEventArgs e)
        // {
        //     if (string.IsNullOrEmpty(e.Data)) return;
        //     foreach (var line in e.Data.Split('\n'))
        //     {
        //         Debug.Log(line);
        //         OnStdoutLine(line.Trim());
        //     }
        // }

        void OnStdoutLine(string line)
        {
            // ReferenceSearchEngine.Log(line);
            var i = FindValue(line, 0, "type");
            if (!IsValue(line, i, "match"))
            {
                return;
            }

            i = FindValue(line, 0, "data");
            i = FindValue(line, i, "path");
            i = FindValue(line, i, "text");
            var path = GetValue(line, i);
            if (path == null)
            {
                return;
            }
            path = path.Replace("\\\\", "\\").Replace('\\', '/');
            if (path.StartsWith(ReferenceSearchEngine.Prefix))
            {
                path = path.Substring(ReferenceSearchEngine.Prefix.Length);
            }

            i = FindValue(line, 0, "submatches");
            i = FindValue(line, i, "match");
            i = FindValue(line, i, "text");
            var m = GetValue(line, i);
            string targetPath = null;
            for (var j = 0; j < TargetGUIDs.Count; ++j)
            {
                var guid = TargetGUIDs[j];
                if (guid == m)
                {
                    targetPath = TargetPaths[j];
                }
            }
            lock (Results)
            {
                if (Results.Contains(path))
                {
                    return;
                }
                Results.Add(path);
            }
            if (targetPath != null)
            {
                Reporter.FoundReference(this, path, targetPath);
                AssetDependencyDatabase.InsertDependencyPath(path, targetPath);
            }
        }

        int FindValue(string s, int i, string key)
        {
            if (i < 0)
            {
                return i;
            }
            for (; i < s.Length; ++i)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                break;
            }
            while (i < s.Length)
            {
                i = s.IndexOf(key, i);
                if (i == -1) return i;
                if (s[i - 1] == '"' && s[i + key.Length] == '"')
                {
                    i += key.Length + 1;
                    for (; i < s.Length; ++i)
                    {
                        var c = s[i];
                        if (c == ':' || c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                        break;
                    }
                    return i;
                }
                i += key.Length;
            }
            return -1;
        }

        bool IsValue(string s, int i, string v)
        {
            if (i < 0)
            {
                return false;
            }
            for (; i < s.Length; ++i)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                break;
            }
            var q = false;
            if (s[i] == '"')
            {
                q = true;
                ++i;
            }
            for (var j = 0; j < v.Length; ++j)
            {
                if (s[i++] != v[j])
                {
                    return false;
                }
            }
            if (q)
            {
                return s[i] == '"';
            }
            return true;
        }

        string GetValue(string s, int i)
        {
            if (i < 0)
            {
                return null;
            }
            for (; i < s.Length; ++i)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                break;
            }

            if (s[i] == '"')
            {
                ++i;
                var j = s.IndexOf('"', i);
                return s.Substring(i, j - i);
            }
            var n = 0;
            for (; n < s.Length - i; ++n)
            {
                var c = s[i + n];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',' || c == ']' || c == '}') break;
            }
            return s.Substring(i, n);
        }

        void OnStderr(object sender, System.Diagnostics.DataReceivedEventArgs e)
        {
            var data = e.Data;
            if (string.IsNullOrEmpty(data) || data.StartsWith("No files were searched")) return;
            ReferenceSearchEngine.Log(data);
        }
    }
}
