using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// 폴더 고르기 창 — 요즘 탐색기 모양(주소창에 경로 입력·붙여넣기, 새 폴더 만들기·이름 바꾸기 가능).
// 앱 화면(Edge) 뒤에 숨지 않도록 앞으로 끌어온다.
static class FolderPicker
{
    static int busy;
    static IntPtr dialogOwner;

    public static string Pick(string start, string title)
    {
        // 이미 열려 있으면 새로 띄우지 않고 앞으로만
        if (Interlocked.Exchange(ref busy, 1) == 1) { BringFront(dialogOwner); return null; }
        string result = null;
        try
        {
            var t = new Thread(() =>
            {
                using (var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1), StartPosition = FormStartPosition.CenterScreen, Opacity = 0 })
                {
                    owner.Show();
                    dialogOwner = owner.Handle;
                    BringFront(owner.Handle);
                    try { result = Modern(owner.Handle, start, title); }
                    catch
                    {
                        using (var d = new FolderBrowserDialog { SelectedPath = start, Description = title, ShowNewFolderButton = true })
                            if (d.ShowDialog(owner) == DialogResult.OK) result = d.SelectedPath;
                    }
                    dialogOwner = IntPtr.Zero;
                }
            });
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        }
        finally { Interlocked.Exchange(ref busy, 0); }
        return result;
    }

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
            if (d.Show(owner) != 0) return null; // 취소
            IShellItem r; d.GetResult(out r);
            string path; r.GetDisplayName(SIGDN_FILESYSPATH, out path);
            return path;
        }
        finally { Marshal.ReleaseComObject(d); }
    }

    // 다른 프로그램(Edge)이 앞에 있어도 이 창을 맨 앞으로
    static void BringFront(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
        IntPtr popup = GetLastActivePopup(h);
        if (popup != IntPtr.Zero && popup != h) SetForegroundWindow(popup);
    }

    const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
    const uint SIGDN_FILESYSPATH = 0x80058000;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
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
