using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// 앱 화면을 최소화하면 화면 오른쪽 아래에 뜨는 작은 진행 알림.
// 별도 프로세스(--toast <port> <token>)로 돌며 /api/state 를 읽어 그린다. 누르면 앱 화면을 다시 연다.
class ProgressToast : Form
{
    public static void HelperMain(string[] args)
    {
        if (args.Length < 3) return;
        Application.EnableVisualStyles();
        Application.Run(new ProgressToast(args[1], args[2]));
    }

    readonly string baseUrl, token;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 33 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly object sync = new object();
    // 서버에서 읽은 값
    bool running, uiMin, uiAlive = true; string phase = ""; int total, done, fresh; double elapsed;
    int misses; DateTime doneUntil = DateTime.MinValue; bool wasRunning;
    float shownPct;   // 막대가 부드럽게 따라가도록

    const int W = 340, H = 104;
    static readonly Color Accent = Color.FromArgb(0xf4, 0x80, 0x3a), Accent2 = Color.FromArgb(0xff, 0xb3, 0x47);
    static readonly Color Fg = Color.FromArgb(0x17, 0x19, 0x1e), Fg2 = Color.FromArgb(0x6a, 0x70, 0x7b), Track = Color.FromArgb(0xf1, 0xf2, 0xf5);

    ProgressToast(string port, string token)
    {
        baseUrl = "http://127.0.0.1:" + port; this.token = token;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H); BackColor = Color.White; DoubleBuffered = true; Cursor = Cursors.Hand;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - W - 16, wa.Bottom - H - 16);
        Opacity = 0;
        anim.Tick += (s, e) => Tick();
        Click += (s, e) => RestoreUi();
        new Thread(PollLoop) { IsBackground = true }.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/;   // 항상 위 아님: 다른 창을 쓰면 그 아래로
            cp.ClassStyle |= 0x20000; // 그림자
            return cp;
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }   // Windows 11 둥근 모서리
        anim.Start();
    }

    // 처음엔 숨겨 두기
    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(IsHandleCreated ? value : false); if (!IsHandleCreated) CreateHandle(); }

    void PollLoop()
    {
        var js = new JavaScriptSerializer();
        while (true)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/state");
                req.Method = "POST"; req.Headers["X-Token"] = token; req.ContentType = "application/json"; req.Timeout = 3000;
                byte[] body = Encoding.UTF8.GetBytes("{\"peek\":1}"); req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                string text; using (var r = req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd();
                var st = js.DeserializeObject(text) as Dictionary<string, object>;
                var job = st["job"] as Dictionary<string, object>;
                IntPtr ui = FindUi();
                lock (sync)
                {
                    running = (bool)job["running"]; phase = job["phase"] as string ?? "";
                    total = Convert.ToInt32(job["total"]); done = Convert.ToInt32(job["done"]); fresh = Convert.ToInt32(job["fresh"]);
                    elapsed = Convert.ToDouble(job["elapsed"]);
                    uiAlive = ui != IntPtr.Zero; uiMin = uiAlive && IsIconic(ui);
                    if (wasRunning && !running && phase == "done") doneUntil = DateTime.Now.AddSeconds(8);
                    wasRunning = running;
                }
                misses = 0;
            }
            catch { if (++misses > 8) { try { BeginInvoke((Action)(() => Application.Exit())); } catch { } return; } }
            Thread.Sleep(600);
        }
    }

    void Tick()
    {
        bool show; float pct;
        lock (sync)
        {
            show = uiMin && (running || DateTime.Now < doneUntil);
            pct = total > 0 ? (float)done / total : 0;
            if (!running && phase == "done") pct = 1;
        }
        shownPct += (pct - shownPct) * 0.12f;
        // 부드럽게 나타나고 사라지기
        double target = show ? 1 : 0;
        if (show && !Visible) { Opacity = 0; Show(); }
        Opacity = Math.Max(0, Math.Min(1, Opacity + (target - Opacity) * 0.25));
        if (!show && Visible && Opacity < 0.03) Hide();
        if (Visible) Invalidate();
    }

    void RestoreUi()
    {
        IntPtr ui = FindUi();
        if (ui != IntPtr.Zero) { ShowWindow(ui, 9); SetForegroundWindow(ui); }
        Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.White);
        float t = (float)clock.Elapsed.TotalSeconds;
        bool isRunning, listing, finished; int tot, dn, fr; double el;
        lock (sync) { isRunning = running; listing = phase == "listing"; finished = !running && phase == "done"; tot = total; dn = done; fr = fresh; el = elapsed; }

        // 글자
        string title = finished ? "다 받았어요!" : listing ? "목록 확인 중" : "사진 받는 중 · " + (int)Math.Floor(shownPct * 100 + 0.001) + "%";
        string sub;
        if (finished) sub = "새로 받은 파일 " + fr.ToString("N0") + "개 · 눌러서 열기";
        else if (listing || tot == 0) sub = "받을 사진을 찾고 있어요";
        else
        {
            double rate = dn / Math.Max(1, el), left = rate > 0 ? (tot - dn) / rate : 0;
            sub = dn.ToString("N0") + " / " + tot.ToString("N0") + (left > 5 ? " · 약 " + (left < 60 ? Math.Ceiling(left) + "초" : Math.Ceiling(left / 60) + "분") + " 남음" : "");
        }
        using (var f1 = new Font("Malgun Gothic", 10.5f, FontStyle.Bold))
        using (var f2 = new Font("Malgun Gothic", 9f))
        using (var b1 = new SolidBrush(Fg)) using (var b2 = new SolidBrush(Fg2))
        {
            g.DrawString(title, f1, b1, 16, 12);
            g.DrawString(sub, f2, b2, 17, 33);
        }

        // 진행 막대
        float bx = 18, by = 80, bw = W - 36, bh = 10;
        using (var path = Round(bx, by, bw, bh, 5)) using (var tb = new SolidBrush(Track)) g.FillPath(tb, path);
        float fillW = listing ? 0 : Math.Max(bh, bw * shownPct);
        if (fillW > 0)
            using (var path = Round(bx, by, fillW, bh, 5))
            using (var lb = new LinearGradientBrush(new RectangleF(bx, by, bw, bh), Accent2, Accent, 0f)) g.FillPath(lb, path);

        // 캐릭터: 막대 위를 달리는 병아리 (목록 확인 중엔 왼쪽에서 제자리 뛰기)
        float cx = listing ? bx + 22 : bx + Math.Max(16, Math.Min(bw - 16, fillW));
        DrawChick(g, cx, by - 2, t, finished);
    }

    static void DrawChick(Graphics g, float x, float ground, float t, bool happy)
    {
        float speed = happy ? 6f : 12f;
        float bob = (float)Math.Abs(Math.Sin(t * speed)) * (happy ? 7f : 3.5f);
        float y = ground - 13 - bob;   // 몸 중심
        var body = Color.FromArgb(0xff, 0xd2, 0x3f); var bodyDark = Color.FromArgb(0xf5, 0xb7, 0x1b);
        // 발 (번갈아)
        float step = (float)Math.Sin(t * speed);
        using (var pen = new Pen(Color.FromArgb(0xf4, 0x80, 0x3a), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, x - 3, y + 10, x - 3 + step * 3, ground - 0.5f);
            g.DrawLine(pen, x + 3, y + 10, x + 3 - step * 3, ground - 0.5f);
        }
        // 그림자 + 몸
        using (var sh = new SolidBrush(Color.FromArgb(28, 0, 0, 0))) g.FillEllipse(sh, x - 9 + bob * 0.3f, ground - 1.5f, 18 - bob * 0.6f, 3.5f);
        using (var bb = new LinearGradientBrush(new RectangleF(x - 12, y - 12, 24, 24), body, bodyDark, 90f)) g.FillEllipse(bb, x - 12, y - 12, 24, 24);
        // 머리깃
        using (var hb = new SolidBrush(bodyDark)) g.FillEllipse(hb, x - 2, y - 15, 5, 6);
        // 날개 (파닥)
        float flap = (float)Math.Sin(t * speed * 2) * 25f;
        var st = g.Save();
        g.TranslateTransform(x - 4, y + 2); g.RotateTransform(-20 + flap);
        using (var wb = new SolidBrush(bodyDark)) g.FillEllipse(wb, -7, -3, 10, 7);
        g.Restore(st);
        // 눈 + 볼 + 부리
        using (var eb = new SolidBrush(Color.FromArgb(0x2a, 0x22, 0x1c)))
        {
            if (happy) using (var ep = new Pen(eb, 1.6f)) g.DrawArc(ep, x + 2.5f, y - 5.5f, 5, 4, 180, 180);
            else g.FillEllipse(eb, x + 3.5f, y - 5, 3.4f, 3.8f);
        }
        using (var wb = new SolidBrush(Color.White)) if (!happy) g.FillEllipse(wb, x + 4.6f, y - 4.4f, 1.3f, 1.3f);
        using (var cb = new SolidBrush(Color.FromArgb(110, 0xff, 0x7a, 0x7a))) g.FillEllipse(cb, x + 1.5f, y + 0.5f, 5, 3);
        using (var bk = new SolidBrush(Color.FromArgb(0xf4, 0x80, 0x3a)))
            g.FillPolygon(bk, new[] { new PointF(x + 10.5f, y - 3), new PointF(x + 16, y - 1), new PointF(x + 10.5f, y + 1) });
        // 달릴 때 뒤로 먼지
        if (!happy)
            for (int i = 0; i < 3; i++)
            {
                float p = (t * 1.6f + i / 3f) % 1f;
                int a = (int)(90 * (1 - p));
                using (var db = new SolidBrush(Color.FromArgb(a, 0xc9, 0xcd, 0xd4)))
                    g.FillEllipse(db, x - 14 - p * 16, ground - 4 - p * 4, 4 + p * 4, 4 + p * 4);
            }
    }

    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); float d = r * 2;
        if (w < d) w = d;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }

    static IntPtr FindUi()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            var sb = new StringBuilder(256); GetWindowText(h, sb, 256); string s = sb.ToString();
            if ((s == Install.Name || s.EndsWith(" · " + Install.Name)) && (IsWindowVisible(h) || IsIconic(h)))
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                try { if (Process.GetProcessById((int)pid).ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)) { found = h; return false; } } catch { }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
}
