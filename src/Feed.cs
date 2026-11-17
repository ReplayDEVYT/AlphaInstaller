// The update feed (updates.json, written by Tools/patch/make_patch.py), read the same way the game's GameUpdater
// and update_game.ps1 read it: mirrors, releases (full downloads), patches (version to version), server package.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace EftInstaller
{
    sealed class Link
    {
        public string Mirror, Url;
    }

    sealed class Mirror
    {
        public string Id, Name, Base;
    }

    /// <summary>One download (a full game, a patch, the server): its own links per mirror, else mirror base + file.</summary>
    class Download
    {
        public string File, Sha256;
        public long Size, UnpackedSize;
        public List<Link> Links = new List<Link>();

        public string UrlFor(Mirror mirror)
        {
            if (mirror != null)
            {
                Link link = Links.FirstOrDefault(l => l.Mirror == mirror.Id && !string.IsNullOrEmpty(l.Url));
                if (link != null) return link.Url;
            }
            else
            {
                // no download location chosen (the server): any link
                Link any = Links.FirstOrDefault(l => !string.IsNullOrEmpty(l.Url));
                if (any != null) return any.Url;
            }
            if (string.IsNullOrEmpty(File)) return null;
            if (File.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || File.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return File;
            if (mirror == null || string.IsNullOrEmpty(mirror.Base)) return null;
            return mirror.Base.TrimEnd('/') + "/" + File.TrimStart('/');
        }
    }

    sealed class Release
    {
        public string Version, Name, Date, Notes;
        public Download Full; // null: no full download for this release
    }

    sealed class Patch : Download
    {
        public string From, To;
    }

    sealed class ServerRelease : Download
    {
        public string Version, Name, Date, Notes;
        // "github": "owner/repo": the latest GitHub release of that repository (resolved by GitHub.Resolve)
        public string GitHubRepo, GitHubApi = "https://api.github.com";
        public string Error; // why the server download is unavailable (GitHub not reachable, no zip in the release)
    }

    /// <summary>The server from a GitHub repository's latest release: tag = version, release text = notes, the first
    /// .zip asset (one with "server" in its name preferred) = the download, its "digest" (sha256:...) = checksum.</summary>
    static class GitHub
    {
        public static void Resolve(ServerRelease server)
        {
            string url = server.GitHubApi.TrimEnd('/') + "/repos/" + server.GitHubRepo + "/releases/latest";
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
                var assets = Feed.Objects(release, "assets").Where(a => (Feed.Str(a, "name") ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
                var asset = assets.FirstOrDefault(a => Feed.Str(a, "name").IndexOf("server", StringComparison.OrdinalIgnoreCase) >= 0) ?? assets.FirstOrDefault();
                server.Version = Feed.Str(release, "tag_name");
                server.Name = Feed.Str(release, "name");
                if (string.IsNullOrEmpty(server.Name)) server.Name = server.Version;
                string published = Feed.Str(release, "published_at");
                server.Date = published != null && published.Length >= 10 ? published.Substring(0, 10) : null;
                server.Notes = Feed.Str(release, "body");
                if (asset == null)
                {
                    server.Error = "the latest server release (" + Launcher.HostOf(server.GitHubApi) + "/" + server.GitHubRepo + ") has no .zip file";
                    return;
                }
                server.File = Feed.Str(asset, "browser_download_url");
                server.Size = Feed.Num(asset, "size");
                string digest = Feed.Str(asset, "digest");
                if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) server.Sha256 = digest.Substring(7).ToLowerInvariant();
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                server.Error = response != null && (int)response.StatusCode == 404
                    ? "no published server release (" + Launcher.HostOf(server.GitHubApi) + "/" + server.GitHubRepo + ")"
                    : "release host not reachable: " + ex.Message;
            }
            catch (Exception ex)
            {
                server.Error = "release host answer not readable: " + ex.Message;
            }
        }
    }

    sealed class Feed
    {
        public string Latest;
        public string News; // news.json, absolute or relative to updates.json ("../news/news.json")
        public List<Mirror> Mirrors = new List<Mirror>();
        public List<Release> Releases = new List<Release>();
        public List<Patch> Patches = new List<Patch>();
        public ServerRelease Server;
        public Dictionary<string, object> LauncherEntry; // "launcher": where launcher updates are published (Launcher.Find)

        public Release LatestRelease { get { return Release(Latest); } }

        public Release Release(string version)
        {
            return Releases.FirstOrDefault(r => r.Version == version);
        }

        /// <summary>The release's name ("0.1.4"), else the short version id.</summary>
        public string NameOf(string version)
        {
            if (string.IsNullOrEmpty(version)) return "-";
            Release r = Release(version);
            return (r != null && !string.IsNullOrEmpty(r.Name)) ? r.Name : version;
        }

        public Mirror MirrorById(string id)
        {
            return Mirrors.FirstOrDefault(m => m.Id == id) ?? Mirrors.FirstOrDefault();
        }

        /// <summary>Shortest patch chain from -> to (breadth first, like update_game.ps1); null when there is none.</summary>
        public List<Patch> Chain(string from, string to)
        {
            if (from == to) return new List<Patch>();
            var cameBy = new Dictionary<string, Patch>();
            var queue = new Queue<string>();
            queue.Enqueue(from);
            var seen = new HashSet<string> { from };
            while (queue.Count > 0 && !cameBy.ContainsKey(to))
            {
                string at = queue.Dequeue();
                foreach (Patch p in Patches)
                {
                    if (p.From == at && seen.Add(p.To))
                    {
                        cameBy[p.To] = p;
                        queue.Enqueue(p.To);
                    }
                }
            }
            if (!cameBy.ContainsKey(to)) return null;
            var chain = new List<Patch>();
            for (string at = to; at != from; at = cameBy[at].From) chain.Insert(0, cameBy[at]);
            return chain;
        }

        /// <summary>The release to download whole for a fresh install: the latest when it has a full download on the
        /// mirror, else the newest one that has one and a patch chain to the latest.</summary>
        public Release InstallBase(Mirror mirror, out List<Patch> chain)
        {
            chain = null;
            for (int i = Releases.Count - 1; i >= 0; i--)
            {
                Release r = Releases[i];
                if (r.Full == null || r.Full.UrlFor(mirror) == null) continue;
                List<Patch> c = Chain(r.Version, Latest);
                if (c == null) continue;
                chain = c;
                return r;
            }
            return null;
        }

        /// <summary>Releases newer than the given one, newest first (their notes go into the notes box).</summary>
        public List<Release> NewerThan(string version)
        {
            int index = Releases.FindIndex(r => r.Version == version);
            var list = Releases.Skip(index + 1).ToList();
            list.Reverse();
            return list;
        }

        // ------------------------------------------------------------------ parsing

        public static Feed Parse(string json)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("updates.json is not a JSON object");
            var feed = new Feed { Latest = Str(root, "latest"), News = Str(root, "news") };
            foreach (var m in Objects(root, "mirrors"))
                feed.Mirrors.Add(new Mirror { Id = Str(m, "id"), Name = Str(m, "name") ?? Str(m, "id"), Base = Str(m, "base") });
            foreach (var r in Objects(root, "releases"))
            {
                var release = new Release { Version = Str(r, "version"), Name = Str(r, "name"), Date = Str(r, "date"), Notes = Str(r, "notes") };
                var full = new Download { File = Str(r, "full"), Sha256 = Str(r, "fullSha256"), Size = Num(r, "fullSize"), UnpackedSize = Num(r, "fullUnpackedSize") };
                full.Links = Links(r, "fullLinks");
                if (!string.IsNullOrEmpty(full.File) || full.Links.Count > 0) release.Full = full;
                feed.Releases.Add(release);
            }
            foreach (var p in Objects(root, "patches"))
                feed.Patches.Add(new Patch { From = Str(p, "from"), To = Str(p, "to"), File = Str(p, "file"), Sha256 = Str(p, "sha256"), Size = Num(p, "size"), Links = Links(p, "links") });
            var s = Obj(root, "server");
            if (s != null)
                feed.Server = new ServerRelease
                {
                    Version = Str(s, "version"), Name = Str(s, "name"), Date = Str(s, "date"), Notes = Str(s, "notes"),
                    File = Str(s, "url") ?? Str(s, "file"), Sha256 = Str(s, "sha256"), Size = Num(s, "size"), UnpackedSize = Num(s, "unpackedSize"),
                    Links = Links(s, "links"), GitHubRepo = Str(s, "github"), GitHubApi = Str(s, "githubApi") ?? "https://api.github.com",
                };
            feed.LauncherEntry = Obj(root, "launcher");
            return feed;
        }

        public static Feed Load(string url)
        {
            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", Ops.UserAgent);
                client.Encoding = Encoding.UTF8;
                // no stale copy from a cache between us and the site
                string sep = url.Contains("?") ? "&" : "?";
                string text = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? client.DownloadString(url + sep + "t=" + DateTime.UtcNow.Ticks)
                    : System.IO.File.ReadAllText(url);
                Feed feed = Parse(text);
                if (feed.Server != null && !string.IsNullOrEmpty(feed.Server.GitHubRepo)) GitHub.Resolve(feed.Server);
                return feed;
            }
        }

        static List<Link> Links(Dictionary<string, object> o, string key)
        {
            return Objects(o, key).Select(l => new Link { Mirror = Str(l, "mirror"), Url = Str(l, "url") }).ToList();
        }

        internal static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
        {
            object v;
            return (o != null && o.TryGetValue(key, out v)) ? v as Dictionary<string, object> : null;
        }

        internal static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || !(v is IEnumerable) || v is string) yield break;
            foreach (object item in (IEnumerable)v)
            {
                var d = item as Dictionary<string, object>;
                if (d != null) yield return d;
            }
        }

        internal static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return (o != null && o.TryGetValue(key, out v) && v != null) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        internal static long Num(Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>A game folder's release_manifest.json (version + every file's SHA-256).</summary>
    sealed class Manifest
    {
        public string Version;
        public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> Sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static Manifest Read(string folder)
        {
            string path = Path.Combine(folder ?? "", "release_manifest.json");
            if (string.IsNullOrEmpty(folder) || !System.IO.File.Exists(path)) return null;
            try
            {
                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(System.IO.File.ReadAllText(path)) as Dictionary<string, object>;
                var m = new Manifest { Version = Feed.Str(root, "version") };
                var files = Feed.Obj(root, "files");
                if (files != null)
                    foreach (var kv in files)
                    {
                        var info = kv.Value as Dictionary<string, object>;
                        m.Files[kv.Key] = Feed.Str(info, "sha256");
                        m.Sizes[kv.Key] = Feed.Num(info, "size");
                    }
                return m;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
