"""Make the Age of Conan in-game browser load scripts again from hosts that dropped old TLS (e.g. qTip on
cdnjs.cloudflare.com, which breaks the Daily Rewards claim button).

    python tools/cdnfix/cdnfix.py [--host cdnjs.cloudflare.com ...] [--cache DIR]

Runs in the background next to the game, attaches to every AgeOfConan*.exe as it appears (Frida) and loads
cdnfix_agent.js. Plain-HTTP requests from the game for the listed hosts are answered from a local cache; files not
cached yet are fetched once over HTTPS (modern TLS) by this script. Only files under the listed hosts are served;
nothing else the game does is touched. Ctrl+C to stop (the game keeps running; the fix ends with this script).
"""
import argparse
import os
import re
import threading
import time
import urllib.error
import urllib.request

import frida
import psutil

HERE = os.path.dirname(os.path.abspath(__file__))
AGENT_JS = os.path.join(HERE, "cdnfix_agent.js")
GAME_EXES = {"ageofconandx10.exe", "ageofconan.exe"}
DEFAULT_HOSTS = ["cdnjs.cloudflare.com"]
CTYPES = {".js": "application/javascript", ".css": "text/css", ".png": "image/png", ".gif": "image/gif",
          ".svg": "image/svg+xml", ".woff": "font/woff", ".woff2": "font/woff2", ".ttf": "font/ttf",
          ".json": "application/json"}


def say(text):
    print(f"{time.strftime('%H:%M:%S')}  {text}", flush=True)


class Fetcher:
    """Files by host and path, cached on disk; fetched over HTTPS the first time."""

    def __init__(self, cache):
        self.cache = cache
        self.lock = threading.Lock()

    def path_for(self, host, path):
        clean = re.sub(r"[?#].*", "", path).lstrip("/")
        parts = [p for p in clean.split("/") if p not in ("", ".", "..")]
        return os.path.join(self.cache, host, *parts) if parts else None

    def get(self, host, path):
        local = self.path_for(host, path)
        if local is None:
            return 404, None, None
        ctype = CTYPES.get(os.path.splitext(local)[1].lower(), "application/octet-stream")
        with self.lock:
            if os.path.isfile(local):
                with open(local, "rb") as f:
                    return 200, ctype, f.read()
        url = f"https://{host}{path}"
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "cdnfix"}), timeout=20) as r:
                body = r.read()
                ctype = r.headers.get_content_type() or ctype
        except urllib.error.HTTPError as e:
            say(f"fetch {url}: HTTP {e.code}")
            return e.code, None, None
        except Exception as e:
            say(f"fetch {url}: {e}")
            return 502, None, None
        with self.lock:
            os.makedirs(os.path.dirname(local), exist_ok=True)
            with open(local, "wb") as f:
                f.write(body)
        say(f"fetched {url} ({len(body)} B) into the cache")
        return 200, ctype, body


class Game:
    def __init__(self, pid, hosts, fetcher):
        self.pid, self.fetcher = pid, fetcher
        self.session = frida.attach(pid)
        self.session.on("detached", lambda reason, crash: say(f"game {pid} detached ({reason})"))
        with open(AGENT_JS, encoding="utf-8") as f:
            self.script = self.session.create_script(f.read())
        self.script.on("message", self.on_message)
        self.script.load()
        port = self.script.exports_sync.start(hosts)
        say(f"attached to game {pid}; serving {', '.join(hosts)} on 127.0.0.1:{port} inside it")

    def on_message(self, msg, data):
        if msg["type"] == "error":
            say(f"agent error: {msg.get('description')}")
            return
        p = msg["payload"]
        if p.get("type") == "log":
            say(f"[{self.pid}] {p['text']}")
        elif p.get("type") == "fetch":
            threading.Thread(target=self.answer, args=(p,), daemon=True).start()

    def answer(self, p):
        status, ctype, body = self.fetcher.get(p["host"], p["path"])
        try:
            self.script.post({"type": f"fetch:{p['id']}", "status": status, "ctype": ctype}, body)
        except frida.InvalidOperationError:
            pass                          # the game went away meanwhile


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", action="append", help=f"host to take over (repeatable; default {DEFAULT_HOSTS})")
    ap.add_argument("--cache", default=os.path.join(HERE, "cache"), help="file cache (default tools/cdnfix/cache)")
    a = ap.parse_args()
    hosts = a.host or DEFAULT_HOSTS
    fetcher = Fetcher(a.cache)
    games = {}
    say("waiting for the game (Ctrl+C to stop)")
    try:
        while True:
            for proc in psutil.process_iter(["name", "pid"]):
                if (proc.info["name"] or "").lower() in GAME_EXES and proc.pid not in games:
                    try:
                        games[proc.pid] = Game(proc.pid, hosts, fetcher)
                    except Exception as e:
                        say(f"attach to {proc.pid} failed: {e}")
                        games[proc.pid] = None
            for pid in [p for p in games if not psutil.pid_exists(p)]:
                del games[pid]
            time.sleep(2)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
