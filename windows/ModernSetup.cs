using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// 요즘 앱 스타일 설치 창: 테두리 없는 둥근 창(Windows 11), 다크 모드 자동, 큰 버튼 하나,
// 누르면 버튼이 부드러운 진행 막대로 바뀌고 → 체크 표시 → 앱이 바로 열린다.
// 화면은 전부 직접 그린다 (외부 라이브러리 없음).

class SetupOption { public string Text; public bool On; public Rectangle Hit; }

class SetupText
{
    public string Install = "설치하고 시작하기", Update = "업데이트하고 시작하기", Done = "준비됐어요", Failed = "설치하지 못했어요",
        Retry = "다시 시도", FinePrint = "", Starting = "준비하는 중…";
}

delegate void SetupProgress(float pct, string status);

class ModernSetup : Form
{
    public string AppName = "", Tagline = "", FontName = "Segoe UI";
    public Image Logo;
    public Color Accent = Color.FromArgb(47, 111, 237);
    public bool IsUpdate;
    public readonly List<SetupOption> Options = new List<SetupOption>();
    public readonly SetupText T = new SetupText();
    public Action<SetupProgress, bool[]> InstallWork;   // 실패하면 예외를 던진다
    public Action Launch;

    enum St { Ready, Busy, Done, Failed }
    St st = St.Ready;
    volatile float target; volatile string status = "";
    volatile bool workDone; string error;
    float shown, checkT, hoverT;
    DateTime doneAt;
    bool hoverBtn, hoverClose, dark;
    int hoverOpt = -1;
    Rectangle btnR, closeR;
    float s = 1f;   // DPI 배율
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 15 };
    Color Bg, Fg, Muted, Line;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    public ModernSetup()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        KeyPreview = true;
        Opacity = 0;
        try
        {
            object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            dark = v is int && (int)v == 0;
        }
        catch { }
        Bg = dark ? Color.FromArgb(23, 25, 29) : Color.White;
        Fg = dark ? Color.FromArgb(236, 237, 240) : Color.FromArgb(28, 31, 36);
        Muted = dark ? Color.FromArgb(140, 146, 156) : Color.FromArgb(110, 116, 128);
        Line = dark ? Color.FromArgb(48, 51, 58) : Color.FromArgb(229, 231, 235);
        BackColor = Bg;
        tick.Tick += (a, b) => Step();
    }

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ClassStyle |= 0x20000; return p; }   // 그림자
    }

    protected override void OnLoad(EventArgs e)
    {
        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        ClientSize = new Size(S(440), S(Options.Count > 0 ? 540 : 500));
        CenterToScreen();   // 크기를 정한 뒤 다시 화면 정가운데로
        int round = 2; try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }   // Windows 11 둥근 모서리
        base.OnLoad(e);
        tick.Start();
    }

    int S(float v) { return (int)Math.Round(v * s); }
    Font F(float size, FontStyle style = FontStyle.Regular) { return new Font(FontName, size, style); }
    static Color Mix(Color a, Color b, float t) { return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // ── 애니메이션 ──
    void Step()
    {
        bool dirty = false;
        if (Opacity < 1) { Opacity = Math.Min(1, Opacity + 0.08); }
        float h = hoverBtn ? 1 : 0;
        if (Math.Abs(hoverT - h) > 0.01f) { hoverT += (h - hoverT) * 0.25f; dirty = true; }
        if (st == St.Busy)
        {
            float goal = workDone ? 1f : Math.Min(target, 0.96f);
            shown += Math.Max((goal - shown) * 0.08f, goal > shown ? 0.002f : 0f);
            if (shown > goal) shown = goal;
            if (workDone && error == null && shown >= 0.995f) { st = St.Done; doneAt = DateTime.Now; }
            if (workDone && error != null) { st = St.Failed; }
            dirty = true;
        }
        if (st == St.Done)
        {
            checkT = Math.Min(1, checkT + 0.05f); dirty = true;
            if ((DateTime.Now - doneAt).TotalMilliseconds > 1100)
            {
                tick.Stop();
                try { if (Launch != null) Launch(); } catch { }
                Close(); return;
            }
        }
        if (dirty) Invalidate();
    }

    void Start()
    {
        if (st == St.Busy || st == St.Done) return;
        st = St.Busy; shown = 0; target = 0.04f; status = T.Starting; workDone = false; error = null;
        var opts = Options.ConvertAll(o => o.On).ToArray();
        new Thread(() =>
        {
            try { InstallWork((p, msg) => { target = p; if (msg != null) status = msg; }, opts); }
            catch (Exception ex) { error = ex.Message; }
            workDone = true;
        }) { IsBackground = true }.Start();
        Invalidate();
    }

    // ── 그리기 ──
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        int W = ClientSize.Width, H = ClientSize.Height, cx = W / 2;

        // 아이콘 뒤 은은한 빛
        var glowR = new RectangleF(cx - S(170), S(-40), S(340), S(300));
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(glowR);
            using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(dark ? 70 : 46, Accent), SurroundColors = new[] { Color.FromArgb(0, Accent) } })
                g.FillPath(pb, gp);
        }
        // 테두리 (Windows 10 에선 각진 창이라 선만)
        using (var pen = new Pen(Line)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);

        // 닫기
        closeR = new Rectangle(W - S(48), S(12), S(36), S(36));
        if (st != St.Busy)
        {
            if (hoverClose) using (var b = new SolidBrush(Color.FromArgb(dark ? 40 : 18, Fg)))
                using (var p = Round(closeR, S(8))) g.FillPath(b, p);
            using (var pen = new Pen(Muted, 1.6f * s))
            {
                int m = S(13);
                g.DrawLine(pen, closeR.Left + m, closeR.Top + m, closeR.Right - m, closeR.Bottom - m);
                g.DrawLine(pen, closeR.Right - m, closeR.Top + m, closeR.Left + m, closeR.Bottom - m);
            }
        }

        // 아이콘 · 이름 · 설명
        int logo = S(92), y = S(64);
        if (Logo != null) g.DrawImage(Logo, cx - logo / 2, y, logo, logo);
        y += logo + S(22);
        using (var f = F(20f, FontStyle.Bold))
            TextRenderer.DrawText(g, AppName, f, new Rectangle(0, y, W, S(40)), Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        y += S(42);
        using (var f = F(10.5f))
            TextRenderer.DrawText(g, Tagline, f, new Rectangle(S(48), y, W - S(96), S(48)), Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        int btnY = H - S(Options.Count > 0 ? 150 : 140);
        btnR = new Rectangle(S(48), btnY, W - S(96), S(50));

        if (st == St.Ready || st == St.Failed)
        {
            // 옵션 (작은 체크)
            int oy = btnY - S(26) - Options.Count * S(30);
            using (var f = F(9.5f))
            {
                int widest = 0;
                foreach (var o0 in Options) widest = Math.Max(widest, TextRenderer.MeasureText(o0.Text, f).Width);
                int ox = Math.Max(S(48), (W - (S(28) + widest)) / 2);   // 옵션 묶음을 가운데로
                for (int i = 0; i < Options.Count; i++)
                {
                    var o = Options[i];
                    var box = new Rectangle(ox, oy + i * S(30) + S(2), S(18), S(18));
                    var tw = TextRenderer.MeasureText(o.Text, f).Width;
                    o.Hit = new Rectangle(box.Left - S(6), box.Top - S(6), box.Width + S(16) + tw, box.Height + S(12));
                    using (var p = Round(box, S(5)))
                    {
                        if (o.On) using (var b = new SolidBrush(Accent)) g.FillPath(b, p);
                        else using (var pen = new Pen(hoverOpt == i ? Accent : Mix(Line, Muted, 0.5f), 1.4f * s)) g.DrawPath(pen, p);
                    }
                    if (o.On) using (var pen = new Pen(Color.White, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new PointF(box.Left + 4.5f * s, box.Top + 9.5f * s), new PointF(box.Left + 7.8f * s, box.Top + 12.8f * s), new PointF(box.Left + 13.5f * s, box.Top + 5.5f * s) });
                    TextRenderer.DrawText(g, o.Text, f, new Point(box.Right + S(10), box.Top - S(1)), Fg, TextFormatFlags.NoPadding);
                }
            }

            // 큰 버튼
            Color c = Mix(Accent, Color.White, 0.10f * hoverT);
            using (var p = Round(btnR, S(14)))
            {
                using (var sh = new SolidBrush(Color.FromArgb((int)(30 + 30 * hoverT), Accent)))
                using (var sp = Round(new RectangleF(btnR.X, btnR.Y + S(4), btnR.Width, btnR.Height), S(14))) g.FillPath(sh, sp);
                using (var b = new SolidBrush(c)) g.FillPath(b, p);
            }
            using (var f = F(11f, FontStyle.Bold))
                TextRenderer.DrawText(g, st == St.Failed ? T.Retry : IsUpdate ? T.Update : T.Install, f, btnR, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (st == St.Failed)
                using (var f = F(9f))
                    TextRenderer.DrawText(g, T.Failed + "\n" + error, f, new Rectangle(S(40), btnR.Bottom + S(12), W - S(80), S(60)), Color.FromArgb(220, 76, 70),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
        else if (st == St.Busy)
        {
            using (var f = F(10f))
            {
                TextRenderer.DrawText(g, status, f, new Rectangle(btnR.X, btnR.Y - S(2), btnR.Width - S(48), S(24)), Fg, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, (int)Math.Round(shown * 100) + "%", f, new Rectangle(btnR.X, btnR.Y - S(2), btnR.Width, S(24)), Muted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }
            var track = new RectangleF(btnR.X, btnR.Y + S(30), btnR.Width, S(8));
            using (var p = Round(track, track.Height / 2)) using (var b = new SolidBrush(Line)) g.FillPath(b, p);
            if (shown > 0.01f)
            {
                var fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * shown), track.Height);
                using (var p = Round(fill, track.Height / 2))
                using (var b = new LinearGradientBrush(fill, Mix(Accent, Color.White, 0.25f), Accent, 0f)) g.FillPath(b, p);
            }
        }
        else if (st == St.Done)
        {
            float r = S(22); var c = new PointF(cx, btnR.Y + S(10));
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1, checkT * 2)), Accent))) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            float t = Math.Max(0, Math.Min(1, checkT * 1.6f - 0.3f));
            var p1 = new PointF(c.X - 9 * s, c.Y + 0.5f * s); var p2 = new PointF(c.X - 2.5f * s, c.Y + 7 * s); var p3 = new PointF(c.X + 10 * s, c.Y - 6.5f * s);
            using (var pen = new Pen(Color.White, 3f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (t > 0) g.DrawLine(pen, p1, Lerp(p1, p2, Math.Min(1, t * 2)));
                if (t > 0.5f) g.DrawLine(pen, p2, Lerp(p2, p3, (t - 0.5f) * 2));
            }
            using (var f = F(11f, FontStyle.Bold))
                TextRenderer.DrawText(g, T.Done, f, new Rectangle(0, (int)(c.Y + r + S(12)), W, S(28)), Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        // 아래 작은 글씨
        if (!string.IsNullOrEmpty(T.FinePrint) && st != St.Failed)
            using (var f = F(8.5f))
                TextRenderer.DrawText(g, T.FinePrint, f, new Rectangle(S(32), H - S(54), W - S(64), S(40)), Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
    static PointF Lerp(PointF a, PointF b, float t) { return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t); }

    // ── 입력 ──
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hb = (st == St.Ready || st == St.Failed) && btnR.Contains(e.Location), hc = st != St.Busy && closeR.Contains(e.Location);
        int ho = -1;
        if (st == St.Ready) for (int i = 0; i < Options.Count; i++) if (Options[i].Hit.Contains(e.Location)) ho = i;
        if (hb != hoverBtn || hc != hoverClose || ho != hoverOpt) { hoverBtn = hb; hoverClose = hc; hoverOpt = ho; Invalidate(); }
        Cursor = hb || hc || ho >= 0 ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hoverBtn = hoverClose = false; hoverOpt = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (st != St.Busy && closeR.Contains(e.Location)) { Close(); return; }
        if ((st == St.Ready || st == St.Failed) && btnR.Contains(e.Location)) { Start(); return; }
        if (st == St.Ready) for (int i = 0; i < Options.Count; i++) if (Options[i].Hit.Contains(e.Location)) { Options[i].On = !Options[i].On; Invalidate(); return; }
        ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // 아무 데나 잡고 창 옮기기
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && (st == St.Ready || st == St.Failed)) Start();
        if (e.KeyCode == Keys.Escape && st != St.Busy) Close();
        base.OnKeyDown(e);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (st == St.Busy) e.Cancel = true;   // 설치 중엔 못 닫음
        base.OnFormClosing(e);
    }
}
