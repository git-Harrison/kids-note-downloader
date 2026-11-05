// VideoCompressor.cs - re-encode a video to H.264/AAC MP4, max 720p, using only Windows built-ins
// (WinRT Windows.Media.Transcoding.MediaTranscoder -> Media Foundation, HW encoder when present).
// C# 5 / .NET Framework 4.5+ on Windows 10/11. Nothing to ship.
//
// Compile references (csc from C:\Windows\Microsoft.NET\Framework64\v4.0.30319):
//   /r:C:\Windows\System32\WinMetadata\Windows.Foundation.winmd
//   /r:C:\Windows\System32\WinMetadata\Windows.Storage.winmd
//   /r:C:\Windows\System32\WinMetadata\Windows.Media.winmd
//   /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll
//   plus /r:System.Drawing.dll for WebpEncoder. AsTask()/System.Runtime.WindowsRuntime.dll is NOT used (see Await).
//
// All WinRT usage is kept inside NoInlining methods, so on a machine without WinRT / Media Foundation
// (Windows 7, Windows N without Media Feature Pack, Server Core) Available returns false instead of
// throwing TypeLoadException from the caller's method.
//
// Call Compress from a worker thread (it blocks until the transcode is done or times out).
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;

sealed class VideoInfo
{
    public int Width;          // encoded frame size (before rotation)
    public int Height;
    public int Rotation;       // 0, 90, 180, 270 (display rotation from container metadata)
    public TimeSpan Duration;
    public long Bitrate;       // bits/s as reported by the Windows property system (0 if unknown)
    public long FileSize;
    public override string ToString()
    {
        return Width + "x" + Height + (Rotation != 0 ? " rot" + Rotation : "") + ", "
            + Duration.TotalSeconds.ToString("0.00") + " s, " + (Bitrate / 1000) + " kbps, " + FileSize + " bytes";
    }
}

static class VideoCompressor
{
    public const int MaxLongSide = 1280;
    public const int MaxShortSide = 720;
    public const uint TargetVideoBitrate = 2500000;
    public const uint TargetAudioBitrate = 128000;

    // MF_MT_VIDEO_ROTATION: the MP4 sink writes it into the track matrix, players rotate on display.
    static readonly Guid MF_MT_VIDEO_ROTATION = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

    static readonly object sync = new object();
    static bool probed, available;
    static string probeError;

    /// <summary>True if WinRT MediaTranscoder plus the H.264 and AAC encoders exist on this PC.</summary>
    public static bool Available
    {
        get
        {
            lock (sync)
            {
                if (!probed)
                {
                    probed = true;
                    try { available = ProbeCore(); }
                    catch (Exception ex) { available = false; probeError = ex.GetType().Name + ": " + ex.Message; }
                }
                return available;
            }
        }
    }

    public static string ProbeError { get { bool a = Available; return probeError; } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ProbeCore()
    {
        Version v = Environment.OSVersion.Version;
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || v < new Version(6, 2))
        { probeError = "Windows 8 or later required"; return false; }
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // Windows "N" editions without the Media Feature Pack lack these MFTs.
        if (!File.Exists(Path.Combine(sys, "mfh264enc.dll")) && !File.Exists(Path.Combine(sys, "msmpeg2venc.dll")))
        { probeError = "H.264 encoder (mfh264enc.dll) missing - Media Feature Pack?"; return false; }
        if (!File.Exists(Path.Combine(sys, "mfAACEnc.dll")))
        { probeError = "AAC encoder (mfAACEnc.dll) missing - Media Feature Pack?"; return false; }
        MediaTranscoder t = new MediaTranscoder();
        MediaEncodingProfile p = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        return t != null && p != null && p.Video != null;
    }

    /// <summary>Re-encode src to H.264/AAC MP4 at dst (max 720p, ~2.5 Mbps). Timeout = 2 min + 5x duration.</summary>
    public static void Compress(string src, string dst)
    {
        Compress(src, dst, TimeSpan.Zero);
    }

    /// <summary>Same as Compress(src, dst) with an explicit timeout (TimeSpan.Zero = automatic).</summary>
    public static void Compress(string src, string dst, TimeSpan timeout)
    {
        if (src == null) throw new ArgumentNullException("src");
        if (dst == null) throw new ArgumentNullException("dst");
        if (!Available) throw new InvalidOperationException("Video compression not available: " + probeError);
        CompressCore(Path.GetFullPath(src), Path.GetFullPath(dst), timeout);
    }

    /// <summary>
    /// False when the source is already at most 720p and at most ~3.5 Mbps (compressing would gain
    /// little or even grow the file). True if it cannot be determined.
    /// </summary>
    public static bool WorthCompressing(string src)
    {
        try
        {
            VideoInfo vi = GetInfo(src);
            if (vi.Width <= 0 || vi.Height <= 0) return true;
            int longSide = Math.Max(vi.Width, vi.Height), shortSide = Math.Min(vi.Width, vi.Height);
            if (longSide > MaxLongSide || shortSide > MaxShortSide) return true;
            return vi.Bitrate <= 0 || vi.Bitrate > 3500000;
        }
        catch { return true; }
    }

    /// <summary>Read size/rotation/duration/bitrate via the Windows property system. Throws on failure.</summary>
    public static VideoInfo GetInfo(string path)
    {
        if (!Available) throw new InvalidOperationException("Video support not available: " + probeError);
        return GetInfoCore(Path.GetFullPath(path));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static VideoInfo GetInfoCore(string path)
    {
        StorageFile f = Await(StorageFile.GetFileFromPathAsync(path), 30000);
        VideoProperties vp = Await(f.Properties.GetVideoPropertiesAsync(), 30000);
        VideoInfo vi = new VideoInfo();
        vi.Width = (int)vp.Width;
        vi.Height = (int)vp.Height;
        vi.Duration = vp.Duration;
        vi.Bitrate = vp.Bitrate;
        vi.Rotation = OrientationToDegrees(vp.Orientation);
        vi.FileSize = new FileInfo(path).Length;
        return vi;
    }

    static int OrientationToDegrees(VideoOrientation o)
    {
        switch (o)
        {
            case VideoOrientation.Rotate90: return 90;
            case VideoOrientation.Rotate180: return 180;
            case VideoOrientation.Rotate270: return 270;
            default: return 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CompressCore(string src, string dst, TimeSpan timeout)
    {
        StorageFile srcFile = Await(StorageFile.GetFileFromPathAsync(src), 30000);
        VideoProperties vp = Await(srcFile.Properties.GetVideoPropertiesAsync(), 30000);
        MediaEncodingProfile srcProfile = Await(MediaEncodingProfile.CreateFromFileAsync(srcFile), 30000);
        if (srcProfile == null || srcProfile.Video == null)
            throw new InvalidDataException("No decodable video stream in " + src);

        uint w = srcProfile.Video.Width, h = srcProfile.Video.Height;
        if (w == 0 || h == 0) { w = vp.Width; h = vp.Height; }
        if (w == 0 || h == 0) throw new InvalidDataException("Unknown video size: " + src);

        // Fit inside 1280x720 (or 720x1280 for portrait), keep aspect, never upscale, even dimensions.
        uint longSide = Math.Max(w, h), shortSide = Math.Min(w, h);
        double scale = Math.Min(1.0, Math.Min((double)MaxLongSide / longSide, (double)MaxShortSide / shortSide));
        uint ow = (uint)Math.Max(2, ((int)Math.Round(w * scale)) & ~1);
        uint oh = (uint)Math.Max(2, ((int)Math.Round(h * scale)) & ~1);

        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Video.Width = ow;
        profile.Video.Height = oh;
        profile.Video.Bitrate = TargetVideoBitrate;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        uint fn = srcProfile.Video.FrameRate.Numerator, fd = srcProfile.Video.FrameRate.Denominator;
        if (fn > 0 && fd > 0 && (double)fn / fd <= 60.0)
        {
            profile.Video.FrameRate.Numerator = fn;
            profile.Video.FrameRate.Denominator = fd;
        }
        int rot = OrientationToDegrees(vp.Orientation);
        if (rot != 0) profile.Video.Properties[MF_MT_VIDEO_ROTATION] = (uint)rot;

        if (srcProfile.Audio == null)
        {
            profile.Audio = null;
        }
        else
        {
            uint rate = srcProfile.Audio.SampleRate;
            if (rate != 44100 && rate != 48000) rate = 48000;
            uint ch = srcProfile.Audio.ChannelCount;
            if (ch < 1 || ch > 2) ch = 2;
            profile.Audio = AudioEncodingProperties.CreateAac(rate, ch, TargetAudioBitrate);
        }

        string dir = Path.GetDirectoryName(dst);
        Directory.CreateDirectory(dir);
        string tmpName = Path.GetFileNameWithoutExtension(dst) + ".part" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4";
        string tmpPath = Path.Combine(dir, tmpName);

        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(vp.Duration.Ticks * 5);

        bool ok = false;
        try
        {
            StorageFolder folder = Await(StorageFolder.GetFolderFromPathAsync(dir), 30000);
            StorageFile tmpFile = Await(folder.CreateFileAsync(tmpName, CreationCollisionOption.ReplaceExisting), 30000);

            MediaTranscoder transcoder = new MediaTranscoder();
            transcoder.HardwareAccelerationEnabled = true;
            transcoder.VideoProcessingAlgorithm = MediaVideoProcessingAlgorithm.Default;
            PrepareTranscodeResult prep = Await(transcoder.PrepareFileTranscodeAsync(srcFile, tmpFile, profile), 60000);
            if (!prep.CanTranscode)
                throw new NotSupportedException("Cannot transcode " + Path.GetFileName(src) + ": " + prep.FailureReason);

            Await(prep.TranscodeAsync(), timeout); // cancels + throws TimeoutException on timeout
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmpPath, dst);
            ok = true;
        }
        finally
        {
            if (!ok) { try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { } }
        }
    }

    // Blocking waits on WinRT async operations via their Completed handlers. (AsTask() from
    // System.Runtime.WindowsRuntime.dll cannot be used here: it is compiled against the merged
    // "Windows.winmd" that only ships with the Windows SDK, and the in-box csc cannot unify it with
    // the split System32\WinMetadata\*.winmd files.)
    internal static T Await<T>(IAsyncOperation<T> op, int timeoutMs)
    {
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            op.Completed = delegate(IAsyncOperation<T> o, AsyncStatus s) { try { done.Set(); } catch (ObjectDisposedException) { } };
            if (!done.WaitOne(timeoutMs)) { try { op.Cancel(); } catch { } throw new TimeoutException("WinRT call timed out"); }
            ThrowIfFailed(op);
            return op.GetResults();
        }
    }

    /// <summary>Set by the app per download job; cancelling it aborts a running transcode.</summary>
    public static System.Threading.CancellationToken Stop = System.Threading.CancellationToken.None;

    internal static bool Await<P>(IAsyncActionWithProgress<P> op, TimeSpan timeout)
    {
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            op.Completed = delegate(IAsyncActionWithProgress<P> o, AsyncStatus s) { try { done.Set(); } catch (ObjectDisposedException) { } };
            // 멈추기 누르면 변환도 바로 취소
            int hit = WaitHandle.WaitAny(new WaitHandle[] { done, Stop.WaitHandle }, timeout);
            if (hit == 1)
            {
                try { op.Cancel(); } catch { }
                done.WaitOne(15000);
                throw new OperationCanceledException();
            }
            if (hit == WaitHandle.WaitTimeout)
            {
                try { op.Cancel(); } catch { }
                done.WaitOne(15000); // let Media Foundation release the output file
                throw new TimeoutException("Video transcode timed out after " + timeout);
            }
            ThrowIfFailed(op);
            op.GetResults();
            return true;
        }
    }

    static void ThrowIfFailed(IAsyncInfo op)
    {
        if (op.Status == AsyncStatus.Error)
        {
            Exception e = op.ErrorCode;
            throw new IOException(e != null ? e.Message : "WinRT operation failed", e);
        }
        if (op.Status == AsyncStatus.Canceled) throw new OperationCanceledException();
    }
}