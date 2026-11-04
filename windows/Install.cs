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
    public const string Version = "1.2.0";
    public const string ExeName = "KidsNoteDownloader.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KidsNoteDownloader";
    const string OldMsiUpgradeCode = "{6F0B8E2C-3D4A-4C61-9E57-2B8A1F3C7D90}";   // 1.0.0 (msi) 버전

    static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
    public static string Dir { get { return Path.Combine(LocalAppData, @"Programs\KidsNoteDownloader"); } }
    public static string Exe { get { return Path.Combine(Dir, ExeName); } }
    public static string DataDir { get { return Path.Combine(LocalAppData, "KidsNoteDownloader"); } }
    static string StartLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); } }
    static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); } }
    public static bool Installed { get { return File.Exists(Exe); } }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern int MsiEnumRelatedProducts(string upgradeCode, int reserved, int index, StringBuilder productCode);

    public static void KillRunning()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try { if (p.Id != Process.GetCurrentProcess().Id) { p.Kill(); p.WaitForExit(3000); } } catch { }
        }
    }

    // 예전 msi 버전이 깔려 있으면 조용히 제거
    public static void RemoveOldMsi()
    {
        var code = new StringBuilder(39);
        for (int i = 0; MsiEnumRelatedProducts(OldMsiUpgradeCode, 0, i, code) == 0; i++)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("msiexec.exe", "/x " + code + " /qn") { CreateNoWindow = true, UseShellExecute = false });
                p.WaitForExit(60000);
            }
            catch { }
            code.Clear();
        }
    }

    public static void Register(bool desktop)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", Name);
            k.SetValue("DisplayIcon", "\"" + Exe + "\",0");
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
        s.TargetPath = Exe; s.WorkingDirectory = Dir; s.IconLocation = Exe + ",0";
        s.Description = "키즈노트 사진·동영상 다운로더";
        s.Save();
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
}
