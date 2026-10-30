using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Win32;

namespace Sonix290
{
    /// <summary>Driver settings, stored per user in HKCU\Software\Sonix290Camera.</summary>
    public sealed class Settings
    {
        const string KeyPath = @"Software\Sonix290Camera";

        public string DeviceMatch { get; set; } = "vid_0c45&pid_6366";
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public string Format { get; set; } = "MJPG";
        /// <summary>UVC frame interval requested at connect in ms (0 = the format's default, 33.3; MJPEG allows up to 66.7).</summary>
        public double FrameIntervalMs { get; set; } = 0;
        public int SensorSlave { get; set; } = 0x1A;
        /// <summary>1H in microseconds (measured, MJPEG 1080p: 29.10 on Linux, 29.099 on Windows).</summary>
        public double LineTimeUs { get; set; } = 29.10;
        /// <summary>VMAX the firmware uses at 30 fps; below its frame time SHS1 sets the exposure.</summary>
        public int BaseVmax { get; set; } = 1125;
        public int WriteRepeats { get; set; } = 3;
        /// <summary>
        /// Write VMAX/SHS1 once inside a STANDBY toggle (STANDBY 1, timing, STANDBY 0) instead of WriteRepeats times
        /// into the running sensor. PC 2, CPU port, 2026-10-29: 120/120 took (incl. right after a stream restart and at
        /// 1 s), against 6/20 for one plain burst write and 17/20 for three; the release also restarts the frame.
        /// About 1 in 20 is followed by the sticky 8-byte frames, which a stream restart clears.
        /// </summary>
        public bool StandbyWrites { get; set; } = true;
        /// <summary>Write VMAX and SHS1 as 3-byte I2C bursts (measured: x3 bursts as reliable as x3 normal writes, half the time).</summary>
        public bool BurstWrites { get; set; } = true;
        public double VerifyTolPct { get; set; } = 1.5;
        /// <summary>Frame timestamp jitter in ms (Windows: arrival times on a ~16 ms grid; 1.5 steps).</summary>
        public double JitterMs { get; set; } = 24.0;
        /// <summary>
        /// Repeat exposures may use the frame in progress if it started at most this share of the exposure before
        /// the request (0 = only frames that start after the request; costs ~2x the exposure per frame).
        /// </summary>
        public double AcceptEarlyStartPct { get; set; } = 30;
        /// <summary>
        /// Exposures of 0.5 s and longer restart the sensor's frame cycle with a STANDBY toggle at the request, so the
        /// frame starts then (Windows 2026-10-29: next frame exposure + ~45 ms after release, full brightness, ~44 ms
        /// of XU traffic). Replaces AcceptEarlyStartPct for those exposures.
        /// </summary>
        public bool StandbyRestart { get; set; } = true;
        /// <summary>
        /// Slow USB port mode when an ASIC read takes longer than this at connect (ms; 0 = always, 1000 = never).
        /// Measured 2026-10-29 (Ryzen, AMD 500-series): CPU USB controller ~2 ms, chipset controller ~27 ms. On the
        /// slow port the firmware's per-frame I2C job wipes our half-written I2C setup at 30 fps (0/105 writes took).
        /// The mode streams at 15 fps, escapes the short frames once through a retried STANDBY, and keeps the frame
        /// period at least SlowPortMinPeriodMs, where writes work normally (8/8 STANDBY toggles, 1/1 rewrites).
        /// </summary>
        public double SlowPortReadMs { get; set; } = 8;
        /// <summary>Shortest frame period in slow USB port mode; shorter exposures use SHS1 inside it.</summary>
        public double SlowPortMinPeriodMs { get; set; } = 250;
        /// <summary>Show a warning box when connecting in slow USB port mode (off only for automated tests).</summary>
        public bool WarnSlowPort { get; set; } = true;
        public int MaxApplyAttempts { get; set; } = 3;
        /// <summary>
        /// Uncompressed 1920x1080 (YUY2 1920x1080 listed by cameras with a modified descriptor/mode table): the
        /// output path delivers no frame at the firmware's line length, so this HMAX is written right after every
        /// stream start (the firmware resets it then). Measured 2026-10-29: frames from HMAX ~28000 on; single frames
        /// get lost at any HMAX (~1 in 4-6 at 2-8 s), at 0x7800 frames of 8 s and more never arrived within 25 s, at
        /// 0xB500 (1H 484 us, base frame 544 ms) they do.
        /// </summary>
        public int HdYuy2Hmax { get; set; } = 0xB500;
        /// <summary>1H per HMAX unit in that mode in ns (measured 10.45; the 30 fps modes run at 6.25).</summary>
        public double HdYuy2NsPerHmax { get; set; } = 10.45;
        /// <summary>
        /// Uncompressed 1920x1080: sensor lines the bridge reads per frame (its input window, from sensor line 16),
        /// written after every stream start; the output stays 1080 lines. With the firmware's 1080 the image pipeline
        /// sends its last 6 lines (23 KB) in one burst after the frame end, and depending on where that falls within
        /// the USB microframe the bridge cuts up to 2.7 KB, so Windows drops the frame (measured 2026-10-30: ~28 % of
        /// frames, in bursts every ~5 s). With 1104 input lines the burst is gone (USB trace) and 1 of 301 frames was
        /// lost. 0 = leave the firmware's window.
        /// </summary>
        public int HdYuy2InputLines { get; set; } = 1104;
        /// <summary>
        /// ASCOM Gain = sensor gain in 0.3 dB steps, written to the sensor with every timing write (0x3014, and from
        /// HcgFromGain on high conversion gain with 0x3014 = Gain - HcgFromGain). The UVC gain is then held at 0: it only
        /// stretches the ISP's 8-bit tone curve, and in uncompressed 1080p it never reaches the sensor (measured
        /// 2026-10-30: 0x3014 doubles the signal per 6 dB; dark noise below one 8-bit level up to 18 dB, 1.6 at 30 dB).
        /// Off = ASCOM Gain is the UVC gain (drivers up to 0.7.3).
        /// </summary>
        public bool SensorGain { get; set; } = true;
        /// <summary>Highest ASCOM Gain with SensorGain (0.3 dB steps). 100 = 30 dB analog (+ HCG = 36 dB); above it the
        /// sensor gain is digital (up to 240 + HcgFromGain). PHD2 sets a share of the range, so a large maximum makes its
        /// default high.</summary>
        public int SensorGainMax { get; set; } = 100;
        /// <summary>
        /// ASCOM Gain from which HCG is on (0x3009 bit 4; -1 = never; 20 = conversion gain low below 6 dB). Measured
        /// 2026-10-30: HCG = x1.9 (~6 dB); at the same total gain (24 and 36 dB) about 18 % less dark noise than
        /// conversion gain low. Default 0: HCG at every gain, Gain 0 = 6 dB.
        /// </summary>
        public int HcgFromGain { get; set; } = 0;
        /// <summary>
        /// Gain at connect (SensorGain). PHD2's ASCOM camera never sets Gain (HasGainControl is always false), so it runs
        /// at this value; clients that set Gain (N.I.N.A., SharpCap) override it. 50 = 21 dB with HCG.
        /// </summary>
        public int ConnectGain { get; set; } = 50;
        /// <summary>UVC exposure set once at connect, DirectShow log2 seconds (-5 = 31 ms).</summary>
        public int UvcExposureLog2 { get; set; } = -5;
        /// <summary>
        /// Returns 0x8007007A on Windows, but must still be sent: without it every long-exposure frame in PHD2 had an
        /// 8-byte payload (measured in PHD2: 3 of 3 sessions without it, 1 of 1 fine with it). On by default.
        /// </summary>
        public bool AutoExposurePriorityOff { get; set; } = true;
        public bool BacklightOff { get; set; } = true;
        public bool FreezeWhiteBalance { get; set; } = true;
        public bool Color { get; set; } = false;
        public bool Trace { get; set; } = true;

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                    if (k != null)
                        foreach (var name in k.GetValueNames())
                            s.TrySet(name, k.GetValue(name)?.ToString());
            }
            catch (Exception e) { Log.Write("settings load failed: " + e.Message); }
            return s;
        }

        /// <summary>
        /// Stores only the values that differ from the defaults and removes the others, so a later driver version's
        /// measured defaults are not overridden by values saved from an older setup dialog.
        /// </summary>
        public void Save()
        {
            var d = new Settings();
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                foreach (var p in GetType().GetProperties())
                {
                    string v = Convert.ToString(p.GetValue(this), CultureInfo.InvariantCulture);
                    if (v == Convert.ToString(p.GetValue(d), CultureInfo.InvariantCulture)) k.DeleteValue(p.Name, false);
                    else k.SetValue(p.Name, v);
                }
        }

        /// <summary>Sets a property by name from text; returns false for unknown names or bad values.</summary>
        public bool TrySet(string name, string text)
        {
            var p = GetType().GetProperty(name);
            if (p == null || text == null) return false;
            try
            {
                object v;
                if (p.PropertyType == typeof(int) && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    v = Convert.ToInt32(text.Substring(2), 16);
                else
                    v = Convert.ChangeType(text, p.PropertyType, CultureInfo.InvariantCulture);
                p.SetValue(this, v);
                return true;
            }
            catch { return false; }
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in GetType().GetProperties())
                sb.Append(p.Name).Append('=').Append(Convert.ToString(p.GetValue(this), CultureInfo.InvariantCulture)).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Timestamped trace to %LOCALAPPDATA%\Sonix290\Logs and optionally the console.</summary>
    public static class Log
    {
        static readonly object Gate = new object();
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static StreamWriter file;
        public static bool ToConsole;
        public static bool Enabled = true;
        public static string FilePath { get; private set; }

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonix290", "Logs");

        public static void Open(string prefix)
        {
            lock (Gate)
            {
                if (file != null) return;
                try
                {
                    Directory.CreateDirectory(Folder);
                    FilePath = Path.Combine(Folder, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                    file = new StreamWriter(FilePath, true) { AutoFlush = true };
                }
                catch { file = null; }
            }
        }

        public static void Write(string text)
        {
            if (!Enabled && !ToConsole) return;
            var line = string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff} {1,9:F1} [{2}] {3}",
                DateTime.Now, Clock.Elapsed.TotalMilliseconds, Thread.CurrentThread.ManagedThreadId, text);
            lock (Gate)
            {
                if (Enabled) file?.WriteLine(line);
                if (ToConsole) Console.Error.WriteLine(line);
            }
        }
    }

    /// <summary>One MTA thread that owns every DirectShow and KS object; callers run work on it.</summary>
    sealed class ComThread : IDisposable
    {
        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        readonly Thread thread;

        public ComThread(string name)
        {
            thread = new Thread(() => { foreach (var a in queue.GetConsumingEnumerable()) a(); })
            { IsBackground = true, Name = name };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public T Run<T>(Func<T> work)
        {
            if (Thread.CurrentThread == thread) return work();
            T result = default(T);
            ExceptionDispatchInfo error = null;
            using (var done = new ManualResetEventSlim())
            {
                queue.Add(() =>
                {
                    try { result = work(); }
                    catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
                    finally { done.Set(); }
                });
                done.Wait();
            }
            error?.Throw();
            return result;
        }

        public void Run(Action work) => Run(() => { work(); return 0; });

        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join(5000);
        }
    }
}
