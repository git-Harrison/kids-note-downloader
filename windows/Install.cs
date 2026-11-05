using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

// 설치·제거 공통 (설치 파일과 앱이 함께 사용). 모두 현재 사용자 범위라 관리자 권한 불필요.
static class Install
{
    public const string Name = "키즈노트 다운로더";
    public const string Version = "1.3.5";
    public const string AppId = "GitHarrison.KidsNoteDownloader";
    public const string ExeName = "KidsNoteDownloader.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KidsNoteDownloader";

    static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
    public static string Dir { get { return Path.Combine(LocalAppData, @"Programs\KidsNoteDownloader"); } }
    public static string Exe { get { return Path.Combine(Dir, ExeName); } }
    // 바로가기·앱 목록은 따로 둔 아이콘 파일을 가리킨다 (모든 크기가 들어 있어 선명, 예전 아이콘 캐시도 안 씀)
    public static string IconFile { get { return Path.Combine(Dir, "KidsNote.ico"); } }
    static string IconOf { get { return File.Exists(IconFile) ? IconFile : Exe; } }
    public static string DataDir { get { return Path.Combine(LocalAppData, "KidsNoteDownloader"); } }
    static string StartLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); } }
    static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); } }
    public static bool Installed { get { return File.Exists(Exe); } }


    public static void KillRunning()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try { if (p.Id != Process.GetCurrentProcess().Id) { p.Kill(); p.WaitForExit(3000); } } catch { }
        }
    }

    public static void Register(bool desktop)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", Name);
            k.SetValue("DisplayIcon", "\"" + IconOf + "\",0");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "git-Harrison");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
            k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        Shortcut(StartLnk);
        if (desktop) Shortcut(DesktopLnk); else TryDelete(DesktopLnk);
        RefreshIconCache();
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        TryDelete(StartLnk); TryDelete(DesktopLnk);
    }

    static void Shortcut(string lnk)
    {
        TryDelete(lnk);
        dynamic ws = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic s = ws.CreateShortcut(lnk);
        s.TargetPath = Exe; s.WorkingDirectory = Dir; s.IconLocation = IconOf + ",0";
        s.Description = "키즈노트 사진·동영상 다운로더";
        s.Save();
        TaskbarId.ApplyToShortcut(lnk, AppId);   // 작업 표시줄이 이 바로가기 아이콘을 쓰게
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, int flags, IntPtr a, IntPtr b);
    // Windows 아이콘 캐시 새로 고침
    static void RefreshIconCache()
    {
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        try { var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ie4uinit.exe"), "-show") { CreateNoWindow = true, UseShellExecute = false }); p.WaitForExit(5000); } catch { }
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
}
