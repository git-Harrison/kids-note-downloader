using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// 키즈노트 사진·동영상 다운로더 (개인용, 비공식)
// 로그인: 진짜 키즈노트 로그인 페이지를 Edge 창으로 띄우고, 사용자가 직접 로그인한다. 비밀번호는 이 프로그램을 거치지 않는다.
// 목록: 로그인된 Edge 창 안에서 키즈노트 API 를 호출한다 (Chrome DevTools Protocol). 사이트가 보내는 X-ENROLLMENT 헤더를 엿들어 쓴다.
// 파일: 원본 이미지/동영상 주소에서 바로 받는다. 이미 받은 파일은 건너뛴다.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--uninstall") { Uninstall(); return; }
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 16;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    // 설정 > 앱 에서 제거할 때 호출됨. 받은 사진은 지우지 않고, 로그인 정보·설정은 지운다.
    static void Uninstall()
    {
        if (MessageBox.Show("키즈노트 다운로더를 제거할까요?\n\n받아 둔 사진과 동영상은 지워지지 않아요.\n이 PC에 남은 키즈노트 로그인 정보는 함께 지워져요.",
            Install.Name + " 제거", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Install.KillRunning();
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            try { if (p.MainModule.FileName != null && GetCmd(p).Contains(Install.DataDir)) p.Kill(); } catch { }
        }
        Install.Unregister();
        try { Directory.Delete(Install.DataDir, true); } catch { }
        string me = Application.ExecutablePath, dir = Path.GetDirectoryName(me);
        if (string.Equals(dir, Install.Dir, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"") { CreateNoWindow = true, UseShellExecute = false });
        MessageBox.Show("키즈노트 다운로더를 제거했어요.", Install.Name);
    }

    static string GetCmd(Process p)
    {
        try
        {
            using (var s = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + p.Id))
                foreach (System.Management.ManagementObject o in s.Get()) return (o["CommandLine"] ?? "").ToString();
        }
        catch { }
        return "";
    }
}

class Child
{
    public string Id, Enrollment, Name;
    public override string ToString() { return string.IsNullOrEmpty(Name) ? "아이 #" + Id : Name; }
}

class Job
{
    public string Url, Path; public DateTime When;
}

// ── Chrome DevTools Protocol 최소 구현 ──
class Cdp : IDisposable
{
    readonly ClientWebSocket ws = new ClientWebSocket();
    readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
    readonly SemaphoreSlim sendLock = new SemaphoreSlim(1);
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
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
                var msg = Js.DeserializeObject(text) as Dictionary<string, object>;
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
        foreach (var p in pending.Values) p.TrySetException(new Exception("Edge 창과 연결이 끊겼어요."));
        if (OnClosed != null) OnClosed();
    }

    public Dictionary<string, object> Send(string method, object prms, int timeoutMs = 120000)
    {
        int id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<Dictionary<string, object>>();
        pending[id] = tcs;
        byte[] data = Encoding.UTF8.GetBytes(Js.Serialize(new Dictionary<string, object> { { "id", id }, { "method", method }, { "params", prms ?? new Dictionary<string, object>() } }));
        sendLock.Wait();
        try { ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
        finally { sendLock.Release(); }
        if (!tcs.Task.Wait(timeoutMs)) { pending.TryRemove(id, out tcs); throw new Exception("Edge 응답이 없어요 (" + method + ")"); }
        var res = tcs.Task.Result;
        if (res.ContainsKey("error")) throw new Exception("Edge 오류: " + Js.Serialize(res["error"]));
        return J.Obj(res, "result");
    }

    // 페이지 안에서 JS 실행 후 문자열 결과
    public string Eval(string expr, int timeoutMs = 120000)
    {
        var r = Send("Runtime.evaluate", new Dictionary<string, object> { { "expression", expr }, { "awaitPromise", true }, { "returnByValue", true } }, timeoutMs);
        if (r.ContainsKey("exceptionDetails")) throw new Exception("페이지 스크립트 오류: " + Js.Serialize(r["exceptionDetails"]));
        var v = J.Obj(r, "result");
        return v != null && v.ContainsKey("value") && v["value"] != null ? v["value"].ToString() : null;
    }

    public void Dispose() { try { ws.Dispose(); } catch { } }
}

// JSON 도우미
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
}

class MainForm : Form
{
    static readonly string AppDir = Install.DataDir;
    static readonly string SettingsFile = Path.Combine(AppDir, "settings.txt");
    const string KnSite = "https://www.kidsnote.com";

    readonly Font fBody = new Font("Malgun Gothic", 9.5f);
    readonly Font fBold = new Font("Malgun Gothic", 9.5f, FontStyle.Bold);
    readonly Font fTitle = new Font("Malgun Gothic", 14f, FontStyle.Bold);
    readonly Color accent = Color.FromArgb(47, 111, 237), muted = Color.FromArgb(110, 116, 128), ink = Color.FromArgb(31, 35, 41);

    Label lblLogin, lblProgress;
    Button btnLogin, btnStart, btnFolder, btnOpen;
    CheckedListBox lstKids;
    TextBox txtFolder, txtLog;
    CheckBox cbReports, cbAlbums, cbVideos, cbTeacherOnly, cbAll;
    DateTimePicker dtFrom;
    ProgressBar bar;

    Process edge;
    Cdp cdp;
    readonly Dictionary<string, Child> kids = new Dictionary<string, Child>();
    volatile bool running, stopRequested;

    public MainForm()
    {
        Text = "키즈노트 사진 다운로더";
        Font = fBody; BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(620, 640);
        MinimumSize = new Size(560, 600);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int x = 24, y = 20, w = 572;
        Controls.Add(new Label { Text = "키즈노트 사진 다운로더", Font = fTitle, ForeColor = ink, Location = new Point(x, y), AutoSize = true });
        y += 40;
        Controls.Add(new Label { Text = "알림장·앨범의 사진과 동영상을 원본 그대로 내 PC에 저장해요. 개인 보관용이에요.", ForeColor = muted, Location = new Point(x, y), AutoSize = true });
        y += 34;

        // 1. 로그인
        Controls.Add(Section("1. 키즈노트 로그인", x, y)); y += 26;
        btnLogin = Btn("로그인 창 열기", true); btnLogin.Location = new Point(x, y); btnLogin.Click += (s, e) => StartLogin();
        Controls.Add(btnLogin);
        lblLogin = new Label { Text = "키즈노트 창에서 평소처럼 로그인하세요.\n비밀번호는 이 프로그램에 저장되지 않아요.", ForeColor = muted, Location = new Point(x + 150, y + 2), Size = new Size(w - 150, 36) };
        Controls.Add(lblLogin);
        y += 44;
        Controls.Add(new Label { Text = "받을 아이 (아이가 여럿이면 키즈노트 창에서 아이를 바꾸면 여기에 추가돼요)", ForeColor = muted, Location = new Point(x, y), AutoSize = true });
        y += 22;
        lstKids = new CheckedListBox { Location = new Point(x, y), Size = new Size(w, 52), CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(lstKids);
        y += 66;

        // 2. 옵션
        Controls.Add(Section("2. 받을 내용", x, y)); y += 26;
        cbReports = Chk("알림장 사진", x, y, true); cbAlbums = Chk("앨범 사진", x + 130, y, true); cbVideos = Chk("동영상", x + 250, y, true);
        cbTeacherOnly = Chk("알림장은 선생님 글만", x + 350, y, true);
        Controls.AddRange(new Control[] { cbReports, cbAlbums, cbVideos, cbTeacherOnly });
        y += 30;
        cbAll = Chk("처음부터 전부", x, y + 2, true);
        dtFrom = new DateTimePicker { Location = new Point(x + 130, y), Width = 150, Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddMonths(-1), Enabled = false };
        cbAll.CheckedChanged += (s, e) => dtFrom.Enabled = !cbAll.Checked;
        Controls.Add(cbAll); Controls.Add(dtFrom);
        Controls.Add(new Label { Text = "이후 글만", ForeColor = muted, Location = new Point(x + 288, y + 4), AutoSize = true });
        y += 38;

        // 3. 저장 위치
        Controls.Add(Section("3. 저장 위치", x, y)); y += 26;
        txtFolder = new TextBox { Location = new Point(x, y + 1), Width = w - 110, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        btnFolder = Btn("찾아보기", false); btnFolder.Location = new Point(x + w - 100, y - 2); btnFolder.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnFolder.Click += (s, e) =>
        {
            using (var d = new FolderBrowserDialog { SelectedPath = txtFolder.Text, Description = "사진을 저장할 폴더를 고르세요" })
                if (d.ShowDialog(this) == DialogResult.OK) txtFolder.Text = d.SelectedPath;
        };
        Controls.Add(txtFolder); Controls.Add(btnFolder);
        y += 44;

        // 4. 시작
        btnStart = Btn("다운로드 시작", true); btnStart.Location = new Point(x, y); btnStart.Width = 140; btnStart.Click += (s, e) => StartOrStop();
        btnOpen = Btn("폴더 열기", false); btnOpen.Location = new Point(x + 150, y); btnOpen.Click += (s, e) =>
        {
            try { Directory.CreateDirectory(txtFolder.Text); Process.Start("explorer.exe", "\"" + txtFolder.Text + "\""); } catch { }
        };
        Controls.Add(btnStart); Controls.Add(btnOpen);
        y += 46;
        bar = new ProgressBar { Location = new Point(x, y), Size = new Size(w, 10), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(bar);
        y += 16;
        lblProgress = new Label { Location = new Point(x, y), Size = new Size(w, 20), ForeColor = ink, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(lblProgress);
        y += 26;
        txtLog = new TextBox { Location = new Point(x, y), Size = new Size(w, ClientSize.Height - y - 20), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(247, 247, 248), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Malgun Gothic", 9f), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(txtLog);

        LoadSettings();
        FormClosing += (s, e) =>
        {
            if (running && MessageBox.Show(this, "다운로드 중이에요. 닫을까요?\n다음에 다시 시작하면 받은 파일은 건너뛰고 이어서 받아요.", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) { e.Cancel = true; return; }
            stopRequested = true; SaveSettings(); CloseEdge();
        };
    }

    // ── UI 도우미 ──
    Label Section(string t, int x, int y) { return new Label { Text = t, Font = fBold, ForeColor = ink, Location = new Point(x, y), AutoSize = true }; }
    CheckBox Chk(string t, int x, int y, bool on) { return new CheckBox { Text = t, Location = new Point(x, y), AutoSize = true, Checked = on, ForeColor = ink }; }
    Button Btn(string t, bool primary)
    {
        var b = new Button { Text = t, Size = new Size(140, 34), FlatStyle = FlatStyle.Flat, Font = primary ? fBold : fBody, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = primary ? accent : Color.FromArgb(214, 217, 222);
        b.BackColor = primary ? accent : Color.White; b.ForeColor = primary ? Color.White : ink;
        if (!primary) b.Width = 96;
        return b;
    }
    void Ui(Action a) { if (IsDisposed) return; if (InvokeRequired) { try { BeginInvoke(a); } catch { } } else a(); }
    void Log(string s) { Ui(() => { txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n"); }); }
    void SetLogin(string s, bool ok) { Ui(() => { lblLogin.Text = s; lblLogin.ForeColor = ok ? Color.FromArgb(26, 127, 55) : muted; }); }

    void LoadSettings()
    {
        txtFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "키즈노트");
        try
        {
            foreach (var line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
            {
                int i = line.IndexOf('='); if (i < 0) continue;
                string k = line.Substring(0, i), v = line.Substring(i + 1);
                if (k == "folder" && v.Length > 0) txtFolder.Text = v;
                if (k == "reports") cbReports.Checked = v == "1";
                if (k == "albums") cbAlbums.Checked = v == "1";
                if (k == "videos") cbVideos.Checked = v == "1";
                if (k == "teacher") cbTeacherOnly.Checked = v == "1";
                if (k == "all") cbAll.Checked = v == "1";
                DateTime d; if (k == "from" && DateTime.TryParse(v, out d)) dtFrom.Value = d;
            }
        }
        catch { }
    }
    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllLines(SettingsFile, new[] {
                "folder=" + txtFolder.Text, "reports=" + B(cbReports), "albums=" + B(cbAlbums), "videos=" + B(cbVideos),
                "teacher=" + B(cbTeacherOnly), "all=" + B(cbAll), "from=" + dtFrom.Value.ToString("yyyy-MM-dd") }, Encoding.UTF8);
        }
        catch { }
    }
    static string B(CheckBox c) { return c.Checked ? "1" : "0"; }

    // ── 1. 로그인: Edge 창 + 디버깅 연결 ──
    void StartLogin()
    {
        if (cdp != null && cdp.Alive) { FocusEdge(); return; }
        btnLogin.Enabled = false;
        SetLogin("키즈노트 창을 여는 중…", false);
        new Thread(() =>
        {
            try { OpenEdge(); WatchLogin(); }
            catch (Exception e) { SetLogin("로그인 창을 열지 못했어요: " + e.Message, false); Log("오류: " + e.Message); }
            finally { Ui(() => btnLogin.Enabled = true); }
        }) { IsBackground = true }.Start();
    }

    void OpenEdge()
    {
        string exe = null;
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (root == null) continue;
            string c = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(c)) { exe = c; break; }
        }
        if (exe == null) throw new Exception("Microsoft Edge를 찾을 수 없어요.");
        int port; var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        string profile = Path.Combine(AppDir, "edge");   // 로그인 상태가 여기에 남아서 다음엔 바로 로그인돼 있음
        Directory.CreateDirectory(profile);
        edge = Process.Start(exe, "--remote-debugging-port=" + port + " --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check " +
            "--window-size=1100,860 --app=" + KnSite + "/service/report");

        string wsUrl = null;
        for (int i = 0; i < 100 && wsUrl == null; i++)
        {
            Thread.Sleep(200);
            try
            {
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                    foreach (Dictionary<string, object> t in (object[])J.Js.DeserializeObject(wc.DownloadString("http://127.0.0.1:" + port + "/json/list")))
                        if (J.Str(t, "type") == "page" && (J.Str(t, "url") ?? "").Contains("kidsnote")) { wsUrl = J.Str(t, "webSocketDebuggerUrl"); break; }
            }
            catch { }
        }
        if (wsUrl == null) throw new Exception("Edge 창에 연결하지 못했어요.");
        cdp = new Cdp();
        cdp.OnEvent += OnCdpEvent;
        cdp.OnClosed += () => { SetLogin("키즈노트 창이 닫혔어요. 다시 [로그인 창 열기]를 눌러 주세요.", false); };
        cdp.Connect(wsUrl);
        cdp.Send("Network.enable", null);
        try { cdp.Send("Page.reload", null, 10000); } catch { }   // 연결 전에 지나간 API 요청을 다시 잡기 위해
        SetLogin("키즈노트 창에서 로그인하세요. 로그인되면 자동으로 이어져요.", false);
    }

    // 사이트가 보내는 API 요청에서 아이 번호와 X-ENROLLMENT 를 엿듣는다
    void OnCdpEvent(string method, Dictionary<string, object> p)
    {
        if (method != "Network.requestWillBeSent") return;
        var req = J.Obj(p, "request"); string url = J.Str(req, "url") ?? "";
        if (!url.Contains("/api/")) return;
        var m = Regex.Match(url, @"/children/(\d+)/");
        string enr = null;
        var h = J.Obj(req, "headers");
        if (h != null) foreach (var kv in h) if (string.Equals(kv.Key, "X-ENROLLMENT", StringComparison.OrdinalIgnoreCase) && kv.Value != null) enr = kv.Value.ToString();
        if (!m.Success || string.IsNullOrEmpty(enr)) return;
        string id = m.Groups[1].Value;
        lock (kids)
        {
            Child c;
            if (kids.TryGetValue(id, out c)) { c.Enrollment = enr; return; }
            c = new Child { Id = id, Enrollment = enr };
            kids[id] = c;
        }
        Log("아이 #" + id + " 확인");
        new Thread(() => FillName(id)) { IsBackground = true }.Start();
    }

    void FillName(string id)
    {
        Child c; lock (kids) c = kids[id];
        try
        {
            var page = Api("/api/v1_2/children/" + id + "/reports/?page_size=1", c.Enrollment);
            foreach (Dictionary<string, object> r in J.Arr(page, "results")) { c.Name = J.Str(r, "child_name"); break; }
        }
        catch (Exception e) { Log("아이 이름을 못 가져왔어요: " + e.Message); }
        Ui(() =>
        {
            int i = -1;
            for (int k = 0; k < lstKids.Items.Count; k++) if (((Child)lstKids.Items[k]).Id == id) i = k;
            if (i < 0) lstKids.Items.Add(c, true); else lstKids.Items[i] = c;
            lblLogin.Text = "로그인됨 · " + string.Join(", ", lstKids.Items.Cast<Child>().Select(x => x.ToString()));
            lblLogin.ForeColor = Color.FromArgb(26, 127, 55);
        });
    }

    // 로그인 페이지를 벗어났는데 API 요청이 안 잡히면 알림장 화면으로 보낸다
    void WatchLogin()
    {
        int idle = 0;
        while (cdp != null && cdp.Alive)
        {
            Thread.Sleep(1500);
            lock (kids) if (kids.Count > 0) return;
            string href = null;
            try { href = cdp.Eval("location.href", 5000); } catch { }
            if (href == null || href.Contains("login")) { idle = 0; continue; }
            if (++idle == 3)
            {
                try { cdp.Send("Page.navigate", new Dictionary<string, object> { { "url", KnSite + "/service/report" } }); } catch { }
                idle = 0;
            }
        }
    }

    void FocusEdge()
    {
        try { if (edge != null && !edge.HasExited) Microsoft.VisualBasic.Interaction.AppActivate(edge.Id); } catch { }
    }
    void CloseEdge()
    {
        try { if (cdp != null && cdp.Alive) cdp.Send("Browser.close", null, 3000); } catch { }
        try { if (cdp != null) cdp.Dispose(); } catch { }
    }

    // 로그인된 페이지 안에서 API 호출
    Dictionary<string, object> Api(string path, string enrollment)
    {
        string expr = "(async()=>{const r=await fetch(" + J.Js.Serialize(path) + ",{credentials:'include',headers:{'Accept':'application/json','X-ENROLLMENT':" + J.Js.Serialize(enrollment) + "}});" +
                      "return JSON.stringify({s:r.status,t:await r.text()});})()";
        string raw = cdp.Eval(expr);
        var o = J.Js.DeserializeObject(raw) as Dictionary<string, object>;
        int status = Convert.ToInt32(o["s"]);
        if (status == 401 || status == 403) throw new Exception("로그인이 풀렸어요. 키즈노트 창에서 다시 로그인해 주세요.");
        if (status != 200) throw new Exception("키즈노트 응답 " + status);
        return J.Js.DeserializeObject((string)o["t"]) as Dictionary<string, object>;
    }

    // 다음 페이지를 따라가며 전부 모은다. from 보다 오래된 글이 나오면 멈춘다(최신순 정렬)
    List<Dictionary<string, object>> ListAll(string path, string enrollment, DateTime? from, string label)
    {
        var all = new List<Dictionary<string, object>>();
        string next = path;
        while (next != null && !stopRequested)
        {
            var page = Api(next, enrollment);
            bool older = false;
            foreach (Dictionary<string, object> r in J.Arr(page, "results"))
            {
                if (from.HasValue && CreatedAt(r) < from.Value) { older = true; continue; }
                all.Add(r);
            }
            Log(label + " 목록 " + all.Count + "개…");
            string n = J.Str(page, "next");
            if (older || string.IsNullOrEmpty(n)) break;
            next = n.StartsWith(KnSite) ? n.Substring(KnSite.Length) : n;
        }
        return all;
    }

    static DateTime CreatedAt(Dictionary<string, object> r)
    {
        DateTime d;
        string s = J.Str(r, "created") ?? J.Str(r, "date_written") ?? "";
        return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d.ToLocalTime() : DateTime.MinValue;
    }

    // ── 2. 다운로드 ──
    void StartOrStop()
    {
        if (running) { stopRequested = true; btnStart.Text = "멈추는 중…"; btnStart.Enabled = false; return; }
        var chosen = lstKids.CheckedItems.Cast<Child>().ToList();
        if (cdp == null || !cdp.Alive || chosen.Count == 0)
        {
            MessageBox.Show(this, "먼저 [로그인 창 열기]로 키즈노트에 로그인해 주세요.", Text); return;
        }
        if (!cbReports.Checked && !cbAlbums.Checked) { MessageBox.Show(this, "알림장이나 앨범 중 하나는 골라 주세요.", Text); return; }
        string root = txtFolder.Text.Trim();
        try { Directory.CreateDirectory(root); } catch (Exception e) { MessageBox.Show(this, "저장 폴더를 만들 수 없어요.\n" + e.Message, Text); return; }
        SaveSettings();
        bool reports = cbReports.Checked, albums = cbAlbums.Checked, videos = cbVideos.Checked, teacherOnly = cbTeacherOnly.Checked;
        DateTime? from = cbAll.Checked ? (DateTime?)null : dtFrom.Value.Date;
        running = true; stopRequested = false;
        btnStart.Text = "중지"; bar.Value = 0;
        new Thread(() =>
        {
            try { Run(chosen, root, reports, albums, videos, teacherOnly, from); }
            catch (Exception e) { Log("중단됨: " + e.Message); }
            finally
            {
                running = false;
                Ui(() => { btnStart.Text = "다운로드 시작"; btnStart.Enabled = true; });
            }
        }) { IsBackground = true }.Start();
    }

    void Run(List<Child> chosen, string root, bool reports, bool albums, bool videos, bool teacherOnly, DateTime? from)
    {
        var jobs = new List<Job>();
        foreach (var c in chosen)
        {
            string kidDir = Path.Combine(root, Safe(c.ToString()));
            if (reports)
            {
                var list = ListAll("/api/v1_2/children/" + c.Id + "/reports/?page_size=100", c.Enrollment, from, c + " 알림장");
                foreach (var r in list)
                {
                    if (teacherOnly && J.Str(J.Obj(r, "author"), "type") != "teacher") continue;
                    AddJobs(jobs, r, Path.Combine(kidDir, "알림장"), false, videos);
                }
            }
            if (albums && !stopRequested)
            {
                var list = ListAll("/api/v1_2/children/" + c.Id + "/albums/?page_size=100", c.Enrollment, from, c + " 앨범");
                foreach (var r in list) AddJobs(jobs, r, Path.Combine(kidDir, "앨범"), true, videos);
            }
        }
        if (stopRequested) { Log("멈췄어요."); return; }

        int total = jobs.Count, done = 0, fresh = 0, skipped = 0, failed = 0;
        Log("받을 파일 " + total + "개");
        Action report = () => Ui(() =>
        {
            bar.Maximum = Math.Max(1, total); bar.Value = Math.Min(done, bar.Maximum);
            lblProgress.Text = done + " / " + total + "   ·   새로 받음 " + fresh + "   ·   이미 있음 " + skipped + (failed > 0 ? "   ·   실패 " + failed : "");
        });
        report();
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
                        if (File.Exists(j.Path)) Interlocked.Increment(ref skipped);
                        else { Download(j); Interlocked.Increment(ref fresh); }
                    }
                    catch (Exception e) { Interlocked.Increment(ref failed); Log("실패: " + Path.GetFileName(j.Path) + " (" + e.Message + ")"); }
                    Interlocked.Increment(ref done);
                    report();
                }
            }) { IsBackground = true };
            t.Start(); workers.Add(t);
        }
        foreach (var t in workers) t.Join();
        report();
        Log(stopRequested ? "멈췄어요. 다시 시작하면 이어서 받아요." : "끝났어요. 새로 받음 " + fresh + "개, 이미 있음 " + skipped + "개" + (failed > 0 ? ", 실패 " + failed + "개 (다시 시작하면 실패한 것만 다시 받아요)" : ""));
    }

    // 글 하나의 사진·동영상을 작업 목록에 추가
    static void AddJobs(List<Job> jobs, Dictionary<string, object> r, string baseDir, bool album, bool videos)
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
            jobs.Add(new Job { Url = url, When = when, Path = Path.Combine(dir, day + "_" + id + "_" + n.ToString("00") + Ext(url, ".jpg")) });
        }
        if (videos)
        {
            object v; r.TryGetValue("attached_video", out v);
            string vurl = v as string;
            var vd = v as Dictionary<string, object>;
            if (vd != null) vurl = J.Str(vd, "high") ?? J.Str(vd, "original") ?? J.Str(vd, "low");
            if (!string.IsNullOrEmpty(vurl))
                jobs.Add(new Job { Url = vurl, When = when, Path = Path.Combine(dir, day + "_" + id + "_video" + Ext(vurl, ".mp4")) });
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

    // 임시 파일로 받은 뒤 이름 바꾸기 → 중간에 끊겨도 깨진 파일이 남지 않음. 파일 날짜는 글 올린 날짜로
    static void Download(Job j)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(j.Path));
        string tmp = j.Path + ".part";
        var req = (HttpWebRequest)WebRequest.Create(j.Url);
        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0";
        req.Referer = KnSite + "/";
        req.Timeout = 60000; req.ReadWriteTimeout = 120000;
        using (var res = req.GetResponse())
        using (var s = res.GetResponseStream())
        using (var f = File.Create(tmp)) s.CopyTo(f);
        if (File.Exists(j.Path)) File.Delete(j.Path);
        File.Move(tmp, j.Path);
        if (j.When > DateTime.MinValue) try { File.SetCreationTime(j.Path, j.When); File.SetLastWriteTime(j.Path, j.When); } catch { }
    }
}
