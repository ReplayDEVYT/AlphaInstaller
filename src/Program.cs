// Escape from Tarkov Alpha installer: installs / updates / repairs the game from the update site (updates.json, the
// same feed as the in-game updater), installs and starts the server, shows the news (news.json).
//
//   Installer.exe                                   the window
//   Installer.exe --feed <url> --news <url>         other site (also installer.cfg next to the exe: feed=..., news=...)
//   Installer.exe --cli install|update|reinstall|verify|server --dir <folder> [--game <folder>] [--mirror ID] [--log <file>]
//   Installer.exe --screenshot <png> [--tab 0|1|2|3] [--dir <game folder>] [--server-dir <folder>] [--server-config]
//   Installer.exe --cli launcher-check | launcher-update      (self-update without the window, for tests)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EftInstaller
{
    static partial class Config
    {
        public static string Feed = BuiltFeed;
        public static string News = BuiltNews;
        /// <summary>The launcher's own version (version.txt at build time) and the repository its updates come from when
        /// the feed names none (a GitHub-style release API: Codeberg/Forgejo or GitHub).</summary>
        public const string Version = BuiltVersion;
        public const string LauncherRepo = BuiltLauncherRepo;
        public const string LauncherApi = BuiltLauncherApi;
        /// <summary>The site before the player's own choice (LAUNCHER tab): build, installer.cfg.</summary>
        public static string DefaultFeed = BuiltFeed, DefaultNews = BuiltNews;

        /// <summary>Built-in site, then installer.cfg (feed=, news=) next to the exe, then the site picked on the
        /// LAUNCHER tab (Settings.Feed/News). --feed/--news on the command line come after this.</summary>
        public static void Resolve()
        {
            try
            {
                string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer.cfg");
                if (File.Exists(cfg))
                    foreach (string line in File.ReadAllLines(cfg))
                    {
                        string l = line.Trim();
                        if (l.StartsWith("feed=", StringComparison.OrdinalIgnoreCase)) Feed = l.Substring(5).Trim();
                        if (l.StartsWith("news=", StringComparison.OrdinalIgnoreCase)) News = l.Substring(5).Trim();
                    }
            }
            catch (Exception) { }
            DefaultFeed = Feed;
            DefaultNews = News;
            Settings s = Settings.Load();
            if (!string.IsNullOrEmpty(s.Feed)) { Feed = s.Feed; News = s.News ?? ""; }
        }

        /// <summary>The news URL: set explicitly (build, installer.cfg, --news), else the feed's "news" field resolved
        /// against the feed URL, else news.json next to updates.json.</summary>
        public static string NewsFor(Feed feed)
        {
            if (!string.IsNullOrEmpty(News)) return News;
            if (string.IsNullOrEmpty(Feed)) return null;
            if (feed != null && !string.IsNullOrEmpty(feed.News))
            {
                try
                {
                    var resolved = new Uri(new Uri(Feed, UriKind.Absolute), feed.News);
                    return resolved.IsFile ? resolved.LocalPath : resolved.AbsoluteUri;
                }
                catch (Exception)
                {
                    return feed.News;
                }
            }
            {
                int slash = Feed.LastIndexOf('/');
                int q = Feed.IndexOf('?');
                if (slash > 0 && (q < 0 || slash < q)) return Feed.Substring(0, slash + 1) + "news.json";
                slash = Feed.LastIndexOf('\\');
                return slash > 0 ? Feed.Substring(0, slash + 1) + "news.json" : null;
            }
        }
    }

    /// <summary>news.json: {"news": [{"title", "date", "text", "url"}]} (newest first, as written by make_patch.py news).</summary>
    static class News
    {
        public static List<TkBlock> Load(string url)
        {
            string text;
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                using (var client = new System.Net.WebClient())
                {
                    client.Headers.Add("User-Agent", Ops.UserAgent);
                    client.Encoding = Encoding.UTF8;
                    text = client.DownloadString(url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks);
                }
            else text = File.ReadAllText(url);
            return Parse(text);
        }

        public static List<TkBlock> Parse(string text)
        {
            object root = new JavaScriptSerializer().DeserializeObject(text);
            var dict = root as Dictionary<string, object>;
            IEnumerable<Dictionary<string, object>> items = dict != null ? Feed.Objects(dict, "news")
                : ((root as object[]) ?? new object[0]).OfType<Dictionary<string, object>>();
            return items.Select(i => new TkBlock
            {
                Title = Feed.Str(i, "title"),
                Date = Feed.Str(i, "date"),
                Body = Feed.Str(i, "text"),
                Url = Feed.Str(i, "url"),
            }).ToList();
        }
    }

    /// <summary>Remembered choices (HKCU\Software\EFTAlphaReconstruction\Installer).</summary>
    sealed class Settings
    {
        const string Key = @"Software\EFTAlphaReconstruction\Installer";
        public string GameDir, ServerDir, Mirror;
        /// <summary>Update site picked on the LAUNCHER tab (empty = the launcher's default); News empty = from the feed.</summary>
        public string Feed, News;
        public bool Desktop = true, UseGame = true, GameFeed = true;
        public int Tab;
        public const int Tabs = 4;

        public static Settings Load()
        {
            string games = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
            var s = new Settings
            {
                GameDir = Path.Combine(games, "Escape from Tarkov Alpha"),
                ServerDir = Path.Combine(games, "Escape from Tarkov Alpha Server"),
            };
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k != null)
                    {
                        s.GameDir = (k.GetValue("GameDir") as string) ?? s.GameDir;
                        s.ServerDir = (k.GetValue("ServerDir") as string) ?? s.ServerDir;
                        s.Mirror = k.GetValue("Mirror") as string;
                        s.Desktop = (k.GetValue("Desktop") as string) != "0";
                        s.UseGame = (k.GetValue("UseGame") as string) != "0";
                        s.GameFeed = (k.GetValue("GameFeed") as string) != "0";
                        s.Feed = k.GetValue("Feed") as string;
                        s.News = k.GetValue("News") as string;
                        int tab;
                        if (int.TryParse(k.GetValue("Tab") as string, out tab) && tab >= 0 && tab < Tabs) s.Tab = tab;
                    }
                }
            }
            catch (Exception) { }
            // the copy of the installer inside a game folder manages that game
            string here = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (File.Exists(Path.Combine(here, Ops.GameExe))) s.GameDir = here;
            return s;
        }

        public void Save()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    k.SetValue("GameDir", GameDir ?? "");
                    k.SetValue("ServerDir", ServerDir ?? "");
                    k.SetValue("Mirror", Mirror ?? "");
                    k.SetValue("Desktop", Desktop ? "1" : "0");
                    k.SetValue("UseGame", UseGame ? "1" : "0");
                    k.SetValue("GameFeed", GameFeed ? "1" : "0");
                    k.SetValue("Feed", Feed ?? "");
                    k.SetValue("News", News ?? "");
                    k.SetValue("Tab", Tab.ToString());
                }
            }
            catch (Exception) { }
        }
    }

    static class Program
    {
        static readonly object logLock = new object();
        static string logPath = Path.Combine(Path.GetTempPath(), "EFTAlphaInstaller.log");
        static bool console;

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public static void Log(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            lock (logLock)
            {
                try { File.AppendAllText(logPath, line + Environment.NewLine); } catch (Exception) { }
                if (console) Console.WriteLine(line);
            }
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
        }

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
            // TLS 1.2 for every request (GitHub and Cloudflare refuse the .NET 4 defaults); set here, not only in Ops's
            // static constructor, which a request that only uses Ops's constants never runs
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
            Config.Resolve();
            if (Arg(args, "--feed") != null) Config.Feed = Arg(args, "--feed");
            if (Arg(args, "--news") != null) Config.News = Arg(args, "--news");
            if (Arg(args, "--log") != null) logPath = Arg(args, "--log");
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = Math.Max(1f, g.DpiX / 96f);
            Theme.LoadFonts();

            if (Arg(args, "--cli") != null) return Cli(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Log("launcher " + Config.Version + " started, feed " + Config.Feed + ", news " + Config.News + ", font " + Theme.FontName + ", scale " + Theme.Scale);

            if (Arg(args, "--screenshot") != null) return Screenshot(args);
            // a self-update started this copy: the previous exe (.old) can go now
            Launcher.CleanUp();
            using (var form = new MainForm())
            {
                string from = Arg(args, "--updated-from");
                form.Load += (s, e) =>
                {
                    form.LoadFeedAsync();
                    if (from != null) form.ShowUpdated(from);
                };
                Application.Run(form);
            }
            return 0;
        }

        static int Screenshot(string[] args)
        {
            // writes the window as it looks after the feed loaded (layout check without a display session)
            var settings = Settings.Load();
            if (Arg(args, "--dir") != null) settings.GameDir = Arg(args, "--dir");
            if (Arg(args, "--server-dir") != null) settings.ServerDir = Arg(args, "--server-dir");
            settings.Tab = int.Parse(Arg(args, "--tab") ?? "1");
            settings.Save();
            using (var form = new MainForm())
            {
                Feed f = null; string error = null; List<TkBlock> n = null; string nError = null;
                try { f = Feed.Load(Config.Feed); } catch (Exception ex) { error = ex.Message; }
                string newsUrl = Config.NewsFor(f);
                try { if (!string.IsNullOrEmpty(newsUrl)) n = News.Load(newsUrl); } catch (Exception ex) { nError = ex.Message; }
                // child controls only get handles (and draw) in a shown window: show it off-screen
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000);
                form.ShowInTaskbar = false;
                form.Show();
                form.FeedLoaded(f, error, n, nError, Launcher.Find(f));
                form.SelectTab(settings.Tab);
                if (Array.IndexOf(args, "--demo-progress") >= 0) form.DemoProgress();
                Application.DoEvents();
                if (Array.IndexOf(args, "--server-config") >= 0)
                {
                    using (var dialog = new ServerConfigForm(settings.ServerDir, settings.GameDir, settings.UseGame))
                    {
                        dialog.StartPosition = FormStartPosition.Manual;
                        dialog.Location = new Point(-20000, -20000);
                        dialog.Show();
                        // a few seconds for background lookups (the external IP) to come back
                        DateTime until = DateTime.Now.AddSeconds(6);
                        while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                        using (var b = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height))
                        {
                            dialog.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                            b.Save(Arg(args, "--screenshot"), ImageFormat.Png);
                        }
                    }
                    return 0;
                }
                using (Bitmap b = form.CaptureImage()) b.Save(Arg(args, "--screenshot"), ImageFormat.Png);
            }
            return 0;
        }

        static int Cli(string[] args)
        {
            console = AttachConsole(-1);
            string what = Arg(args, "--cli");
            string dir = Arg(args, "--dir");
            var job = new Job { Log = text => Log(text) };
            string lastStatus = null;
            var poll = new System.Threading.Timer(_ =>
            {
                string text, more; double fraction;
                job.Read(out text, out more, out fraction);
                if (text != lastStatus && text != null && !text.StartsWith("Installing ", StringComparison.Ordinal)) { lastStatus = text; Log("  " + text + (more.Length > 0 ? "  [" + more + "]" : "")); }
            }, null, 500, 500);
            try
            {
                if (what == "launcher-check" || what == "launcher-update")
                {
                    Feed lf = null;
                    try { lf = Feed.Load(Config.Feed); } catch (Exception ex) { Log("feed: " + ex.Message); }
                    LauncherRelease lr = Launcher.Find(lf);
                    Log("LAUNCHER: running " + Config.Version + ", latest " + (lr.Error ?? lr.Version + " from " + lr.Source + (Launcher.IsNewer(lr) ? " (newer)" : " (not newer)")));
                    if (what == "launcher-check" || !Launcher.IsNewer(lr)) return lr.Error == null ? 0 : 1;
                    Log("UPDATED: " + Launcher.Update(lr, job));
                    return 0;
                }
                if (what == "verify")
                {
                    List<string> bad = Ops.VerifyGame(dir, job);
                    Log("VERIFY: " + bad.Count + " bad" + (bad.Count > 0 ? ": " + string.Join(", ", bad.Take(20)) : ""));
                    return bad.Count == 0 ? 0 : 3;
                }
                Feed feed = Feed.Load(Config.Feed);
                Mirror mirror = feed.MirrorById(Arg(args, "--mirror"));
                Log("feed: latest " + feed.NameOf(feed.Latest) + ", mirror " + (mirror != null ? mirror.Id : "-"));
                switch (what)
                {
                    case "install":
                    case "reinstall":
                        Ops.InstallGame(feed, mirror, dir, job);
                        break;
                    case "update":
                        Ops.UpdateGame(feed, mirror, dir, job);
                        break;
                    case "server":
                        Ops.InstallServer(feed, dir, Arg(args, "--game"), Arg(args, "--game") != null, job);
                        break;
                    default:
                        Log("unknown --cli " + what);
                        return 2;
                }
                Manifest m = Manifest.Read(dir);
                Log("DONE" + (m != null ? ": version " + m.Version : ""));
                return 0;
            }
            catch (Exception ex)
            {
                Log("FAILED: " + ex.Message);
                return 1;
            }
            finally
            {
                poll.Dispose();
            }
        }
    }
}
