// AoC Rewards Fix - makes Daily Login Rewards (and the Item Shop tooltips) work again in Age of Conan,
// without injecting anything into the game and without changing any file or system setting.
//
// The game's built-in browser (Awesomium 1.6, from 2011) loads the qTip script from cdnjs.cloudflare.com over
// plain HTTP. cdnjs now redirects that to HTTPS with modern TLS, which the old browser cannot do, so the script
// never arrives and the GET REWARD button breaks.
//
// What this program does:
//   1. runs a tiny web server on this PC only (127.0.0.1:8777) that hands out files from cdnjs.cloudflare.com,
//      which it downloads itself over a modern secure connection (kept in the "cache" folder);
//   2. starts the game with the browser's own option
//        --host-resolver-rules="MAP cdnjs.cloudflare.com 127.0.0.1:8777"
//      so the browser asks this server instead of cdnjs.
//
// Run it again to start another client (multiboxing): the second copy sees the server is already running,
// starts one more client and closes. The first window keeps serving until the last client is closed.
//
// Usage:  "AoC Rewards Fix.exe" [dx10] [nogame] ["path to the Age of Conan folder"]
// The game folder is found in this order: the path given, game_path.txt next to this exe, this exe's own folder.
//
// Build (no extra tools needed, the compiler ships with Windows):
//   C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /out:"AoC Rewards Fix.exe" AoCRewardsFix.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class AoCRewardsFix
{
    const string CdnHost = "cdnjs.cloudflare.com";
    const int Port = 8777;
    static readonly object Gate = new object();
    static readonly Dictionary<string, byte[]> Mem = new Dictionary<string, byte[]>();
    static string CacheDir;

    static void Say(string text)
    {
        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
    }

    static int Main(string[] args)
    {
        Console.Title = "AoC Rewards Fix";
        string here = AppDomain.CurrentDomain.BaseDirectory;
        CacheDir = Path.Combine(here, "cache");
        bool dx10 = false, noGame = false;
        string gameDir = null;
        foreach (string a in args)
        {
            string l = a.ToLowerInvariant().TrimStart('-', '/');
            if (l == "dx10") dx10 = true;
            else if (l == "nogame") noGame = true;
            else gameDir = a;
        }
        string exeName = dx10 ? "AgeOfConanDX10.exe" : "AgeOfConan.exe";
        if (!noGame)
        {
            if (gameDir == null)
            {
                string note = Path.Combine(here, "game_path.txt");
                if (File.Exists(note)) gameDir = File.ReadAllText(note).Trim().Trim('"');
            }
            if (gameDir == null) gameDir = here;
            gameDir = gameDir.TrimEnd('\\', '/');
            if (!File.Exists(Path.Combine(gameDir, exeName)))
            {
                Say("Can't find " + exeName + " in: " + gameDir);
                Say("Put this program in the Age of Conan folder, or write the folder's path into game_path.txt next to it.");
                Say("Press Enter to close.");
                Console.ReadLine();
                return 1;
            }
        }

        // modern TLS for our own downloads (3072 = TLS 1.2; newer .NET versions add 1.3 by themselves)
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

        TcpListener listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
        }
        catch (SocketException)
        {
            listener = null;        // port taken: another copy of this program is already serving
        }

        if (!noGame) StartGame(gameDir, exeName);

        if (listener == null)
        {
            Say("The fix is already running in another window. Started one more client; this window closes.");
            Thread.Sleep(2500);
            return 0;
        }

        Say("Serving " + CdnHost + " to the game on 127.0.0.1:" + Port + ". Keep this window open while you play.");
        Thread t = new Thread(delegate() { AcceptLoop(listener); });
        t.IsBackground = true;
        t.Start();

        // stay until every client is closed (or forever with "nogame")
        bool seen = false;
        int gone = 0;
        while (true)
        {
            Thread.Sleep(5000);
            if (noGame) continue;
            int n = Process.GetProcessesByName("AgeOfConan").Length + Process.GetProcessesByName("AgeOfConanDX10").Length;
            if (n > 0) { seen = true; gone = 0; }
            else if (seen && ++gone >= 6) { Say("All clients closed. Bye."); Thread.Sleep(1500); return 0; }
            else if (!seen && ++gone >= 24) { Say("The game never started. Closing."); Thread.Sleep(1500); return 1; }
        }
    }

    static void StartGame(string gameDir, string exeName)
    {
        string rule = "\"MAP " + CdnHost + " 127.0.0.1:" + Port + "\"";
        ProcessStartInfo si = new ProcessStartInfo();
        si.FileName = Path.Combine(gameDir, exeName);
        si.WorkingDirectory = gameDir;
        // first argument is what the game's own patcher passes; the rest are options of the built-in browser
        si.Arguments = "\"" + Path.Combine(gameDir, "ConanPatcher.exe") + "\" --host-resolver-rules=" + rule + " --host-rules=" + rule;
        si.UseShellExecute = false;
        Process.Start(si);
        Say("Started " + exeName + " (without the patcher: run the normal patcher when the game has an update).");
    }

    static void AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); }
            catch (Exception e) { Say("server stopped: " + e.Message); return; }
            ThreadPool.QueueUserWorkItem(delegate(object o) { Serve((TcpClient)o); }, c);
        }
    }

    static void Serve(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 10000;
            NetworkStream s = client.GetStream();
            byte[] buf = new byte[8192];
            StringBuilder sb = new StringBuilder();
            while (sb.ToString().IndexOf("\r\n\r\n") < 0 && sb.Length < 65536)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            string[] lines = sb.ToString().Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string host = "";
            foreach (string l in lines)
            {
                Match h = Regex.Match(l, @"^host:\s*([^:\s]+)", RegexOptions.IgnoreCase);
                if (h.Success) host = h.Groups[1].Value.ToLowerInvariant();
            }
            Match m = Regex.Match(lines[0], @"^(GET|HEAD) (/\S*) HTTP/1\.[01]$");
            string status = "400 Bad Request";
            string ctype = "text/plain";
            byte[] body = new byte[0];
            if (m.Success && host == CdnHost)
            {
                string path = m.Groups[2].Value;
                byte[] data = GetFile(path);
                if (data != null) { status = "200 OK"; body = data; ctype = TypeFor(path); }
                else status = "502 Bad Gateway";
                Say(m.Groups[1].Value + " http://" + host + path + " -> " + status + " (" + body.Length + " B)");
            }
            else
            {
                Say("refused a request that was not for " + CdnHost + ": " + lines[0]);
            }
            string head = "HTTP/1.1 " + status + "\r\nContent-Type: " + ctype + "\r\nContent-Length: " + body.Length +
                          "\r\nCache-Control: max-age=86400\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            if (m.Success && m.Groups[1].Value == "GET" && body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
        }
        catch (Exception e) { Say("request error: " + e.Message); }
        finally { client.Close(); }
    }

    static string TypeFor(string path)
    {
        string p = Regex.Replace(path, @"[?#].*", "").ToLowerInvariant();
        if (p.EndsWith(".js")) return "application/javascript";
        if (p.EndsWith(".css")) return "text/css";
        if (p.EndsWith(".png")) return "image/png";
        if (p.EndsWith(".gif")) return "image/gif";
        if (p.EndsWith(".svg")) return "image/svg+xml";
        if (p.EndsWith(".json")) return "application/json";
        return "application/octet-stream";
    }

    // memory, then the cache folder, then cdnjs itself (HTTPS). The disk name is a hash of the path, so a
    // request can never make this program read or write outside the cache folder.
    static byte[] GetFile(string path)
    {
        lock (Gate)
        {
            byte[] data;
            if (Mem.TryGetValue(path, out data)) return data;
            string name;
            using (SHA1 sha = SHA1.Create())
                name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "") + ".bin";
            string file = Path.Combine(CacheDir, name);
            try
            {
                if (File.Exists(file)) data = File.ReadAllBytes(file);
                else
                {
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers["User-Agent"] = "cdnfix";
                        data = wc.DownloadData("https://" + CdnHost + path);
                    }
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllBytes(file, data);
                    Say("downloaded https://" + CdnHost + path + " (" + data.Length + " B)");
                }
            }
            catch (Exception e)
            {
                Say("could not get " + path + ": " + e.Message);
                return null;
            }
            Mem[path] = data;
            return data;
        }
    }
}
