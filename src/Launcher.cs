// The launcher's own updates: where the newest launcher is published (the feed's "launcher" entry, else the latest
// release of the repository (Codeberg or GitHub release API) the launcher was built for) and replacing the running exe with it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace EftInstaller
{
    /// <summary>The newest launcher: version, notes and its download (the .exe itself).</summary>
    sealed class LauncherRelease : Download
    {
        public string Version, Name, Date, Notes, Source;
        public string Error; // why no launcher release could be read
    }

    static class Launcher
    {
        /// <summary>The feed's "launcher" entry ({"github": "owner/repo"} or {"version", "url", "sha256", "size", ...})
        /// wins; without one the launcher asks GitHub for the latest release of the repository it was built for.</summary>
        public static LauncherRelease Find(Feed feed)
        {
            Dictionary<string, object> entry = feed != null ? feed.LauncherEntry : null;
            string repo = Config.LauncherRepo;
            string api = Config.LauncherApi;
            if (entry != null)
            {
                string github = Feed.Str(entry, "github");
                if (!string.IsNullOrEmpty(github)) { repo = github; api = Feed.Str(entry, "githubApi") ?? api; }
                else
                {
                    var direct = new LauncherRelease
                    {
                        Version = Feed.Str(entry, "version"), Name = Feed.Str(entry, "name"), Date = Feed.Str(entry, "date"),
                        Notes = Feed.Str(entry, "notes"), Sha256 = Feed.Str(entry, "sha256"), Size = Feed.Num(entry, "size"),
                        File = Resolve(Feed.Str(entry, "url") ?? Feed.Str(entry, "file")), Source = "update site",
                    };
                    if (string.IsNullOrEmpty(direct.Version) || string.IsNullOrEmpty(direct.File))
                        direct.Error = "the update site's launcher entry has no version or download";
                    return direct;
                }
            }
            if (string.IsNullOrEmpty(repo)) return new LauncherRelease { Error = "no place to look for launcher updates" };
            return FromGitHub(repo, api);
        }

        /// <summary>A relative launcher URL ("../download/x.exe") is relative to updates.json, like "news".</summary>
        static string Resolve(string url)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(Config.Feed)) return url;
            try
            {
                var resolved = new Uri(new Uri(Config.Feed, UriKind.Absolute), url);
                return resolved.IsFile ? resolved.LocalPath : resolved.AbsoluteUri;
            }
            catch (Exception)
            {
                return url;
            }
        }

        /// <summary>Latest GitHub release: tag = version, release text = notes, the .exe asset (one named like the
        /// installer/launcher first) = download, its digest = checksum.</summary>
        static LauncherRelease FromGitHub(string repo, string api)
        {
            var r = new LauncherRelease { Source = HostOf(api) + "/" + repo };
            string url = api.TrimEnd('/') + "/repos/" + repo + "/releases/latest";
            try
            {
                string text;
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", Ops.UserAgent);
                    client.Headers.Add("Accept", "application/vnd.github+json");
                    client.Encoding = Encoding.UTF8;
                    text = client.DownloadString(url);
                }
                var release = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
                var exes = Feed.Objects(release, "assets").Where(a => (Feed.Str(a, "name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
                var asset = exes.FirstOrDefault(a => Feed.Str(a, "name").IndexOf("installer", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || Feed.Str(a, "name").IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0) ?? exes.FirstOrDefault();
                r.Version = Feed.Str(release, "tag_name");
                r.Name = Feed.Str(release, "name");
                if (string.IsNullOrEmpty(r.Name)) r.Name = r.Version;
                string published = Feed.Str(release, "published_at");
                r.Date = published != null && published.Length >= 10 ? published.Substring(0, 10) : null;
                r.Notes = Feed.Str(release, "body");
                if (asset == null)
                {
                    r.Error = "the latest launcher release has no .exe";
                    return r;
                }
                r.File = Feed.Str(asset, "browser_download_url");
                r.Size = Feed.Num(asset, "size");
                string digest = Feed.Str(asset, "digest");
                if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) r.Sha256 = digest.Substring(7).ToLowerInvariant();
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                r.Error = response != null && (int)response.StatusCode == 404 ? "no published launcher release (" + r.Source + ")" : "release host not reachable (" + r.Source + "): " + ex.Message;
            }
            catch (Exception ex)
            {
                r.Error = "release host answer not readable: " + ex.Message;
            }
            return r;
        }

        /// <summary>"https://codeberg.org/api/v1" -> "codeberg.org", "https://api.github.com" -> "github.com".</summary>
        internal static string HostOf(string api)
        {
            try { string host = new Uri(api).Host; return host.StartsWith("api.") ? host.Substring(4) : host; }
            catch (Exception) { return api; }
        }

        /// <summary>Version order: numbers part by part ("1.10" after "1.9"), a leading "v" ignored.</summary>
        public static int Compare(string a, string b)
        {
            string[] x = (a ?? "").Trim().TrimStart('v', 'V').Split('.', '-', '+');
            string[] y = (b ?? "").Trim().TrimStart('v', 'V').Split('.', '-', '+');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                string p = i < x.Length ? x[i] : "0", q = i < y.Length ? y[i] : "0";
                int m, n;
                int c = int.TryParse(p, out m) && int.TryParse(q, out n) ? m.CompareTo(n) : string.Compare(p, q, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return 0;
        }

        public static bool IsNewer(LauncherRelease r)
        {
            return r != null && r.Error == null && !string.IsNullOrEmpty(r.Version) && Compare(r.Version, Config.Version) > 0;
        }

        public static string ExePath { get { return Process.GetCurrentProcess().MainModule.FileName; } }

        /// <summary>Downloads the new launcher next to the running one, checks it, and swaps the files (a running exe can
        /// be renamed, not overwritten: the old one becomes .old and is deleted on the next start). Returns the exe to start.</summary>
        public static string Update(LauncherRelease r, Job job)
        {
            string exe = ExePath;
            string dir = Path.GetDirectoryName(exe);
            string fresh = exe + ".new";
            try
            {
                File.WriteAllText(Path.Combine(dir, ".write_test"), "");
                File.Delete(Path.Combine(dir, ".write_test"));
            }
            catch (Exception)
            {
                throw new Exception("The launcher cannot replace itself in " + dir + " (no write access). Download the new version from " + (r.Source ?? "its release page") + ".");
            }
            job.Step("Downloading launcher " + (r.Name ?? r.Version));
            Ops.Download(r.File, fresh, r.Size, job, "the launcher");
            job.Cancellable = false;
            if (!string.IsNullOrEmpty(r.Sha256))
            {
                job.Step("Checking the new launcher");
                if (Ops.Sha256(fresh, job) != r.Sha256)
                {
                    File.Delete(fresh);
                    throw new Exception("The new launcher did not download correctly (checksum). Try again.");
                }
            }
            using (var f = File.OpenRead(fresh))
                if (f.Length < 64 * 1024 || f.ReadByte() != 'M' || f.ReadByte() != 'Z')
                {
                    f.Close();
                    File.Delete(fresh);
                    throw new Exception("The launcher download is not a program (" + r.File + ").");
                }
            job.Step("Installing the new launcher");
            string old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); }
            catch (Exception) { old = exe + "." + DateTime.Now.Ticks + ".old"; }
            File.Move(exe, old);
            try
            {
                File.Move(fresh, exe);
            }
            catch (Exception)
            {
                File.Move(old, exe);
                throw;
            }
            job.Log("launcher " + Config.Version + " -> " + r.Version);
            return exe;
        }

        /// <summary>Removes the exe left behind by an update (exe.old, exe.<ticks>.old) and an unfinished download.</summary>
        public static void CleanUp()
        {
            try
            {
                string exe = ExePath;
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(exe), Path.GetFileName(exe) + "*.old"))
                    try { File.Delete(f); } catch (Exception) { }
            }
            catch (Exception) { }
        }
    }
}
