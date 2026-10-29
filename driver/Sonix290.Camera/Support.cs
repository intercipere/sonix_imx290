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

        public void Save()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                foreach (var p in GetType().GetProperties())
                    k.SetValue(p.Name, Convert.ToString(p.GetValue(this), CultureInfo.InvariantCulture));
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
