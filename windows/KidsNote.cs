using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// 키즈노트 사진·동영상 다운로더 (개인용, 비공식)
// 화면: 로컬 서버(localhost)가 ui.html 을 띄우고 Edge 앱 창으로 보여 준다. 화면은 /api/state 를 주기적으로 읽어 그린다.
// 로그인: 같은 Edge 안에 진짜 키즈노트 창을 열고 사용자가 직접 로그인한다. 비밀번호는 이 프로그램을 거치지 않는다.
// 목록: 로그인된 키즈노트 창 안에서 API 를 호출한다 (Chrome DevTools Protocol). 사이트가 보내는 X-ENROLLMENT 헤더를 엿들어 쓴다.
// 파일: 원본 이미지/동영상 주소에서 바로 받는다. 이미 받은 파일은 건너뛴다.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--uninstall") { Uninstall(); return; }
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 16;
        bool created;
        using (var m = new Mutex(true, @"Local\KidsNoteDownloader", out created))
        {
            if (!created) { App.FocusExisting(); return; }
            try { new App().Run(); }
            catch (Exception e) { MessageBox.Show(e.Message, Install.Name); }
        }
    }

    // 설정 > 앱 에서 제거할 때 호출됨. 받은 사진은 지우지 않고, 로그인 정보·설정은 지운다.
    static void Uninstall()
    {
        if (MessageBox.Show("키즈노트 다운로더를 제거할까요?\n\n받아 둔 사진과 동영상은 지워지지 않아요.\n이 PC에 남은 키즈노트 로그인 정보는 함께 지워져요.",
            Install.Name + " 제거", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Install.KillRunning();
        App.KillProfileEdge();
        Install.Unregister();
        try { Directory.Delete(Install.DataDir, true); } catch { }
        string me = Application.ExecutablePath, dir = Path.GetDirectoryName(me);
        if (string.Equals(dir, Install.Dir, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"") { CreateNoWindow = true, UseShellExecute = false });
        MessageBox.Show("키즈노트 다운로더를 제거했어요.", Install.Name);
    }
}

class Child
{
    public string Id, Enrollment, Name;
    public override string ToString() { return string.IsNullOrEmpty(Name) ? "아이 #" + Id : Name; }
}

class Job { public string Url, Path, Orig; public DateTime When; public bool Video, Compact; }

// ── Chrome DevTools Protocol 최소 구현 ──
class Cdp : IDisposable
{
    readonly ClientWebSocket ws = new ClientWebSocket();
    readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
    readonly SemaphoreSlim sendLock = new SemaphoreSlim(1);
    int nextId;
    public event Action<string, Dictionary<string, object>> OnEvent;
    public event Action OnClosed;
    public bool Alive { get { return ws.State == WebSocketState.Open; } }

    public void Connect(string url)
    {
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.ConnectAsync(new Uri(url), CancellationToken.None).Wait();
        new Thread(ReceiveLoop) { IsBackground = true }.Start();
    }

    void ReceiveLoop()
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                string text = Encoding.UTF8.GetString(ms.ToArray()); ms.SetLength(0);
                var msg = J.Js.DeserializeObject(text) as Dictionary<string, object>;
                if (msg == null) continue;
                if (msg.ContainsKey("id"))
                {
                    TaskCompletionSource<Dictionary<string, object>> tcs;
                    if (pending.TryRemove(Convert.ToInt32(msg["id"]), out tcs)) tcs.TrySetResult(msg);
                }
                else if (msg.ContainsKey("method") && OnEvent != null)
                {
                    try { OnEvent((string)msg["method"], J.Obj(msg, "params")); } catch { }
                }
            }
        }
        catch { }
        foreach (var p in pending.Values) p.TrySetException(new Exception("키즈노트 창과 연결이 끊겼어요."));
        if (OnClosed != null) OnClosed();
    }

    public Dictionary<string, object> Send(string method, object prms, int timeoutMs = 120000)
    {
        int id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<Dictionary<string, object>>();
        pending[id] = tcs;
        byte[] data = Encoding.UTF8.GetBytes(J.Js.Serialize(new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", prms ?? new Dictionary<string, object>() } }));
        sendLock.Wait();
        try { ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
        finally { sendLock.Release(); }
        if (!tcs.Task.Wait(timeoutMs)) { pending.TryRemove(id, out tcs); throw new Exception("Edge 응답이 없어요 (" + method + ")"); }
        var res = tcs.Task.Result;
        if (res.ContainsKey("error")) throw new Exception("Edge 오류: " + J.Js.Serialize(res["error"]));
        return J.Obj(res, "result");
    }

    public string Eval(string expr, int timeoutMs = 120000)
    {
        var r = Send("Runtime.evaluate", new Dictionary<string, object> { { "expression", expr }, { "awaitPromise", true }, { "returnByValue", true } }, timeoutMs);
        if (r.ContainsKey("exceptionDetails")) throw new Exception("페이지 스크립트 오류");
        var v = J.Obj(r, "result");
        return v != null && v.ContainsKey("value") && v["value"] != null ? v["value"].ToString() : null;
    }

    public void Dispose() { try { ws.Dispose(); } catch { } }
}

static class J
{
    public static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
    public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
    {
        object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
    }
    public static object[] Arr(Dictionary<string, object> d, string k)
    {
        object v; if (d == null || !d.TryGetValue(k, out v) || v == null) return new object[0];
        var a = v as object[]; if (a != null) return a;
        var l = v as System.Collections.ArrayList; return l != null ? l.ToArray() : new object[0];
    }
    public static string Str(Dictionary<string, object> d, string k)
    {
        object v; return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : null;
    }
    public static bool Bool(Dictionary<string, object> d, string k, bool def)
    {
        object v; return d != null && d.TryGetValue(k, out v) && v is bool ? (bool)v : def;
    }
}

// 사용자 설정 (settings.txt 에 저장)
class Settings
{
    public string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "키즈노트");
    public bool Reports = true, Albums = true, Videos = true, TeacherOnly = true;
    public string Range = "all", From = DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd");
    public string Quality = "original";   // original: 원본 그대로 · compact: 사진 WebP + 동영상 720p 로 줄여서
    public HashSet<string> SkipKids = new HashSet<string>();
    static string FileName { get { return Path.Combine(Install.DataDir, "settings.txt"); } }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            foreach (var line in File.ReadAllLines(FileName, Encoding.UTF8))
            {
                int i = line.IndexOf('='); if (i < 0) continue;
                string k = line.Substring(0, i), v = line.Substring(i + 1);
                if (k == "folder" && v.Length > 0) s.Folder = v;
                if (k == "reports") s.Reports = v == "1";
                if (k == "albums") s.Albums = v == "1";
                if (k == "videos") s.Videos = v == "1";
                if (k == "teacher") s.TeacherOnly = v == "1";
                if (k == "range") s.Range = v;
                if (k == "from") s.From = v;
                if (k == "quality" && (v == "original" || v == "compact")) s.Quality = v;
                if (k == "all" && v == "0" && s.Range == "all") s.Range = "custom";   // 예전 버전 설정
                if (k == "skip") foreach (var x in v.Split(',')) if (x.Length > 0) s.SkipKids.Add(x);
            }
        }
        catch { }
        return s;
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Install.DataDir);
            File.WriteAllLines(FileName, new[] {
                "folder=" + Folder, "reports=" + B(Reports), "albums=" + B(Albums), "videos=" + B(Videos), "teacher=" + B(TeacherOnly),
                "range=" + Range, "from=" + From, "quality=" + Quality, "skip=" + string.Join(",", SkipKids) }, Encoding.UTF8);
        }
        catch { }
    }
    static string B(bool b) { return b ? "1" : "0"; }

    public DateTime? FromDate()
    {
        DateTime d;
        if (Range == "1m") return DateTime.Today.AddMonths(-1);
        if (Range == "3m") return DateTime.Today.AddMonths(-3);
        if (Range == "1y") return DateTime.Today.AddYears(-1);
        if (Range == "custom" && DateTime.TryParse(From, out d)) return d.Date;
        return null;
    }
}

class App
{
    const string KnSite = "https://www.kidsnote.com";
    const string UiTitle = "키즈노트 다운로더";
    static string EdgeProfile { get { return Path.Combine(Install.DataDir, "edge"); } }

    readonly string token = Guid.NewGuid().ToString("N");
    int port, debugPort;
    Settings cfg = Settings.Load();
    readonly object gate = new object();

    // 로그인 상태: checking(확인 중) · needed(로그인 필요) · waiting(로그인 창 열림) · ok
    string login = "checking", hint;
    readonly Dictionary<string, Child> kids = new Dictionary<string, Child>();
    Cdp browser, page;
    string uiTarget, knTarget;

    // 다운로드 상태
    volatile bool running, stopRequested;
    string phase = "idle", message = "";
    int total, done, fresh, skipped, failed, freshVideos;
    long bytesIn, bytesOut;   // 압축 모드: 원본 크기 합 / 저장한 크기 합
    DateTime startedAt;
    readonly List<Dictionary<string, object>> recent = new List<Dictionary<string, object>>();
    readonly List<string> log = new List<string>();
    readonly Dictionary<string, byte[]> thumbs = new Dictionary<string, byte[]>();

    DateTime lastSeen = DateTime.MinValue; bool seen, bye;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr p);

    public void Run()
    {
        Directory.CreateDirectory(Install.DataDir);
        KillProfileEdge();   // 지난번에 남은 Edge 가 있으면 디버깅 포트가 달라서 정리

        var l = StartServer();
        debugPort = FreePort();
        string edge = FindEdge();
        Process.Start(edge, "--remote-debugging-port=" + debugPort + " --user-data-dir=\"" + EdgeProfile + "\" --no-first-run --no-default-browser-check " +
            "--window-size=1120,860 --app=http://127.0.0.1:" + port + "/?k=" + token);
        new Thread(ConnectBrowser) { IsBackground = true }.Start();

        // 화면이 닫히면(신호가 끊기면) 종료
        var start = DateTime.Now; IntPtr tagged = IntPtr.Zero;
        while (true)
        {
            Thread.Sleep(tagged == IntPtr.Zero ? 200 : 1000);
            IntPtr ui = FindUi();
            if (ui != IntPtr.Zero && ui != tagged) { TaskbarId.ApplyToWindow(ui, Install.AppId, Install.Exe, (File.Exists(Install.IconFile) ? Install.IconFile : Install.Exe) + ",0", Install.Name); tagged = ui; }
            double quiet = (DateTime.Now - lastSeen).TotalSeconds;
            if (seen && bye && quiet > 3) break;
            if (seen && quiet > 20) break;
            if (!seen && (DateTime.Now - start).TotalSeconds > 60) break;
        }
        stopRequested = true;
        try { if (browser != null && browser.Alive) browser.Send("Browser.close", null, 3000); } catch { }
        Thread.Sleep(300);
        KillProfileEdge();
        cfg.Save();
        try { l.Stop(); } catch { }
    }

    static IntPtr FindUi()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => { if (!IsWindowVisible(h)) return true; var t = new StringBuilder(256); GetWindowText(h, t, 256); string s = t.ToString(); if (s == UiTitle || s.EndsWith(" · " + UiTitle)) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }

    public static void FocusExisting()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            if (!IsWindowVisible(h)) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            if (t.ToString() == UiTitle) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (found != IntPtr.Zero) { if (IsIconic(found)) ShowWindow(found, 9); SetForegroundWindow(found); }
    }

    public static void KillProfileEdge()
    {
        try
        {
            using (var s = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'"))
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    string cmd = (o["CommandLine"] ?? "").ToString();
                    if (cmd.IndexOf(EdgeProfile, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { Process.GetProcessById(Convert.ToInt32(o["ProcessId"])).Kill(); } catch { }
                }
        }
        catch { }
    }

    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    static string FindEdge()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (root == null) continue;
            string c = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(c)) return c;
        }
        throw new Exception("Microsoft Edge를 찾을 수 없어요.");
    }

    void Log(string s)
    {
        lock (gate) { log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + s); if (log.Count > 300) log.RemoveAt(0); }
    }

    // ── Edge 연결 + 키즈노트 창 (자동 로그인 확인) ──
    void ConnectBrowser()
    {
        try
        {
            string wsUrl = null;
            for (int i = 0; i < 150 && wsUrl == null; i++)
            {
                Thread.Sleep(200);
                try
                {
                    using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                        wsUrl = J.Str(J.Js.DeserializeObject(wc.DownloadString("http://127.0.0.1:" + debugPort + "/json/version")) as Dictionary<string, object>, "webSocketDebuggerUrl");
                }
                catch { }
            }
            if (wsUrl == null) throw new Exception("Edge 에 연결하지 못했어요.");
            browser = new Cdp(); browser.Connect(wsUrl);
            foreach (var t in Targets()) if ((J.Str(t, "url") ?? "").Contains("127.0.0.1:" + port)) uiTarget = J.Str(t, "id");
            OpenKidsnote(true);
        }
        catch (Exception e) { lock (gate) login = "needed"; Log("오류: " + e.Message); }
    }

    List<Dictionary<string, object>> Targets()
    {
        var list = new List<Dictionary<string, object>>();
        using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            foreach (var o in (object[])J.Js.DeserializeObject(wc.DownloadString("http://127.0.0.1:" + debugPort + "/json/list")))
            {
                var t = o as Dictionary<string, object>;
                if (t != null && J.Str(t, "type") == "page") list.Add(t);
            }
        return list;
    }

    // background=true: 시작할 때 조용히 열어 로그인 여부만 확인 (창은 최소화)
    void OpenKidsnote(bool background)
    {
        if (page != null && page.Alive && knTarget != null) { ShowKidsnote(); return; }
        var r = browser.Send("Target.createTarget", new Dictionary<string, object> { { "url", KnSite + "/service/report" }, { "newWindow", true }, { "background", background } });
        knTarget = J.Str(r, "targetId");
        if (background) SetWindowState(knTarget, "minimized");
        page = new Cdp();
        page.OnEvent += OnPageEvent;
        page.OnClosed += () => { lock (gate) { if (login != "ok" || kids.Count == 0) login = "needed"; } page = null; knTarget = null; };
        page.Connect("ws://127.0.0.1:" + debugPort + "/devtools/page/" + knTarget);
        page.Send("Network.enable", null);
        try { page.Send("Page.reload", null, 10000); } catch { }
        lock (gate) if (login != "ok") login = background ? "checking" : "waiting";
        new Thread(WatchLogin) { IsBackground = true }.Start();
    }

    void ShowKidsnote()
    {
        SetWindowState(knTarget, "normal");
        try { browser.Send("Target.activateTarget", new Dictionary<string, object> { { "targetId", knTarget } }); } catch { }
        lock (gate) if (login != "ok") login = "waiting";
    }

    void FocusUi()
    {
        try { if (uiTarget != null) browser.Send("Target.activateTarget", new Dictionary<string, object> { { "targetId", uiTarget } }); } catch { }
        FocusExisting();
    }

    void SetWindowState(string target, string state)
    {
        try
        {
            var w = browser.Send("Browser.getWindowForTarget", new Dictionary<string, object> { { "targetId", target } });
            browser.Send("Browser.setWindowBounds", new Dictionary<string, object> { { "windowId", w["windowId"] }, { "bounds", new Dictionary<string, object> { { "windowState", state } } } });
        }
        catch { }
    }

    // 사이트가 보내는 요청에서 아이 번호, 기관(enrollment) 정보, API 주소를 엿듣는다
    string apiPrefix = KnSite + "/api/v1_2", enrHeader = "X-ENROLLMENT";
    readonly HashSet<string> seenApi = new HashSet<string>();
    void OnPageEvent(string method, Dictionary<string, object> p)
    {
        if (method != "Network.requestWillBeSent") return;
        var req = J.Obj(p, "request"); string url = J.Str(req, "url") ?? "";
        if (!Regex.IsMatch(url, @"^https?://[^/]*kidsnote\.com/", RegexOptions.IgnoreCase)) return;
        if (Regex.IsMatch(url, @"\.(js|css|png|jpe?g|gif|svg|woff2?|ico|webp|mp4)(\?|$)", RegexOptions.IgnoreCase)) return;
        var h = J.Obj(req, "headers");
        // 진단용: 처음 보는 요청 모양을 기록 (숫자는 # 으로 가림)
        string shape = Regex.Replace(Regex.Replace(url, @"\?.*$", ""), @"\d+", "#");
        bool fresh; lock (seenApi) fresh = seenApi.Count < 40 && seenApi.Add(shape);
        if (fresh && (shape.Contains("/api") || shape.Contains("children")))
            Log("요청: " + shape + (h != null ? "  [헤더: " + string.Join(",", h.Keys.Where(k => k.IndexOf("enroll", StringComparison.OrdinalIgnoreCase) >= 0 || k.StartsWith("X-", StringComparison.OrdinalIgnoreCase))) + "]" : ""));

        var m = Regex.Match(url, @"^(https?://[^/]+)(/[^?]*?)/children/(\d+)(/|\?|$)", RegexOptions.IgnoreCase);
        if (!m.Success) return;
        string enr = null;
        if (h != null) foreach (var kv in h)
            if (kv.Key.IndexOf("enrollment", StringComparison.OrdinalIgnoreCase) >= 0 && kv.Value != null) { enr = kv.Value.ToString(); enrHeader = kv.Key; }
        if (enr == null) { var q = Regex.Match(url, @"[?&]enrollment(?:_id)?=(\d+)", RegexOptions.IgnoreCase); if (q.Success) enr = q.Groups[1].Value; }
        apiPrefix = m.Groups[1].Value + m.Groups[2].Value;
        string id = m.Groups[3].Value;
        bool first;
        lock (gate)
        {
            Child c;
            if (kids.TryGetValue(id, out c)) { if (enr != null) c.Enrollment = enr; return; }
            first = kids.Count == 0;
            kids[id] = new Child { Id = id, Enrollment = enr };
        }
        new Thread(() =>
        {
            FillName(id);
            bool wasWaiting;
            lock (gate) { wasWaiting = login == "waiting"; login = "ok"; }
            Log((kids[id]) + " 확인");
            if (first)
            {
                // 로그인 끝: 키즈노트 창은 내려 두고 우리 화면으로
                SetWindowState(knTarget, "minimized");
                if (wasWaiting) FocusUi();
            }
        }) { IsBackground = true }.Start();
    }

    void FillName(string id)
    {
        Child c; lock (gate) c = kids[id];
        try
        {
            var pg = Api(apiPrefix + "/children/" + id + "/reports/?page_size=1", c.Enrollment);
            foreach (Dictionary<string, object> r in J.Arr(pg, "results")) { c.Name = J.Str(r, "child_name"); break; }
        }
        catch (Exception e) { Log("아이 이름을 못 가져왔어요: " + e.Message); }
    }

    // 로그인 페이지에 머무르면 '로그인 필요'. 로그인 뒤 아이 정보가 안 잡히면 알림장 화면으로 딱 한 번만 이동하고, 그 뒤엔 안내만 한다
    void WatchLogin()
    {
        var me = page; int outside = 0; bool moved = false;
        while (me != null && me.Alive && page == me)
        {
            Thread.Sleep(1500);
            lock (gate) if (kids.Count > 0) return;
            string href = null;
            try { href = me.Eval("location.href", 5000); } catch { }
            if (href == null) continue;
            if (Regex.IsMatch(href, @"login|signin|auth", RegexOptions.IgnoreCase)) { lock (gate) { if (login == "checking") login = "needed"; hint = null; } outside = 0; continue; }
            outside++;
            if (outside == 4 && !moved && !href.Contains("/service/report"))
            {
                moved = true;
                try { me.Send("Page.navigate", new Dictionary<string, object> { { "url", KnSite + "/service/report" } }); } catch { }
            }
            if (outside == 8)
            {
                lock (gate) hint = "키즈노트 창에서 '알림장'이나 '앨범' 메뉴를 한 번 눌러 주세요. 아이 정보가 잡히면 자동으로 넘어가요.";
                Log("로그인 뒤 아이 정보를 아직 못 찾았어요 (현재 화면: " + Regex.Replace(Regex.Replace(href, @"\?.*$", ""), @"\d+", "#") + ")");
            }
        }
    }
    Dictionary<string, object> Api(string path, string enrollment)
    {
        var pg = page;
        if (pg == null || !pg.Alive) throw new Exception("키즈노트 창이 닫혔어요. 다시 로그인해 주세요.");
        string hdr = "'Accept':'application/json'" + (string.IsNullOrEmpty(enrollment) ? "" : "," + J.Js.Serialize(enrHeader) + ":" + J.Js.Serialize(enrollment));
        string expr = "(async()=>{const r=await fetch(" + J.Js.Serialize(path) + ",{credentials:'include',headers:{" + hdr + "}});" +
                      "return JSON.stringify({s:r.status,t:await r.text()});})()";
        var o = J.Js.DeserializeObject(pg.Eval(expr)) as Dictionary<string, object>;
        int status = Convert.ToInt32(o["s"]);
        if (status == 401 || status == 403) { lock (gate) { login = "needed"; kids.Clear(); } throw new Exception("로그인이 풀렸어요. 다시 로그인해 주세요."); }
        if (status != 200) { Log("응답 " + status + ": " + Regex.Replace(path, @"\d{3,}", "#")); throw new Exception("키즈노트 응답 " + status); }
        return J.Js.DeserializeObject((string)o["t"]) as Dictionary<string, object>;
    }

    // ── 로컬 서버 ──
    HttpListener StartServer()
    {
        var asm = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, byte[]>();
        foreach (var name in new[] { "ui.html", "icon.png", "app.ico" })
            using (var s = asm.GetManifestResourceStream(name))
            using (var m = new MemoryStream()) { s.CopyTo(m); files[name] = m.ToArray(); }
        files["ui.html"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["ui.html"]).Replace("__VERSION__", Install.Version));

        HttpListener l = null;
        for (int i = 0; i < 20 && l == null; i++)
        {
            port = FreePort();
            try { l = new HttpListener(); l.Prefixes.Add("http://127.0.0.1:" + port + "/"); l.Start(); }
            catch { l = null; }
        }
        if (l == null) throw new Exception("화면용 로컬 포트를 열지 못했어요.");
        new Thread(() =>
        {
            while (l.IsListening)
            {
                HttpListenerContext c;
                try { c = l.GetContext(); } catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c, files));
            }
        }) { IsBackground = true }.Start();
        return l;
    }

    void Handle(HttpListenerContext c, Dictionary<string, byte[]> files)
    {
        var req = c.Request; var res = c.Response;
        try
        {
            string path = req.Url.AbsolutePath;
            if (path == "/" || path == "/icon.png" || path == "/app.ico")
            {
                if (path == "/" && req.QueryString["k"] != token) { res.StatusCode = 403; return; }
                byte[] b = files[path == "/" ? "ui.html" : path.Substring(1)];
                res.ContentType = path == "/" ? "text/html; charset=utf-8" : path.EndsWith(".ico") ? "image/x-icon" : "image/png";
                res.Headers["Cache-Control"] = "no-store";
                res.OutputStream.Write(b, 0, b.Length); return;
            }
            if (path == "/thumb")
            {
                if (req.QueryString["k"] != token) { res.StatusCode = 403; return; }
                byte[] t = Thumb(req.QueryString["p"]);
                if (t == null) { res.StatusCode = 404; return; }
                res.ContentType = "image/jpeg"; res.Headers["Cache-Control"] = "max-age=3600";
                res.OutputStream.Write(t, 0, t.Length); return;
            }
            if (!path.StartsWith("/api/") || req.Headers["X-Token"] != token) { res.StatusCode = 403; return; }
            string body; using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            var q = body.Length > 0 ? J.Js.DeserializeObject(body) as Dictionary<string, object> : null;
            lastSeen = DateTime.Now; seen = true;
            object result = true;
            switch (path)
            {
                case "/api/state": bye = false; result = State(); break;
                case "/api/bye": bye = true; break;
                case "/api/login":
                    new Thread(() => { try { if (browser == null) return; OpenKidsnote(false); ShowKidsnote(); } catch (Exception e) { Log("오류: " + e.Message); } }) { IsBackground = true }.Start();
                    break;
                case "/api/settings": ApplySettings(q); break;
                case "/api/pick":
                    { string p = PickFolder(); if (p != null) { cfg.Folder = p; cfg.Save(); } result = cfg.Folder; }
                    break;
                case "/api/start": result = Start(); break;
                case "/api/stop": stopRequested = true; break;
                case "/api/open":
                    {
                        string p = J.Str(q, "path") ?? cfg.Folder;
                        if (!p.StartsWith(cfg.Folder, StringComparison.OrdinalIgnoreCase)) { res.StatusCode = 403; return; }
                        Directory.CreateDirectory(cfg.Folder);
                        if (File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                        else Process.Start("explorer.exe", "\"" + p + "\"");
                    }
                    break;
                default: res.StatusCode = 404; return;
            }
            byte[] json = Encoding.UTF8.GetBytes(J.Js.Serialize(result));
            res.ContentType = "application/json; charset=utf-8";
            res.OutputStream.Write(json, 0, json.Length);
        }
        catch (Exception e)
        {
            try { res.StatusCode = 500; byte[] b = Encoding.UTF8.GetBytes(e.Message); res.OutputStream.Write(b, 0, b.Length); } catch { }
        }
        finally { try { res.Close(); } catch { } }
    }

    object State()
    {
        lock (gate)
        {
            return new Dictionary<string, object> {
                { "login", login }, { "hint", hint },
                { "kids", kids.Values.Select(k => new Dictionary<string, object> { { "id", k.Id }, { "name", k.ToString() }, { "on", !cfg.SkipKids.Contains(k.Id) } }).ToList() },
                { "cfg", new Dictionary<string, object> {
                    { "folder", cfg.Folder }, { "reports", cfg.Reports }, { "albums", cfg.Albums }, { "videos", cfg.Videos },
                    { "teacher", cfg.TeacherOnly }, { "range", cfg.Range }, { "from", cfg.From }, { "quality", cfg.Quality } } },
                { "encoders", new Dictionary<string, object> { { "webp", WebpEncoder.Available }, { "video", VideoCompressor.Available } } },
                { "job", new Dictionary<string, object> {
                    { "running", running }, { "phase", phase }, { "message", message }, { "total", total }, { "done", done },
                    { "fresh", fresh }, { "skipped", skipped }, { "failed", failed }, { "freshVideos", freshVideos },
                    { "bytesIn", bytesIn }, { "bytesOut", bytesOut }, { "compact", cfg.Quality == "compact" },
                    { "elapsed", running ? (DateTime.Now - startedAt).TotalSeconds : 0 } } },
                { "recent", recent.ToList() },
                { "log", log.Skip(Math.Max(0, log.Count - 80)).ToList() },
            };
        }
    }

    void ApplySettings(Dictionary<string, object> q)
    {
        if (q == null) return;
        lock (gate)
        {
            cfg.Reports = J.Bool(q, "reports", cfg.Reports); cfg.Albums = J.Bool(q, "albums", cfg.Albums);
            cfg.Videos = J.Bool(q, "videos", cfg.Videos); cfg.TeacherOnly = J.Bool(q, "teacher", cfg.TeacherOnly);
            cfg.Range = J.Str(q, "range") ?? cfg.Range; cfg.From = J.Str(q, "from") ?? cfg.From;
            string ql = J.Str(q, "quality"); if (ql == "original" || ql == "compact") cfg.Quality = ql;
            var skip = J.Arr(q, "skip");
            if (q.ContainsKey("skip")) { cfg.SkipKids.Clear(); foreach (var x in skip) cfg.SkipKids.Add(x.ToString()); }
        }
        cfg.Save();
    }

    // 폴더 선택 창 (화면 위에 뜨도록)
    string PickFolder()
    {
        string result = null;
        var t = new Thread(() =>
        {
            using (var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1), StartPosition = FormStartPosition.CenterScreen, Opacity = 0 })
            {
                owner.Show(); owner.Activate();
                using (var d = new FolderBrowserDialog { SelectedPath = cfg.Folder, Description = "사진을 저장할 폴더를 고르세요", ShowNewFolderButton = true })
                    if (d.ShowDialog(owner) == DialogResult.OK) result = d.SelectedPath;
            }
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        return result;
    }

    // 받은 사진 미리보기 (작게 줄여서)
    byte[] Thumb(string p)
    {
        if (string.IsNullOrEmpty(p) || !p.StartsWith(cfg.Folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(p)) return null;
        if (!Regex.IsMatch(p, @"\.(jpe?g|png|gif|webp|bmp)$", RegexOptions.IgnoreCase)) return null;
        lock (thumbs) { byte[] hit; if (thumbs.TryGetValue(p, out hit)) return hit; }
        var bytes = RenderThumb(p);
        if (bytes != null) lock (thumbs) { if (thumbs.Count > 200) thumbs.Clear(); thumbs[p] = bytes; }
        return bytes;
    }

    // 압축 모드: WebP 는 미리보기로 못 읽어서, 변환 전 원본으로 미리보기를 만들어 둔다
    void PrecacheThumb(string key, string source)
    {
        var bytes = RenderThumb(source);
        if (bytes != null) lock (thumbs) { if (thumbs.Count > 200) thumbs.Clear(); thumbs[key] = bytes; }
    }

    static byte[] RenderThumb(string p)
    {
        try
        {
            using (var img = Image.FromFile(p))
            {
                // 사진 방향(EXIF) 반영
                if (img.PropertyIdList.Contains(0x0112))
                {
                    int o = img.GetPropertyItem(0x0112).Value[0];
                    if (o == 3) img.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    if (o == 6) img.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    if (o == 8) img.RotateFlip(RotateFlipType.Rotate270FlipNone);
                }
                int side = 360; float k = Math.Min(1f, (float)side / Math.Min(img.Width, img.Height));
                int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
                using (var bmp = new Bitmap(w, h))
                {
                    using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, w, h); }
                    using (var ms = new MemoryStream())
                    {
                        var enc = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
                        var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                        bmp.Save(ms, enc, ep);
                        return ms.ToArray();
                    }
                }
            }
        }
        catch { return null; }
    }

    // ── 다운로드 ──
    string Start()
    {
        List<Child> chosen;
        lock (gate)
        {
            if (running) return "already";
            if (login != "ok" || kids.Count == 0) return "login";
            chosen = kids.Values.Where(k => !cfg.SkipKids.Contains(k.Id)).ToList();
        }
        if (chosen.Count == 0) return "nokids";
        if (!cfg.Reports && !cfg.Albums) return "nothing";
        try { Directory.CreateDirectory(cfg.Folder); } catch { return "folder"; }
        lock (gate)
        {
            running = true; stopRequested = false; phase = "listing"; message = "목록을 확인하는 중…";
            total = done = fresh = skipped = failed = freshVideos = 0; bytesIn = bytesOut = 0; startedAt = DateTime.Now; recent.Clear();
        }
        string root = cfg.Folder; bool reports = cfg.Reports, albums = cfg.Albums, videos = cfg.Videos, teacherOnly = cfg.TeacherOnly, compact = cfg.Quality == "compact";
        DateTime? from = cfg.FromDate();
        new Thread(() =>
        {
            try
            {
                RunJob(chosen, root, reports, albums, videos, teacherOnly, compact, from);
                lock (gate) phase = stopRequested ? "stopped" : "done";
            }
            catch (Exception e) { lock (gate) { phase = "error"; message = e.Message; } Log("중단됨: " + e.Message); }
            finally { running = false; }
        }) { IsBackground = true }.Start();
        return "ok";
    }

    List<Dictionary<string, object>> ListAll(string path, string enrollment, DateTime? from, string label)
    {
        var all = new List<Dictionary<string, object>>();
        string next = path;
        while (next != null && !stopRequested)
        {
            var pg = Api(next, enrollment);
            bool older = false;
            foreach (Dictionary<string, object> r in J.Arr(pg, "results"))
            {
                if (from.HasValue && CreatedAt(r) < from.Value) { older = true; continue; }
                all.Add(r);
            }
            lock (gate) message = label + " 확인 중 · " + all.Count + "개";
            string n = J.Str(pg, "next");
            if (older || string.IsNullOrEmpty(n)) break;
            next = n;
        }
        Log(label + " " + all.Count + "개");
        return all;
    }

    static DateTime CreatedAt(Dictionary<string, object> r)
    {
        DateTime d;
        string s = J.Str(r, "created") ?? J.Str(r, "date_written") ?? "";
        return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d.ToLocalTime() : DateTime.MinValue;
    }

    void RunJob(List<Child> chosen, string root, bool reports, bool albums, bool videos, bool teacherOnly, bool compact, DateTime? from)
    {
        var jobs = new List<Job>();
        foreach (var c in chosen)
        {
            string kidDir = Path.Combine(root, Safe(c.ToString()));
            if (reports)
                foreach (var r in ListAll(apiPrefix + "/children/" + c.Id + "/reports/?page_size=100", c.Enrollment, from, c + " 알림장"))
                {
                    if (teacherOnly && J.Str(J.Obj(r, "author"), "type") != "teacher") continue;
                    AddJobs(jobs, r, Path.Combine(kidDir, "알림장"), false, videos, compact);
                }
            if (albums && !stopRequested)
                foreach (var r in ListAll(apiPrefix + "/children/" + c.Id + "/albums/?page_size=100", c.Enrollment, from, c + " 앨범"))
                    AddJobs(jobs, r, Path.Combine(kidDir, "앨범"), true, videos, compact);
        }
        if (stopRequested) return;

        lock (gate) { total = jobs.Count; phase = "downloading"; message = "사진을 받는 중"; }
        Log("받을 파일 " + jobs.Count + "개");
        var queue = new ConcurrentQueue<Job>(jobs);
        var workers = new List<Thread>();
        for (int i = 0; i < 4; i++)
        {
            var t = new Thread(() =>
            {
                Job j;
                while (!stopRequested && queue.TryDequeue(out j))
                {
                    try
                    {
                        if (File.Exists(j.Path)) lock (gate) skipped++;
                        else
                        {
                            Save(j);
                            lock (gate)
                            {
                                fresh++; if (j.Video) freshVideos++;
                                recent.Add(new Dictionary<string, object> { { "path", j.Path }, { "video", j.Video }, { "date", j.When.ToString("yyyy.M.d") } });
                                if (recent.Count > 24) recent.RemoveAt(0);
                            }
                        }
                    }
                    catch (Exception e) { lock (gate) failed++; Log("실패: " + Path.GetFileName(j.Path) + " (" + e.Message + ")"); }
                    lock (gate) done++;
                }
            }) { IsBackground = true };
            t.Start(); workers.Add(t);
        }
        foreach (var t in workers) t.Join();
        Log(stopRequested ? "멈췄어요" : "끝났어요 · 새로 받음 " + fresh + " · 이미 있음 " + skipped + (failed > 0 ? " · 실패 " + failed : ""));
    }

    static void AddJobs(List<Job> jobs, Dictionary<string, object> r, string baseDir, bool album, bool videos, bool compact)
    {
        DateTime when = CreatedAt(r);
        string day = when.ToString("yyyy-MM-dd"), id = J.Str(r, "id") ?? "0";
        string title = album ? (J.Str(r, "title") ?? "") : "";
        string dir = Path.Combine(baseDir, Safe(title.Length > 0 ? day + " " + title : day));
        int n = 0;
        foreach (var o in J.Arr(r, "attached_images"))
        {
            var img = o as Dictionary<string, object>;
            string url = img != null ? (J.Str(img, "original") ?? J.Str(img, "large") ?? J.Str(img, "url")) : o as string;
            if (string.IsNullOrEmpty(url)) continue;
            n++;
            string b = Path.Combine(dir, day + "_" + id + "_" + n.ToString("00")), orig = b + Ext(url, ".jpg");
            jobs.Add(new Job { Url = url, When = when, Orig = orig, Compact = compact, Path = compact && !orig.EndsWith(".gif") ? b + ".webp" : orig });
        }
        if (videos)
        {
            object v; r.TryGetValue("attached_video", out v);
            string vurl = v as string;
            var vd = v as Dictionary<string, object>;
            if (vd != null) vurl = J.Str(vd, "high") ?? J.Str(vd, "original") ?? J.Str(vd, "low");
            if (!string.IsNullOrEmpty(vurl))
            {
                string b = Path.Combine(dir, day + "_" + id + "_video"), orig = b + Ext(vurl, ".mp4");
                jobs.Add(new Job { Url = vurl, When = when, Video = true, Orig = orig, Compact = compact, Path = compact ? b + "_720p.mp4" : orig });
            }
        }
    }

    static string Ext(string url, string def)
    {
        try
        {
            string e = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            return Regex.IsMatch(e, @"^\.(jpe?g|png|gif|webp|heic|mp4|mov|m4v)$") ? (e == ".jpeg" ? ".jpg" : e) : def;
        }
        catch { return def; }
    }

    static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        return s.Length > 80 ? s.Substring(0, 80).Trim() : (s.Length == 0 ? "제목 없음" : s);
    }

    // 동영상 압축은 무거워서 한 번에 하나씩
    static readonly SemaphoreSlim videoGate = new SemaphoreSlim(1);

    // 원본 그대로 저장하거나, '용량 줄여서'면 받은 뒤 사진은 WebP, 동영상은 720p MP4 로 바꿔 저장한다.
    // 예전에 원본으로 받아 둔 파일이 있으면 다시 받지 않고 그걸로 변환한다 (원본은 그대로 둠).
    // 변환이 안 되면 원본이라도 남긴다.
    void Save(Job j)
    {
        if (!j.Compact || j.Path == j.Orig) { Download(j.Url, j.Path, j.When); return; }
        bool haveOrig = File.Exists(j.Orig);
        string src = haveOrig ? j.Orig : j.Path + ".src" + Path.GetExtension(j.Orig);
        if (!haveOrig) Download(j.Url, src, j.When);
        string tmp = j.Path + ".part";
        try
        {
            if (j.Video)
            {
                if (!VideoCompressor.Available) throw new Exception("이 PC에서는 동영상 압축을 쓸 수 없어요");
                if (!VideoCompressor.WorthCompressing(src)) File.Copy(src, tmp, true);   // 이미 720p 이하·저용량이면 그대로
                else
                {
                    videoGate.Wait();
                    try { VideoCompressor.Compress(src, tmp); } finally { videoGate.Release(); }
                }
                // 이미 작은 영상이라 더 커졌으면 원본 내용을 그대로 쓴다
                if (new FileInfo(tmp).Length >= new FileInfo(src).Length) File.Copy(src, tmp, true);
            }
            else
            {
                if (!WebpEncoder.Available) throw new Exception("WebP 변환 도구를 찾을 수 없어요");
                PrecacheThumb(j.Path, src);
                WebpEncoder.Convert(src, tmp, 80f);
            }
            if (File.Exists(j.Path)) File.Delete(j.Path);
            File.Move(tmp, j.Path);
            SetTimes(j.Path, j.When);
            lock (gate) { bytesIn += new FileInfo(src).Length; bytesOut += new FileInfo(j.Path).Length; }
            if (!haveOrig) try { File.Delete(src); } catch { }
        }
        catch (Exception e)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            if (!haveOrig) { if (File.Exists(j.Orig)) File.Delete(src); else File.Move(src, j.Orig); }
            Log("압축하지 못해 원본으로 저장: " + Path.GetFileName(j.Orig) + " (" + e.Message + ")");
            throw new Exception("압축 실패 · 원본으로 저장함");
        }
    }

    // 임시 파일로 받은 뒤 이름 바꾸기 → 중간에 끊겨도 깨진 파일이 남지 않음. 파일 날짜는 글 올린 날짜로
    static void Download(string url, string path, DateTime when)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".part";
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0";
        req.Referer = KnSite + "/";
        req.Timeout = 60000; req.ReadWriteTimeout = 120000;
        using (var res = req.GetResponse())
        using (var s = res.GetResponseStream())
        using (var f = File.Create(tmp)) s.CopyTo(f);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        SetTimes(path, when);
    }

    static void SetTimes(string path, DateTime when)
    {
        if (when > DateTime.MinValue) try { File.SetCreationTime(path, when); File.SetLastWriteTime(path, when); } catch { }
    }
}
