using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// 작업 표시줄에서 우리 앱으로 보이게 하기.
// 앱 화면은 Edge 창이라 그냥 두면 Edge 아이콘이나 흐린 파비콘이 나온다.
// 창과 시작 메뉴 바로가기에 같은 앱 ID(AppUserModelID)를 붙이면 Windows 가 바로가기의 아이콘(.ico, 모든 크기)을 작업 표시줄에 쓴다.
static class TaskbarId
{
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(out uint c);
        int GetAt(uint i, out PropertyKey k);
        int GetValue(ref PropertyKey k, [Out] PropVariant v);
        int SetValue(ref PropertyKey k, [In] PropVariant v);
        int Commit();
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid fmtid; public uint pid; public PropertyKey(Guid g, uint p) { fmtid = g; pid = p; } }
    [StructLayout(LayoutKind.Explicit)]
    class PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);

    static readonly Guid Fmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");   // System.AppUserModel.*
    const uint RelaunchCommand = 2, RelaunchIcon = 3, RelaunchName = 4, AppId = 5;

    static void Set(IPropertyStore ps, uint pid, string val)
    {
        var k = new PropertyKey(Fmt, pid);
        var v = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(val) };
        try { ps.SetValue(ref k, v); } finally { Marshal.FreeCoTaskMem(v.p); }
    }

    // 실행 중인 창에 앱 ID + 다시 실행 정보(작업 표시줄 고정용) 붙이기
    public static void ApplyToWindow(IntPtr hwnd, string appId, string exe, string icon, string name)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID; IPropertyStore ps;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out ps) != 0 || ps == null) return;
            Set(ps, RelaunchCommand, "\"" + exe + "\"");
            Set(ps, RelaunchName, name);
            Set(ps, RelaunchIcon, icon);
            Set(ps, AppId, appId);
            ps.Commit();
            Marshal.ReleaseComObject(ps);
        }
        catch { }
    }

    // 시작 메뉴 바로가기에 같은 앱 ID 붙이기
    public static void ApplyToShortcut(string lnk, string appId)
    {
        try
        {
            var link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 2 /* STGM_READWRITE */);
            var ps = (IPropertyStore)link;
            Set(ps, AppId, appId);
            ps.Commit();
            ((IPersistFile)link).Save(lnk, true);
            Marshal.ReleaseComObject(link);
        }
        catch { }
    }
}
