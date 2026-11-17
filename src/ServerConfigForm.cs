// SERVER tab > CONFIG: the server's address and port, the match client (which program runs the raids) and the
// matchmaking addresses and raid ports, written into Files/configs/server.json (ServerSettings).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EftInstaller
{
    /// <summary>Borderless window in the launcher's look: title, close button, drag by the title bar, Esc closes.</summary>
    class TkDialog : Form
    {
        readonly string title;
        protected const int TitleHeight = 40;

        public TkDialog(string titleText, int w, int h)
        {
            title = titleText;
            Text = titleText;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(w), Theme.S(h));
            BackColor = Theme.Window;
            DoubleBuffered = true;
            KeyPreview = true;
            Icon = Theme.AppIcon();
            var close = new TkButton("X");
            Controls.Add(close);
            Place(close, w - 44, 6, 36, 28);
            close.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || e.Y > Theme.S(TitleHeight)) return;
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            };
        }

        protected static void Place(Control c, int x, int y, int w, int h)
        {
            c.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
        }

        protected TkLabel Caption(string text, int x, int y, int w)
        {
            var l = new TkLabel(text.ToUpperInvariant(), 13, true) { Color = Theme.TextDim };
            Controls.Add(l);
            Place(l, x, y, w, 18);
            return l;
        }

        protected TkLabel Section(string text, int y)
        {
            var l = new TkLabel(text.ToUpperInvariant(), 17, true) { Color = Color.White };
            Controls.Add(l);
            Place(l, 24, y, 700, 24);
            return l;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            base.OnKeyDown(e);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            using (var b = new SolidBrush(Color.FromArgb(20, 20, 18))) g.FillRectangle(b, 0, 0, ClientSize.Width, Theme.S(TitleHeight));
            Theme.DrawText(g, title.ToUpperInvariant(), Theme.Font(17, true), Theme.Text, new RectangleF(Theme.S(16), 0, ClientSize.Width, Theme.S(TitleHeight)));
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, Theme.S(TitleHeight), ClientSize.Width, Theme.S(TitleHeight));
            Theme.Frame(g, ClientRectangle, Theme.LineLight);
        }
    }

    sealed class ServerConfigForm : TkDialog
    {
        const int W = 1240, H = 700;
        const int RX = 784, RW = 432; // right column: network recommendations, firewall
        readonly string serverDir, gameDir;
        readonly ServerSettings config;

        /// <summary>After OK: whether the installed game is the match client (the launcher keeps gameServerPath on it).</summary>
        public bool UseInstalledGame;

        readonly TkInput ip = new TkInput(), port = new TkInput(), name = new TkInput();
        readonly TkTab cInstalled = new TkTab("Installed game"), cOther = new TkTab("Other program"), cNone = new TkTab("None");
        readonly TkLabel clientHint = new TkLabel("", 14) { Color = Theme.TextDim, Wrap = true };
        readonly TkInput clientPath = new TkInput(), clientArgs = new TkInput();
        readonly TkButton clientBrowse = new TkButton("Browse");
        readonly TkTab mAuto = new TkTab("Auto"), mDedicated = new TkTab("Dedicated"), mHost = new TkTab("Host");
        readonly TkLabel modeHint = new TkLabel("", 14) { Color = Theme.TextDim, Wrap = true };
        readonly TkInput matchIp = new TkInput(), hostIp = new TkInput(), hostPort = new TkInput(), portStart = new TkInput(), portEnd = new TkInput();
        readonly TkLabel problems = new TkLabel("", 14) { Color = Theme.Bad, Wrap = true };
        readonly TkButton openJson = new TkButton("Open server.json"), cancel = new TkButton("Cancel"), save = new TkButton("Save", true);

        readonly TkLabel netInfo = new TkLabel("", 14) { Wrap = true };
        readonly TkLabel recIp = new TkLabel("", 14) { Wrap = true }, recMatch = new TkLabel("", 14) { Wrap = true };
        readonly TkLabel connectInfo = new TkLabel("", 14) { Color = Theme.TextDim, Wrap = true };
        readonly TkLabel fwStatus = new TkLabel("", 14) { Wrap = true };
        readonly TkButton useAllIp = new TkButton("Use 0.0.0.0"), usePublic = new TkButton("Use public IP"), fwOpen = new TkButton("Open ports");
        List<string> lanIps = new List<string>(), vpnIps = new List<string>();
        string publicIp; // null = still looking it up, "" = not found

        int client; // 0 installed game, 1 other program, 2 none
        string mode;
        string otherPath = "";

        public ServerConfigForm(string serverFolder, string gameFolder, bool useInstalledGame) : base("Server config", W, H)
        {
            serverDir = serverFolder;
            gameDir = gameFolder;
            config = ServerSettings.Load(serverFolder);

            Section("Backend", 52);
            Caption("Server address", 24, 82, 240);
            Caption("Port", 284, 82, 100);
            Caption("Server name", 404, 82, 300);
            Add(ip, 24, 102, 250);
            Add(port, 284, 102, 110);
            Add(name, 404, 102, 332);
            ip.Text = config.Ip;
            port.Text = config.Port.ToString();
            name.Text = config.Name;

            Section("Match client (runs the raids)", 156);
            int x = 24;
            foreach (TkTab t in new[] { cInstalled, cOther, cNone })
            {
                Controls.Add(t);
                Place(t, x, 186, 200, 32);
                x += 208;
            }
            cInstalled.Click += (s, e) => SetClient(0);
            cOther.Click += (s, e) => SetClient(1);
            cNone.Click += (s, e) => SetClient(2);
            Controls.Add(clientHint);
            Place(clientHint, 24, 224, 712, 22);
            Add(clientPath, 24, 248, 600);
            Controls.Add(clientBrowse);
            Place(clientBrowse, 636, 248, 100, 34);
            clientBrowse.Click += (s, e) => BrowseClient();
            clientPath.Box.TextChanged += (s, e) => { if (client == 1) otherPath = clientPath.Text.Trim(); };
            Caption("Extra arguments for the match client", 24, 292, 500);
            Add(clientArgs, 24, 312, 712);
            clientArgs.Text = config.GameServerArgs;

            Section("Matchmaking", 366);
            x = 24;
            foreach (TkTab t in new[] { mAuto, mDedicated, mHost })
            {
                Controls.Add(t);
                Place(t, x, 396, 150, 32);
                x += 158;
            }
            mAuto.Click += (s, e) => SetMode("auto");
            mDedicated.Click += (s, e) => SetMode("dedicated");
            mHost.Click += (s, e) => SetMode("host");
            Controls.Add(modeHint);
            Place(modeHint, 24, 432, 712, 38);
            Caption("Raid address (internet players)", 24, 474, 250);
            Caption("Host public address", 284, 474, 250);
            Caption("Host port", 544, 474, 192);
            Add(matchIp, 24, 494, 250);
            Add(hostIp, 284, 494, 250);
            Add(hostPort, 544, 494, 110);
            Caption("Raid ports (UDP), first and last", 24, 540, 400);
            Add(portStart, 24, 560, 110);
            var dash = new TkLabel("to", 15) { Color = Theme.TextDim, Align = StringAlignment.Center };
            Controls.Add(dash);
            Place(dash, 134, 560, 40, 34);
            Add(portEnd, 174, 560, 110);
            matchIp.Text = config.MatchIp;
            hostIp.Text = config.HostPublicIp;
            hostPort.Text = config.HostDefaultPort.ToString();
            portStart.Text = config.PortStart.ToString();
            portEnd.Text = config.PortEnd.ToString();
            var portsNote = new TkLabel("Forward these ports (UDP) and the server port (TCP) for players outside your network.", 13) { Color = Theme.TextDim, Wrap = true };
            Controls.Add(portsNote);
            Place(portsNote, 300, 556, 436, 40);

            Controls.Add(problems);
            Place(problems, 24, 600, 712, 40);
            foreach (TkButton b in new[] { openJson, cancel, save }) Controls.Add(b);
            Place(openJson, 24, 648, 200, 40);
            Place(cancel, 494, 648, 110, 40);
            Place(save, 616, 648, 120, 40);
            openJson.Click += (s, e) => OpenJson();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            save.Click += (s, e) => Save();

            string installed = InstalledGameExe;
            bool pathIsGame = installed != null && SamePath(config.GameServerPath, installed);
            if (useInstalledGame && installed != null) client = 0;
            else if (!string.IsNullOrEmpty(config.GameServerPath) && !pathIsGame) client = 1;
            else if (pathIsGame) client = 0;
            else client = 2;
            if (client == 1) otherPath = config.GameServerPath.Replace('/', '\\');
            SetClient(client);
            SetMode(config.MatchMode);
            BuildNetworkColumn();
            if (Ops.ServerRunning(serverDir)) Problems(new List<string> { "The server is running: restart it after saving to use the new settings." }, Theme.TextDim);
        }

        // ------------------------------------------------------------------ network recommendations, firewall

        void BuildNetworkColumn()
        {
            var network = new TkLabel("NETWORK", 17, true) { Color = Color.White };
            Controls.Add(network);
            Place(network, RX, 52, RW, 24);
            Caption("This PC's addresses", RX, 82, RW);
            Controls.Add(netInfo);
            Place(netInfo, RX, 102, RW, 62);

            Caption("Recommended", RX, 172, RW);
            Controls.Add(recIp);
            Place(recIp, RX, 194, RW, 56);
            Controls.Add(useAllIp);
            Place(useAllIp, RX, 252, 200, 32);
            useAllIp.Click += (s, e) => ip.Text = "0.0.0.0";
            Controls.Add(recMatch);
            Place(recMatch, RX, 296, RW, 72);
            Controls.Add(usePublic);
            Place(usePublic, RX, 370, 200, 32);
            usePublic.Click += (s, e) => { if (!string.IsNullOrEmpty(publicIp)) matchIp.Text = publicIp; };

            Caption("Players connect with", RX, 414, RW);
            Controls.Add(connectInfo);
            Place(connectInfo, RX, 434, RW, 74);

            var fw = new TkLabel("WINDOWS FIREWALL", 17, true) { Color = Color.White };
            Controls.Add(fw);
            Place(fw, RX, 516, RW, 24);
            Controls.Add(fwStatus);
            Place(fwStatus, RX, 546, RW, 54);
            Controls.Add(fwOpen);
            Place(fwOpen, RX, 604, 200, 36);
            fwOpen.Click += (s, e) => OpenFirewall();

            foreach (TkInput input in new[] { ip, port, matchIp, hostPort, portStart, portEnd })
                input.Box.TextChanged += (s, e) => UpdateNetwork();
            LocalAddresses();
            UpdateNetwork();
            UpdateFirewall();
            LookUpPublicIp();
        }

        /// <summary>IPv4 addresses of this PC's network cards: private ones (LAN), others (VPN adapters such as Radmin).</summary>
        void LocalAddresses()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address)) continue;
                        string a = u.Address.ToString();
                        if (a.StartsWith("169.254.")) continue; // no DHCP answer, not usable
                        List<string> list = IsPrivate(u.Address) && !a.StartsWith("100.") ? lanIps : vpnIps;
                        if (!list.Contains(a)) list.Add(a);
                    }
                }
            }
            catch (Exception ex) { Program.Log("network addresses: " + ex.Message); }
        }

        static bool IsPrivate(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        /// <summary>The address the internet sees (asked from a "what is my IP" service; only a plain request is sent).</summary>
        void LookUpPublicIp()
        {
            var thread = new System.Threading.Thread(() =>
            {
                string found = "";
                foreach (string url in new[] { "https://api.ipify.org", "https://icanhazip.com" })
                {
                    try
                    {
                        var request = (HttpWebRequest)WebRequest.Create(url);
                        request.Timeout = 5000;
                        request.UserAgent = "EFT-Alpha-Launcher";
                        using (var response = request.GetResponse())
                        using (var reader = new StreamReader(response.GetResponseStream()))
                        {
                            string text = reader.ReadToEnd().Trim();
                            IPAddress parsed;
                            if (IPAddress.TryParse(text, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork) { found = text; break; }
                        }
                    }
                    catch (Exception ex) { Program.Log("public IP from " + url + ": " + ex.Message); }
                }
                try
                {
                    if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { publicIp = found; UpdateNetwork(); }));
                }
                catch (Exception) { }
            }) { IsBackground = true };
            thread.Start();
        }

        void UpdateNetwork()
        {
            string lan = lanIps.Count > 0 ? string.Join(", ", lanIps.ToArray()) : "none found";
            string pub = publicIp == null ? "looking up..." : publicIp == "" ? "not found (no internet?)" : publicIp;
            netInfo.Set("Internal (LAN): " + lan + (vpnIps.Count > 0 ? "\nVPN adapters: " + string.Join(", ", vpnIps.ToArray()) : "") +
                "\nExternal (internet): " + pub, Theme.Text);

            string a = ip.Text.Trim();
            if (a == "0.0.0.0")
                recIp.Set("Server address 0.0.0.0: set. This PC, your LAN and the internet can connect.", Theme.Good);
            else if (a == "127.0.0.1" || a == "localhost")
                recIp.Set("Server address: use 0.0.0.0. With 127.0.0.1 only this PC can connect, nobody on your LAN or the internet.", Theme.Bad);
            else
                recIp.Set("Server address: 0.0.0.0 is recommended (all of this PC's addresses). " + a + " only answers on that one address.", Theme.TextDim);
            useAllIp.Enabled = a != "0.0.0.0";

            string m = matchIp.Text.Trim();
            bool havePublic = !string.IsNullOrEmpty(publicIp);
            usePublic.Text = havePublic ? "Use " + publicIp : "Use public IP";
            usePublic.Enabled = havePublic && m != publicIp;
            if (!havePublic)
                recMatch.Set("Raid address: your external IP (or a domain) for players on the internet. Players on your LAN and on this PC get the right address by themselves.", Theme.TextDim);
            else if (m == publicIp)
                recMatch.Set("Raid address " + publicIp + ": set. Players on your LAN and on this PC still get their own address by themselves.", Theme.Good);
            else if (m == "")
                recMatch.Set("Raid address: " + publicIp + " for players on the internet (empty = the address they typed to reach the server). LAN and this PC get their own address by themselves.", Theme.TextDim);
            else
                recMatch.Set("Raid address " + m + " goes to players on the internet. Your external IP is " + publicIp + ". LAN and this PC get their own address by themselves.", Theme.TextDim);

            int p = Int(port);
            string ps = p > 0 ? p.ToString() : "6969";
            string connect = "This PC: 127.0.0.1:" + ps;
            foreach (string l in lanIps) connect += "\nYour LAN: " + l + ":" + ps;
            connect += "\nInternet: " + (havePublic ? publicIp : "<external IP>") + ":" + ps + " (router: forward TCP " + ps + " and UDP " + UdpText() + " to " + (lanIps.Count > 0 ? lanIps[0] : "this PC") + ")";
            connectInfo.Set(connect, Theme.TextDim);
            UpdateFirewallText();
        }

        string UdpText()
        {
            int s = Int(portStart), e = Int(portEnd), h = Int(hostPort);
            if (s <= 0) return "7777-7796";
            return Firewall.UdpPorts(s, e >= s ? e : s, h);
        }

        Firewall.State fwState = Firewall.State.Unknown;
        string fwCheckedPorts; // "tcp/udp" the state is for

        void UpdateFirewall()
        {
            int p = Int(port);
            fwState = Firewall.Check(p > 0 ? p : 6969, UdpText());
            fwCheckedPorts = (p > 0 ? p : 6969) + "/" + UdpText();
            UpdateFirewallText();
        }

        void UpdateFirewallText()
        {
            if (fwCheckedPorts == null) return;
            int p = Int(port);
            string udp = UdpText();
            // ports changed in the window since the check: the rules are for the old ones
            Firewall.State state = fwState == Firewall.State.Ok && fwCheckedPorts != (p > 0 ? p : 6969) + "/" + udp ? Firewall.State.Outdated : fwState;
            fwStatus.Set(Firewall.Describe(state, p > 0 ? p : 6969, udp), state == Firewall.State.Ok ? Theme.Good : state == Firewall.State.Unknown ? Theme.TextDim : Theme.Bad);
            fwOpen.Text = state == Firewall.State.Ok ? "Open again" : "Open ports";
        }

        void OpenFirewall()
        {
            int p = Int(port);
            string error;
            if (p <= 0) { fwStatus.Set("Enter the server port first.", Theme.Bad); return; }
            if (Firewall.Open(p, UdpText(), out error)) UpdateFirewall();
            else fwStatus.Set("Not changed: " + error, Theme.Bad);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Line))
                e.Graphics.DrawLine(pen, Theme.S(RX - 22), Theme.S(TitleHeight + 14), Theme.S(RX - 22), Theme.S(H - 16));
        }

        void Add(TkInput input, int x, int y, int w)
        {
            Controls.Add(input);
            Place(input, x, y, w, 34);
        }

        string InstalledGameExe
        {
            get
            {
                string exe = Ops.GameExePath(gameDir);
                return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : null;
            }
        }

        static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a.Replace('/', '\\')), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        void SetClient(int index)
        {
            client = index;
            cInstalled.Selected = index == 0;
            cOther.Selected = index == 1;
            cNone.Selected = index == 2;
            foreach (TkTab t in new[] { cInstalled, cOther, cNone }) t.Invalidate();
            string installed = InstalledGameExe;
            if (index == 0)
            {
                clientPath.Text = installed ?? "";
                clientHint.Set(installed != null ? "The game installed on the GAME tab runs each raid without a window. It follows that folder."
                    : "No game installed on the GAME tab yet: install it, or pick another program.", installed != null ? Theme.TextDim : Theme.Bad);
            }
            else if (index == 1)
            {
                clientPath.Text = otherPath;
                clientHint.Set("Another copy of the game (Escape from Tarkov Alpha.exe) runs the raids.", Theme.TextDim);
            }
            else
            {
                clientPath.Text = "";
                clientHint.Set("The server starts no raid servers: run them yourself (game exe with -gameserver=<port>).", Theme.TextDim);
            }
            clientPath.Enabled = index == 1;
            clientPath.Box.ReadOnly = index != 1;
            clientBrowse.Enabled = index == 1;
            clientArgs.Enabled = index != 2;
        }

        void SetMode(string value)
        {
            mode = value == "dedicated" || value == "host" ? value : "auto";
            mAuto.Selected = mode == "auto";
            mDedicated.Selected = mode == "dedicated";
            mHost.Selected = mode == "host";
            foreach (TkTab t in new[] { mAuto, mDedicated, mHost }) t.Invalidate();
            modeHint.Set(mode == "auto" ? "Players who turn on hosting in the game's matching options host their own raids; everyone else gets a raid server on a port from the range below."
                : mode == "dedicated" ? "Every match gets a raid server on the next free port from the range below, at the raid address."
                : "The player who creates a match hosts it on their own address and port (host port by default).", Theme.TextDim);
        }

        void BrowseClient()
        {
            using (var dialog = new OpenFileDialog { Filter = "Game (*.exe)|*.exe", Title = "Program that runs the raids" })
            {
                if (!string.IsNullOrEmpty(otherPath) && Directory.Exists(Path.GetDirectoryName(otherPath))) dialog.InitialDirectory = Path.GetDirectoryName(otherPath);
                if (dialog.ShowDialog(this) == DialogResult.OK) clientPath.Text = otherPath = dialog.FileName;
            }
        }

        static int Int(TkInput input)
        {
            int v;
            return int.TryParse(input.Text.Trim(), out v) ? v : 0;
        }

        void Problems(List<string> list, Color color)
        {
            problems.Set(string.Join("\n", list.ToArray()), color);
        }

        void Save()
        {
            config.Ip = ip.Text.Trim();
            config.Port = Int(port);
            config.Name = name.Text.Trim();
            config.MatchMode = mode;
            config.MatchIp = matchIp.Text.Trim();
            config.HostPublicIp = hostIp.Text.Trim();
            config.HostDefaultPort = Int(hostPort);
            config.PortStart = Int(portStart);
            config.PortEnd = Int(portEnd);
            config.GameServerArgs = clientArgs.Text.Trim();
            config.GameServerPath = client == 0 ? (InstalledGameExe ?? "") : client == 1 ? otherPath : "";
            List<string> list = config.Check();
            if (client == 1 && string.IsNullOrEmpty(otherPath)) list.Insert(0, "Match client: pick the program, or choose None.");
            if (client == 0 && InstalledGameExe == null) list.Insert(0, "Match client: no game is installed on the GAME tab.");
            if (list.Count > 0)
            {
                Problems(list, Theme.Bad);
                return;
            }
            try
            {
                config.Save();
            }
            catch (Exception ex)
            {
                Problems(new List<string> { "Could not save server.json: " + ex.Message }, Theme.Bad);
                return;
            }
            UseInstalledGame = client == 0;
            Program.Log("server config saved: " + config.Ip + ":" + config.Port + ", mode " + config.MatchMode + ", raids " + config.PortStart + "-" + config.PortEnd + ", client " + (config.GameServerPath == "" ? "none" : config.GameServerPath));
            DialogResult = DialogResult.OK;
            Close();
        }

        void OpenJson()
        {
            string json = Path.Combine(serverDir, "Files", "configs", "server.json");
            if (!File.Exists(json))
            {
                Problems(new List<string> { "There is no server.json yet: press SAVE first." }, Theme.Bad);
                return;
            }
            try { Process.Start("notepad.exe", "\"" + json + "\""); }
            catch (Exception ex) { Problems(new List<string> { ex.Message }, Theme.Bad); }
        }
    }
}
