// The installer window: NEWS / GAME / SERVER tabs, status line and progress bar at the bottom.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace EftInstaller
{
    sealed class MainForm : Form
    {
        const int W = 1000, H = 640;

        Feed feed;
        string feedError;
        List<TkBlock> news;
        string newsError;
        Settings settings = Settings.Load();

        // running operation
        Job job;
        Thread worker;
        string resultText;
        Color resultColor = Theme.Text;
        bool closeWhenDone;

        readonly TkTab tabNews = new TkTab("News"), tabGame = new TkTab("Game"), tabServer = new TkTab("Server");
        readonly Panel pageNews = Page(), pageGame = Page(), pageServer = Page();
        readonly TkLabel status = new TkLabel("", 15), detail = new TkLabel("", 14) { Align = StringAlignment.Far, Color = Theme.TextDim };
        readonly TkProgress bar = new TkProgress();
        readonly TkButton cancel = new TkButton("Cancel");
        readonly TkControl minimize = new TitleButton(false), close = new TitleButton(true);

        readonly TkFeed newsFeed = new TkFeed();

        readonly TkInput gameDir = new TkInput();
        readonly TkLabel gInstalled = Value(), gLatest = Value(), gDownload = Value(), gSpace = Value();
        readonly List<TkTab> gMirrors = new List<TkTab>();
        readonly Panel gMirrorRow = new Panel { BackColor = Color.Transparent };
        readonly TkCheck desktop = new TkCheck("Create a desktop shortcut");
        readonly TkFeed gameNotes = new TkFeed();
        readonly TkButton gOpen = new TkButton("Open folder"), gVerify = new TkButton("Verify files"),
            gReinstall = new TkButton("Reinstall"), gMain = new TkButton("Install", true), gBrowse = new TkButton("Browse");

        readonly TkInput serverDir = new TkInput();
        readonly TkLabel sInstalled = Value(), sLatest = Value(), sDownload = Value(), sState = Value();
        readonly TkLabel sPorts = new TkLabel("", 14) { Color = Theme.TextDim };
        readonly TkCheck useGame = new TkCheck("Run raids with the installed game (sets gameServerPath)");
        readonly TkFeed serverNotes = new TkFeed();
        readonly TkButton sOpen = new TkButton("Open folder"), sConfig = new TkButton("Edit config"),
            sInstall = new TkButton("Install server"), sStart = new TkButton("Start server", true), sBrowse = new TkButton("Browse");

        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 50 };
        int refreshTick;

        static Panel Page()
        {
            return new DoubleBufferedPanel { BackColor = Color.Transparent };
        }

        static TkLabel Value()
        {
            return new TkLabel("", 15);
        }

        public MainForm()
        {
            Text = "Escape from Tarkov Alpha - Installer";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(W), Theme.S(H));
            BackColor = Theme.Window;
            DoubleBuffered = true;
            Icon = Theme.AppIcon();
            KeyPreview = true;

            foreach (TkTab t in new[] { tabNews, tabGame, tabServer }) Controls.Add(t);
            Place(tabNews, 24, 158, 150, 36);
            Place(tabGame, 182, 158, 150, 36);
            Place(tabServer, 340, 158, 150, 36);
            tabNews.Click += (s, e) => ShowTab(0);
            tabGame.Click += (s, e) => ShowTab(1);
            tabServer.Click += (s, e) => ShowTab(2);
            foreach (Panel p in new[] { pageNews, pageGame, pageServer })
            {
                Controls.Add(p);
                Place(p, 0, 206, W, 352);
            }
            Controls.Add(minimize);
            Controls.Add(close);
            Place(minimize, W - 76, 0, 38, 32);
            Place(close, W - 38, 0, 38, 32);
            minimize.Click += (s, e) => WindowState = FormWindowState.Minimized;
            close.Click += (s, e) => Close();

            Controls.Add(status);
            Controls.Add(detail);
            Controls.Add(bar);
            Controls.Add(cancel);
            Place(status, 24, 566, 640, 22);
            Place(detail, 560, 566, 416, 22);
            Place(bar, 24, 594, 952, 26);
            Place(cancel, 866, 592, 110, 30);
            cancel.Visible = false;
            cancel.Click += (s, e) => { if (job != null && job.Cancellable) { job.Cancel = true; job.Status("Cancelling..."); } };

            BuildNews();
            BuildGame();
            BuildServer();
            ShowTab(settings.Tab);

            timer.Tick += (s, e) => Tick();
            timer.Start();
            MouseDown += DragWindow;
        }

        // ------------------------------------------------------------------ layout helpers

        static void Place(Control c, int x, int y, int w, int h)
        {
            c.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
        }

        static TkLabel Caption(Control parent, string text, int x, int y, int w)
        {
            var l = new TkLabel(text.ToUpperInvariant(), 13, true) { Color = Theme.TextDim };
            parent.Controls.Add(l);
            Place(l, x, y, w, 18);
            return l;
        }

        static void InfoLine(Control parent, string key, TkLabel value, int x, int y)
        {
            var k = new TkLabel(key, 15) { Color = Theme.TextDim };
            parent.Controls.Add(k);
            parent.Controls.Add(value);
            Place(k, x, y, 110, 24);
            Place(value, x + 110, y, 386, 24);
        }

        void ShowTab(int index)
        {
            settings.Tab = index;
            tabNews.Selected = index == 0;
            tabGame.Selected = index == 1;
            tabServer.Selected = index == 2;
            pageNews.Visible = index == 0;
            pageGame.Visible = index == 1;
            pageServer.Visible = index == 2;
            foreach (TkTab t in new[] { tabNews, tabGame, tabServer }) t.Invalidate();
        }

        void BuildNews()
        {
            pageNews.Controls.Add(newsFeed);
            Place(newsFeed, 24, 8, 952, 338);
            newsFeed.SetBlocks(null, "Loading news...");
        }

        void BuildGame()
        {
            Panel p = pageGame;
            Caption(p, "Install folder", 24, 8, 300);
            p.Controls.Add(gameDir);
            Place(gameDir, 24, 30, 380, 38);
            gameDir.Text = settings.GameDir;
            gameDir.Box.TextChanged += (s, e) => { settings.GameDir = gameDir.Text.Trim(); RefreshState(); };
            p.Controls.Add(gBrowse);
            Place(gBrowse, 412, 30, 108, 38);
            gBrowse.Click += (s, e) => Browse(gameDir, "Folder to install Escape from Tarkov Alpha in");

            InfoLine(p, "Installed", gInstalled, 24, 82);
            InfoLine(p, "Latest", gLatest, 24, 106);
            InfoLine(p, "Download", gDownload, 24, 130);
            InfoLine(p, "Disk space", gSpace, 24, 154);

            Caption(p, "Download from", 24, 188, 300);
            p.Controls.Add(gMirrorRow);
            Place(gMirrorRow, 24, 210, 496, 32);
            p.Controls.Add(desktop);
            Place(desktop, 24, 254, 400, 26);
            desktop.Checked = settings.Desktop;
            desktop.CheckedChanged += (s, e) => settings.Desktop = desktop.Checked;

            Caption(p, "What's new", 544, 8, 300);
            p.Controls.Add(gameNotes);
            Place(gameNotes, 544, 30, 432, 254);

            foreach (TkButton b in new[] { gOpen, gVerify, gReinstall, gMain }) p.Controls.Add(b);
            Place(gOpen, 24, 298, 150, 44);
            Place(gVerify, 184, 298, 150, 44);
            Place(gReinstall, 544, 298, 140, 44);
            Place(gMain, 696, 298, 280, 44);
            gOpen.Click += (s, e) => OpenFolder(settings.GameDir);
            gVerify.Click += (s, e) => Run("Verify", j => VerifyResult(Ops.VerifyGame(settings.GameDir, j)));
            gReinstall.Click += (s, e) => Run("Reinstall", j => { Ops.InstallGame(feed, CurrentMirror, settings.GameDir, j); Ops.GameShortcuts(settings.GameDir, settings.Desktop, j); }, "The game was reinstalled.");
            gMain.Click += (s, e) => GameMain();
        }

        void BuildServer()
        {
            Panel p = pageServer;
            Caption(p, "Server folder", 24, 8, 300);
            p.Controls.Add(serverDir);
            Place(serverDir, 24, 30, 380, 38);
            serverDir.Text = settings.ServerDir;
            serverDir.Box.TextChanged += (s, e) => { settings.ServerDir = serverDir.Text.Trim(); RefreshState(); };
            p.Controls.Add(sBrowse);
            Place(sBrowse, 412, 30, 108, 38);
            sBrowse.Click += (s, e) => Browse(serverDir, "Folder to install the server in");

            InfoLine(p, "Installed", sInstalled, 24, 82);
            InfoLine(p, "Latest", sLatest, 24, 106);
            InfoLine(p, "Download", sDownload, 24, 130);
            InfoLine(p, "Status", sState, 24, 154);
            p.Controls.Add(sPorts);
            Place(sPorts, 24, 178, 496, 22);

            p.Controls.Add(useGame);
            Place(useGame, 24, 214, 496, 26);
            useGame.Checked = settings.UseGame;
            useGame.CheckedChanged += (s, e) => settings.UseGame = useGame.Checked;

            Caption(p, "Hosting", 544, 8, 300);
            p.Controls.Add(serverNotes);
            Place(serverNotes, 544, 30, 432, 254);

            foreach (TkButton b in new[] { sOpen, sConfig, sInstall, sStart }) p.Controls.Add(b);
            Place(sOpen, 24, 298, 150, 44);
            Place(sConfig, 184, 298, 150, 44);
            Place(sInstall, 544, 298, 190, 44);
            Place(sStart, 746, 298, 230, 44);
            sOpen.Click += (s, e) => OpenFolder(settings.ServerDir);
            sConfig.Click += (s, e) => EditConfig();
            sInstall.Click += (s, e) => Run("Server", j =>
            {
                Ops.InstallServer(feed, settings.ServerDir, settings.GameDir, settings.UseGame, j);
                Ops.ServerShortcut(settings.ServerDir, j);
            }, "The server is installed. Press START SERVER to run it.");
            sStart.Click += (s, e) =>
            {
                try
                {
                    Ops.EnsureServerConfig(settings.ServerDir, settings.UseGame ? Ops.GameExePath(settings.GameDir) : null, new Job());
                    Ops.StartServer(settings.ServerDir);
                    Result("Server started in its own window. Type exit there to stop it.", Theme.Good);
                }
                catch (Exception ex)
                {
                    Result(ex.Message, Theme.Bad);
                }
            };
        }

        void Browse(TkInput input, string description)
        {
            using (var dialog = new FolderBrowserDialog { Description = description, ShowNewFolderButton = true })
            {
                string current = input.Text.Trim();
                while (!string.IsNullOrEmpty(current) && !Directory.Exists(current)) current = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string chosen = dialog.SelectedPath;
                // an empty or foreign folder gets its own subfolder, so the game never spreads over e.g. D:\
                bool isGame = input == gameDir;
                string marker = isGame ? Ops.GameExe : Ops.ServerExe;
                if (!File.Exists(Path.Combine(chosen, marker)) && Directory.EnumerateFileSystemEntries(chosen).Any())
                    chosen = Path.Combine(chosen, isGame ? "Escape from Tarkov Alpha" : "Escape from Tarkov Alpha Server");
                input.Text = chosen;
            }
        }

        static void OpenFolder(string folder)
        {
            if (Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        void EditConfig()
        {
            string configs = Path.Combine(settings.ServerDir, "Files", "configs");
            string config = Path.Combine(configs, "server.json");
            try
            {
                Ops.EnsureServerConfig(settings.ServerDir, settings.UseGame ? Ops.GameExePath(settings.GameDir) : null, new Job());
                if (File.Exists(config)) Process.Start("notepad.exe", "\"" + config + "\"");
                else Result("Install the server first.", Theme.Bad);
            }
            catch (Exception ex)
            {
                Result(ex.Message, Theme.Bad);
            }
        }

        Mirror CurrentMirror { get { return feed != null ? feed.MirrorById(settings.Mirror) : null; } }

        void BuildMirrors(Panel row, List<TkTab> list)
        {
            foreach (TkTab t in list) { row.Controls.Remove(t); t.Dispose(); }
            list.Clear();
            if (feed == null) return;
            int x = 0;
            foreach (Mirror m in feed.Mirrors)
            {
                Mirror mirror = m;
                var t = new TkTab(m.Name ?? m.Id);
                row.Controls.Add(t);
                t.SetBounds(Theme.S(x), 0, Theme.S(160), row.Height);
                t.Click += (s, e) => { settings.Mirror = mirror.Id; RefreshState(); };
                list.Add(t);
                x += 168;
            }
        }

        // ------------------------------------------------------------------ feed and state

        public void LoadFeedAsync()
        {
            var thread = new Thread(() =>
            {
                Feed f = null;
                string error = null;
                try
                {
                    if (string.IsNullOrEmpty(Config.Feed)) error = "No update site is set for this installer (installer.cfg: feed=<URL of updates.json>).";
                    else f = Feed.Load(Config.Feed);
                }
                catch (Exception ex)
                {
                    error = "Could not read the update site: " + ex.Message;
                }
                List<TkBlock> n = null;
                string nError = null;
                try
                {
                    string newsUrl = Config.NewsFor(f);
                    if (!string.IsNullOrEmpty(newsUrl)) n = News.Load(newsUrl);
                }
                catch (Exception ex)
                {
                    nError = "Could not load the news: " + ex.Message;
                }
                try { BeginInvoke((Action)(() => FeedLoaded(f, error, n, nError))); }
                catch (InvalidOperationException) { }
            }) { IsBackground = true };
            thread.Start();
        }

        public void FeedLoaded(Feed f, string error, List<TkBlock> n, string nError)
        {
            feed = f;
            feedError = error;
            news = n;
            newsError = nError;
            if (feed != null && feed.MirrorById(settings.Mirror) != null) settings.Mirror = feed.MirrorById(settings.Mirror).Id;
            BuildMirrors(gMirrorRow, gMirrors);
            newsFeed.SetBlocks(news, newsError ?? (string.IsNullOrEmpty(Config.NewsFor(feed)) ? "No news." : "No news yet."));
            if (feedError != null) Result(feedError, Theme.Bad);
            RefreshState();
        }

        void RefreshState()
        {
            bool busy = job != null;
            Mirror mirror = CurrentMirror;
            foreach (TkTab t in gMirrors) { t.Selected = mirror != null && t.Text == (mirror.Name ?? mirror.Id); t.Invalidate(); }

            // game
            string dir = settings.GameDir;
            Manifest installed = Manifest.Read(dir);
            bool hasExe = !string.IsNullOrEmpty(dir) && File.Exists(Ops.GameExePath(dir));
            List<Patch> chain = null;
            Release installBase = null;
            List<Patch> baseChain = null;
            if (feed != null)
            {
                installBase = feed.InstallBase(mirror, out baseChain);
                if (installed != null) chain = feed.Chain(installed.Version, feed.Latest);
            }
            bool upToDate = feed != null && installed != null && installed.Version == feed.Latest;
            gInstalled.Set(installed != null ? (feed != null ? feed.NameOf(installed.Version) : installed.Version) + (upToDate ? "  (up to date)" : "")
                : hasExe ? "unknown version (no release_manifest.json)" : "not installed", installed != null ? (upToDate ? Theme.Good : Theme.Text) : Theme.TextDim);
            Release latest = feed != null ? feed.LatestRelease : null;
            gLatest.Set(feed == null ? (feedError != null ? "update site not reachable" : "checking...") :
                feed.NameOf(feed.Latest) + (latest != null && !string.IsNullOrEmpty(latest.Date) ? "   " + latest.Date : ""), feed == null ? Theme.TextDim : Theme.Text);
            long need = 0;
            if (feed != null && installed != null && !upToDate && chain != null)
            {
                long bytes = chain.Sum(p => p.Size);
                gDownload.Set(chain.Count + " update" + (chain.Count == 1 ? "" : "s") + ", " + Ops.Size(bytes), Theme.Text);
                need = bytes * 3;
            }
            else if (feed != null && installed != null && !upToDate && chain == null)
                gDownload.Set("no update path from this version: use REINSTALL", Theme.Bad);
            else if (feed != null && installBase != null && installed == null)
            {
                long bytes = installBase.Full.Size + baseChain.Sum(p => p.Size);
                gDownload.Set((bytes > 0 ? Ops.Size(bytes) : "size unknown") + (installBase.Full.UnpackedSize > 0 ? ",  " + Ops.Size(installBase.Full.UnpackedSize) + " installed" : ""), Theme.Text);
                need = installBase.Full.Size + installBase.Full.UnpackedSize;
            }
            else if (feed != null && installBase == null && installed == null)
                gDownload.Set("no full download" + (mirror != null ? " at " + mirror.Name : ""), Theme.Bad);
            else gDownload.Set(upToDate ? "nothing to download" : "-", Theme.TextDim);
            long free = string.IsNullOrEmpty(dir) ? -1 : Ops.FreeSpace(dir);
            gSpace.Set(free < 0 ? "-" : Ops.Size(free) + " free" + (need > 0 ? ",  " + Ops.Size(need) + " needed" : ""), free >= 0 && need > free ? Theme.Bad : Theme.Text);

            if (installed == null)
            {
                gMain.Text = "Install";
                gMain.Enabled = !busy && feed != null && installBase != null && !string.IsNullOrEmpty(dir);
            }
            else if (!upToDate && chain != null)
            {
                gMain.Text = "Update";
                gMain.Enabled = !busy;
            }
            else
            {
                gMain.Text = "Play";
                gMain.Enabled = !busy && hasExe;
            }
            gReinstall.Enabled = !busy && installed != null && installBase != null;
            gVerify.Enabled = !busy && installed != null;
            gOpen.Enabled = Directory.Exists(dir);
            gBrowse.Enabled = !busy;
            gameDir.Enabled = !busy;

            var notes = new List<TkBlock>();
            if (feed != null)
                foreach (Release r in feed.NewerThan(installed != null ? installed.Version : null).Take(installed != null ? 50 : 3))
                    notes.Add(new TkBlock { Title = r.Name ?? r.Version, Date = r.Date, Body = r.Notes });
            if (!showingVerify) gameNotes.SetBlocks(notes, feed == null ? "" : upToDate ? "You have the latest version." : "No release notes.");

            // server
            string sdir = settings.ServerDir;
            string sv = Ops.ServerVersion(sdir);
            ServerRelease sr = feed != null ? feed.Server : null;
            bool serverInstalled = sv != null;
            bool serverCurrent = serverInstalled && sr != null && sv == sr.Version;
            sInstalled.Set(!serverInstalled ? "not installed" : (sr != null && sv == sr.Version ? (sr.Name ?? sv) + "  (up to date)" : sv), !serverInstalled ? Theme.TextDim : serverCurrent ? Theme.Good : Theme.Text);
            if (sr != null && sr.Error != null)
            {
                sLatest.Set(sr.Error, Theme.Bad);
                sDownload.Set("-", Theme.TextDim);
            }
            else
            {
                sLatest.Set(sr == null ? (feed == null ? "-" : "no server on the update site") : (sr.Name ?? sr.Version) + (string.IsNullOrEmpty(sr.Date) ? "" : "   " + sr.Date), sr == null ? Theme.TextDim : Theme.Text);
                sDownload.Set(sr == null ? "-" : serverCurrent ? "nothing to download" : (sr.Size > 0 ? Ops.Size(sr.Size) : "size unknown") + (sr.UnpackedSize > 0 ? ",  " + Ops.Size(sr.UnpackedSize) + " installed" : ""), serverCurrent || sr == null ? Theme.TextDim : Theme.Text);
            }
            bool running = serverInstalled && Ops.ServerRunning(sdir);
            sState.Set(!serverInstalled ? "-" : running ? "running" : "stopped", running ? Theme.Good : Theme.TextDim);
            sPorts.Set(serverInstalled ? (Ops.ServerPorts(sdir) ?? "") : "", Theme.TextDim);
            sInstall.Text = serverInstalled ? (serverCurrent ? "Reinstall server" : "Update server") : "Install server";
            sInstall.Enabled = !busy && sr != null && sr.Error == null && !running && !string.IsNullOrEmpty(sdir);
            sStart.Enabled = !busy && serverInstalled && File.Exists(Ops.ServerExePath(sdir)) && !running;
            sConfig.Enabled = serverInstalled;
            sOpen.Enabled = Directory.Exists(sdir);
            sBrowse.Enabled = !busy;
            serverDir.Enabled = !busy;

            var hosting = new List<TkBlock>();
            if (sr != null && !string.IsNullOrEmpty(sr.Notes)) hosting.Add(new TkBlock { Title = sr.Name ?? sr.Version, Date = sr.Date, Body = sr.Notes });
            hosting.Add(new TkBlock
            {
                Title = "Hosting your own server",
                Body = "The server keeps accounts, profiles, traders and quests, and starts a raid server for each match " +
                       "(the installed game runs it, without a window).\n\n" +
                       "Playing alone: start the server, then pick it in the game's server list (SERVER button on the login screen, address 127.0.0.1, port 6969).\n\n" +
                       "Playing with others: they connect to your address. Forward TCP for the server's port and UDP for the raid ports " +
                       "in your router, or use a VPN such as Radmin VPN or ZeroTier and set \"ip\" and \"matchmaking\" > \"matchIp\" in the config to that address."
            });
            serverNotes.SetBlocks(hosting, "");
        }

        bool showingVerify;

        void VerifyResult(List<string> bad)
        {
            BeginInvoke((Action)(() =>
            {
                showingVerify = true;
                gameNotes.SetBlocks(new List<TkBlock>
                {
                    new TkBlock
                    {
                        Title = bad.Count == 0 ? "All files are OK" : bad.Count + " file" + (bad.Count == 1 ? " is" : "s are") + " missing or changed",
                        Body = bad.Count == 0 ? "Every file matches this version." : "REINSTALL downloads the game again and replaces them.\n\n" + string.Join("\n", bad.Take(60)) + (bad.Count > 60 ? "\n..." : "")
                    }
                }, "");
                Result(bad.Count == 0 ? "All game files are OK." : bad.Count + " game files are missing or changed. REINSTALL repairs them.", bad.Count == 0 ? Theme.Good : Theme.Bad);
            }));
        }

        void GameMain()
        {
            if (gMain.Text == "Play")
            {
                try
                {
                    Ops.StartGame(settings.GameDir);
                    settings.Save();
                    Close();
                }
                catch (Exception ex)
                {
                    Result(ex.Message, Theme.Bad);
                }
            }
            else if (gMain.Text == "Update")
                Run("Update", j => Ops.UpdateGame(feed, CurrentMirror, settings.GameDir, j), "The game is up to date.");
            else
                Run("Install", j => { Ops.InstallGame(feed, CurrentMirror, settings.GameDir, j); Ops.GameShortcuts(settings.GameDir, settings.Desktop, j); }, "The game is installed. Press PLAY to start it.");
        }

        // ------------------------------------------------------------------ running operations

        void Result(string text, Color color)
        {
            resultText = text;
            resultColor = color;
            status.Set(text, color);
            detail.Set("", Theme.TextDim);
            bar.Set(-1, "");
        }

        void Run(string name, Action<Job> action, string success = null)
        {
            if (job != null) return;
            settings.Save();
            showingVerify = false;
            var j = new Job();
            j.Log = text => Program.Log(name + ": " + text);
            job = j;
            cancel.Visible = true;
            bar.Width = Theme.S(830);
            RefreshState();
            worker = new Thread(() =>
            {
                string message = success;
                Color color = Theme.Good;
                try
                {
                    action(j);
                }
                catch (CancelledException)
                {
                    message = "Cancelled. The download continues where it stopped next time.";
                    color = Theme.TextDim;
                }
                catch (Exception ex)
                {
                    Program.Log(name + " failed: " + ex);
                    message = ex.Message;
                    color = Theme.Bad;
                }
                try { BeginInvoke((Action)(() => Finished(message, color))); }
                catch (InvalidOperationException) { }
            }) { IsBackground = true };
            worker.Start();
        }

        void Finished(string message, Color color)
        {
            job = null;
            worker = null;
            cancel.Visible = false;
            bar.Width = Theme.S(952);
            if (message != null) Result(message, color);
            else if (!showingVerify) Result("", Theme.Text);
            RefreshState();
            if (closeWhenDone) Close();
        }

        void Tick()
        {
            if (job != null)
            {
                string text, more;
                double fraction;
                job.Read(out text, out more, out fraction);
                status.Set(text, Theme.Text);
                detail.Set(more, Theme.TextDim);
                bar.Set(fraction, fraction >= 0 ? (int)(fraction * 100) + "%" : "");
                cancel.Enabled = job.Cancellable;
            }
            // folders change outside (game started, server window closed): check every 2 s
            if (++refreshTick % 40 == 0 && job == null) RefreshState();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (job != null)
            {
                if (!job.Cancellable)
                {
                    e.Cancel = true;
                    status.Set("Wait until the update is applied.", Theme.Bad);
                    return;
                }
                job.Cancel = true;
                closeWhenDone = true;
                e.Cancel = true;
                return;
            }
            settings.Save();
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------ window chrome

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                cp.Style |= 0x20000;      // WS_MINIMIZEBOX: taskbar click minimises
                return cp;
            }
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Y > Theme.S(150)) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            int w = ClientSize.Width;
            // header: a raid photo of the game's info window, darkened towards the tabs, with the logo
            var header = new Rectangle(0, 0, w, Theme.S(150));
            Theme.Cover(g, Theme.Img("test_infowindow_external"), header, 0.55f);
            using (var shade = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, header.Bottom - Theme.S(110), w, Theme.S(111)), Color.FromArgb(0, Theme.Window), Theme.Window, 90f))
                g.FillRectangle(shade, 0, header.Bottom - Theme.S(110), w, Theme.S(110));
            using (var top = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, Theme.S(33)), Color.FromArgb(200, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
                g.FillRectangle(top, 0, 0, w, Theme.S(32));
            Theme.Draw(g, Theme.Img("trader_panel_diagonal_lines"), new Rectangle(w - Theme.S(420), Theme.S(40), Theme.S(400), Theme.S(100)), null, 0.35f);
            Image logo = Theme.Img("escape_from_tarkov_logo");
            int lh = Theme.S(104), lw = logo.Width * lh / logo.Height;
            Theme.Draw(g, logo, new Rectangle(Theme.S(18), Theme.S(36), lw, lh));
            Theme.DrawText(g, "ALPHA", Theme.Font(22, true), Theme.Text, new RectangleF(Theme.S(26) + lw, Theme.S(100), Theme.S(200), Theme.S(30)));
            Theme.DrawText(g, "INSTALLER", Theme.Font(13, true), Theme.TextDim, new RectangleF(Theme.S(14), 0, Theme.S(300), Theme.S(32)));
            // header line under the tabs, the game's header_panel plate
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, Theme.S(24), Theme.S(196), w - Theme.S(24), Theme.S(196));
            Theme.Draw(g, Theme.Img("header_shadow"), new Rectangle(Theme.S(24), Theme.S(197), Theme.S(140), Theme.S(10)), null, 0.6f);
            // bottom bar
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, Theme.S(24), Theme.S(558), w - Theme.S(24), Theme.S(558));
            Theme.Frame(g, ClientRectangle, Theme.LineLight);
        }

        sealed class TitleButton : TkControl
        {
            readonly bool isClose;

            public TitleButton(bool closeButton)
            {
                isClose = closeButton;
                Cursor = Cursors.Hand;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Theme.Quality(g);
                if (hover) using (var b = new SolidBrush(isClose ? Color.FromArgb(150, 160, 0, 0) : Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(b, ClientRectangle);
                if (isClose)
                {
                    Image x = Theme.Img("close_window_x");
                    int w = Theme.S(x.Width), h = Theme.S(x.Height);
                    Theme.Draw(g, x, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h), hover ? Color.White : Theme.Text);
                }
                else
                {
                    using (var b = new SolidBrush(hover ? Color.White : Theme.Text))
                        g.FillRectangle(b, Width / 2 - Theme.S(5), Height / 2 + Theme.S(3), Theme.S(10), Math.Max(1, Theme.S(2)));
                }
            }
        }

        sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            }
        }

        // ------------------------------------------------------------------ screenshot (layout check)

        public Bitmap CaptureImage()
        {
            var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
            DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            return bitmap;
        }

        public void SelectTab(int index) { ShowTab(index); }

        /// <summary>Screenshot only: the bottom bar as it looks during a download.</summary>
        public void DemoProgress()
        {
            job = new Job();
            cancel.Visible = true;
            bar.Width = Theme.S(830);
            job.Step("Downloading the game (0.1.4)");
            job.Bytes(0, 3400000000, true);
            Thread.Sleep(600);
            job.Bytes(1312000000, 3400000000, true);
            Tick();
        }
    }
}
