// Frida agent for the Age of Conan client: serves plain-HTTP requests for selected hosts from a small HTTP server
// inside the game process, so the in-game browser (Awesomium 1.6, TLS 1.0 only) gets files that their real host
// now only hands out over TLS 1.2+ (it answers plain HTTP with a redirect to HTTPS).
//
// How: getaddrinfo() for a listed host returns a marker loopback address; connect() to that marker on port 80 is
// sent to the agent's listener on 127.0.0.1; the listener asks the host script (send/recv) for the file and writes
// it back as an HTTP response. Everything else the game resolves or connects to is left alone.
//
// Host script protocol:
//   rpc.exports.start(hosts)  -> listener port; hosts = list of host names to take over
//   agent -> host: { type: 'fetch', id, host, path }
//   host -> agent: post({ type: 'fetch:' + id, status, ctype }, body bytes)
//   agent -> host: { type: 'log', text }

'use strict';

const MARKER = '127.0.0.77';      // what a listed host resolves to inside the game
let hosts = new Set();
let listenPort = 0;
let nextId = 1;

const ws2 = Process.getModuleByName('ws2_32.dll');

function log(text) { send({ type: 'log', text: text }); }

function ascii(s) {
  const b = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) b[i] = s.charCodeAt(i) & 0xff;
  return b;
}

function concat(a, b) {
  const r = new Uint8Array(a.length + b.length);
  r.set(a, 0);
  r.set(b, a.length);
  return r;
}

// --- name resolution: listed hosts resolve to MARKER

// The game's browser uses the ANSI getaddrinfo; the wide one is hooked too for other callers.
const resolvers = [
  ['getaddrinfo', Memory.allocAnsiString(MARKER), p => p.readCString()],
  ['GetAddrInfoW', Memory.allocUtf16String(MARKER), p => p.readUtf16String()],
];

for (const [fn, markerStr, read] of resolvers) {
  Interceptor.attach(ws2.getExportByName(fn), {
    onEnter(args) {
      if (args[0].isNull()) return;
      const name = read(args[0]);
      if (name !== null && hosts.has(name.toLowerCase())) {
        args[0] = markerStr;
        log('resolve ' + name + ' -> ' + MARKER);
      }
    },
  });
}

// --- connect: MARKER:80 goes to the listener

const markerIp = MARKER.split('.').map(Number);

Interceptor.attach(ws2.getExportByName('connect'), {
  onEnter(args) {
    const sa = args[1];
    if (sa.isNull() || sa.readU16() !== 2) return;                       // AF_INET only
    const port = (sa.add(2).readU8() << 8) | sa.add(3).readU8();
    for (let i = 0; i < 4; i++) if (sa.add(4 + i).readU8() !== markerIp[i]) return;
    if (port !== 80 || listenPort === 0) {
      log('connect to ' + MARKER + ':' + port + ' left alone (only port 80 is served)');
      return;
    }
    const n = Memory.alloc(16);
    n.writeU16(2);
    n.add(2).writeU8(listenPort >> 8);
    n.add(3).writeU8(listenPort & 0xff);
    [127, 0, 0, 1].forEach((v, i) => n.add(4 + i).writeU8(v));
    this.sa = n;                                                          // keep it alive for the call
    args[1] = n;
  },
});

// --- the listener

function fetchFromHost(host, path) {
  return new Promise(resolve => {
    const id = nextId++;
    recv('fetch:' + id, (msg, data) => resolve({ status: msg.status, ctype: msg.ctype, body: data }));
    send({ type: 'fetch', id: id, host: host, path: path });
  });
}

async function readRequest(conn) {
  let buf = '';
  while (buf.indexOf('\r\n\r\n') < 0) {
    const chunk = await conn.input.read(4096);
    if (chunk.byteLength === 0) return null;
    const u = new Uint8Array(chunk);
    for (let i = 0; i < u.length; i++) buf += String.fromCharCode(u[i]);
    if (buf.length > 65536) return null;
  }
  const lines = buf.split('\r\n');
  const m = /^(GET|HEAD) (\S+) HTTP\/1\.[01]$/.exec(lines[0]);
  if (!m) return { bad: lines[0] };
  let host = '';
  for (const l of lines.slice(1)) {
    const h = /^host:\s*([^:\s]+)/i.exec(l);
    if (h) host = h[1].toLowerCase();
  }
  return { method: m[1], path: m[2], host: host };
}

async function serve(conn) {
  try {
    const req = await readRequest(conn);
    if (req === null) return;
    let status = 400, ctype = 'text/plain', body = null;
    if (req.bad === undefined && hosts.has(req.host)) {
      const r = await fetchFromHost(req.host, req.path);
      status = r.status;
      ctype = r.ctype || 'application/octet-stream';
      body = r.body;
      log(req.method + ' http://' + req.host + req.path + ' -> ' + status + ' (' + (body ? body.byteLength : 0) + ' B)');
    } else {
      log('refused request ' + JSON.stringify(req));
    }
    const bytes = body ? new Uint8Array(body) : new Uint8Array(0);
    const reason = { 200: 'OK', 400: 'Bad Request', 404: 'Not Found', 502: 'Bad Gateway' }[status] || 'Status';
    const head = ascii('HTTP/1.1 ' + status + ' ' + reason + '\r\n' +
                       'Content-Type: ' + ctype + '\r\n' +
                       'Content-Length: ' + bytes.length + '\r\n' +
                       'Cache-Control: max-age=86400\r\n' +
                       'Connection: close\r\n\r\n');
    await conn.output.writeAll((req.method === 'HEAD' ? head : concat(head, bytes)).buffer);
  } catch (e) {
    log('serve error: ' + e);
  } finally {
    try { await conn.close(); } catch (e) { /* already closed */ }
  }
}

async function acceptLoop(listener) {
  for (;;) {
    let conn;
    try {
      conn = await listener.accept();
    } catch (e) {
      log('listener stopped: ' + e);
      return;
    }
    serve(conn);
  }
}

rpc.exports = {
  async start(list) {
    hosts = new Set(list.map(h => h.toLowerCase()));
    if (listenPort === 0) {
      const listener = await Socket.listen({ family: 'ipv4', host: '127.0.0.1', port: 0 });
      listenPort = listener.port;
      acceptLoop(listener);
    }
    return listenPort;
  },
};
