# AoC cdnfix: make Daily Login Rewards claimable again

**Made by Nitrogen** (Age of Conan player). Shared here with their permission by Achreen / moonbah.
Background and the full investigation: [Funcom forum thread](https://forums.funcom.com/t/daily-login-rewards-cant-be-claimed-rewards-page-scripts-fail-in-the-in-game-browser-cause-found/304007).

## The problem

Age of Conan's in-game browser (Awesomium, from 2011) can only use very old secure connections. The Daily Rewards
page (`http://dailyrewards.ageofconan.com/`) loads the qTip script from `cdnjs.cloudflare.com`, which now refuses
those old connections. Without qTip, clicking **GET REWARD!** crashes the page's script (`qtip('hide')`) before it
sends `POST /claim`: the button disappears, but nothing is claimed.

## What this tool does

While it runs, it serves the missing script to the game's browser from your own PC (fetched once from cdnjs over
modern TLS). Then the page works, the claim is sent, and Funcom's server hands out the reward normally.
Details: [README_technical.md](README_technical.md).

## How to use

1. Download **`AoC cdnfix.exe`** from the [Releases page](../../releases).
2. **Close Age of Conan.**
3. Double-click `AoC cdnfix.exe` (normally, *not* as administrator). A black window says it's waiting for the game.
   If Windows shows "Windows protected your PC", click **More info → Run anyway** (it's an unsigned home-made program).
4. **Keep that window open**, start the game, open Daily Login Rewards and click **GET REWARD!**
5. Close the black window when you're done. Repeat next time you claim.

If the rewards page was already opened without the tool running, restart the game.

## ⚠️ Please read

- The tool uses **[Frida](https://frida.re) to inject into the running game client** (`AgeOfConan.exe` /
  `AgeOfConanDX10.exe`) and redirect the browser's request for the script. It changes **no files and no system
  settings** (no hosts file, no certificates) and does nothing to gameplay.
- Injecting into the client **may still be against the game's rules**. Use it at your own risk.
- The real fix has to come from Funcom: host the qTip script on `dailyrewards.ageofconan.com` itself.

## Checked before sharing

- Source code (`cdnfix.py`, `cdnfix_agent.js`) read in full: no password, cookie or token access, no unexpected
  network use. The only internet access is fetching files from `cdnjs.cloudflare.com` over HTTPS.
- The exe is a PyInstaller build of this same script (Python 3.13, with Frida and psutil); its script and agent match
  the source here.
- Windows Defender scan: no threats. While running, it only connected to `cdnjs.cloudflare.com` (104.17.24.14:443)
  and to the local PC.
- SHA-256 of `AoC cdnfix.exe`: `e2b63a7bcc1ff1044f2a564e252fd5a43f5da02c6b405fd3d2f672706ea88e4d`

## Running from source instead of the exe

```
python -m venv .venv
.venv\Scripts\python -m pip install frida psutil
.venv\Scripts\python cdnfix.py
```

`cache/` holds qTip 2.2.1 (MIT license, from cdnjs) so the first claim doesn't need to download it.
