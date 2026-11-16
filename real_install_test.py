"""Installs the real published game with the installer's --cli, from HostSite served locally (so the real feed, the
real download links and the real RAR are used), then verifies it.

    python Tools/installer/real_install_test.py <NA|EU> <target folder> [--keep]

Needs about 3 GB (download) + 5.3 GB (game) free on the target's drive; the folder is deleted afterwards unless --keep.
"""
import functools
import http.server
import json
import os
import shutil
import subprocess
import sys
import threading
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "Build", "Installer", "Escape from Tarkov Alpha Installer.exe")
mirror, target = sys.argv[1], os.path.abspath(sys.argv[2])
keep = "--keep" in sys.argv
handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=os.path.join(ROOT, "HostSite"))
handler.log_message = lambda *a: None
server = http.server.ThreadingHTTPServer(("127.0.0.1", 8785), handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
feed = "http://127.0.0.1:8785/versions/updates.json"
log = target + ".log"
ok = True
try:
    started = time.time()
    r = subprocess.run([EXE, "--feed", feed, "--log", log, "--cli", "install", "--dir", target, "--mirror", mirror])
    print("install exit %d after %.0f s" % (r.returncode, time.time() - started))
    ok &= r.returncode == 0
    r = subprocess.run([EXE, "--feed", feed, "--log", log, "--cli", "verify", "--dir", target])
    print("verify exit %d" % r.returncode)
    ok &= r.returncode == 0
    replays = os.path.exists(os.path.join(target, "Replays"))
    print("personal replays installed: %s" % replays)
    ok &= not replays
    feed_txt = open(os.path.join(target, "Escape from Tarkov Alpha_Data", "StreamingAssets", "Reconstruction", "update_feed.txt")).read()
    print("update_feed.txt ends with the feed: %s" % feed_txt.strip().endswith(feed))
    ok &= feed_txt.strip().endswith(feed)
    version = json.load(open(os.path.join(target, "release_manifest.json")))["version"]
    print("installed version %s" % version)
finally:
    server.shutdown()
    print(open(log, encoding="utf-8").read()[-3000:] if os.path.exists(log) else "(no log)")
    if not keep:
        shutil.rmtree(target, ignore_errors=True)
print("RESULT: %s" % ("PASS" if ok else "FAIL"))
sys.exit(0 if ok else 1)
