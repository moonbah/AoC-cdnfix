# AoC cdnfix: make Daily Login Rewards claimable again

**The cause was found, and the first fix made, by Nitrogen** (Age of Conan player). Shared here with their
permission by Achreen / moonbah.
Background and the full investigation: [Funcom forum thread](https://forums.funcom.com/t/daily-login-rewards-cant-be-claimed-rewards-page-scripts-fail-in-the-in-game-browser-cause-found/304007).

## The problem

Age of Conan's in-game browser (Awesomium, from 2011) can only use very old secure connections. The Daily Rewards
page (`http://dailyrewards.ageofconan.com/`) loads the qTip script from `cdnjs.cloudflare.com`, which now refuses
those old connections. Without qTip, clicking **GET REWARD!** crashes the page's script (`qtip('hide')`) before it
sends `POST /claim`: the button disappears, but nothing is claimed.

## Two fixes in this repo

| | **AoC Rewards Fix** (new) | **AoC cdnfix** (Nitrogen's original) |
|---|---|---|
| How | starts the game with an option of its own built-in browser | injects into the running game with Frida |
| Touches the game client | no | yes (in memory only) |
| Several clients (multiboxing) | yes, run it once per client | yes, attaches to every client |
| Game must be started | through the tool | any way, but after the tool |

Both hand the game the same missing script from your own PC. Neither changes game files or system settings.

## Option 1: AoC Rewards Fix (no injection)

Built by Achreen / moonbah with Claude, **on top of Nitrogen's investigation**: Nitrogen found the cause and made
the first working fix (Option 2 below). This version only changes *how* the script reaches the game.
Thanks to **Strelzia** for the idea of making it work for multiboxers (many clients, no restarts), which is what
led to this version.

The game's browser (Awesomium 1.6) accepts the old Chromium option `--host-resolver-rules` on the game's command
line. `AoC Rewards Fix.exe` runs a tiny web server on your PC only (`127.0.0.1:8777`) and starts the game with
`--host-resolver-rules="MAP cdnjs.cloudflare.com 127.0.0.1:8777"`, so the browser asks that server for the script.
The server downloads files from `cdnjs.cloudflare.com` over HTTPS the first time and keeps them in a `cache` folder.
It covers every page that loads from cdnjs (Daily Rewards and the Item Shop).

### How to use

1. Download **`AoC.Rewards.Fix.exe`** from the [Releases page](../../releases) (release "AoC Rewards Fix").
2. Put it in your Age of Conan folder, next to `AgeOfConan.exe`.
   (Or keep it anywhere and put a `game_path.txt` next to it containing the path of your game folder.)
3. **Close Age of Conan**, then double-click the exe. A black window opens and starts the game.
   If Windows shows "Windows protected your PC", click **More info → Run anyway** (unsigned home-made program).
4. Keep the black window open (minimised is fine). It closes by itself after the last client is closed.
5. **More clients:** double-click the exe again for each one. The first window serves all of them.

For the DX10 client, start it with `dx10` added (for example a shortcut with ` dx10` at the end of its Target).

### Please read

- The game is started **without the patcher**. When the game has an update, run the normal patcher once.
- Only clients started through the exe get the fix. A client started from Steam or the patcher does not.
- Tested on one PC (Windows 11, `AgeOfConan.exe`): the reward was claimed. Several clients at once and DX10 are
  not confirmed yet. It is unofficial; use it at your own risk.
- Source: [`rewards-fix/AoCRewardsFix.cs`](rewards-fix/AoCRewardsFix.cs), one file. The exe is built from it with
  the C# compiler that ships with Windows (the command is at the top of the file). It needs .NET Framework 4,
  which Windows 10 and 11 already have.
- SHA-256 of the released exe: `6497FCA96200ACAF27F5998E5618F8400E70DA9E63E1E816ABF6DE567D1D2DEA`

## Option 2: AoC cdnfix by Nitrogen (the original fix)

### What this tool does

While it runs, it serves the missing script to the game's browser from your own PC (fetched once from cdnjs over
modern TLS). Then the page works, the claim is sent, and Funcom's server hands out the reward normally.
Details: [README_technical.md](README_technical.md).

### How to use

1. Download **`AoC.cdnfix.exe`** from the [Releases page](../../releases) (release "AoC cdnfix v1.0").
2. **Close Age of Conan.**
3. Double-click `AoC cdnfix.exe` (normally, *not* as administrator). A black window says it's waiting for the game.
   If Windows shows "Windows protected your PC", click **More info → Run anyway** (it's an unsigned home-made program).
4. **Keep that window open**, start the game, open Daily Login Rewards and click **GET REWARD!**
5. Close the black window when you're done. Repeat next time you claim.

If the rewards page was already opened without the tool running, restart the game.

### ⚠️ Please read

- The tool uses **[Frida](https://frida.re) to inject into the running game client** (`AgeOfConan.exe` /
  `AgeOfConanDX10.exe`) and redirect the browser's request for the script. It changes **no files and no system
  settings** (no hosts file, no certificates) and does nothing to gameplay.
- Injecting into the client **may still be against the game's rules**. Use it at your own risk.
- The real fix has to come from Funcom: host the qTip script on `dailyrewards.ageofconan.com` itself.

### Checked before sharing

- Source code (`cdnfix.py`, `cdnfix_agent.js`) read in full: no password, cookie or token access, no unexpected
  network use. The only internet access is fetching files from `cdnjs.cloudflare.com` over HTTPS.
- The exe is a PyInstaller build of this same script (Python 3.13, with Frida and psutil); its script and agent match
  the source here.
- Windows Defender scan: no threats. While running, it only connected to `cdnjs.cloudflare.com` (104.17.24.14:443)
  and to the local PC.
- SHA-256 of `AoC cdnfix.exe`: `e2b63a7bcc1ff1044f2a564e252fd5a43f5da02c6b405fd3d2f672706ea88e4d`

### Running from source instead of the exe

```
python -m venv .venv
.venv\Scripts\python -m pip install frida psutil
.venv\Scripts\python cdnfix.py
```

`cache/` holds qTip 2.2.1 (MIT license, from cdnjs) so the first claim doesn't need to download it.
