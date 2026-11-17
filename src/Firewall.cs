// Windows Firewall rules for the server: TCP <port> (backend) and UDP <portStart>-<portEnd> (+ the host port) for raids.
// Same rule names as the server's "Open firewall ports.bat" and its start-up check (ServerLib NetworkReport/Firewall),
// so either side sees what the other set up. The first START SERVER asks for them once (Files/configs/firewall_setup.txt).
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace EftInstaller
{
    static class Firewall
    {
        public const string RuleTcp = "EFT Alpha Server (backend TCP)";
        public const string RuleUdp = "EFT Alpha Server (raids UDP)";
        const string MarkerName = "firewall_setup.txt";

        public enum State { Ok, Missing, Outdated, Unknown }

        /// <summary>UDP ports the raids need: the range, plus the default host port when it is outside it.</summary>
        public static string UdpPorts(ServerSettings s) { return UdpPorts(s.PortStart, s.PortEnd, s.HostDefaultPort); }

        public static string UdpPorts(int start, int end, int hostPort)
        {
            string range = start == end ? start.ToString() : start + "-" + end;
            if (hostPort > 0 && (hostPort < start || hostPort > end)) range += "," + hostPort;
            return range;
        }

        /// <summary>Whether both rules exist with these ports. Reading rules needs no administrator rights.</summary>
        public static State Check(ServerSettings s) { return Check(s.Port, UdpPorts(s)); }

        public static State Check(int port, string udpPorts)
        {
            string tcp, udp;
            int tcpCode = Netsh("advfirewall firewall show rule name=\"" + RuleTcp + "\"", out tcp);
            int udpCode = Netsh("advfirewall firewall show rule name=\"" + RuleUdp + "\"", out udp);
            if (tcpCode < 0 || udpCode < 0) return State.Unknown;
            if (tcpCode != 0 || udpCode != 0) return State.Missing;
            // the labels are in the Windows language, the port numbers are not
            if (!HasToken(tcp, port.ToString()) || !HasToken(udp, udpPorts)) return State.Outdated;
            return State.Ok;
        }

        static bool HasToken(string output, string token)
        {
            return Regex.IsMatch(output ?? "", @"(^|\s)" + Regex.Escape(token) + @"(\s|$)", RegexOptions.Multiline);
        }

        public static string Describe(State state, int port, string udpPorts)
        {
            string ports = "TCP " + port + " and UDP " + udpPorts;
            switch (state)
            {
                case State.Ok: return "Windows Firewall allows " + ports + ".";
                case State.Outdated: return "The firewall rules are for other ports: press OPEN PORTS to allow " + ports + ".";
                case State.Missing: return "Windows Firewall may block other PCs: press OPEN PORTS to allow " + ports + ".";
                default: return "Could not read the Windows Firewall rules.";
            }
        }

        /// <summary>Adds (replaces) both rules. Asks for administrator rights (UAC). False with a reason when refused or failed.</summary>
        public static bool Open(ServerSettings s, out string error) { return Open(s.Port, UdpPorts(s), out error); }

        public static bool Open(int port, string udpPorts, out string error)
        {
            error = null;
            string del = "netsh advfirewall firewall delete rule name=\"{0}\" >nul 2>&1";
            string add = "netsh advfirewall firewall add rule name=\"{0}\" dir=in action=allow protocol={1} localport={2} profile=any >nul";
            string command = string.Format(del, RuleTcp) + " & " + string.Format(del, RuleUdp) + " & " +
                string.Format(add, RuleTcp, "TCP", port) + " && " + string.Format(add, RuleUdp, "UDP", udpPorts);
            try
            {
                // cmd strips the outer quotes of /c "..." and runs the rest as typed
                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + command + "\"")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { error = "The firewall command did not start."; return false; }
                    if (!p.WaitForExit(30000)) { error = "The firewall command did not finish."; return false; }
                    if (p.ExitCode != 0) { error = "netsh could not add the rules (exit code " + p.ExitCode + ")."; return false; }
                }
            }
            catch (Win32Exception ex)
            {
                error = ex.NativeErrorCode == 1223 ? "Administrator rights were not given." : ex.Message;
                return false;
            }
            Program.Log("firewall: rules added for TCP " + port + ", UDP " + udpPorts);
            return true;
        }

        static string MarkerPath(string serverDir) { return Path.Combine(serverDir, "Files", "configs", MarkerName); }

        /// <summary>
        /// Before the server's first start from the launcher: when the rules are missing, ask once (UAC) to add them.
        /// The answer is remembered either way, so later starts do not ask again (the CONFIG window still can).
        /// Returns a note for the status line, or null.
        /// </summary>
        public static string FirstStart(string serverDir)
        {
            string marker = MarkerPath(serverDir);
            if (File.Exists(marker)) return null;
            ServerSettings s;
            try { s = ServerSettings.Load(serverDir); }
            catch (Exception) { return null; }
            State state = Check(s);
            string note = null, error;
            if (state == State.Ok) note = null;
            else if (state == State.Unknown) note = null;
            else if (Open(s, out error)) note = "Windows Firewall now allows TCP " + s.Port + " and UDP " + UdpPorts(s) + ".";
            else note = "Firewall ports not opened: " + error + " You can do it later in CONFIG.";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker));
                File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " " + (note ?? "rules " + state) + "\r\n");
            }
            catch (Exception) { }
            return note;
        }

        /// <summary>Runs netsh without a window. Exit code, or -1 when it could not run.</summary>
        static int Netsh(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("netsh", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(5000)) return -1;
                    return p.ExitCode;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
