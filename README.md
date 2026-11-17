# Installer

`Build\Installer\Escape from Tarkov Alpha Installer.exe` (the launcher): one file, .NET Framework 4.8 (part of
Windows 10/11). Tabs: **NEWS** (`news.json`), **GAME** (install, update, reinstall, verify, play; NA/EU download
location), **SERVER** (install/update the server from GitHub, start it, **CONFIG** window), **LAUNCHER** (update site,
launcher self-update). The look is the game's: Bender font and the project's UI sprites (`Assets/`, cropped from
`Unity/Assets` by `extract_ui_sprites.py`).

## Build

```powershell
powershell -ExecutionPolicy Bypass -File Tools\installer\AlphaInstaller\build_installer.ps1 [-Feed https://<site>/updates.json] [-News https://<site>/news.json] [-Version 1.2] [-LauncherRepo owner/repo] [-LauncherApi <API URL>]
```

Without `-Feed` it takes the game's own feed URL from `Unity\Assets\StreamingAssets\Reconstruction\update_feed.txt`.
`-News` defaults to the feed's `news` field / `news.json` next to `updates.json`. `-Version` defaults to
`version.txt`; `-LauncherRepo` (default `mizmii/AlphaInstaller`) and `-LauncherApi` (default `https://codeberg.org/api/v1`; GitHub:
`https://api.github.com`) are where launcher updates are looked for.
The site can be changed later without a rebuild: `installer.cfg` next to the exe (`feed=<URL>` / `news=<URL>`), or by
the player on the LAUNCHER tab (stored in `HKCU\Software\EFTAlphaReconstruction\Installer`, `Feed`/`News`; wins over
installer.cfg; USE DEFAULT clears it). With "Also use this site for the installed game's UPDATES button" the game's
`StreamingAssets/Reconstruction/update_feed.txt` is pointed at the new site too.

## LAUNCHER tab: self-update

The launcher shows its version (`version.txt`) and the latest published one: the feed's `"launcher"` entry when it
has one (`make_patch.py launcher`), else the latest release of `-LauncherRepo` on `-LauncherApi` (tag = version, release text =
notes, the `.exe` asset, GitHub's digest = checksum). UPDATE downloads it next to the running exe (`.new`), checks
the checksum and that it is a program, renames the running exe to `.old` (Windows allows renaming a running exe, not
overwriting it), moves the new one into its place, starts it with `--updated-from <old version>` and closes; the new
one deletes the `.old`. A launcher in a folder it cannot write to says where to download the update instead.

Publishing a launcher update: raise `version.txt`, build, attach the exe to a new GitHub release of the repository
with the version as tag (e.g. `1.2`). Launchers before 1.1 have no self-update: their players download 1.1 once.

```powershell
python -I Tools\patch\make_patch.py launcher HostSite\versions --github owner/repo         # another repository
python -I Tools\patch\make_patch.py launcher HostSite\versions <exe> --version 1.2 --url <link or ../download/x.exe>
python -I Tools\patch\make_patch.py launcher HostSite\versions --remove                    # back to the built-in repo
```

## SERVER tab: CONFIG

Edits `Files/configs/server.json` (created from `server_base.json` when missing; the previous file is kept as
`server.json.bak`): server address/port/name; the **match client**, i.e. the program that runs the raids
(`matchmaking.gameServerPath`): the installed game (kept in step with the GAME tab's folder on every start), another
exe, or none; its extra arguments (`gameServerArgs`); match mode (auto/dedicated/host), raid address (`matchIp`, empty
= `servers[0].ip`), host public address (`hostPublicIp`), host port and the raid port range (`portStart`-`portEnd`).
Values are checked (addresses, port ranges, server port outside the raid ports, the exe exists). Every other key of
the file stays, in its order. OPEN SERVER.JSON opens the file in Notepad for the rest.

## Publishing (per release)

```powershell
# game: full zip (first installs) and patch (updates) from the new player build
python -I Tools\patch\make_patch.py full  Build\Player Build\Patches
python -I Tools\patch\make_patch.py patch <last release snapshot> Build\Player Build\Patches
# upload both zips to NA and EU, then:
python -I Tools\patch\make_patch.py release Build\UpdateSite Build\Player --name 0.1.4 --notes "..." `
    --full Build\Patches\game_<id>.zip --full-url NA=<link> --full-url EU=<link> `
    --patch Build\Patches\patch_<a>_to_<b>.zip --patch-url NA=<link> --patch-url EU=<link>
# news
python -I Tools\patch\make_patch.py news Build\UpdateSite --title "..." --text "line 1\nline 2" [--url <link>]
```

Upload `updates.json` and `news.json` to the update site. `--full` with links only reads the local zip for its
checksum and sizes. A release without a full zip is fine: fresh installs take the newest full download and apply the
patches after it.

## The live site: HostSite/

`HostSite/` is what gets uploaded to the website: `versions/updates.json` (the feed; its URL goes into the installer
and the game), `news/news.json` (found through the feed's `"news": "../news/news.json"`), `_headers` (JSON content
type, CORS, no-cache). Game downloads are external links (buzzheavier, NA and EU), not files on the site.

- Full downloads may be **zip or RAR** (WinRAR 7 / RAR5 included): the installer carries 7-Zip 26.01 (`7zip/`,
  `7z.exe` + `7z.dll`, LGPL with the unRAR restriction; `License.txt` is unpacked next to it) and decides by content.
  Personal files in a full download (Replays, logs, BepInEx config, crash folders) are not installed.
- Games installed or updated by the installer get the feed URL in `StreamingAssets/Reconstruction/update_feed.txt`
  when that file names none (Verify ignores that file).
- `real_install_test.py NA|EU <folder>` installs the real published game from HostSite served locally and verifies it.

## Server (GitHub releases)

Packaging: double-click `Package server.bat` (project root) -> `Build\ServerPackage\EFT-Alpha-Server_<date>.zip`,
shown in Explorer, with the `make_patch.py server ... --url <link>` line to run once it is uploaded.
`smoke_server.ps1 <zip>` starts a package on port 6979 and asks it for its info.

Once: `python -I Tools\patch\make_patch.py server Build\UpdateSite --github <owner>/<repo>` (upload `updates.json`).
Each server release: `powershell -ExecutionPolicy Bypass -File Tools\installer\package_server.ps1` makes
`Build\ServerPackage\server_<date>.zip` (self-contained .NET, no runtime needed; no profiles/logs). Attach it to a new
GitHub release of that repository. The installer reads the latest release: tag = version, release text = notes, the
`.zip` asset (one with "server" in its name first), GitHub's SHA-256 digest = checksum. Server updates keep
`profiles/`, `logs/`, `TraderStock.json` and the `Files/configs/*.json` the server writes.

## Test

`python Tools\installer\test_installer.py`: synthetic builds, local NA (dead) / EU hosts with Range support, a stand-in
GitHub API; installs, resume, update, reinstall, verify, server; screenshots in `Build\InstallerTest\shot_*.png`.

Without the window: `--cli launcher-check` / `--cli launcher-update` (self-update), `--screenshot <png> --tab 3`
(LAUNCHER tab) and `--screenshot <png> --server-config` (the CONFIG window). Screenshots save the launcher's settings
to the registry: back up `HKCU\Software\EFTAlphaReconstruction\Installer` first on a player's machine.
