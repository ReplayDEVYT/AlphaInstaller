// The server's Files/configs/server.json as the SERVER tab's config window edits it: backend address and port, the
// match client (the game exe that runs raids, matchmaking.gameServerPath) and the matchmaking addresses and ports.
// Everything else in the file is kept as it is (same keys, same order); missing values fall back to server_base.json.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace EftInstaller
{
    sealed class ServerSettings
    {
        public string Ip, Name, MatchMode, MatchIp, HostPublicIp, GameServerPath, GameServerArgs;
        public int Port, PortStart, PortEnd, HostDefaultPort;
        /// <summary>servers[0].ip: the raid address the server hands out when matchIp is empty.</summary>
        public string FallbackMatchIp;

        Dictionary<string, object> root;
        string path;

        static string Configs(string folder) { return Path.Combine(folder, "Files", "configs"); }

        public static ServerSettings Load(string folder)
        {
            string configs = Configs(folder);
            Dictionary<string, object> config = Read(Path.Combine(configs, "server.json"));
            Dictionary<string, object> defaults = Read(Path.Combine(configs, "server_base.json"));
            if (config == null && defaults == null) throw new Exception("The server has no Files\\configs\\server.json. Install the server first.");
            var s = new ServerSettings { root = config ?? Copy(defaults), path = Path.Combine(configs, "server.json") };
            Func<string, string, object> get = (section, key) =>
            {
                object v;
                Dictionary<string, object> c = section == null ? config : Feed.Obj(config, section);
                if (c != null && c.TryGetValue(key, out v) && v != null) return v;
                Dictionary<string, object> d = section == null ? defaults : Feed.Obj(defaults, section);
                return d != null && d.TryGetValue(key, out v) ? v : null;
            };
            Func<string, string, string, string> str = (section, key, fallback) => { object v = get(section, key); return v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback; };
            Func<string, string, int, int> num = (section, key, fallback) =>
            {
                object v = get(section, key);
                try { return v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : fallback; } catch (Exception) { return fallback; }
            };
            s.Ip = str(null, "ip", "127.0.0.1");
            s.Port = num(null, "port", 6969);
            s.Name = str(null, "name", "Tarkov Server");
            s.MatchMode = str("matchmaking", "mode", "auto");
            s.MatchIp = str("matchmaking", "matchIp", "");
            s.HostPublicIp = str("matchmaking", "hostPublicIp", "");
            s.PortStart = num("matchmaking", "portStart", 7777);
            s.PortEnd = num("matchmaking", "portEnd", 7796);
            s.HostDefaultPort = num("matchmaking", "hostDefaultPort", 7777);
            s.GameServerPath = str("matchmaking", "gameServerPath", "");
            s.GameServerArgs = str("matchmaking", "gameServerArgs", "");
            object servers = get(null, "servers");
            var first = (servers as IEnumerable) != null ? ((IEnumerable)servers).OfType<Dictionary<string, object>>().FirstOrDefault() : null;
            s.FallbackMatchIp = Feed.Str(first, "ip") ?? "127.0.0.1";
            return s;
        }

        /// <summary>Problems with the values, one line each (empty = fine).</summary>
        public List<string> Check()
        {
            var problems = new List<string>();
            if (!IsAddress(Ip, false)) problems.Add("Server address: an IP address such as 127.0.0.1 or 0.0.0.0.");
            if (!IsPort(Port)) problems.Add("Server port: 1 to 65535.");
            if (!IsAddress(MatchIp, true)) problems.Add("Raid address: an IP address or host name, or empty.");
            if (!IsAddress(HostPublicIp, true)) problems.Add("Host public address: an IP address or host name, or empty.");
            if (!IsPort(PortStart) || !IsPort(PortEnd)) problems.Add("Raid ports: 1 to 65535.");
            else if (PortEnd < PortStart) problems.Add("Raid ports: the last port is lower than the first.");
            if (!IsPort(HostDefaultPort)) problems.Add("Host default port: 1 to 65535.");
            if (IsPort(Port) && IsPort(PortStart) && IsPort(PortEnd) && Port >= PortStart && Port <= PortEnd)
                problems.Add("The server port " + Port + " is inside the raid ports " + PortStart + "-" + PortEnd + ".");
            if (!string.IsNullOrEmpty(GameServerPath) && !File.Exists(GameServerPath)) problems.Add("Match client: " + GameServerPath + " does not exist.");
            if (MatchMode != "auto" && MatchMode != "dedicated" && MatchMode != "host") problems.Add("Match mode: auto, dedicated or host.");
            return problems;
        }

        static bool IsPort(int p) { return p >= 1 && p <= 65535; }

        static bool IsAddress(string a, bool emptyOk)
        {
            if (string.IsNullOrEmpty(a)) return emptyOk;
            IPAddress ip;
            if (IPAddress.TryParse(a, out ip)) return true;
            return !emptyOk ? false : Uri.CheckHostName(a) == UriHostNameType.Dns;
        }

        /// <summary>Writes the values into server.json; every other key stays.</summary>
        public void Save()
        {
            root["ip"] = Ip;
            root["port"] = Port;
            root["name"] = Name;
            Dictionary<string, object> mm = Feed.Obj(root, "matchmaking");
            if (mm == null) root["matchmaking"] = mm = new Dictionary<string, object>();
            mm["mode"] = MatchMode;
            mm["matchIp"] = MatchIp ?? "";
            mm["hostPublicIp"] = HostPublicIp ?? "";
            mm["portStart"] = PortStart;
            mm["portEnd"] = PortEnd;
            mm["hostDefaultPort"] = HostDefaultPort;
            mm["gameServerPath"] = (GameServerPath ?? "").Replace('\\', '/');
            mm["gameServerArgs"] = GameServerArgs ?? "";
            var sb = new StringBuilder();
            Write(sb, root, 0);
            sb.Append('\n');
            string temp = path + ".saving";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Copy(temp, path, true);
            File.Delete(temp);
        }

        // ------------------------------------------------------------------ JSON in key order

        static Dictionary<string, object> Read(string file)
        {
            if (!File.Exists(file)) return null;
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
        }

        static Dictionary<string, object> Copy(Dictionary<string, object> d)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(d)) as Dictionary<string, object>;
        }

        static void Write(StringBuilder sb, object value, int depth)
        {
            string indent = new string('\t', depth + 1), close = new string('\t', depth);
            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (var kv in dict)
                {
                    sb.Append(indent).Append(Quote(kv.Key)).Append(": ");
                    Write(sb, kv.Value, depth + 1);
                    sb.Append(++i < dict.Count ? ",\n" : "\n");
                }
                sb.Append(close).Append('}');
                return;
            }
            if (value is string) { sb.Append(Quote((string)value)); return; }
            if (value == null) { sb.Append("null"); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            var list = value as IEnumerable;
            if (list != null)
            {
                var items = list.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append(indent);
                    Write(sb, items[i], depth + 1);
                    sb.Append(i + 1 < items.Count ? ",\n" : "\n");
                }
                sb.Append(close).Append(']');
                return;
            }
            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
