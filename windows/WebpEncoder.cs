// WebpEncoder.cs - JPEG/PNG/GIF/BMP -> lossy WebP for KidsNote Downloader.
// C# 5 / .NET Framework 4.x. References: System.dll, System.Drawing.dll.
//
// Two backends, chosen automatically (in this order):
//   1. In-process libwebp.dll (+ libsharpyuv.dll) next to the app exe, via P/Invoke.
//      Binaries: conda-forge "libwebp-base" 1.6.0 win-64 (built with MSVC from the
//      official libwebp-1.6.0.tar.gz). They import VCRUNTIME140.dll (VC++ 2015-2022 x64 runtime).
//   2. Google's official cwebp.exe next to the app exe, run as a child process on a temp PNG.
// Ship either set (or both). Compile with /platform:anycpu; on x64 Windows the process is 64-bit,
// so the x64 binaries are required (a 32-bit process will report Available = false for the DLL).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

static class WebpEncoder
{
    const string DllName = "libwebp.dll";
    const string SharpYuvName = "libsharpyuv.dll";
    const string ExeName = "cwebp.exe";
    const int ExeTimeoutMs = 120000;
    const int MaxDimension = 16383; // WebP format limit

    static readonly object sync = new object();
    static bool probed;
    static bool dllOk;
    static bool exeOk;
    static string backend = "none";
    static string lastError;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
    const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern int WebPGetEncoderVersion();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern UIntPtr WebPEncodeBGRA(IntPtr bgra, int width, int height, int stride, float quality, out IntPtr output);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    static extern void WebPFree(IntPtr ptr);

    static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

    /// <summary>True if libwebp.dll or cwebp.exe is usable from the app folder.</summary>
    public static bool Available { get { Probe(); return dllOk || exeOk; } }

    /// <summary>"libwebp.dll x.y.z", "cwebp.exe" or "none".</summary>
    public static string Backend { get { Probe(); return backend; } }

    /// <summary>Why the DLL backend could not be loaded (null if it loaded).</summary>
    public static string LastProbeError { get { Probe(); return lastError; } }

    static void Probe()
    {
        lock (sync)
        {
            if (probed) return;
            probed = true;
            try
            {
                string dll = Path.Combine(BaseDir, DllName);
                if (!File.Exists(dll)) throw new FileNotFoundException(dll);
                if (IntPtr.Size != 8) throw new PlatformNotSupportedException("x64 libwebp.dll needs a 64-bit process");
                string yuv = Path.Combine(BaseDir, SharpYuvName);
                // Pre-load the dependency by full path, then libwebp itself. The [DllImport] above
                // then binds to the already-loaded module instead of searching PATH.
                if (File.Exists(yuv) && LoadLibraryExW(yuv, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH) == IntPtr.Zero)
                    throw new DllNotFoundException(SharpYuvName + " load failed, Win32 error " + Marshal.GetLastWin32Error());
                if (LoadLibraryExW(dll, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH) == IntPtr.Zero)
                    throw new DllNotFoundException(DllName + " load failed, Win32 error " + Marshal.GetLastWin32Error()
                        + " (126 = missing dependency such as VCRUNTIME140.dll)");
                int v = WebPGetEncoderVersion();
                dllOk = v > 0;
                if (dllOk) backend = DllName + " " + ((v >> 16) & 0xff) + "." + ((v >> 8) & 0xff) + "." + (v & 0xff);
            }
            catch (Exception ex)
            {
                dllOk = false;
                lastError = ex.GetType().Name + ": " + ex.Message;
            }
            if (!dllOk)
            {
                exeOk = File.Exists(Path.Combine(BaseDir, ExeName));
                if (exeOk) backend = ExeName;
            }
        }
    }

    /// <summary>Force the cwebp.exe backend (for testing). Returns false if cwebp.exe is missing.</summary>
    public static bool ForceExeBackend()
    {
        Probe();
        lock (sync)
        {
            if (!File.Exists(Path.Combine(BaseDir, ExeName))) return false;
            dllOk = false; exeOk = true; backend = ExeName;
            return true;
        }
    }

    /// <summary>
    /// Decode src (JPEG/PNG/GIF/BMP, first frame only), apply EXIF orientation, encode lossy WebP
    /// at quality 0-100 and write it to dst. Metadata (EXIF/GPS) is not carried over. Throws on failure.
    /// </summary>
    public static void Convert(string src, string dst, float quality)
    {
        if (src == null) throw new ArgumentNullException("src");
        if (dst == null) throw new ArgumentNullException("dst");
        if (quality < 0) quality = 0;
        if (quality > 100) quality = 100;
        Probe();
        if (!dllOk && !exeOk) throw new InvalidOperationException("WebP encoder not available: " + lastError);

        using (Bitmap bmp = LoadOriented(src))
        {
            if (bmp.Width > MaxDimension || bmp.Height > MaxDimension)
                throw new NotSupportedException("Image too large for WebP: " + bmp.Width + "x" + bmp.Height);
            if (dllOk) EncodeWithDll(bmp, dst, quality);
            else EncodeWithExe(bmp, dst, quality);
        }
    }

    static Bitmap LoadOriented(string src)
    {
        // Read into memory so the source file is not locked, and so GDI+ errors surface here.
        byte[] data = File.ReadAllBytes(src);
        using (MemoryStream ms = new MemoryStream(data))
        using (Image img = Image.FromStream(ms, true, true))
        {
            RotateFlipType rf = RotateFlipType.RotateNoneFlipNone;
            foreach (int id in img.PropertyIdList)
            {
                if (id != 0x0112) continue;
                PropertyItem p = img.GetPropertyItem(0x0112);
                int o = (p.Value != null && p.Value.Length >= 2) ? BitConverter.ToUInt16(p.Value, 0) : 1;
                switch (o)
                {
                    case 2: rf = RotateFlipType.RotateNoneFlipX; break;
                    case 3: rf = RotateFlipType.Rotate180FlipNone; break;
                    case 4: rf = RotateFlipType.Rotate180FlipX; break;
                    case 5: rf = RotateFlipType.Rotate90FlipX; break;
                    case 6: rf = RotateFlipType.Rotate90FlipNone; break;
                    case 7: rf = RotateFlipType.Rotate270FlipX; break;
                    case 8: rf = RotateFlipType.Rotate270FlipNone; break;
                }
                break;
            }
            // Copy to a fresh 32bpp ARGB bitmap (independent of the stream, first frame of GIFs).
            Bitmap bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            try
            {
                bmp.SetResolution(96, 96);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
                }
                if (rf != RotateFlipType.RotateNoneFlipNone) bmp.RotateFlip(rf);
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
        }
    }

    static void EncodeWithDll(Bitmap bmp, string dst, float quality)
    {
        Rectangle r = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData bd = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb); // memory order B,G,R,A
        IntPtr output = IntPtr.Zero;
        try
        {
            UIntPtr size = WebPEncodeBGRA(bd.Scan0, bd.Width, bd.Height, bd.Stride, quality, out output);
            ulong n = size.ToUInt64();
            if (n == 0 || output == IntPtr.Zero) throw new InvalidOperationException("WebPEncodeBGRA failed");
            if (n > int.MaxValue) throw new InvalidOperationException("WebP output too large");
            byte[] buf = new byte[(int)n];
            Marshal.Copy(output, buf, 0, (int)n);
            WriteAtomically(dst, buf);
        }
        finally
        {
            bmp.UnlockBits(bd);
            if (output != IntPtr.Zero) WebPFree(output);
        }
    }

    static void EncodeWithExe(Bitmap bmp, string dst, float quality)
    {
        string exe = Path.Combine(BaseDir, ExeName);
        string tmpPng = Path.Combine(Path.GetTempPath(), "kn_webp_" + Guid.NewGuid().ToString("N") + ".png");
        string tmpOut = dst + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            bmp.Save(tmpPng, ImageFormat.Png);
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.Arguments = "-quiet -metadata none -q " + quality.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " \"" + tmpPng + "\" -o \"" + tmpOut + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            using (Process p = Process.Start(psi))
            {
                // Read stderr asynchronously so a chatty child cannot deadlock on a full pipe.
                System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(ExeTimeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("cwebp.exe timed out");
                }
                if (p.ExitCode != 0 || !File.Exists(tmpOut))
                    throw new InvalidOperationException("cwebp.exe failed (exit " + p.ExitCode + "): " + (err.Wait(2000) ? err.Result : ""));
            }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmpOut, dst);
        }
        finally
        {
            TryDelete(tmpPng);
            TryDelete(tmpOut);
        }
    }

    static void WriteAtomically(string dst, byte[] buf)
    {
        string tmp = dst + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            File.WriteAllBytes(tmp, buf);
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }
        finally { TryDelete(tmp); }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
