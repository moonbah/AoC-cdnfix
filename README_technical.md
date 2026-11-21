# cdnfix: scripts for the in-game browser that old TLS can no longer get

Age of Conan's in-game browser is Awesomium 1.6 (Chromium of 2011), which speaks TLS 1.0 at most. Hosts that
now require TLS 1.2 or newer can no longer serve it, and pages that load libraries from them break. The first
known case is the **Daily Rewards** window (`http://dailyrewards.ageofconan.com/`):

* the page loads qTip from `http://cdnjs.cloudflare.com/ajax/libs/qtip2/2.2.1/jquery.qtip.min.js`;
  cdnjs answers plain HTTP with a redirect to HTTPS, and the TLS 1.0 handshake fails
  (`chrome_debug.log`: `handshake with server cdnjs.cloudflare.com:443 failed; NSS error code -12190`)
* the GET REWARD handler removes the button, then calls `$('*').qtip('hide')`, which throws
  (`Uncaught TypeError: Object [object Object] has no method 'qtip'`) before it sends `POST /claim`: the button
  disappears and nothing is claimed

## What it does

`cdnfix.py` attaches to the running game with Frida and loads `cdnfix_agent.js`. Inside the game:

* `getaddrinfo` for a listed host (default `cdnjs.cloudflare.com`) returns a marker address (127.0.0.77)
* `connect` to the marker on port 80 goes to a small HTTP server the agent runs on 127.0.0.1
* that server answers with the requested file, which `cdnfix.py` fetches once over HTTPS (modern TLS, Python)
  and keeps in `tools/cdnfix/cache/`

So every library under the listed hosts works, not only qTip, as long as the page asks for it over plain HTTP
(as the Daily Rewards page does). Nothing else the game resolves or connects to is touched, and nothing on the
system changes (no hosts file, no certificates). The fix lasts while `cdnfix.py` runs.

## Use

```
python -m venv .venv
.venv\Scripts\python -m pip install frida psutil
.venv\Scripts\python tools\cdnfix\cdnfix.py [--host cdnjs.cloudflare.com ...]
```

Start it before the game, or at least before the game first opens the Daily Rewards window: once the browser has
seen cdnjs's `301 Moved Permanently` to HTTPS it keeps that redirect in memory and goes straight to HTTPS for the
rest of the game session, so the fix only takes effect after a game restart. It attaches to every
`AgeOfConanDX10.exe` / `AgeOfConan.exe` as they start, and can run next to AoC Unfuck and aocmon.

Tested 2026-11-21: with cdnfix running from game start, the page got qTip, `POST /claim` went out and the server
answered with the day's reward.

For another tool that already injects Frida agents: load `cdnfix_agent.js`, call `rpc.exports.start(hosts)`, and
answer its `{type: 'fetch', id, host, path}` messages with `post({type: 'fetch:' + id, status, ctype}, body)`
(see the protocol at the top of the agent). The bodies could also be bundled instead of fetched.

Limit: files the page asks for over `https://` from such a host cannot be served this way (that would need a
certificate the browser trusts); the Daily Rewards page only uses plain-HTTP URLs for them.
