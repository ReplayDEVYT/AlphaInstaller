"""End-to-end test of the installer (Build/InstallerTest), outside the game:
synthetic old/new game builds -> make_patch.py full + patch + release with per-mirror links (NA dead, EU on a local
host that supports Range requests), a server package, news -> the installer's --cli mode:
fresh install (old full + patch to the new build), dead mirror, update, reinstall keeping player files, verify,
resume of a cut-off download, server install keeping profiles/config and setting gameServerPath. Then screenshots of
the three tabs (Build/InstallerTest/shot_*.png).

    python Tools/installer/test_installer.py
"""
import functools
import hashlib
import http.server
import json
import os
import shutil
import subprocess
import sys
import threading
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Build", "InstallerTest")
PATCH = os.path.join(ROOT, "Tools", "patch", "make_patch.py")
EXE = os.path.join(ROOT, "Build", "Installer", "Escape from Tarkov Alpha Installer.exe")
PORT = 8775
failures = []
requests = []


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else "  " + str(detail)[-1500:]))
    if not ok:
        failures.append(name)


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(data)


def tree(root, skip=("release_manifest.json",)):
    result = {}
    for dirpath, _dirs, names in os.walk(root):
        for name in names:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            if rel in skip or rel.startswith("_installer_download"):
                continue
            result[rel] = hashlib.sha256(open(full, "rb").read()).hexdigest()
    return result


def run(*args):
    r = subprocess.run(list(args), capture_output=True, text=True)
    return r


class RangeHandler(http.server.SimpleHTTPRequestHandler):
    """Static files with single Range requests (206), like real hosts; logs the requests."""

    def log_message(self, *a):
        pass

    def send_head(self):
        requests.append((self.path, self.headers.get("Range")))
        path = self.translate_path(self.path.split("?")[0])
        rng = self.headers.get("Range")
        if not rng or not os.path.isfile(path):
            return super().send_head()
        size = os.path.getsize(path)
        start = int(rng.split("=")[1].split("-")[0])
        if start >= size:
            self.send_error(416)
            return None
        f = open(path, "rb")
        f.seek(start)
        self.send_response(206)
        self.send_header("Content-Type", "application/zip")
        self.send_header("Content-Range", "bytes %d-%d/%d" % (start, size - 1, size))
        self.send_header("Content-Length", str(size - start))
        self.end_headers()
        return f


def serve(directory, port):
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), functools.partial(RangeHandler, directory=directory))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def cli(*args):
    log = os.path.join(OUT, "cli.log")
    if os.path.exists(log):
        os.remove(log)
    r = run(EXE, "--feed", feed_url, "--log", log, "--cli", *args)
    text = open(log, encoding="utf-8").read() if os.path.exists(log) else ""
    return r.returncode, text


shutil.rmtree(OUT, ignore_errors=True)
old, new, site, host, patches = (os.path.join(OUT, n) for n in ("old", "new", "site", "host", "patches"))
rnd = os.urandom(700 * 1024)
write(os.path.join(old, "Escape from Tarkov Alpha.exe"), b"MZ old exe")
write(os.path.join(old, "Escape from Tarkov Alpha_Data", "Managed", "Assembly-CSharp.dll"), rnd)
write(os.path.join(old, "Escape from Tarkov Alpha_Data", "sharedassets0.assets"), os.urandom(400 * 1024))
write(os.path.join(old, "client.config.json"), b'{"BackendUrl":"http://127.0.0.1:6969"}')
write(os.path.join(old, "gone.txt"), b"removed in the new build")
shutil.copytree(old, new)
write(os.path.join(new, "Escape from Tarkov Alpha_Data", "Managed", "Assembly-CSharp.dll"), rnd[:300000] + b"NEW" * 5000 + rnd[300000:])
write(os.path.join(new, "Escape from Tarkov Alpha_Data", "StreamingAssets", "added.bin"), os.urandom(60000))
os.remove(os.path.join(new, "gone.txt"))

py = [sys.executable, "-I", PATCH]
host_url = "http://127.0.0.1:%d/" % (PORT + 1)
os.makedirs(host, exist_ok=True)
r = run(*py, "full", old, patches)
full_zip = [f for f in os.listdir(patches) if f.startswith("game_")][0]
check("full zip made", r.returncode == 0, r.stdout + r.stderr)
with zipfile.ZipFile(os.path.join(patches, full_zip)) as z:
    names = z.namelist()
check("full zip has manifest + client.config.json at the root", "release_manifest.json" in names and "client.config.json" in names, names)
shutil.copy(os.path.join(patches, full_zip), os.path.join(host, "dl1_game.zip"))
r = run(*py, "release", site, old, "--name", "0.1.3", "--notes", "First build",
        "--mirror", "NA", "North America", "-", "--mirror", "EU", "Europe", "-",
        "--full", os.path.join(patches, full_zip),
        "--full-url", "NA=http://127.0.0.1:1/game.zip", "--full-url", "EU=" + host_url + "dl1_game.zip")
check("release old with full links", r.returncode == 0 and not os.path.exists(os.path.join(site, "files", full_zip)), r.stdout + r.stderr)
r = run(*py, "patch", old, new, patches)
patch_zip = [f for f in os.listdir(patches) if f.startswith("patch_")][0]
shutil.copy(os.path.join(patches, patch_zip), os.path.join(host, "dl2_patch.zip"))
r = run(*py, "release", site, new, "--name", "0.1.4", "--notes", "Trader sales\\nRaid fixes", "--patch", os.path.join(patches, patch_zip),
        "--patch-url", "NA=http://127.0.0.1:1/patch.zip", "--patch-url", "EU=" + host_url + "dl2_patch.zip")
check("release new with patch links", r.returncode == 0, r.stdout + r.stderr)

# server package: the real one's layout (ConsoleApp.exe at the root, configs base only)
srv = os.path.join(OUT, "server_pkg")
write(os.path.join(srv, "ConsoleApp.exe"), b"MZ server")
write(os.path.join(srv, "Files", "configs", "server_base.json"), json.dumps({"ip": "127.0.0.1", "port": 6969, "matchmaking": {"portStart": 7777, "portEnd": 7796, "gameServerPath": ""}}, indent=1).encode())
write(os.path.join(srv, "Files", "traders", "a.json"), b"{}")
write(os.path.join(srv, "_References", "Assembly-CSharp.dll"), b"ref")
srv_zip = os.path.join(OUT, "server_test.zip")
shutil.make_archive(srv_zip[:-4], "zip", srv)
shutil.copy(srv_zip, os.path.join(host, "dl3_server.zip"))
# the server comes from a GitHub repository's latest release; a static copy of the API answer stands in for GitHub
r = run(*py, "server", site, "--github", "someone/eft-server")
check("server in the feed (github)", r.returncode == 0, r.stdout + r.stderr)
api = os.path.join(host, "api", "repos", "someone", "eft-server", "releases")
write(os.path.join(api, "latest"), json.dumps({
    "tag_name": "v1.0", "name": "Server 1.0", "published_at": "2026-11-16T10:00:00Z", "body": "First server",
    "assets": [{"name": "notes.txt", "browser_download_url": host_url + "x.txt", "size": 3},
               {"name": "server_20261116.zip", "browser_download_url": host_url + "dl3_server.zip", "size": os.path.getsize(srv_zip),
                "digest": "sha256:" + hashlib.sha256(open(srv_zip, "rb").read()).hexdigest()}]}).encode())
feed_json = json.load(open(os.path.join(site, "updates.json"), encoding="utf-8"))
check("feed server entry", feed_json.get("server") == {"github": "someone/eft-server"}, feed_json.get("server"))
feed_json["server"]["githubApi"] = host_url + "api"
json.dump(feed_json, open(os.path.join(site, "updates.json"), "w", encoding="utf-8"), indent=1)
# a second feed whose repository has no release yet
feed_json["server"]["github"] = "someone/nothing-yet"
json.dump(feed_json, open(os.path.join(site, "updates_norelease.json"), "w", encoding="utf-8"), indent=1)
r = run(*py, "news", site, "--title", "Wipe on Friday", "--text", "All profiles reset.\\nNew traders.", "--url", "https://example.com/wipe")
r = run(*py, "news", site, "--title", "Welcome", "--text", "First post.", "--date", "2026-11-10")
news = json.load(open(os.path.join(site, "news.json"), encoding="utf-8"))
check("news: two posts, newest first", [n["title"] for n in news["news"]] == ["Welcome", "Wipe on Friday"], news)
feed = json.load(open(os.path.join(site, "updates.json"), encoding="utf-8"))
old_v = json.load(open(os.path.join(old, "release_manifest.json")))["version"]
new_v = json.load(open(os.path.join(new, "release_manifest.json")))["version"]
rel_old = [x for x in feed["releases"] if x["version"] == old_v][0]
check("feed: full download sha/size", rel_old.get("fullSha256") and rel_old.get("fullSize") and rel_old.get("fullUnpackedSize"), rel_old)

site_server = serve(site, PORT)
host_server = serve(host, PORT + 1)
feed_url = "http://127.0.0.1:%d/updates.json" % PORT
try:
    game = os.path.join(OUT, "game")
    code, log = cli("install", "--dir", os.path.join(OUT, "game_na"), "--mirror", "NA")
    check("NA (dead host): install fails", code == 1 and "FAILED" in log, log)
    check("NA: nothing installed", not os.path.exists(os.path.join(OUT, "game_na", "Escape from Tarkov Alpha.exe")))

    # resume: half of the full zip already downloaded
    part_dir = os.path.join(game, "_installer_download")
    os.makedirs(part_dir)
    data = open(os.path.join(host, "dl1_game.zip"), "rb").read()
    open(os.path.join(part_dir, "game_%s.download.partial" % old_v), "wb").write(data[:len(data) // 2])
    requests.clear()
    code, log = cli("install", "--dir", game, "--mirror", "EU")
    check("EU: fresh install ok", code == 0 and "DONE: version " + new_v in log, log)
    check("resume: Range request sent", any(p.startswith("/dl1_game.zip") and rng for p, rng in requests), requests)
    check("install = old full + patch: equals the new build", tree(game) == tree(new), (sorted(tree(game)), sorted(tree(new))))
    check("download folder removed", not os.path.exists(part_dir))

    code, log = cli("update", "--dir", game, "--mirror", "EU")
    check("update when current: up to date", code == 0 and "up to date" in log, log)

    old_game = os.path.join(OUT, "game_old")
    shutil.copytree(old, old_game)
    code, log = cli("update", "--dir", old_game, "--mirror", "EU")
    check("update old -> new", code == 0 and tree(old_game) == tree(new), log)

    # reinstall over a damaged old game: player files and the player's client.config.json stay
    re_game = os.path.join(OUT, "game_re")
    shutil.copytree(old, re_game)
    write(os.path.join(re_game, "client.config.json"), b'{"BackendUrl":"http://my.server:6969"}')
    write(os.path.join(re_game, "Replays", "raid.eftreplay"), b"replay")
    write(os.path.join(re_game, "Escape from Tarkov Alpha_Data", "sharedassets0.assets"), b"broken")
    code, log = cli("reinstall", "--dir", re_game, "--mirror", "EU")
    t = tree(re_game)
    expect = tree(new)
    check("reinstall ok", code == 0, log)
    check("reinstall: game files equal the new build", all(t.get(k) == v for k, v in expect.items() if k != "client.config.json"), log)
    check("reinstall: removed file of the old version gone", "gone.txt" not in t)
    check("reinstall: replay kept", "Replays/raid.eftreplay" in t)
    check("reinstall: player's client.config.json kept", b"my.server" in open(os.path.join(re_game, "client.config.json"), "rb").read())

    code, log = cli("verify", "--dir", game)
    check("verify clean: 0 bad", code == 0 and "VERIFY: 0 bad" in log, log)
    write(os.path.join(game, "Escape from Tarkov Alpha_Data", "StreamingAssets", "added.bin"), b"x")
    os.remove(os.path.join(game, "Escape from Tarkov Alpha.exe"))
    code, log = cli("verify", "--dir", game)
    check("verify damaged: 2 bad", code == 3 and "VERIFY: 2 bad" in log, log)

    server = os.path.join(OUT, "server")
    write(os.path.join(server, "profiles", "abc", "character.json"), b"mine")
    write(os.path.join(server, "Files", "configs", "server.json"), json.dumps({"ip": "26.1.2.3", "port": 6969, "matchmaking": {"mode": "auto", "gameServerPath": "C:/old/path.exe"}}, indent=4).encode())
    code, log = cli("server", "--dir", server, "--mirror", "EU", "--game", re_game)
    check("server install ok", code == 0, log)
    check("server: files installed", os.path.exists(os.path.join(server, "ConsoleApp.exe")) and os.path.exists(os.path.join(server, "_References", "Assembly-CSharp.dll")))
    check("server: profile kept", open(os.path.join(server, "profiles", "abc", "character.json"), "rb").read() == b"mine")
    cfg = json.load(open(os.path.join(server, "Files", "configs", "server.json"), encoding="utf-8"))
    check("server: own config kept, gameServerPath = installed game", cfg["ip"] == "26.1.2.3" and cfg["matchmaking"]["gameServerPath"] == os.path.join(re_game, "Escape from Tarkov Alpha.exe").replace("\\", "/"), cfg)
    check("server: version file = release tag", open(os.path.join(server, "server_version.txt")).read().strip() == "v1.0")
    check("server: checked against the GitHub digest", "Checking the server" in log, log)
    code, log = cli("server", "--dir", os.path.join(OUT, "server_na"), "--mirror", "NA")
    check("server: download location ignored (NA has no game links)", code == 0, log)
    feed_url_saved = feed_url
    feed_url = "http://127.0.0.1:%d/updates_norelease.json" % PORT
    code, log = cli("server", "--dir", os.path.join(OUT, "server_none"))
    feed_url = feed_url_saved
    check("server: repository without a release fails clearly", code == 1 and "no published server release" in log, log)

    for tab, name in ((0, "news"), (1, "game"), (2, "server")):
        png = os.path.join(OUT, "shot_%s.png" % name)
        r = run(EXE, "--feed", feed_url, "--screenshot", png, "--tab", str(tab), "--dir", old_game if tab == 1 else game, "--server-dir", server)
        check("screenshot " + name, os.path.exists(png), r.stdout + r.stderr)
finally:
    site_server.shutdown()
    host_server.shutdown()

print("FAILURES: %d" % len(failures))
sys.exit(1 if failures else 0)
