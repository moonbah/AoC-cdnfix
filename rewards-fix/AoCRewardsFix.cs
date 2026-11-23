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
// Several clients: run it again for another client (the second copy sees the server is already running, starts
// one more client and closes), or point it at a folder that holds several installs (AOC1, AOC2, ...): it lists
// them and starts the ones you pick. The first window keeps serving until the last client is closed.
// The multi-install picker is based on a modification by Strelzia, who also had the multibox idea.
//
// The game folder needs an empty file named IgnorePatcher.enable, otherwise the game opens the patcher a second
// after it starts and the option is lost. If the file is missing, this program says so and offers to create it.
//
// DX10: add the option dx10, or copy this exe and rename it so its name ends in _DX10 (for example
// "AoC Rewards Fix_DX10.exe"): that copy starts the DX10 client by default.
//
// Usage:  "AoC Rewards Fix.exe" [dx10] [nogame] ["path"]
// path = the Age of Conan folder, or a folder that holds several installs. It is found in this order: the path
// given (Steam's %command% gives the patcher's path, its folder is used), game_path.txt next to this exe, this
// exe's own folder. A folder that has AgeOfConan.exe is one install; otherwise every folder directly inside it
// that has AgeOfConan.exe (or AgeOfConanDX10.exe) is offered as an install.
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
        // an exe whose name ends in _DX10 (copy and rename it) starts the DX10 client by default (idea by Strelzia)
        string self = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
        bool dx10 = self.EndsWith("_DX10", StringComparison.OrdinalIgnoreCase);
        bool noGame = false;
        string given = null;
        foreach (string a in args)
        {
            string l = a.ToLowerInvariant().TrimStart('-', '/');
            if (l == "dx10") dx10 = true;
            else if (l == "nogame") noGame = true;
            else if (Directory.Exists(a)) given = a;
            else if (File.Exists(a)) { if (given == null) given = Path.GetDirectoryName(a); }   // Steam's %command% passes the patcher's path: use its folder
            // anything else (other options Steam may add) is ignored
        }

        List<string> games = new List<string>();
        if (!noGame)
        {
            string root = given;
            if (root == null)
            {
                string note = Path.Combine(here, "game_path.txt");
                if (File.Exists(note)) root = File.ReadAllText(note).Trim().Trim('"');
            }
            if (root == null) root = here;
            if (root.Length > 3) root = root.TrimEnd('\\', '/');
            games = FindGames(root, dx10);
            if (games.Count == 0)
            {
                Say("Can't find the game in: " + root + " (or in any folder directly inside it).");
                Say("Put this program in the Age of Conan folder, or write the folder's path into game_path.txt next to it.");
                Say("For several installs, write the folder that holds all of them into game_path.txt.");
                Say("Press Enter to close.");
                Console.ReadLine();
                return 1;
            }
            games = ChooseGames(games);
            if (games == null) return 1;
            CheckIgnorePatcher(games);
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

        // the server must be answering before the first client starts
        if (listener != null)
        {
            TcpListener lst = listener;
            Thread t = new Thread(delegate() { AcceptLoop(lst); });
            t.IsBackground = true;
            t.Start();
            Say("Serving " + CdnHost + " to the game on 127.0.0.1:" + Port + ". Keep this window open while you play.");
        }

        if (!noGame) StartGames(games, dx10);

        if (listener == null)
        {
            Say("The fix is already running in another window. Started the client(s); this window closes.");
            Thread.Sleep(2500);
            return 0;
        }

        // stay until every client is closed (or forever with "nogame")
        bool seen = false;
        bool hinted = false;
        int gone = 0;
        int ticks = 0;
        while (true)
        {
            Thread.Sleep(5000);
            if (noGame) continue;
            ticks++;
            int n = Process.GetProcessesByName("AgeOfConan").Length + Process.GetProcessesByName("AgeOfConanDX10").Length;
            if (!hinted && ticks <= 6 && n == 0 && Process.GetProcessesByName("ConanPatcher").Length > 0)
            {
                hinted = true;
                Say("The game handed over to the patcher instead of starting. The fix only works for a game started by this program.");
                Say("Usual reason: the game folder has no IgnorePatcher.enable file. Close the patcher, run this program again and answer Y when asked.");
            }
            if (n > 0) { seen = true; gone = 0; }
            else if (seen && ++gone >= 6) { Say("All clients closed. Bye."); Thread.Sleep(1500); return 0; }
            else if (!seen && ++gone >= 24) { Say("The game never started. Closing."); Thread.Sleep(1500); return 1; }
        }
    }

    // The exe to start in an install: the preferred one (DX9 or DX10), else the other one, else null.
    static string PickExe(string dir, bool dx10)
    {
        string a = dx10 ? "AgeOfConanDX10.exe" : "AgeOfConan.exe";
        string b = dx10 ? "AgeOfConan.exe" : "AgeOfConanDX10.exe";
        if (File.Exists(Path.Combine(dir, a))) return a;
        if (File.Exists(Path.Combine(dir, b))) return b;
        return null;
    }

    // One install (the folder itself has the game), or every folder directly inside it that has the game,
    // sorted by the number at the end of the folder name (AOC2 before AOC10).
    static List<string> FindGames(string root, bool dx10)
    {
        List<string> found = new List<string>();
        if (!Directory.Exists(root)) return found;
        if (PickExe(root, dx10) != null) { found.Add(root); return found; }
        string[] subs;
        try { subs = Directory.GetDirectories(root); }
        catch (Exception) { return found; }
        foreach (string d in subs)
            if (PickExe(d, dx10) != null) found.Add(d);
        found.Sort(delegate(string a, string b)
        {
            string na = Path.GetFileName(a);
            string nb = Path.GetFileName(b);
            int ia, ib;
            bool aNum = int.TryParse(Regex.Match(na, @"\d+$").Value, out ia);
            bool bNum = int.TryParse(Regex.Match(nb, @"\d+$").Value, out ib);
            if (aNum && bNum)
            {
                int c = ia.CompareTo(ib);
                if (c != 0) return c;
            }
            return StringComparer.OrdinalIgnoreCase.Compare(na, nb);
        });
        return found;
    }

    // One install: no question. Several: a numbered list; "1,3,5", ranges like "1-5", or ALL. null = no answer.
    static List<string> ChooseGames(List<string> games)
    {
        if (games.Count == 1) return games;
        Console.WriteLine();
        Console.WriteLine("Found " + games.Count + " installations:");
        Console.WriteLine();
        for (int i = 0; i < games.Count; i++)
            Console.WriteLine("{0,3}. {1}", i + 1, Path.GetFileName(games[i]));
        Console.WriteLine();
        Console.WriteLine("Type the numbers to start, separated by commas (ranges like 1-5 work),");
        Console.WriteLine("or ALL to start every install.");
        while (true)
        {
            Console.Write("Selection: ");
            string sel = Console.ReadLine();
            if (sel == null) return null;
            sel = sel.Trim();
            if (sel.Length == 0) continue;
            List<string> picked = new List<string>();
            if (sel.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                picked.AddRange(games);
                return picked;
            }
            bool ok = true;
            foreach (string part in sel.Split(','))
            {
                string p = part.Trim();
                int lo = 0, hi = 0;
                bool good;
                int dash = p.IndexOf('-');
                if (dash > 0)
                    good = int.TryParse(p.Substring(0, dash).Trim(), out lo) && int.TryParse(p.Substring(dash + 1).Trim(), out hi);
                else
                {
                    good = int.TryParse(p, out lo);
                    hi = lo;
                }
                if (!good || lo < 1 || hi > games.Count || lo > hi)
                {
                    Say("Not understood: '" + p + "'. Use numbers from 1 to " + games.Count + ", or ALL.");
                    ok = false;
                    break;
                }
                for (int n = lo; n <= hi; n++)
                    if (!picked.Contains(games[n - 1])) picked.Add(games[n - 1]);
            }
            if (ok && picked.Count > 0) return picked;
        }
    }

    // The game skips the patcher only when an empty file IgnorePatcher.enable sits in its folder. Without it the
    // game hands over to the patcher about a second after it starts, and the option below is lost. Creating the
    // empty file is the game's own switch, but it is a change in the game folder, so ask first (once for all).
    static void CheckIgnorePatcher(List<string> games)
    {
        List<string> missing = new List<string>();
        foreach (string g in games)
            if (!File.Exists(Path.Combine(g, "IgnorePatcher.enable"))) missing.Add(g);
        if (missing.Count == 0) return;
        if (missing.Count == 1)
            Say("The game folder " + Path.GetFileName(missing[0]) + " has no file named IgnorePatcher.enable.");
        else
            Say(missing.Count + " of the selected installs have no file named IgnorePatcher.enable (for example " + Path.GetFileName(missing[0]) + ").");
        Say("Without it the game opens the patcher a second after it starts, and this fix is lost.");
        Say("The file is empty and is the game's own switch for skipping the patcher. You can delete it any time.");
        Console.Write(missing.Count == 1 ? "Create it now? (Y/N): " : "Create it in all " + missing.Count + "? (Y/N): ");
        string a = Console.ReadLine();
        if (a == null || !a.Trim().ToLowerInvariant().StartsWith("y"))
        {
            Say("Not created. Starting anyway; if the patcher opens, create an empty file called IgnorePatcher.enable in the game folder.");
            return;
        }
        foreach (string g in missing)
        {
            string f = Path.Combine(g, "IgnorePatcher.enable");
            try
            {
                File.WriteAllBytes(f, new byte[0]);
                Say("Created " + f);
            }
            catch (Exception e)
            {
                Say("Could not create " + f + " (" + e.Message + "). Create an empty file with that name yourself.");
            }
        }
    }

    // Start every chosen install, 3 seconds apart so they don't all load at once.
    static void StartGames(List<string> games, bool dx10)
    {
        for (int i = 0; i < games.Count; i++)
        {
            StartGame(games[i], PickExe(games[i], dx10), games.Count > 1);
            if (i < games.Count - 1) Thread.Sleep(3000);
        }
        Say("Started without the patcher: run the normal patcher when the game has an update.");
    }

    static void StartGame(string gameDir, string exeName, bool showFolder)
    {
        string label = showFolder ? Path.GetFileName(gameDir) + " (" + exeName + ")" : exeName;
        string rule = "\"MAP " + CdnHost + " 127.0.0.1:" + Port + "\"";
        ProcessStartInfo si = new ProcessStartInfo();
        si.FileName = Path.Combine(gameDir, exeName);
        si.WorkingDirectory = gameDir;
        // first argument is what the game's own patcher passes; the rest are options of the built-in browser
        si.Arguments = "\"" + Path.Combine(gameDir, "ConanPatcher.exe") + "\" --host-resolver-rules=" + rule + " --host-rules=" + rule;
        si.UseShellExecute = false;
        try
        {
            Process.Start(si);
            Say("Started " + label);
        }
        catch (Exception e)
        {
            Say("Could not start " + label + ": " + e.Message);
        }
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
