// What the installer does: downloads (resumable, checked against the feed's SHA-256), zip extraction, game install /
// update (the patches' own apply_patch.ps1, like the in-game updater) / verify, server install and start.
// Everything runs on a worker thread and reports through a Job, which the window polls.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace EftInstaller
{
    sealed class CancelledException : Exception
    {
        public CancelledException() : base("Cancelled.") { }
    }

    /// <summary>Progress of the running operation, written by the worker and read by the window.</summary>
    sealed class Job
    {
        readonly object sync = new object();
        string status = "", step = "";
        long done, total;
        bool counting; // bytes are a transfer: show speed and time left
        readonly Queue<KeyValuePair<DateTime, long>> samples = new Queue<KeyValuePair<DateTime, long>>();
        public volatile bool Cancel;
        public volatile bool Cancellable = true;
        public Action<string> Log = delegate { };

        public void Step(string text)
        {
            lock (sync) { step = text; status = text; done = total = 0; counting = false; samples.Clear(); }
            Log(text);
        }

        public void Status(string text)
        {
            lock (sync) status = text;
        }

        public void Bytes(long doneBytes, long totalBytes, bool transfer)
        {
            lock (sync)
            {
                done = doneBytes;
                total = totalBytes;
                counting = transfer;
                if (transfer)
                {
                    DateTime now = DateTime.UtcNow;
                    samples.Enqueue(new KeyValuePair<DateTime, long>(now, doneBytes));
                    while (samples.Count > 2 && (now - samples.Peek().Key).TotalSeconds > 4) samples.Dequeue();
                }
            }
        }

        public void Check()
        {
            if (Cancel) throw new CancelledException();
        }

        /// <summary>status, detail ("1.2 GB of 3.4 GB  ·  12.3 MB/s  ·  2 min left"), fraction (-1 = none).</summary>
        public void Read(out string statusText, out string detail, out double fraction)
        {
            lock (sync)
            {
                statusText = status;
                fraction = total > 0 ? Math.Min(1.0, done / (double)total) : -1;
                if (total <= 0) { detail = ""; return; }
                detail = Ops.Size(done) + " of " + Ops.Size(total);
                if (counting && samples.Count >= 2)
                {
                    var first = samples.Peek();
                    var last = samples.Last();
                    double seconds = (last.Key - first.Key).TotalSeconds;
                    if (seconds > 0.5)
                    {
                        double rate = (last.Value - first.Value) / seconds;
                        detail += "   ·   " + Ops.Size((long)rate) + "/s";
                        if (rate > 1) detail += "   ·   " + Ops.Eta((total - done) / rate);
                    }
                }
            }
        }
    }

    static class Ops
    {
        public const string UserAgent = "EFTAlphaInstaller/1";
        public const string GameExe = "Escape from Tarkov Alpha.exe";
        public const string ServerExe = "ConsoleApp.exe";
        public const string DownloadDir = "_installer_download";
        // per-server state an update must never replace (the package does not carry them either)
        static readonly string[] ServerKeep = { "profiles\\", "logs\\", "ServerResponses\\", "TraderStock.json",
            "Files\\configs\\server.json", "Files\\configs\\bot.json", "Files\\configs\\custom.json", "Files\\configs\\plugin.json" };

        static Ops()
        {
            // Windows PowerShell / .NET 4 defaults may still offer TLS 1.0, which Cloudflare and most hosts refuse
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            ServicePointManager.DefaultConnectionLimit = 8;
        }

        // ------------------------------------------------------------------ formatting

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0") + " MB";
            return (bytes / 1073741824.0).ToString("0.00") + " GB";
        }

        public static string Eta(double seconds)
        {
            if (seconds < 60) return Math.Max(1, (int)seconds) + " s left";
            if (seconds < 3600) return (int)(seconds / 60) + " min " + ((int)seconds % 60) + " s left";
            return (int)(seconds / 3600) + " h " + ((int)seconds % 3600 / 60) + " min left";
        }

        // ------------------------------------------------------------------ download / hash

        public static void Download(string url, string path, long expected, Job job, string what)
        {
            if (string.IsNullOrEmpty(url)) throw new Exception(what + " has no download at this location. Pick another download location.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string partial = path + ".partial";
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (expected > 0 && have > expected) { File.Delete(partial); have = 0; }
            job.Status("Downloading " + what + (have > 0 ? " (resuming)" : ""));
            if (!(expected > 0 && have == expected))
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                        DownloadOnce(url, partial, have, expected, job, what);
                        break;
                    }
                    catch (WebException ex)
                    {
                        var response = ex.Response as HttpWebResponse;
                        if (response != null && (int)response.StatusCode == 416 && File.Exists(partial)) break; // already complete
                        if (job.Cancel) throw new CancelledException();
                        bool fatal = response != null && ((int)response.StatusCode == 404 || (int)response.StatusCode == 403 || (int)response.StatusCode == 401);
                        if (fatal || attempt >= 4) throw new Exception("Download of " + what + " failed: " + ex.Message + " (" + url + ")");
                        job.Status("Connection lost, retrying (" + attempt + "/3)...");
                        Thread.Sleep(2000 * attempt);
                    }
                    catch (IOException ex)
                    {
                        if (job.Cancel) throw new CancelledException();
                        if (attempt >= 4) throw new Exception("Download of " + what + " failed: " + ex.Message);
                        job.Status("Connection lost, retrying (" + attempt + "/3)...");
                        Thread.Sleep(2000 * attempt);
                    }
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(partial, path);
        }

        static void DownloadOnce(string url, string partial, long have, long expected, Job job, string what)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.AllowAutoRedirect = true;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            if (have > 0) request.AddRange(have);
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.ContentType != null && response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("The link for " + what + " opens a web page, not the file. The download link must be a direct link: " + url);
                bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) have = 0;
                long total = response.ContentLength > 0 ? response.ContentLength + have : expected;
                using (Stream input = response.GetResponseStream())
                using (var file = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buffer = new byte[1 << 20];
                    long done = have;
                    job.Bytes(done, total, true);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (job.Cancel) throw new CancelledException();
                        file.Write(buffer, 0, read);
                        done += read;
                        job.Bytes(done, total, true);
                    }
                    if (total > 0 && done < total) throw new IOException("the connection closed early (" + Size(done) + " of " + Size(total) + ")");
                }
            }
        }

        public static string Sha256(string path, Job job)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                long done = 0, total = stream.Length;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (job != null)
                    {
                        job.Check();
                        done += read;
                        job.Bytes(done, total, false);
                    }
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Downloads (or reuses a complete earlier download of) the file and checks it against the feed.</summary>
        static string Fetch(Download d, Mirror mirror, string dir, string name, Job job, string what)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path) && !string.IsNullOrEmpty(d.Sha256))
            {
                job.Step("Checking the earlier download of " + what);
                if (Sha256(path, job) == d.Sha256) return path;
                File.Delete(path);
            }
            job.Step("Downloading " + what);
            Download(d.UrlFor(mirror), path, d.Size, job, what);
            if (!string.IsNullOrEmpty(d.Sha256))
            {
                job.Step("Checking " + what);
                if (Sha256(path, job) != d.Sha256)
                {
                    File.Delete(path);
                    throw new Exception(what + " did not download correctly (checksum). Try again or pick the other download location.");
                }
            }
            return path;
        }

        // ------------------------------------------------------------------ zip

        /// <summary>Extracts a zip or (through 7-Zip) a RAR/7z into folder, see ExtractZip. Non-zip archives are unpacked
        /// into a staging folder next to the download first, then moved into place (same drive: no second copy).</summary>
        static HashSet<string> Extract(string archive, string folder, string marker, Func<string, bool> skip, Job job, long unpackedSize = 0)
        {
            if (Archive.IsZip(archive)) return ExtractZip(archive, folder, marker, skip, job);
            string staging = Path.Combine(Path.GetDirectoryName(archive), "unpacked");
            job.Status("Unpacking " + Path.GetFileName(archive));
            Archive.ExtractWith7Zip(archive, staging, unpackedSize, job);
            try
            {
                string found = Directory.EnumerateFiles(staging, marker, SearchOption.AllDirectories)
                    .OrderBy(p => p.Length).FirstOrDefault();
                if (found == null) throw new Exception("The download is not a valid package (no " + marker + " in it).");
                string root = Path.GetDirectoryName(found).TrimEnd('\\') + "\\";
                string target = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                job.Status("Moving files into place");
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
                {
                    job.Check();
                    string rel = file.Substring(root.Length);
                    if (skip != null && skip(rel)) continue;
                    string dest = Path.GetFullPath(Path.Combine(target, rel));
                    if (!dest.StartsWith(target, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe path in the package: " + rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (File.Exists(dest))
                    {
                        File.SetAttributes(dest, FileAttributes.Normal);
                        File.Delete(dest);
                    }
                    File.Move(file, dest);
                    written.Add(rel.Replace('\\', '/'));
                }
                return written;
            }
            finally
            {
                TryDeleteDir(staging);
            }
        }

        /// <summary>Extracts the zip into folder. The files' root is the folder holding the marker file (zips made with a
        /// top folder work too). skip(rel) leaves a file alone. Returns the extracted relative paths.</summary>
        static HashSet<string> ExtractZip(string zipPath, string folder, string marker, Func<string, bool> skip, Job job)
        {
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                string prefix = zip.Entries.Where(e => e.Name.Equals(marker, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.FullName.Substring(0, e.FullName.Length - e.Name.Length)).OrderBy(p => p.Length).FirstOrDefault();
                if (prefix == null) throw new Exception("The download is not a valid package (no " + marker + " in it).");
                var entries = zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
                long total = entries.Sum(e => e.Length), done = 0;
                var buffer = new byte[1 << 20];
                foreach (ZipArchiveEntry entry in entries)
                {
                    job.Check();
                    string rel = entry.FullName.Substring(prefix.Length).Replace('/', '\\');
                    if (skip != null && skip(rel)) { done += entry.Length; continue; }
                    string dest = Path.GetFullPath(Path.Combine(root, rel));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe path in the package: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                    job.Status("Installing " + rel);
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            done += read;
                            job.Bytes(done, total, false);
                        }
                    }
                    written.Add(rel.Replace('\\', '/'));
                }
            }
            return written;
        }

        // ------------------------------------------------------------------ checks

        public static string GameExePath(string folder)
        {
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, GameExe);
        }

        static bool RunningFrom(string processName, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            foreach (Process p in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (p.MainModule.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception)
                {
                    // another user's or an elevated process: cannot tell, assume not ours
                }
            }
            return false;
        }

        public static bool GameRunning(string folder) { return RunningFrom(Path.GetFileNameWithoutExtension(GameExe), folder); }

        public static bool ServerRunning(string folder) { return RunningFrom(Path.GetFileNameWithoutExtension(ServerExe), folder); }

        public static long FreeSpace(string folder)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(folder));
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        static void CheckSpace(string folder, long needed)
        {
            long free = FreeSpace(folder);
            if (free >= 0 && needed > 0 && free < needed)
                throw new Exception("Not enough disk space: " + Size(needed) + " needed, " + Size(free) + " free on " + Path.GetPathRoot(Path.GetFullPath(folder)) + ".");
        }

        static void CheckWritable(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".installer_write_test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new Exception("Cannot write to " + folder + " (" + ex.Message + "). Pick a folder you own, for example in your user folder or on another drive, not in Program Files.");
            }
        }

        // ------------------------------------------------------------------ game

        /// <summary>Fresh install or full reinstall: the newest full download, then any patches up to the latest.
        /// A reinstall over an existing game removes the files of the old version that the new one does not have; files
        /// the game creates (logs, replays, settings) and an existing client.config.json are kept.</summary>
        public static void InstallGame(Feed feed, Mirror mirror, string folder, Job job)
        {
            if (GameRunning(folder)) throw new Exception("The game is running. Close it first.");
            List<Patch> chain;
            Release release = feed.InstallBase(mirror, out chain);
            if (release == null) throw new Exception("The update site has no full game download" + (mirror != null ? " at " + mirror.Name : "") + ".");
            CheckWritable(folder);
            Manifest old = Manifest.Read(folder);
            long patchBytes = chain.Sum(p => p.Size);
            long already = old != null ? old.Sizes.Values.Sum() : 0;
            CheckSpace(folder, release.Full.Size + Math.Max(0, release.Full.UnpackedSize - already) + patchBytes * 2);

            string dir = Path.Combine(folder, DownloadDir);
            // zip or RAR: the content decides (Archive.IsZip), not the name
            string zip = Fetch(release.Full, mirror, dir, "game_" + release.Version + ".download", job, "the game (" + feed.NameOf(release.Version) + ")");

            job.Step("Installing the game");
            bool hadConfig = File.Exists(Path.Combine(folder, "client.config.json"));
            HashSet<string> written = Extract(zip, folder, "release_manifest.json",
                rel => Archive.IsPersonal(rel) || (hadConfig && rel.Equals("client.config.json", StringComparison.OrdinalIgnoreCase)),
                job, release.Full.UnpackedSize);
            if (old != null)
            {
                job.Step("Removing files of the old version");
                foreach (string rel in old.Files.Keys)
                {
                    if (written.Contains(rel)) continue;
                    string path = Path.Combine(folder, rel.Replace('/', '\\'));
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception ex) { job.Log("could not remove " + rel + ": " + ex.Message); }
                }
            }
            TryDelete(zip);
            if (chain.Count > 0) ApplyChain(feed, mirror, folder, chain, job);
            TryDeleteDir(dir);
            Archive.SetGameFeed(folder, Config.Feed, job);
        }

        /// <summary>Installed version -> latest through the feed's patches.</summary>
        public static void UpdateGame(Feed feed, Mirror mirror, string folder, Job job)
        {
            if (GameRunning(folder)) throw new Exception("The game is running. Close it first.");
            Manifest installed = Manifest.Read(folder);
            if (installed == null) throw new Exception("No installed game in " + folder + ". Use INSTALL.");
            if (installed.Version == feed.Latest)
            {
                Archive.SetGameFeed(folder, Config.Feed, job);
                job.Step("The game is up to date.");
                return;
            }
            List<Patch> chain = feed.Chain(installed.Version, feed.Latest);
            if (chain == null) throw new Exception("There is no update path from your version (" + feed.NameOf(installed.Version) + "). Use REINSTALL.");
            CheckWritable(folder);
            CheckSpace(folder, chain.Sum(p => p.Size) * 3);
            ApplyChain(feed, mirror, folder, chain, job);
            TryDeleteDir(Path.Combine(folder, DownloadDir));
            Archive.SetGameFeed(folder, Config.Feed, job);
        }

        static void ApplyChain(Feed feed, Mirror mirror, string folder, List<Patch> chain, Job job)
        {
            string dir = Path.Combine(folder, DownloadDir);
            for (int i = 0; i < chain.Count; i++)
            {
                Patch p = chain[i];
                string what = "update " + (i + 1) + " of " + chain.Count + " (" + feed.NameOf(p.From) + " to " + feed.NameOf(p.To) + ")";
                string zip = Fetch(p, mirror, dir, "patch_" + p.From + "_to_" + p.To + ".zip", job, what);
                job.Step("Unpacking " + what);
                string unpacked = Path.Combine(dir, "patch_" + p.From + "_to_" + p.To);
                TryDeleteDir(unpacked);
                Extract(zip, unpacked, "apply_patch.ps1", null, job);
                job.Step("Applying " + what);
                job.Cancellable = false; // the patch checks everything first, but must not be stopped while it replaces files
                try
                {
                    RunPatch(Path.Combine(unpacked, "apply_patch.ps1"), folder, job);
                }
                finally
                {
                    job.Cancellable = true;
                }
                Manifest now = Manifest.Read(folder);
                if (now == null || now.Version != p.To) throw new Exception("The game is not at version " + feed.NameOf(p.To) + " after " + what + ".");
                TryDeleteDir(unpacked);
                TryDelete(zip);
            }
        }

        static void RunPatch(string script, string folder, Job job)
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Game \"" + folder.TrimEnd('\\') + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            var errors = new StringBuilder();
            string last = "";
            using (Process process = Process.Start(psi))
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    last = e.Data.Trim();
                    job.Status(last);
                    job.Log("  " + last);
                };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit(); // flushes the async readers
                if (process.ExitCode != 0)
                {
                    string err = errors.ToString().Trim();
                    throw new Exception("The update could not be applied: " + (err.Length > 0 ? FirstLine(err) : last) + " The game folder was left as it was before this update.");
                }
            }
        }

        /// <summary>Checks every file of release_manifest.json. Returns the files that are missing or changed.</summary>
        public static List<string> VerifyGame(string folder, Job job)
        {
            Manifest m = Manifest.Read(folder);
            if (m == null) throw new Exception("No installed game in " + folder + ".");
            var bad = new List<string>();
            long total = m.Sizes.Values.Sum(), done = 0;
            job.Step("Checking the game files");
            foreach (var kv in m.Files)
            {
                job.Check();
                if (kv.Key.Equals(Archive.FeedFile, StringComparison.OrdinalIgnoreCase)) continue; // the installer adds the feed URL
                string path = Path.Combine(folder, kv.Key.Replace('/', '\\'));
                job.Status("Checking " + kv.Key);
                if (!File.Exists(path)) { bad.Add(kv.Key + " (missing)"); }
                else if (Sha256(path, null) != kv.Value) { bad.Add(kv.Key + " (changed)"); }
                long size;
                done += m.Sizes.TryGetValue(kv.Key, out size) ? size : 0;
                job.Bytes(done, total, false);
            }
            return bad;
        }

        // ------------------------------------------------------------------ server

        public static string ServerExePath(string folder)
        {
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, ServerExe);
        }

        /// <summary>The installed server's version (server_version.txt, written by this installer).</summary>
        public static string ServerVersion(string folder)
        {
            string path = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "server_version.txt");
            if (path == null || !File.Exists(path)) return File.Exists(ServerExePath(folder) ?? "") ? "unknown" : null;
            return File.ReadAllText(path).Trim();
        }

        /// <summary>The server has one download (a GitHub release, or one link), no download location.</summary>
        public static void InstallServer(Feed feed, string folder, string gameFolder, bool useGame, Job job)
        {
            ServerRelease server = feed.Server;
            if (server == null) throw new Exception("The update site offers no server download.");
            if (server.Error != null) throw new Exception("Server download not available: " + server.Error + ".");
            Mirror mirror = null;
            if (ServerRunning(folder)) throw new Exception("The server is running. Close its window first.");
            CheckWritable(folder);
            CheckSpace(folder, server.Size + server.UnpackedSize);
            string dir = Path.Combine(folder, DownloadDir);
            string zip = Fetch(server, mirror, dir, "server_" + server.Version + ".zip", job, "the server (" + (server.Name ?? server.Version) + ")");
            job.Step("Installing the server");
            Extract(zip, folder, ServerExe, rel => ServerKeep.Any(k => k.EndsWith("\\")
                ? rel.StartsWith(k, StringComparison.OrdinalIgnoreCase)
                : rel.Equals(k, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(folder, rel))), job);
            File.WriteAllText(Path.Combine(folder, "server_version.txt"), server.Version);
            TryDelete(zip);
            TryDeleteDir(dir);
            EnsureServerConfig(folder, useGame ? GameExePath(gameFolder) : null, job);
        }

        /// <summary>Creates Files/configs/server.json from server_base.json when missing (the server would) and points
        /// matchmaking.gameServerPath at the installed game, which runs the raids (-gameserver).</summary>
        public static void EnsureServerConfig(string folder, string gameExe, Job job)
        {
            string configs = Path.Combine(folder, "Files", "configs");
            string config = Path.Combine(configs, "server.json");
            string baseConfig = Path.Combine(configs, "server_base.json");
            if (!File.Exists(config) && File.Exists(baseConfig)) File.Copy(baseConfig, config);
            if (string.IsNullOrEmpty(gameExe) || !File.Exists(config)) return;
            if (!File.Exists(gameExe))
            {
                job.Log("game not installed: gameServerPath left as it is");
                return;
            }
            string text = File.ReadAllText(config);
            string value = "\"" + gameExe.Replace('\\', '/') + "\"";
            var key = new Regex("(\"gameServerPath\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"");
            if (key.IsMatch(text)) text = key.Replace(text, m => m.Groups[1].Value + value, 1);
            else
            {
                var section = new Regex("(\"matchmaking\"\\s*:\\s*\\{)");
                if (!section.IsMatch(text)) { job.Log("server.json has no matchmaking section: gameServerPath not set"); return; }
                text = section.Replace(text, m => m.Groups[1].Value + "\"gameServerPath\": " + value + ",", 1);
            }
            File.WriteAllText(config, text, new UTF8Encoding(false));
            job.Log("server.json: gameServerPath = " + gameExe);
        }

        /// <summary>Backend port and raid ports from server.json (else server_base.json).</summary>
        public static string ServerPorts(string folder)
        {
            try
            {
                string configs = Path.Combine(folder, "Files", "configs");
                Func<string, Dictionary<string, object>> read = name =>
                {
                    string p = Path.Combine(configs, name);
                    return File.Exists(p) ? new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(p)) as Dictionary<string, object> : null;
                };
                Dictionary<string, object> config = read("server.json"), defaults = read("server_base.json");
                if (config == null && defaults == null) return null;
                // a value missing from server.json is the server's default (server_base.json)
                Func<string, string, long> num = (section, key) =>
                {
                    long v = Feed.Num(section == null ? config : Feed.Obj(config, section), key);
                    return v != 0 ? v : Feed.Num(section == null ? defaults : Feed.Obj(defaults, section), key);
                };
                long port = num(null, "port");
                long start = num("matchmaking", "portStart"), end = num("matchmaking", "portEnd");
                string text = "Players connect to port " + (port > 0 ? port : 6969) + " (TCP)";
                if (start > 0) text += ", raids use " + start + (end > start ? "-" + end : "") + " (UDP)";
                return text + ".";
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void StartServer(string folder)
        {
            Process.Start(new ProcessStartInfo(ServerExePath(folder)) { WorkingDirectory = folder, UseShellExecute = true });
        }

        public static void StartGame(string folder)
        {
            Process.Start(new ProcessStartInfo(GameExePath(folder)) { WorkingDirectory = folder, UseShellExecute = true });
        }

        // ------------------------------------------------------------------ shortcuts

        public static void Shortcut(string lnk, string target, string workingDir, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type t = link.GetType();
                t.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { target });
                t.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { workingDir });
                t.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { description });
                t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        /// <summary>Desktop (optional) and Start menu shortcuts for the game; a copy of this installer in the game folder
        /// with its own Start menu entry, so players find it again for updates.</summary>
        public static void GameShortcuts(string folder, bool desktop, Job job)
        {
            try
            {
                string exe = GameExePath(folder);
                string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Escape from Tarkov Alpha");
                Directory.CreateDirectory(menu);
                Shortcut(Path.Combine(menu, "Escape from Tarkov Alpha.lnk"), exe, folder, "Escape from Tarkov Alpha");
                if (desktop)
                    Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Escape from Tarkov Alpha.lnk"), exe, folder, "Escape from Tarkov Alpha");
                string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string copy = Path.Combine(folder, "Installer.exe");
                if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, copy, true);
                Shortcut(Path.Combine(menu, "Escape from Tarkov Alpha Installer.lnk"), copy, folder, "Install, update or repair Escape from Tarkov Alpha, host a server");
            }
            catch (Exception ex)
            {
                job.Log("shortcuts: " + ex.Message);
            }
        }

        public static void ServerShortcut(string folder, Job job)
        {
            try
            {
                string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Escape from Tarkov Alpha");
                Directory.CreateDirectory(menu);
                Shortcut(Path.Combine(menu, "Escape from Tarkov Alpha Server.lnk"), ServerExePath(folder), folder, "Escape from Tarkov Alpha server");
            }
            catch (Exception ex)
            {
                job.Log("server shortcut: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ helpers

        static string FirstLine(string text)
        {
            int n = text.IndexOf('\n');
            return (n < 0 ? text : text.Substring(0, n)).Trim();
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception) { }
        }
    }
}
