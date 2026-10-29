// sxtest: measurement tool for the Sonix IMX290 camera on Windows. Uses the driver's own code
// (Sonix290 namespace). No flash access exists anywhere in this code.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Sonix290;

static class Program
{
    static readonly Settings S = new Settings();
    static readonly Dictionary<string, string> Opt = new Dictionary<string, string>();
    static readonly List<string> Args = new List<string>();

    const string Usage = @"sxtest - Sonix IMX290 measurements (output: one line per frame, '#' lines are notes)

  list                         video devices (name, path)
  formats                      formats of the Sonix camera
  controls                     UVC control ranges and values
  probe                        start stream, find XU, read ASIC chip id, check sensor ACK
  stream [--frames N]          stream only, no XU traffic: frame intervals and brightness
  exp MS                       raw test: baseline frames, write VMAX/SHS1 for MS, then log frames
       [--frames N] [--base N] [--repeats R] [--burst] [--vmax V --shs S]
       [--toggle CTRL@K] [--save DIR] [--noluma]
       CTRL: wb blc aep gain uvcexp  (toggled after K frames past the write)
  restart MS [--cycles N] [--pause] [--stagger MS]   set MS, then stop/start (or pause/run) the stream
                               N times (waiting c*stagger ms before restart c); logs first frames after each
  standby MS [--cycles N] [--stagger MS] [--hold MS] [--reg R]   set MS, then toggle STANDBY (0x3000, or R)
                               at staggered points of the frame; logs first frames after each release
  driver MS [--count N] [--save DIR] [--gap MS]   exposures through the driver engine (verification,
                               retries); --gap waits between exposures like a client
  com MS [--count N]           exposures through the registered ASCOM driver (ProgID, late binding)
  asic ADDR [N]                read ASIC registers
  i2cr REG [N]                 read sensor registers (UNRELIABLE, repeat and compare)
  i2cw REG VAL                 write one sensor register

Global: --set Name=Value (driver setting, repeatable via comma: --set A=1,B=2)   --quiet (no trace on stderr)
Settings: " ;

    static int Main(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i].StartsWith("--"))
            {
                string k = argv[i].Substring(2);
                bool flag = i + 1 >= argv.Length || argv[i + 1].StartsWith("--");
                Opt[k] = flag ? "1" : argv[++i];
            }
            else Args.Add(argv[i]);
        }
        if (Args.Count == 0) { Console.WriteLine(Usage + S); return 2; }
        if (Opt.TryGetValue("set", out var sets))
            foreach (var kv in sets.Split(','))
            {
                var p = kv.Split(new[] { '=' }, 2);
                if (p.Length != 2 || !S.TrySet(p[0], p[1])) { Console.WriteLine("bad setting: " + kv); return 2; }
            }
        Log.ToConsole = !Opt.ContainsKey("quiet");
        Log.Enabled = true;
        Log.Open("sxtest");
        Note("sxtest " + typeof(Camera).Assembly.GetName().Version.ToString(2) + ": " + string.Join(" ", argv));
        Note("log file: " + Log.FilePath);
        Note("settings: " + S);
        try
        {
            switch (Args[0])
            {
                case "list": return List();
                case "formats": return WithEngine(e => { e.OnDevice((d, x) => { foreach (var f in d.DescribeFormats()) Note(f); return 0; }); return 0; });
                case "controls": return WithEngine(Controls);
                case "probe": return WithEngine(Probe);
                case "stream": return WithEngine(e => { LogFrames(e, Int("frames", 60), null, null); return 0; });
                case "exp": return WithEngine(Exp);
                case "restart": return WithEngine(Restart);
                case "driver": return WithEngine(Driver);
                case "com": return Com();
                case "asic": return WithEngine(Asic);
                case "i2cr": return WithEngine(I2cRead);
                case "i2cw": return WithEngine(I2cWrite);
                case "standby": return WithEngine(Standby);
                default: Console.WriteLine(Usage + S); return 2;
            }
        }
        catch (Exception e)
        {
            Note("FAILED: " + e);
            return 1;
        }
    }

    static void Note(string s)
    {
        Console.WriteLine("# " + s);
        Log.Write(s);
    }

    static int Int(string k, int def) => Opt.TryGetValue(k, out var v) ? ParseInt(v) : def;
    static int ParseInt(string v) =>
        v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(v.Substring(2), 16) : int.Parse(v, CultureInfo.InvariantCulture);
    static double Ms(string v) => double.Parse(v, CultureInfo.InvariantCulture);

    static int List()
    {
        using (var com = new ComThread("list"))
            foreach (var d in com.Run(() => UvcDevice.List())) Note(d.ToString());
        return 0;
    }

    static int WithEngine(Func<CameraEngine, int> body)
    {
        using (var e = new CameraEngine(S))
        {
            e.Open();
            Note($"opened '{e.DeviceName}' {e.Width}x{e.Height}");
            return body(e);
        }
    }

    // ---------- frame logging ----------

    sealed class FrameStats
    {
        public List<double> Dt = new List<double>();
        public List<double> Mean = new List<double>();
    }

    /// <summary>Logs N frames from the buffer: index, host time, intervals, size, mean luma, MAD to previous.
    /// onFrame(k) runs after the k-th logged frame (for toggles).</summary>
    static FrameStats LogFrames(CameraEngine e, int n, string saveDir, Action<int> onFrame, bool header = true)
    {
        bool luma = !Opt.ContainsKey("noluma");
        if (saveDir != null) Directory.CreateDirectory(saveDir);
        if (header) Console.WriteLine("# idx t_ms dt_sample_ms dt_host_ms bytes meanY madPrev skipped");
        var st = new FrameStats();
        long cursor = e.Frames.Count;
        Picture prev = null;
        long t0 = Stopwatch.GetTimestamp();
        for (int k = 0; k < n; k++)
        {
            var f = e.Frames.WaitFrom(cursor, TimeSpan.FromSeconds(20), CancellationToken.None);
            if (f == null) { Note("no frame for 20 s"); break; }
            long skipped = f.Index - cursor;
            cursor = f.Index + 1;
            double mean = double.NaN, mad = double.NaN;
            if (luma)
            {
                try
                {
                    var p = e.Decode(f, false);
                    mean = p.Mean();
                    mad = p.MeanAbsDiff(prev);
                    prev = p;
                }
                catch (Exception ex) { Note("decode failed: " + ex.Message); }
            }
            double t = (f.Ticks - t0) * 1000.0 / Stopwatch.Frequency;
            double dt = f.SampleDtValid ? (f.SampleTime - f.PrevSampleTime) * 1000 : double.NaN, dth = f.HasPrev ? f.HostDt * 1000 : double.NaN;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} {1:F1} {2:F1} {3:F1} {4} {5:F2} {6:F3} {7}",
                f.Index, t, dt, dth, f.Data.Length, mean, mad, skipped));
            if (f.HasPrev) st.Dt.Add(f.HostDt);
            if (!double.IsNaN(mean)) st.Mean.Add(mean);
            if (saveDir != null) File.WriteAllBytes(Path.Combine(saveDir, $"f{f.Index:D5}.jpg"), f.Data);
            onFrame?.Invoke(k);
        }
        return st;
    }

    static string Summary(FrameStats s, int skipFirst)
    {
        var d = s.Dt.Skip(skipFirst).ToList();
        if (d.Count == 0) return "no intervals";
        d.Sort();
        double med = d[d.Count / 2];
        var m = s.Mean.Skip(skipFirst).ToList();
        return string.Format(CultureInfo.InvariantCulture, "host intervals n={0} mean {5:F1} ms median {1:F1} min {2:F1} max {3:F1}; mean luma {4:F1}",
            d.Count, med * 1000, d[0] * 1000, d[d.Count - 1] * 1000, m.Count > 0 ? m.Average() : double.NaN, d.Average() * 1000);
    }

    // ---------- commands ----------

    static int Controls(CameraEngine e)
    {
        e.OnDevice((d, x) =>
        {
            string[] cc = { "pan", "tilt", "roll", "zoom", "exposure", "iris", "focus" };
            if (d.CameraControl != null)
                for (int i = 0; i < cc.Length; i++)
                    if (d.CameraControl.GetRange(i, out int min, out int max, out int step, out int def, out int caps) == 0)
                    {
                        d.CameraControl.Get(i, out int v, out int fl);
                        Note($"camera {cc[i]}: {min}..{max} step {step} default {def} caps {caps}; now {v} flags {fl}");
                    }
            string[] pa = { "brightness", "contrast", "hue", "saturation", "sharpness", "gamma", "colorenable", "whitebalance", "backlight", "gain" };
            if (d.ProcAmp != null)
                for (int i = 0; i < pa.Length; i++)
                    if (d.ProcAmp.GetRange(i, out int min, out int max, out int step, out int def, out int caps) == 0)
                    {
                        d.ProcAmp.Get(i, out int v, out int fl);
                        Note($"procamp {pa[i]}: {min}..{max} step {step} default {def} caps {caps}; now {v} flags {fl}");
                    }
            return 0;
        });
        return 0;
    }

    static int Probe(CameraEngine e)
    {
        e.OnDevice((d, x) =>
        {
            Note($"XU node {x.Node}");
            for (int i = 0; i < 3; i++) Note($"ASIC 0x101F = 0x{x.AsicRead(0x101F):X2}");
            foreach (int s in new[] { 0x1A, 0x34, 0x36, 0x1B })
                Note($"sensor slave 0x{s:X2}: {(x.Probe(s) ? "ACK" : "no answer")}");
            Note($"XU transfers so far: {x.Transfers}");
            return 0;
        });
        return 0;
    }

    static SensorTiming TimingFromArgs(CameraEngine e, double ms)
    {
        if (Opt.ContainsKey("vmax"))
            return new SensorTiming { Vmax = Int("vmax", 1125), Shs1 = Int("shs", SensorTiming.LongShs1) };
        return SensorTiming.For(ms / 1000, e.LineSec, S.BaseVmax);
    }

    static int Exp(CameraEngine e)
    {
        double ms = Ms(Args[1]);
        var t = TimingFromArgs(e, ms);
        int baseN = Int("base", 30), n = Int("frames", Math.Max(8, (int)(15000 / Math.Max(ms, 33))));
        Note($"target {ms} ms -> {t}, expected period {t.Period(e.LineSec) * 1000:F2} ms, exposure {t.Exposure(e.LineSec) * 1000:F2} ms");
        Note($"baseline {baseN} frames");
        var b = LogFrames(e, baseN, null, null);
        Note("baseline: " + Summary(b, 2));
        var sw = Stopwatch.StartNew();
        e.WriteTiming(t, Int("repeats", S.WriteRepeats), Opt.ContainsKey("burst"));
        Note($"write took {sw.ElapsedMilliseconds} ms");
        string toggle = null; int at = -1;
        if (Opt.TryGetValue("toggle", out var tg))
        {
            var p = tg.Split('@');
            toggle = p[0]; at = p.Length > 1 ? int.Parse(p[1]) : 5;
        }
        var a = LogFrames(e, n, Opt.TryGetValue("save", out var dir) ? dir : null, k =>
        {
            if (k == at) Toggle(e, toggle);
        }, false);
        Note("after write: " + Summary(a, 2));
        return 0;
    }

    static void Toggle(CameraEngine e, string what)
    {
        Note("toggle " + what);
        e.OnDevice((d, x) =>
        {
            int v, fl, hr;
            switch (what)
            {
                case "wb":
                    d.ProcAmp.Get(7, out v, out fl);
                    hr = d.ProcAmp.Set(7, v, fl == 1 ? 2 : 1);
                    Note($"white balance flags {fl} -> {(fl == 1 ? 2 : 1)}: hr=0x{hr:X8}");
                    break;
                case "blc":
                    d.ProcAmp.Get(8, out v, out fl);
                    hr = d.ProcAmp.Set(8, v == 0 ? 1 : 0, 2);
                    Note($"backlight {v} -> {(v == 0 ? 1 : 0)}: hr=0x{hr:X8}");
                    break;
                case "aep":
                    hr = e.SetKsControl(19, 1);
                    Note($"auto exposure priority -> 1: hr=0x{hr:X8}");
                    break;
                case "gain":
                    d.ProcAmp.GetRange(9, out int min, out int max, out _, out _, out _);
                    d.ProcAmp.Get(9, out v, out fl);
                    int nv = Math.Min(max, v + Math.Max(1, (max - min) / 10));
                    hr = d.ProcAmp.Set(9, nv, 2);
                    Note($"gain {v} -> {nv}: hr=0x{hr:X8}");
                    break;
                case "uvcexp":
                    d.CameraControl.Get(4, out v, out fl);
                    hr = d.CameraControl.Set(4, v + 1, 2);
                    Note($"UVC exposure {v} -> {v + 1}: hr=0x{hr:X8}");
                    break;
                default:
                    Note("unknown toggle " + what);
                    break;
            }
            return 0;
        });
    }

    static int Restart(CameraEngine e)
    {
        double ms = Ms(Args[1]);
        var t = TimingFromArgs(e, ms);
        Note($"target {ms} ms -> {t}");
        e.WriteTiming(t);
        var s = LogFrames(e, 4, null, null);
        Note("before restarts: " + Summary(s, 1));
        int cycles = Int("cycles", 3);
        bool pause = Opt.ContainsKey("pause");
        // Restarting at different points of the running frame shows whether Run restarts the sensor's frame
        // (first frame a fixed time after Run) or the sensor free-runs (that time shrinks by the delay).
        int stagger = Int("stagger", 0);
        for (int c = 0; c < cycles; c++)
        {
            if (stagger > 0)
            {
                Note($"restart {c}: waiting {c * stagger} ms first");
                Thread.Sleep(c * stagger);
            }
            long t0 = Stopwatch.GetTimestamp();
            e.RestartStream(pause);
            double runMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Note($"restart {c}: {(pause ? "pause" : "stop")}+run took {runMs:F0} ms; first frames (t_ms is from after Run returned)");
            LogFrames(e, 4, null, null);
        }
        return 0;
    }

    /// <summary>
    /// Does a sensor-level stop (STANDBY 0x3000, or XMSTA 0x3002) restart the frame cycle? Per cycle: sync to a frame,
    /// wait c*stagger ms, write REG=1, hold, write REG=0, then log frames with t_ms from the release. A restart shows
    /// as a first-frame time that does not depend on the stagger; a free-running sensor shifts it by the stagger.
    /// </summary>
    static int Standby(CameraEngine e)
    {
        double ms = Ms(Args[1]);
        var t = TimingFromArgs(e, ms);
        int reg = Opt.TryGetValue("reg", out var rs) ? ParseInt(rs) : 0x3000;
        int hold = Int("hold", 0), stagger = Int("stagger", 250), cycles = Int("cycles", 4);
        Note($"target {ms} ms -> {t}; register 0x{reg:X4}, hold {hold} ms, stagger {stagger} ms");
        e.WriteTiming(t);
        var s = LogFrames(e, 3, null, null);
        Note("before: " + Summary(s, 1));
        for (int c = 0; c < cycles; c++)
        {
            e.Frames.WaitFrom(e.Frames.Count, TimeSpan.FromSeconds(20), CancellationToken.None);
            Thread.Sleep(c * stagger);
            long t0 = Stopwatch.GetTimestamp();
            bool on = false, off = false;
            long t1 = 0;
            e.OnDevice((d, x) =>
            {
                on = x.SensorWrite(S.SensorSlave, reg, 1);
                t1 = Stopwatch.GetTimestamp();
                if (hold > 0) Thread.Sleep(hold);
                off = x.SensorWrite(S.SensorSlave, reg, 0);
                return 0;
            });
            double setMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            double relMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Note($"cycle {c}: waited {c * stagger} ms after a frame; 0x{reg:X4}=1 took {setMs:F0} ms (status {(on ? "ok" : "fail")}), " +
                 $"released {relMs:F0} ms after the request (status {(off ? "ok" : "fail")}); t_ms is from the release");
            LogFrames(e, 3, null, null);
        }
        return 0;
    }

    static int Driver(CameraEngine e)
    {
        double ms = Ms(Args[1]);
        int count = Int("count", 5);
        string dir = Opt.TryGetValue("save", out var d) ? d : null;
        if (dir != null) Directory.CreateDirectory(dir);
        Console.WriteLine("# n requested_ms expected_ms measured_ms applies skipped total_s meanY");
        // --gap: client time between exposures (PHD2: ~160-450 ms for download and display)
        int gap = Int("gap", 0);
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && gap > 0) Thread.Sleep(gap);
            var r = e.Expose(ms / 1000, CancellationToken.None, p => Note(p));
            var pic = e.Decode(r.Frame, false);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:F1} {3:F1} {4} {5} {6:F2} {7:F2}",
                i, ms, r.ExpectedPeriod * 1000, r.MeasuredPeriod * 1000, r.Applies, r.FramesSkipped, r.TotalSeconds, pic.Mean()));
            if (dir != null) File.WriteAllBytes(Path.Combine(dir, $"exp{i:D3}.jpg"), r.Frame.Data);
        }
        return 0;
    }

    static int Com()
    {
        double sec = Ms(Args[1]) / 1000;
        int count = Int("count", 3);
        var type = Type.GetTypeFromProgID(Camera.ProgIdName);
        if (type == null) { Note("ProgID " + Camera.ProgIdName + " not registered (run install.cmd as administrator)"); return 1; }
        dynamic cam = Activator.CreateInstance(type);
        Note($"created {cam.Name}, {cam.DriverInfo}");
        cam.Connected = true;
        Note($"connected: {cam.CameraXSize}x{cam.CameraYSize}, exposure {cam.ExposureMin}..{cam.ExposureMax} s");
        try
        {
            for (int i = 0; i < count; i++)
            {
                var sw = Stopwatch.StartNew();
                cam.StartExposure(sec, true);
                while (!(bool)cam.ImageReady) Thread.Sleep(50);
                var img = (int[,])cam.ImageArray;
                long sum = 0;
                foreach (int v in img) sum += v;
                Note(string.Format(CultureInfo.InvariantCulture, "exposure {0}: {1:F2} s total, last duration {2:F4} s, start {3}, {4}x{5}, mean {6:F2}",
                    i, sw.Elapsed.TotalSeconds, (double)cam.LastExposureDuration, (string)cam.LastExposureStartTime,
                    img.GetLength(0), img.GetLength(1), sum / (double)img.Length));
            }
        }
        finally { cam.Connected = false; }
        return 0;
    }

    static int Asic(CameraEngine e)
    {
        int a = ParseInt(Args[1]), n = Args.Count > 2 ? ParseInt(Args[2]) : 1;
        e.OnDevice((d, x) => { for (int i = 0; i < n; i++) Note($"ASIC 0x{a + i:X4} = 0x{x.AsicRead(a + i):X2}"); return 0; });
        return 0;
    }

    static int I2cRead(CameraEngine e)
    {
        int r = ParseInt(Args[1]), n = Args.Count > 2 ? ParseInt(Args[2]) : 1;
        e.OnDevice((d, x) =>
        {
            for (int i = 0; i < n; i++) Note($"sensor 0x{r + i:X4} = {x.SensorRead(S.SensorSlave, r + i)} (unreliable)");
            return 0;
        });
        return 0;
    }

    static int I2cWrite(CameraEngine e)
    {
        int r = ParseInt(Args[1]), v = ParseInt(Args[2]);
        e.OnDevice((d, x) => { Note($"write 0x{r:X4} <- 0x{v:X2}: {(x.SensorWrite(S.SensorSlave, r, (byte)v) ? "status ok" : "status fail")}"); return 0; });
        LogFrames(e, 20, null, null);
        return 0;
    }
}
