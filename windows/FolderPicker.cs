using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 폴더 고르기 창 — 요즘 탐색기 모양(주소창에 경로 입력·붙여넣기, 새 폴더 만들기·이름 바꾸기 가능).
// 앱 화면(Edge 창)을 주인으로 삼아 띄운다: 주인 창 바로 위에 뜨고, 다른 창 뒤로 숨지 않는다.
static class FolderPicker
{
    static int busy;
    public static Action<string> Log = s => { };

    // 앱 쪽: 폴더 창은 별도 프로세스(같은 exe, --pick-folder)로 띄운다.
    // 앱 안의 다른 스레드와 얽혀 창이 멈추거나 결과가 안 돌아오는 일을 원천 차단.
    public static string Pick(string start, string title, string ownerTitle)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            IntPtr h = FindWindow("#32770", title);
            if (h != IntPtr.Zero) SetForegroundWindow(h);
            return null;
        }
        try
        {
            IntPtr owner = FindOwner(ownerTitle);
            var psi = new ProcessStartInfo(Application.ExecutablePath,
                "--pick-folder " + Quote(start ?? "") + " " + Quote(title) + " " + owner.ToInt64())
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                foreach (var line in output.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.StartsWith("ok:")) return s.Substring(3);
                    if (s.StartsWith("log:")) Log(s.Substring(4));
                }
                return null;
            }
        }
        catch (Exception e) { Log("폴더 창을 열 수 없어요: " + e.Message); return null; }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    static string Quote(string s) { return "\"" + s.TrimEnd('\\').Replace("\"", "") + "\""; }

    // 도우미 프로세스 쪽 (Main 에서 --pick-folder 로 호출, 메인 STA 스레드)
    public static void HelperMain(string[] args)
    {
        string start = args.Length > 1 ? args[1] : "", title = args.Length > 2 ? args[2] : "폴더 고르기";
        long ownerVal; IntPtr owner = args.Length > 3 && long.TryParse(args[3], out ownerVal) ? new IntPtr(ownerVal) : IntPtr.Zero;
        var outp = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        string result = null;
        try { result = Modern(owner, start, title); }
        catch (Exception e)
        {
            outp.WriteLine("log:새 폴더 창 오류, 예전 창으로 대신: " + e.Message);
            try
            {
                using (var d = new FolderBrowserDialog { SelectedPath = start, Description = title, ShowNewFolderButton = true })
                    if (d.ShowDialog(owner == IntPtr.Zero ? null : new Owner(owner)) == DialogResult.OK) result = d.SelectedPath;
            }
            catch (Exception e2) { outp.WriteLine("log:폴더 창을 열 수 없어요: " + e2.Message); }
        }
        if (result != null) outp.WriteLine("ok:" + result);
    }
    class Owner : IWin32Window { readonly IntPtr h; public Owner(IntPtr h) { this.h = h; } public IntPtr Handle { get { return h; } } }

    static string Modern(IntPtr owner, string start, string title)
    {
        var d = (IFileDialog)new FileOpenDialogCo();
        try
        {
            uint opts; d.GetOptions(out opts);
            d.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            d.SetTitle(title);
            d.SetOkButtonLabel("이 폴더에 저장");
            // 시작 위치: 지금 폴더(없으면 있는 상위 폴더)
            string dir = start;
            while (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) dir = System.IO.Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(dir))
            {
                IShellItem si; var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(dir, IntPtr.Zero, ref iid, out si) == 0 && si != null) d.SetFolder(si);
            }
            // 창이 뜨면 한 번 앞으로
            bool shown = false;
            var watch = new System.Threading.Timer(_ =>
            {
                IntPtr h = FindWindow("#32770", title);
                if (h != IntPtr.Zero && !shown) { shown = true; SetForegroundWindow(h); }
            }, null, 150, 300);
            int hr;
            try { hr = d.Show(owner); }
            finally { watch.Dispose(); }
            if (hr != 0) return null; // 취소
            IShellItem r; d.GetResult(out r);
            string path; r.GetDisplayName(SIGDN_FILESYSPATH, out path);
            return path;
        }
        finally { Marshal.ReleaseComObject(d); }
    }

    // 앱 화면(Edge --app 창) 찾기: 제목이 앱 이름으로 끝나는 msedge 창
    static IntPtr FindOwner(string ownerTitle)
    {
        IntPtr found = IntPtr.Zero;
        if (string.IsNullOrEmpty(ownerTitle)) return found;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
            if (!sb.ToString().EndsWith(ownerTitle)) return true;
            uint pid; GetWindowThreadProcessId(h, out pid);
            try { if (Process.GetProcessById((int)pid).ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)) { found = h; return false; } } catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
    const uint SIGDN_FILESYSPATH = 0x80058000;

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCo { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint c, IntPtr specs);
        void SetFileTypeIndex(uint i);
        void GetFileTypeIndex(out uint i);
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem si);
        void SetFolder(IShellItem si);
        void GetFolder(out IShellItem si);
        void GetCurrentSelection(out IShellItem si);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem si);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem si);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem si, uint hint, out int order);
    }
}
