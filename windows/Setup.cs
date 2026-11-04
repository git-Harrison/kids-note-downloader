using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// 키즈노트 다운로더 설치 파일: 앱 exe 를 품고 있다가 %LOCALAPPDATA%\Programs\KidsNoteDownloader 에 설치한다.
static class SetupMain
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        if (args.Length > 0 && (args[0] == "/S" || args[0] == "/silent")) { DoInstall(null, true); return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var w = new ModernSetup
        {
            Text = Install.Name + " 설치",
            AppName = Install.Name,
            Tagline = "알림장·앨범의 사진과 동영상을\n원본 그대로 내 PC에 보관해요",
            FontName = "Malgun Gothic",
            Accent = Color.FromArgb(244, 128, 54),
            IsUpdate = Install.Installed,
        };
        var asm = Assembly.GetExecutingAssembly();
        using (var s = asm.GetManifestResourceStream("icon.png")) w.Logo = new Bitmap(Image.FromStream(s));
        try { w.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        w.Options.Add(new SetupOption { Text = "바탕화면에 바로가기 만들기", On = true });
        w.T.FinePrint = "v" + Install.Version + " · 관리자 권한 없이 내 계정에만 설치돼요\n비공식 앱이에요 · 받은 사진은 가족 보관용으로 써 주세요";
        w.InstallWork = (p, opts) => DoInstall(p, opts[0]);
        w.Launch = () => Process.Start(Install.Exe);
        Application.Run(w);
    }

    static void DoInstall(SetupProgress p, bool desktop)
    {
        Action<float, string> say = (v, m) => { if (p != null) { p(v, m); Thread.Sleep(250); } };
        say(0.12f, "실행 중인 프로그램을 닫는 중…");
        Install.KillRunning();
        say(0.45f, "파일을 복사하는 중…");
        Directory.CreateDirectory(Install.Dir);
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Install.ExeName))
        using (var o = File.Create(Install.Exe)) s.CopyTo(o);
        // 압축 모드용 라이브러리 (libwebp: BSD-3, vcruntime140: Microsoft 재배포 허용 파일)
        foreach (var name in new[] { "libwebp.dll", "libsharpyuv.dll", "vcruntime140.dll", "libwebp-COPYING.txt", "libwebp-PATENTS.txt" })
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                string dst = Path.Combine(Install.Dir, name);
                try { using (var o = File.Create(dst)) s.CopyTo(o); } catch (IOException) { }   // 실행 중이라 잠겨 있으면 기존 것 사용
            }
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
        using (var o = File.Create(Install.IconFile)) s.CopyTo(o);
        say(0.75f, "바로가기를 만드는 중…");
        Install.Register(desktop);
        say(1f, "마무리하는 중…");
    }
}
