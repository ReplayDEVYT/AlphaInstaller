// Archives other than zip (the full client is a RAR5 made by WinRAR 7, method v6, which neither .NET nor Windows'
// tar can read): extracted with 7-Zip (Tools/installer/7zip: 7z.exe, 7z.dll, License.txt, embedded and unpacked to
// %TEMP%\EFTAlphaInstaller\7-Zip on first use; LGPL with the unRAR restriction, license shipped next to it).
// Also: which game files are the player's own (never installed from a full download), and the in-game update feed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace EftInstaller
{
    static class Archive
    {
        public static bool IsZip(string path)
        {
            using (var f = File.OpenRead(path))
            {
                var head = new byte[4];
                return f.Read(head, 0, 4) == 4 && head[0] == 'P' && head[1] == 'K';
            }
        }

        static string SevenZip()
        {
            string dir = Path.Combine(Path.GetTempPath(), "EFTAlphaInstaller", "7-Zip");
            Directory.CreateDirectory(dir);
            foreach (string name in new[] { "7z.exe", "7z.dll", "License.txt" })
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SevenZip." + name))
                {
                    if (s == null) throw new Exception("7-Zip is missing from this installer.");
                    string path = Path.Combine(dir, name);
                    if (File.Exists(path) && new FileInfo(path).Length == s.Length) continue;
                    using (var f = File.Create(path)) s.CopyTo(f);
                }
            }
            return Path.Combine(dir, "7z.exe");
        }

        /// <summary>Extracts any archive 7-Zip reads into staging (progress from 7-Zip's percent output).</summary>
        public static void ExtractWith7Zip(string archive, string staging, long unpackedSize, Job job)
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            var psi = new ProcessStartInfo(SevenZip(), "x -y -bso0 -bse2 -bsp1 -o\"" + staging + "\" \"" + archive + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var errors = new StringBuilder();
            long total = unpackedSize > 0 ? unpackedSize : 100;
            using (Process p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                // progress is written with backspaces, no line ends: read characters
                var buffer = new char[4096];
                var recent = new StringBuilder();
                var percent = new Regex(@"(\d{1,3})%");
                int read;
                while ((read = p.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (job.Cancel)
                    {
                        try { p.Kill(); } catch (Exception) { }
                        throw new CancelledException();
                    }
                    recent.Append(buffer, 0, read);
                    if (recent.Length > 400) recent.Remove(0, recent.Length - 200);
                    MatchCollection m = percent.Matches(recent.ToString());
                    if (m.Count > 0)
                    {
                        int pc = int.Parse(m[m.Count - 1].Groups[1].Value);
                        job.Bytes(total * pc / 100, total, false);
                    }
                }
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    string err = errors.ToString().Trim();
                    throw new Exception("The download could not be unpacked (7-Zip " + p.ExitCode + "): " +
                        (err.Length > 0 ? err.Split('\n')[0].Trim() : "unknown error") + ". Try again; if it persists, the file on the host is damaged.");
                }
            }
        }

        // the player's own files: excluded from release manifests and patches (make_patch.py EXCLUDE), so a full
        // download that happens to contain them (e.g. someone's replays) must not install them
        static readonly Regex[] Personal =
        {
            new Regex(@"^Replays\\", RegexOptions.IgnoreCase),
            new Regex(@"^FrameLogs\\", RegexOptions.IgnoreCase),
            new Regex(@"^Errors\\", RegexOptions.IgnoreCase),
            new Regex(@"(^|\\)crash[^\\]*\\", RegexOptions.IgnoreCase),
            new Regex(@"\.log$", RegexOptions.IgnoreCase),
            new Regex(@"output_log\.txt$", RegexOptions.IgnoreCase),
            new Regex(@"^BepInEx\\(config|cache)\\", RegexOptions.IgnoreCase),
            new Regex(@"\.before-[^\\]*\.dll$", RegexOptions.IgnoreCase),
            new Regex(@"(^|\\)Thumbs\.db$", RegexOptions.IgnoreCase),
            new Regex(@"^_installer_download\\", RegexOptions.IgnoreCase),
        };

        public static bool IsPersonal(string rel)
        {
            return Personal.Any(r => r.IsMatch(rel));
        }

        public const string FeedFile = "Escape from Tarkov Alpha_Data/StreamingAssets/Reconstruction/update_feed.txt";

        /// <summary>Builds shipped without a feed URL in update_feed.txt never check for updates: give them the
        /// installer's. A file that already names a URL is left alone.</summary>
        /// <summary>Puts the feed URL into the game's update_feed.txt: only when it names none, or (replace, the LAUNCHER
        /// tab's site change) in place of the one it names. Comment lines stay.</summary>
        public static void SetGameFeed(string folder, string feedUrl, Job job, bool replace = false)
        {
            if (string.IsNullOrEmpty(feedUrl) || !feedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            string path = Path.Combine(folder ?? "", FeedFile.Replace('/', '\\'));
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(Path.GetDirectoryName(path))) return;
                string[] lines = File.Exists(path) ? File.ReadAllLines(path) : new string[0];
                bool named = lines.Any(l => l.Trim().Length > 0 && !l.Trim().StartsWith("#"));
                if (named && !replace) return;
                if (named && lines.Any(l => l.Trim() == feedUrl)) return;
                lines = lines.Where(l => l.Trim().Length == 0 || l.Trim().StartsWith("#")).ToArray();
                File.WriteAllLines(path, lines.Concat(new[] { feedUrl }).ToArray(), new UTF8Encoding(false));
                job.Log("update_feed.txt: " + feedUrl);
            }
            catch (Exception ex)
            {
                job.Log("update_feed.txt not written: " + ex.Message);
            }
        }
    }
}
