using System;
using System.Diagnostics;
using System.Threading;

namespace Sonix290
{
    public sealed class ExposureResult
    {
        public Frame Frame;
        public DateTime StartUtc;
        public double Requested;
        public SensorTiming Timing;
        /// <summary>Measured interval of the returned frame (s); NaN if not measurable.</summary>
        public double MeasuredPeriod;
        public double ExpectedPeriod;
        public int Applies;
        public int FramesSkipped;
        public double TotalSeconds;
    }

    /// <summary>
    /// The camera: a free-running MJPEG stream whose frame length is set directly in the sensor.
    /// Every returned frame is checked by its interval (the only trustworthy report of what the sensor did);
    /// when the interval is wrong the VMAX/SHS1 write is repeated.
    /// </summary>
    public sealed class CameraEngine : IDisposable
    {
        static readonly object SharedGate = new object();
        static CameraEngine shared;
        static int sharedRefs;

        public readonly Settings Settings;
        readonly ComThread com = new ComThread("Sonix290 COM");
        readonly FrameBuffer frames = new FrameBuffer();
        UvcDevice dev;
        SonixXu xu;
        SensorTiming? applied;
        readonly object exposeGate = new object();

        public int Width => dev?.Width ?? 0;
        public int Height => dev?.Height ?? 0;
        public bool IsOpen => dev != null;
        public string DeviceName => dev?.Name;
        public FrameBuffer Frames => frames;
        /// <summary>1H. The firmware doubles it at 15 fps instead of VMAX (measured 58.18 us; VMAX 17191 -> 1000.1 ms).
        /// In the uncompressed 1080p mode it follows the HMAX the engine writes.</summary>
        public double LineSec => HdYuy2 ? Settings.HdYuy2Hmax * Settings.HdYuy2NsPerHmax * 1e-9 : Settings.LineTimeUs * 1e-6 * LineFactor;
        /// <summary>Uncompressed 1920x1080 (cameras whose firmware lists it): needs HMAX written at every stream start.</summary>
        bool HdYuy2 => dev != null && dev.SubType == MediaGuids.Yuy2 && dev.Width >= 1920;
        double LineFactor => dev != null && dev.Fps > 0 && dev.Fps < 20 ? 2 : 1;
        /// <summary>Slow USB port mode (see Settings.SlowPortReadMs).</summary>
        public bool SlowPort { get; private set; }
        /// <summary>Slow port mode with MJPEG at 15 fps, where the image can be grainier (GitHub issue #1: the JPEG
        /// encoder settles at other quality levels). Uncompressed modes keep their frame rate and have no encoder.</summary>
        public bool SlowPortMjpeg => SlowPort && dev != null && dev.SubType == MediaGuids.Mjpg && dev.Fps < 20;
        /// <summary>Mean ASIC read time measured at connect (ms).</summary>
        public double XuReadMs { get; private set; }
        /// <summary>The slow port warning was shown for this connection (the engine is shared by driver instances).</summary>
        internal bool SlowPortWarned;
        /// <summary>VMAX below which SHS1 sets the exposure; in slow port mode the frame never gets shorter than the floor.</summary>
        public int BaseVmax => SlowPort
            ? Math.Max(Settings.BaseVmax, (int)Math.Round(Settings.SlowPortMinPeriodMs / 1000 / LineSec))
            : Settings.BaseVmax;
        public int GainMin { get; private set; }
        public int GainMax { get; private set; }
        public bool HasGain { get; private set; }

        public CameraEngine(Settings s) { Settings = s; }

        /// <summary>One engine per process, shared by driver instances (the camera can only be opened once).</summary>
        public static CameraEngine Acquire(Settings s)
        {
            lock (SharedGate)
            {
                if (shared == null)
                {
                    var e = new CameraEngine(s);
                    try { e.Open(); }
                    catch { e.Dispose(); throw; }
                    shared = e;
                }
                sharedRefs++;
                return shared;
            }
        }

        public static void ReleaseShared()
        {
            lock (SharedGate)
            {
                if (sharedRefs > 0 && --sharedRefs == 0)
                {
                    shared.Dispose();
                    shared = null;
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);
        bool timerRaised, wroteTiming, usedStandby;
        /// <summary>VMAX of the last timing write (uncompressed 1080p: an SHS1-only change is written without STANDBY).</summary>
        int lastVmax = -1;
        /// <summary>Slow port mode: the sensor has left the short default frames (bootstrap through STANDBY done).</summary>
        bool escaped;

        /// <summary>Opens and starts the camera. On failure call Dispose (it releases the device).</summary>
        public void Open()
        {
            Log.Write("open: " + Settings);
            // Default Windows timer resolution (15.6 ms) would stretch the I2C polling sleeps 3-4x.
            if (!timerRaised) timerRaised = timeBeginPeriod(1) == 0;
            sensorGain = Settings.ConnectGain;   // clamped to the range in ConfigureControls
            var sub =Settings.Format.Equals("YUY2", StringComparison.OrdinalIgnoreCase) ? MediaGuids.Yuy2 : MediaGuids.Mjpg;
            StartDevice(sub, Settings.FrameIntervalMs);
            com.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                const int reads = 6;
                for (int i = 0; i < reads; i++) xu.AsicRead(0x101F);
                XuReadMs = sw.Elapsed.TotalMilliseconds / reads;
            });
            SlowPort = XuReadMs > Settings.SlowPortReadMs;
            Log.Write($"ASIC read {XuReadMs:F1} ms (slow port threshold {Settings.SlowPortReadMs} ms)" +
                      (SlowPort ? ": slow USB port mode" : ""));
            if (SlowPort && Settings.FrameIntervalMs <= 0 && sub == MediaGuids.Mjpg)
            {
                // 15 fps doubles the time between the firmware's I2C jobs, which the STANDBY bootstrap needs
                // (2026-10-29: STANDBY took 0/30 at 30 fps, 4/30 at 15 fps on the chipset port).
                com.Run(() => { xu.Dispose(); xu = null; dev.Dispose(); dev = null; });
                frames.BreakChain();
                StartDevice(sub, 66.6667);
            }
            if (SlowPortMjpeg)
                Log.Write("WARNING: MJPEG at 15 fps on a slow USB port, the image may be grainier; for guiding use a port " +
                          "on the CPU's USB controller");
            applied = null;
            escaped = false;
        }

        void StartDevice(Guid sub, double intervalMs)
        {
            long first = frames.Count;
            com.Run(() =>
            {
                dev = UvcDevice.Open(Settings.DeviceMatch, Settings.Width, Settings.Height, sub, intervalMs, frames);
                Log.Write($"device '{dev.Name}' {MediaGuids.Name(dev.SubType)} {dev.Width}x{dev.Height} {dev.Fps:F1} fps, {dev.Path}");
                ConfigureControls();
                dev.Start();
                // uncompressed 1080p delivers no frame at the firmware's line length: HMAX before the first frame
                if (HdYuy2)
                {
                    xu = SonixXu.Find(dev.KsControl, dev.Topology);
                    WriteHdHmax();
                }
            });
            // A frame length left from an earlier session can make the first frame take up to 7.6 s.
            // Uncompressed 1080p: the HMAX write can get lost on a slow port (connect 1 of 3 on the chipset port,
            // 2026-10-30), and without it no frame ever comes, so it is written again every 5 s.
            int tries = HdYuy2 ? 4 : 1;
            var wait = TimeSpan.FromSeconds(HdYuy2 ? 5 : 20);
            bool got = false;
            for (int i = 0; i < tries && !got; i++)
            {
                if (i > 0)
                {
                    Log.Write($"no frame {wait.TotalSeconds:F0} s after the HMAX write, writing it again");
                    com.Run(WriteHdHmax);
                }
                got = frames.WaitFrom(first, wait, CancellationToken.None) != null;
            }
            if (!got)
            {
                throw new InvalidOperationException(HdYuy2
                    ? "Camera delivers no frames in uncompressed 1920x1080 (HMAX write failed, or the line length is too short: raise HdYuy2Hmax)"
                    : "Camera delivers no frames (in use by another program, or camera access for desktop apps off in Windows privacy settings?)");
            }
            com.Run(() =>
            {
                if (xu == null) xu = SonixXu.Find(dev.KsControl, dev.Topology);
                Log.Write($"ASIC chip id (0x101F) = 0x{xu.AsicRead(0x101F):X2}");
                bool ack = xu.Probe(Settings.SensorSlave);
                Log.Write($"sensor slave 0x{Settings.SensorSlave:X2} ack: {ack}");
            });
        }

        void ConfigureControls()
        {
            var cc = dev.CameraControl;
            if (cc != null)
            {
                const int exposure = 4;
                if (cc.GetRange(exposure, out int min, out int max, out _, out int def, out _) == 0)
                {
                    int v = Math.Max(min, Math.Min(max, Settings.UvcExposureLog2));
                    int hr = cc.Set(exposure, v, 2 /* manual */);
                    Log.Write($"UVC exposure range {min}..{max} (default {def}); set manual {v}: hr=0x{hr:X8}");
                }
                else Log.Write("UVC exposure control not available");
            }
            if (Settings.AutoExposurePriorityOff)
            {
                int hr = SetKsCameraControl(Ks.AutoExposurePriority, 0);
                Log.Write($"auto exposure priority (dynamic framerate) off: hr=0x{hr:X8}");
            }
            var pa = dev.ProcAmp;
            if (pa != null)
            {
                foreach (var (prop, name) in new[] { (1, "contrast"), (5, "gamma"), (0, "brightness"), (3, "saturation"), (4, "sharpness") })
                    Freeze(pa, prop, name, null);
                if (Settings.BacklightOff) Freeze(pa, 8, "backlight compensation", 0);
                if (Settings.FreezeWhiteBalance) Freeze(pa, 7, "white balance", null);
                if (Settings.SensorGain)
                {
                    GainMin = 0;
                    GainMax = Math.Max(0, Math.Min(Settings.SensorGainMax, SensorTiming.GainLimit + Math.Max(0, Settings.HcgFromGain)));
                    HasGain = true;
                    sensorGain = Math.Max(0, Math.Min(sensorGain, GainMax));
                    Log.Write($"gain: sensor gain 0..{GainMax} (0.3 dB steps, HCG from {Settings.HcgFromGain}), now {sensorGain}");
                    if (pa.GetRange(9, out _, out _, out _, out _, out _) == 0) Freeze(pa, 9, "UVC gain (held at 0)", 0);
                }
                else if (pa.GetRange(9, out int gmin, out int gmax, out _, out int gdef, out _) == 0)
                {
                    GainMin = gmin; GainMax = gmax; HasGain = true;
                    pa.Get(9, out int g, out int f);
                    pa.Set(9, g, 2);
                    Log.Write($"gain range {gmin}..{gmax} default {gdef}, now {g} (flags {f})");
                }
            }
        }

        static void Freeze(IAMVideoProcAmp pa, int prop, string name, int? value)
        {
            if (pa.GetRange(prop, out int min, out int max, out _, out int def, out int caps) != 0) return;
            pa.Get(prop, out int cur, out int flags);
            int v = value ?? cur;
            int hr = pa.Set(prop, v, 2);
            Log.Write($"{name}: range {min}..{max} default {def} was {cur} (flags {flags}); set manual {v}: hr=0x{hr:X8}");
        }

        /// <summary>Sets a KSPROPERTY_CAMERACONTROL_S value through IKsControl (for ids DirectShow does not wrap).</summary>
        int SetKsCameraControl(int id, int value)
        {
            if (dev.KsControl == null) return -1;
            // KSPROPERTY_CAMERACONTROL_S: KSPROPERTY (24) + Value + Flags + Capabilities
            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(36);
            try
            {
                var g = Ks.CameraControlSet.ToByteArray();
                System.Runtime.InteropServices.Marshal.Copy(g, 0, buf, 16);
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, 16, id);
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, 20, Ks.TypeSet);
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, 24, value);
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, 28, 2 /* manual */);
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, 32, 0);
                return dev.KsControl.KsProperty(buf, 36, buf, 36, out _);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(buf); }
        }

        /// <summary>ASCOM Gain with Settings.SensorGain (0.3 dB steps); written with the next exposure's timing.</summary>
        int sensorGain;

        public int Gain
        {
            get => Settings.SensorGain ? sensorGain : com.Run(() => { dev.ProcAmp.Get(9, out int g, out _); return g; });
            set
            {
                // N.I.N.A. sets the gain before every exposure; only touch the camera on a real change.
                // Measured: a gain change keeps VMAX, and every frame's interval is checked anyway.
                int old = Gain;
                if (old == value) return;
                if (Settings.SensorGain) sensorGain = value;
                else com.Run(() => dev.ProcAmp.Set(9, value, 2));
                Log.Write($"gain {old} -> {value}");
            }
        }

        /// <summary>Adds the sensor gain registers for an ASCOM gain to a timing (SensorGain only).</summary>
        SensorTiming WithGain(SensorTiming t, int gain)
        {
            if (!Settings.SensorGain) return t;
            t.Hcg = Settings.HcgFromGain >= 0 && gain >= Settings.HcgFromGain;
            t.Gain = Math.Min(SensorTiming.GainLimit, t.Hcg ? gain - Settings.HcgFromGain : gain);
            return t;
        }

        /// <summary>After a stream start the sensor may hold the firmware's gain again: rewrite ours (COM thread).</summary>
        void RewriteGain()
        {
            if (!Settings.SensorGain || !applied.HasValue) return;
            var g = applied.Value;
            bool ok = xu.SensorWrite(Settings.SensorSlave, 0x3001, 1)
                    & xu.SensorWrite(Settings.SensorSlave, 0x3014, (byte)g.Gain.Value)
                    & xu.SensorWrite(Settings.SensorSlave, 0x3009, SensorTiming.Reg3009(g.Hcg))
                    & xu.SensorWrite(Settings.SensorSlave, 0x3001, 0);
            Log.Write($"gain rewritten after the stream start (GAIN={g.Gain}{(g.Hcg ? " HCG" : "")}): {(ok ? "ok" : "I2C failed")}");
        }

        /// <summary>Writes VMAX/SHS1 Settings.WriteRepeats times (a single write does not always take effect).</summary>
        public void WriteTiming(SensorTiming t, int? repeats = null, bool? burst = null)
        {
            wroteTiming = true;
            int n = repeats ?? Settings.WriteRepeats;
            bool b = burst ?? Settings.BurstWrites;
            var sw = Stopwatch.StartNew();
            int ok = 0, total = 0;
            com.Run(() =>
            {
                for (int i = 0; i < n; i++)
                {
                    ok += t.Write(xu, Settings.SensorSlave, b, out int tr);
                    total += tr;
                }
            });
            Log.Write($"wrote {t} x{n}{(b ? " burst" : "")}: {ok}/{total} I2C ok, {sw.ElapsedMilliseconds} ms");
        }

        static double Sec(long ticks) => ticks / (double)Stopwatch.Frequency;

        /// <summary>
        /// Frame timestamps on Windows sit on a ~16 ms grid (host arrival), and DirectShow sample times are
        /// synthetic (stuck at 33.3 ms for periods up to ~50 ms). So only host arrival times are used, and a new
        /// timing is verified on the mean interval over a window of about this length.
        /// </summary>
        const double VerifyWindowSec = 0.25;

        static int WindowFor(double expected) => Math.Min(8, Math.Max(1, (int)Math.Ceiling(VerifyWindowSec / expected)));

        double Tolerance(double expected, int intervals) =>
            Math.Max(expected * Settings.VerifyTolPct / 100, Settings.JitterMs / 1000 / intervals + 0.0005);

        /// <summary>Confirmed timing, one interval: allow jitter, and whole multiples for dropped short frames.</summary>
        bool SingleMatches(double dt, double expected)
        {
            double tol = Tolerance(expected, 1);
            for (int k = 1; k <= (expected < 0.2 ? 4 : 1); k++)
                if (Math.Abs(dt - k * expected) <= tol * k) return true;
            return false;
        }

        const int StandbyReg = 0x3000;
        /// <summary>Shortest exposure that uses the STANDBY restart; below it the early-start rule costs at most a short frame.</summary>
        const double StandbyMinSec = 0.5;
        /// <summary>The frame cut by STANDBY arrives ~45 ms after the release; frames arriving this soon are discarded.</summary>
        const double StandbyFlushSec = 0.2;

        /// <summary>
        /// STANDBY 1 then 0 restarts the sensor's frame cycle: the running frame is cut (and arrives ~45 ms later), the
        /// next one starts at the release and keeps VMAX/SHS1. Returns the release time.
        /// </summary>
        long RestartFrame()
        {
            var sw = Stopwatch.StartNew();
            bool on = false, off = false;
            com.Run(() =>
            {
                on = xu.SensorWrite(Settings.SensorSlave, StandbyReg, 1);
                off = xu.SensorWrite(Settings.SensorSlave, StandbyReg, 0);
            });
            long release = Stopwatch.GetTimestamp();
            usedStandby = true;
            Log.Write($"standby restart: {sw.ElapsedMilliseconds} ms (I2C {(on ? "ok" : "fail")}/{(off ? "ok" : "fail")})");
            return release;
        }

        /// <summary>
        /// VMAX/SHS1 written once while the sensor is in STANDBY: the firmware's per-frame I2C job, which otherwise
        /// wipes or garbles about a third of the writes, is idle then. The release restarts the frame. Returns the
        /// release time.
        /// </summary>
        long StandbyWrite(SensorTiming t)
        {
            var sw = Stopwatch.StartNew();
            bool on = false, off = false;
            int ok = 0, total = 0;
            com.Run(() =>
            {
                on = xu.SensorWrite(Settings.SensorSlave, StandbyReg, 1);
                ok = t.Write(xu, Settings.SensorSlave, Settings.BurstWrites, out total);
                off = xu.SensorWrite(Settings.SensorSlave, StandbyReg, 0);
            });
            long release = Stopwatch.GetTimestamp();
            usedStandby = wroteTiming = true;
            Log.Write($"wrote {t} in a STANDBY toggle: {ok}/{total} I2C ok ({(on ? "ok" : "fail")}/{(off ? "ok" : "fail")}), " +
                      $"{sw.ElapsedMilliseconds} ms");
            return release;
        }

        /// <summary>
        /// Uncompressed 1080p: HMAX in a STANDBY toggle (the firmware leaves the I2C master alone in STANDBY), repeated
        /// until all writes report done (at most 3 tries). Call on the COM thread right after every stream start.
        /// </summary>
        void WriteHdHmax()
        {
            int h = Settings.HdYuy2Hmax, ok = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 3 && ok < 5; i++)
            {
                ok = (xu.SensorWrite(Settings.SensorSlave, StandbyReg, 1) ? 1 : 0)
                   + (xu.SensorWrite(Settings.SensorSlave, 0x3001, 1) ? 1 : 0)
                   + (xu.SensorWrite(Settings.SensorSlave, 0x301C, (byte)h, (byte)(h >> 8)) ? 1 : 0)
                   + (xu.SensorWrite(Settings.SensorSlave, 0x3001, 0) ? 1 : 0)
                   + (xu.SensorWrite(Settings.SensorSlave, StandbyReg, 0) ? 1 : 0);
            }
            usedStandby = true;
            // The firmware's per-frame sensor job rewrites SHS1 from the UVC exposure in this mode (measured: SHS1 8 and
            // 1000 gave reversed brightness). RAM 0x0A54 = 1 makes the job return at once (from the firmware analysis;
            // measured 2026-10-29: SHS1 then sticks, 40 ms vs 358 ms). UVC exposure/gain then no longer reach the sensor.
            xu.AsicWrite(0x0A54, 1);
            byte job = xu.AsicRead(0x0A54);
            lastVmax = -1;   // the stream start re-ran the sensor init
            Log.Write($"uncompressed 1080p: HMAX 0x{h:X4} written ({ok}/5 I2C ok, {sw.ElapsedMilliseconds} ms), 1H {LineSec * 1e6:F1} us; " +
                      $"firmware sensor job off (0x0A54 = {job:X2}); {WriteHdInputWindow()}");
        }

        /// <summary>
        /// Input window height (HdYuy2InputLines): 0x118A bits 5:4 and 0x118B hold lines / 2 (stock 0x21C = 1080).
        /// The output size (0x1125, 0x111D) is not touched, so frames stay 1920x1080.
        /// </summary>
        string WriteHdInputWindow()
        {
            int lines = Settings.HdYuy2InputLines;
            if (lines <= 0) return "input window left at the firmware's";
            int half = lines / 2;
            byte hi = xu.AsicRead(0x118A), lo = xu.AsicRead(0x118B);
            xu.AsicWrite(0x118A, (byte)((hi & 0xCF) | ((half >> 8) & 3) << 4));
            xu.AsicWrite(0x118B, (byte)half);
            int now = ((xu.AsicRead(0x118A) >> 4) & 3) << 8 | xu.AsicRead(0x118B);
            return $"input window {(((hi >> 4) & 3) << 8 | lo) * 2} -> {now * 2} lines{(now == half ? "" : " (DID NOT TAKE)")}";
        }

        int HdLines => Settings.HdYuy2InputLines > 0 ? Settings.HdYuy2InputLines : 1080;

        /// <summary>Uncompressed 1080p: a frame arrives one period plus about the readout of the input lines after a
        /// STANDBY release (measured +363..378 ms at 1H 321 us); the frame cut by the release is not delivered.</summary>
        double HdFirstFrameLatency => HdLines * LineSec + 0.03;

        /// <summary>Makes sure the sensor is not left in STANDBY (the release write may not have landed).</summary>
        void ReleaseStandby() => com.Run(() => xu.SensorWrite(Settings.SensorSlave, StandbyReg, 0));

        /// <summary>Longest the slow port bootstrap tries to get STANDBY through.</summary>
        const double BootstrapLimitSec = 30;
        /// <summary>STANDBY writes per check (each ~13 % at 15 fps on the chipset port; a check costs ~2 writes).</summary>
        const int BootstrapWritesPerCheck = 3;

        /// <summary>
        /// Slow port mode: a write while the frames are short loses against the firmware's per-frame I2C job. STANDBY
        /// is a single I2C write, retried until the frames stop; in STANDBY the firmware leaves the I2C master alone,
        /// so VMAX/SHS1 are written then, and the release restarts the frame. Returns the release time.
        /// </summary>
        long Bootstrap(SensorTiming t, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            double period = Math.Min(Math.Max(frames.LastHostDt, 0.03), 8);
            // the running frame is cut and flushed (~45 ms); after that no frame may arrive while in STANDBY
            int settleMs = (int)(period * 1000) + 60, quietMs = (int)(period * 2000) + 20;
            int tries = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (sw.Elapsed.TotalSeconds > BootstrapLimitSec)
                    throw new InvalidOperationException(
                        $"camera did not take the exposure ({t}): its register access is slow on this USB port " +
                        $"({XuReadMs:F0} ms per read) and STANDBY did not take in {tries} writes; connect the camera to " +
                        "another USB port (on desktop PCs: a rear port on the CPU's USB controller)");
                tries += BootstrapWritesPerCheck;
                com.Run(() => { for (int i = 0; i < BootstrapWritesPerCheck; i++) xu.SensorWrite(Settings.SensorSlave, StandbyReg, 1); });
                Thread.Sleep(settleMs);
                long c = frames.Count;
                Thread.Sleep(quietMs);
                if (frames.Count == c) break;
            }
            usedStandby = true;
            long ws = sw.ElapsedMilliseconds;
            int ok = 0, total = 0;
            com.Run(() => { ok = t.Write(xu, Settings.SensorSlave, Settings.BurstWrites, out total); });
            wroteTiming = true;
            long release = 0;
            for (int i = 0; i < 10; i++)
            {
                long c = frames.Count;
                com.Run(() => xu.SensorWrite(Settings.SensorSlave, StandbyReg, 0));
                release = Stopwatch.GetTimestamp();
                // the first frame after the release ends one period later
                if (frames.WaitFrom(c, TimeSpan.FromSeconds(t.Period(LineSec) + 1), ct) != null) break;
                Log.Write("bootstrap: no frame after the STANDBY release, releasing again");
            }
            Log.Write($"bootstrap: STANDBY took within {tries} writes ({ws} ms), wrote {t} in STANDBY ({ok}/{total} I2C ok), " +
                      $"{sw.ElapsedMilliseconds} ms in all");
            escaped = true;
            return release;
        }

        /// <summary>
        /// Consecutive invalid frames before the stream is restarted. They arrive at the right intervals, so the timing
        /// is fine and a VMAX rewrite does not help (measured: 8-byte frames through 3 rewrites in PHD2); a graph
        /// Stop/Run cleared them every time (2026-10-29). Seen after the first write in ~1 of 3 32-bit connects.
        /// Once they start, every later frame is 8 bytes until the restart (PC 2: all of ~20 cases), so the first
        /// one is enough and saves a whole exposure.
        /// </summary>
        const int MaxInvalidInRow = 1;

        /// <summary>A frame the camera really encoded (8-byte samples appear after some VMAX writes).</summary>
        bool ValidPayload(Frame f) =>
            dev.SubType == MediaGuids.Yuy2
                ? f.Data.Length == dev.Width * dev.Height * 2
                : f.Data.Length >= 2048 && f.Data[0] == 0xFF && f.Data[1] == 0xD8;

        public Picture Decode(Frame f, bool color) =>
            dev.SubType == MediaGuids.Yuy2 ? Imaging.DecodeYuy2(f.Data, dev.Width, dev.Height, color) : Imaging.Decode(f.Data, color);

        /// <summary>
        /// Returns a frame with the requested exposure, verified by frame intervals. After a VMAX/SHS1 write the
        /// frame must have started after the write. From StandbyMinSec on (StandbyRestart), the frame cycle is
        /// restarted at the request and after each write, so the frame starts then. Otherwise, without a write, a
        /// frame that started at most AcceptEarlyStartPct of the exposure before this call is accepted (looping
        /// clients get ~1x cadence instead of 2x; set 0 for strict start-after-request).
        /// </summary>
        public ExposureResult Expose(double seconds, CancellationToken ct, Action<string> phase = null)
        {
            lock (exposeGate)
            {
                if (dev == null) throw new InvalidOperationException("not connected");
                var t = WithGain(SensorTiming.For(seconds, LineSec, BaseVmax), sensorGain);
                double expected = t.Period(LineSec);
                var r = new ExposureResult { Requested = seconds, Timing = t, ExpectedPeriod = expected };
                long t0 = Stopwatch.GetTimestamp();
                long cursor = frames.Count;
                bool confirmed = applied.HasValue && applied.Value.Equals(t);
                long notBefore = t0 - (long)(expected * Settings.AcceptEarlyStartPct / 100 * Stopwatch.Frequency);
                int need = 1, skipFirst = 0, windowN = 0, badInRow = 0, invalidInRow = 0, streamRestarts = 0;
                long windowStart = 0, flushUntil = 0, releaseAt = 0;
                bool standby = Settings.StandbyRestart && expected >= StandbyMinSec;

                void Restart()
                {
                    long release = RestartFrame();
                    notBefore = releaseAt = release;
                    flushUntil = release + (long)(StandbyFlushSec * Stopwatch.Frequency);
                    cursor = frames.Count;
                }

                void Write(string why)
                {
                    applied = null;
                    if (r.Applies >= Settings.MaxApplyAttempts)
                        throw new InvalidOperationException(
                            $"camera did not take the exposure ({t}) after {r.Applies} write attempts; last: {why}");
                    if (r.Applies > 0) Log.Write($"rewriting {t}: {why}");
                    phase?.Invoke("applying " + t);
                    // Slow port: the first write and every rewrite go through STANDBY (a failed write may have left
                    // the short default frames, where normal writes lose).
                    bool boot = SlowPort && (!escaped || r.Applies > 0);
                    // Uncompressed 1080p: after a STANDBY release at the base frame length the stream needs 1-2 s to
                    // resume (2026-10-29), so a change of SHS1 alone (same VMAX) is written plainly; with the firmware's
                    // sensor job off nothing overwrites it. A first write or rewrite still goes through STANDBY.
                    bool plainHd = HdYuy2 && r.Applies == 0 && lastVmax == t.Vmax;
                    long release = boot ? Bootstrap(t, ct) : Settings.StandbyWrites && !plainHd ? StandbyWrite(t) : 0;
                    lastVmax = t.Vmax;
                    if (release == 0) WriteTiming(t);
                    r.Applies++;
                    confirmed = false;
                    notBefore = Stopwatch.GetTimestamp();
                    cursor = frames.Count;
                    need = WindowFor(expected);
                    // Arrival lags the sensor's frame boundary by about a frame; for short periods skipping one
                    // more frame is cheap and keeps a frame that began before the write out of the window.
                    skipFirst = expected < VerifyWindowSec ? 1 : 0;
                    // uncompressed 1080p arrives ~one base frame (the readout) after its end: after a plain write the
                    // first frame to arrive started before it (measured: the brightness lagged by one request)
                    if (HdYuy2 && release == 0) skipFirst = 1;
                    windowN = 0;
                    badInRow = 0;
                    if (release != 0)
                    {
                        // the release restarted the frame like a STANDBY restart
                        notBefore = releaseAt = release;
                        flushUntil = release + (long)(StandbyFlushSec * Stopwatch.Frequency);
                    }
                    else if (standby) Restart();
                }

                if (!confirmed) Write("new timing");
                else if (standby) Restart();

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // uncompressed 1080p: single frames get lost (~1 in 4-6), and a rewrite (STANDBY) costs more of them,
                    // so a missing frame is waited for two periods plus the readout before the write counts as failed
                    var wait = TimeSpan.FromSeconds(HdYuy2 ? 2 * expected + HdFirstFrameLatency + 3 : Math.Max(expected, 0.1) * 2.5 + 3);
                    var f = frames.WaitFrom(cursor, wait, ct);
                    if (f == null)
                    {
                        Log.Write($"no frame for {wait.TotalSeconds:F1} s");
                        if (usedStandby) ReleaseStandby();
                        Write("timeout");
                        continue;
                    }
                    cursor = f.Index + 1;
                    if (f.Ticks < flushUntil)
                    {
                        r.FramesSkipped++;   // the frame cut by the STANDBY restart
                        continue;
                    }
                    // Uncompressed 1080p: the frame cut by the release never arrives, so the first frame after it has no
                    // interval of its own; judge it by its arrival after the release (one period plus the readout).
                    if (HdYuy2 && releaseAt != 0 && f.PrevTicks < releaseAt && ValidPayload(f))
                    {
                        long rel = releaseAt;
                        releaseAt = 0;
                        // frames get lost now and then in this mode (~1 in 4-6, any length): the k-th period after the
                        // release is a complete exposure of the right length as well
                        double since = Sec(f.Ticks - rel), after = since - HdFirstFrameLatency;
                        int k = Math.Max(1, (int)Math.Round(after / expected));
                        double period = after / k;
                        bool ok = k <= 3 && Math.Abs(period - expected) <= Tolerance(expected, 1) + 0.03 / k;
                        Log.Write($"frame {f.Index}: {since * 1000:F0} ms after the release = {k} period(s) of {period * 1000:F1} ms + readout, " +
                                  $"expected {expected * 1000:F1} ms -> {(ok ? "ok" : "WRONG")}");
                        if (ok)
                        {
                            applied = t;
                            r.Frame = f;
                            r.MeasuredPeriod = period;
                            r.StartUtc = DateTime.UtcNow - TimeSpan.FromSeconds(Sec(Stopwatch.GetTimestamp() - rel));
                            r.TotalSeconds = Sec(Stopwatch.GetTimestamp() - t0);
                            return r;
                        }
                        r.FramesSkipped++;   // later frames are judged by their intervals as usual
                        continue;
                    }
                    if (!f.HasPrev || f.PrevTicks < notBefore)
                    {
                        r.FramesSkipped++;   // started before the request or the write (transition frame)
                        continue;
                    }
                    if (!ValidPayload(f))
                    {
                        Log.Write($"frame {f.Index}: invalid payload ({f.Data.Length} bytes)");
                        r.FramesSkipped++;
                        windowN = 0;
                        if (++invalidInRow >= MaxInvalidInRow)
                        {
                            if (streamRestarts >= Settings.MaxApplyAttempts)
                                throw new InvalidOperationException(
                                    $"camera delivers invalid frames ({f.Data.Length} bytes) after {streamRestarts} stream restarts");
                            streamRestarts++;
                            var sw = Stopwatch.StartNew();
                            com.Run(() =>
                            {
                                dev.Stop();
                                frames.BreakChain();
                                dev.Start();
                                if (HdYuy2) WriteHdHmax();
                                RewriteGain();
                            });
                            Log.Write($"restarted the stream after {invalidInRow} invalid frames ({sw.ElapsedMilliseconds} ms)");
                            invalidInRow = 0;
                            // VMAX survives Stop/Run; the frame after Run started before it, so wait for a fresh one.
                            if (standby) Restart();
                            else { notBefore = Stopwatch.GetTimestamp(); cursor = frames.Count; }
                        }
                        continue;
                    }
                    invalidInRow = 0;
                    if (skipFirst > 0)
                    {
                        skipFirst--;
                        r.FramesSkipped++;
                        continue;
                    }
                    if (windowN == 0) windowStart = f.PrevTicks;
                    windowN++;
                    // One interval far beyond the expected one is a bad write, not grid jitter: judge it now instead of
                    // after the whole window (Windows 2026-10-29: a bad 33 ms write gave 360 ms frames, 6.8 s to recover).
                    bool farOff = !confirmed && windowN < need && f.HostDt > 2 * expected + Settings.JitterMs / 1000;
                    if (windowN < need && !farOff)
                    {
                        r.FramesSkipped++;
                        continue;
                    }

                    double mean = farOff ? f.HostDt : Sec(f.Ticks - windowStart) / windowN;
                    bool good = !farOff && (confirmed ? SingleMatches(f.HostDt, expected)
                                                      : Math.Abs(mean - expected) <= Tolerance(expected, windowN));
                    double sampleDt = f.SampleDtValid ? (f.SampleTime - f.PrevSampleTime) * 1000 : double.NaN;
                    Log.Write($"frame {f.Index}: {(windowN > 1 && !farOff ? $"mean of {windowN} intervals" : "interval")} {mean * 1000:F1} ms" +
                              $" (sample dt {sampleDt:F1}), expected {expected * 1000:F1} ms" +
                              $"{(confirmed ? "" : " (verifying)")} -> {(good ? "ok" : farOff ? "WRONG (far off)" : "WRONG")}");
                    if (good)
                    {
                        applied = t;
                        r.Frame = f;
                        r.MeasuredPeriod = mean;
                        r.StartUtc = DateTime.UtcNow - TimeSpan.FromSeconds(Sec(Stopwatch.GetTimestamp() - f.PrevTicks));
                        r.TotalSeconds = Sec(Stopwatch.GetTimestamp() - t0);
                        double early = Sec(t0 - f.PrevTicks);
                        if (early > 0) Log.Write($"frame {f.Index} started ~{early * 1000:F0} ms before the request (accepted)");
                        return r;
                    }
                    r.FramesSkipped++;
                    windowN = 0;
                    if (++badInRow >= 2) Write($"interval {mean * 1000:F1} ms");
                }
            }
        }

        /// <summary>Marks the sensor state unknown so the next exposure writes VMAX/SHS1 again.</summary>
        public void Invalidate() => applied = null;

        /// <summary>Runs work on the camera's COM thread with the device and XU (for the test tool).</summary>
        internal T OnDevice<T>(Func<UvcDevice, SonixXu, T> work) => com.Run(() => work(dev, xu));

        /// <summary>Test path: Stop (or Pause) and Run the graph. Neither restarts the sensor's frame (measured; Run waits
        /// for the next boundary of the running frame cycle); see RestartFrame for what does.</summary>
        internal void RestartStream(bool pause = false) => com.Run(() =>
        {
            if (pause) dev.Pause(); else dev.Stop();
            frames.BreakChain();
            dev.Start();
            if (HdYuy2) WriteHdHmax();
            RewriteGain();
        });

        internal int SetKsControl(int id, int value) => com.Run(() => SetKsCameraControl(id, value));

        /// <summary>Test path: rebuild the graph with another UVC frame interval (0 = the format's default).</summary>
        internal void Reopen(double intervalMs)
        {
            com.Run(() => { xu.Dispose(); xu = null; dev.Dispose(); dev = null; });
            frames.BreakChain();
            StartDevice(Settings.Format.Equals("YUY2", StringComparison.OrdinalIgnoreCase) ? MediaGuids.Yuy2 : MediaGuids.Mjpg, intervalMs);
        }

        void Close()
        {
            com.Run(() =>
            {
                // Leave the sensor at 30 fps so the next program (or connect) does not inherit a long frame.
                if (xu != null && dev != null && dev.Running && wroteTiming)
                {
                    try
                    {
                        // ... and at 0 dB, conversion gain low (SensorGain), as after a replug
                        var b = WithGain(new SensorTiming { Vmax = Settings.BaseVmax, Shs1 = SensorTiming.LongShs1 }, 0);
                        if (Settings.StandbyWrites) StandbyWrite(b);
                        else
                        {
                            if (usedStandby) xu.SensorWrite(Settings.SensorSlave, StandbyReg, 0);
                            WriteTiming(b, SlowPort ? 1 : (int?)null);
                        }
                    }
                    catch (Exception e) { Log.Write("reset to base timing failed: " + e.Message); }
                }
                xu?.Dispose();
                xu = null;
                dev?.Dispose();
                dev = null;
            });
        }

        public void Dispose()
        {
            Log.Write("close");
            try { Close(); } catch (Exception e) { Log.Write("close failed: " + e.Message); }
            com.Dispose();
            if (timerRaised) { timeEndPeriod(1); timerRaised = false; }
        }
    }
}
