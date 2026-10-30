using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace Sonix290
{
    public sealed class Frame
    {
        public long Index;
        public byte[] Data;
        /// <summary>Host arrival time (Stopwatch ticks) of this frame and of the one before it (0 = none).</summary>
        public long Ticks, PrevTicks;
        /// <summary>DirectShow sample time in seconds (stream time) of this frame and the one before it.</summary>
        public double SampleTime, PrevSampleTime;

        public bool HasPrev => PrevTicks != 0;
        public double HostDt => (Ticks - PrevTicks) / (double)Stopwatch.Frequency;
        /// <summary>DirectShow sample times are synthetic on this camera (33.3 ms for periods up to ~50 ms); diagnostics only.</summary>
        public bool SampleDtValid => HasPrev && SampleTime > 0 && PrevSampleTime > 0 && SampleTime > PrevSampleTime;
    }

    /// <summary>Receives frames from the sample grabber; keeps the last few and lets readers wait for new ones.</summary>
    public sealed class FrameBuffer : ISampleGrabberCB
    {
        const int Depth = 8;
        readonly Frame[] ring = new Frame[Depth];
        readonly object gate = new object();
        long count, lastTicks;
        double lastSampleTime;

        public long Count { get { lock (gate) return count; } }
        public long LastTicks { get { lock (gate) return lastTicks; } }
        /// <summary>Host interval of the last frame in seconds (0 if unknown).</summary>
        public double LastHostDt
        {
            get
            {
                lock (gate)
                {
                    var f = count > 0 ? ring[(count - 1) % Depth] : null;
                    return f != null && f.HasPrev ? f.HostDt : 0;
                }
            }
        }

        /// <summary>Forget the previous frame, so the next one has no interval (after a stream restart).</summary>
        public void BreakChain()
        {
            lock (gate) { lastTicks = 0; lastSampleTime = 0; }
        }

        int ISampleGrabberCB.SampleCB(double sampleTime, IntPtr sample) => 0;

        int ISampleGrabberCB.BufferCB(double sampleTime, IntPtr buffer, int length)
        {
            long now = Stopwatch.GetTimestamp();
            var data = new byte[length];
            Marshal.Copy(buffer, data, 0, length);
            lock (gate)
            {
                var f = new Frame
                {
                    Index = count, Data = data, Ticks = now, PrevTicks = lastTicks,
                    SampleTime = sampleTime, PrevSampleTime = lastSampleTime,
                };
                ring[count % Depth] = f;
                count++;
                lastTicks = now;
                lastSampleTime = sampleTime;
                Monitor.PulseAll(gate);
            }
            return 0;
        }

        /// <summary>
        /// Returns the oldest buffered frame with Index >= next (the oldest still held if readers fell behind),
        /// or null on timeout.
        /// </summary>
        public Frame WaitFrom(long next, TimeSpan timeout, CancellationToken ct)
        {
            var end = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            lock (gate)
            {
                while (count <= next)
                {
                    ct.ThrowIfCancellationRequested();
                    long left = end - Stopwatch.GetTimestamp();
                    if (left <= 0) return null;
                    Monitor.Wait(gate, (int)Math.Min(200, left * 1000 / Stopwatch.Frequency + 1));
                }
                long first = Math.Max(next, count - Depth);
                return ring[first % Depth];
            }
        }
    }

    public sealed class DeviceEntry
    {
        public string Name, Path;
        public override string ToString() => Name + "  " + Path;
    }

    /// <summary>
    /// A UVC camera in a DirectShow graph: capture pin -> sample grabber -> null renderer, compressed frames
    /// delivered as they arrive. Create and use on a <see cref="ComThread"/> only.
    /// </summary>
    sealed class UvcDevice : IDisposable
    {
        public string Name, Path;
        public int Width, Height;
        public double Fps;
        public Guid SubType;
        public IKsControl KsControl;
        public IKsTopologyInfo Topology;
        public IAMCameraControl CameraControl;
        public IAMVideoProcAmp ProcAmp;

        IGraphBuilder graph;
        IMediaControl control;
        IBaseFilter source, grabFilter, nullFilter;
        public bool Running { get; private set; }

        static IEnumerable<(DeviceEntry entry, IMoniker moniker)> Monikers()
        {
            var devEnum = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid.SystemDeviceEnum));
            var cat = Clsid.VideoInputDeviceCategory;
            if (devEnum.CreateClassEnumerator(ref cat, out var em, 0) != 0 || em == null) yield break;
            var one = new IMoniker[1];
            while (em.Next(1, one, IntPtr.Zero) == 0)
            {
                var m = one[0];
                DeviceEntry e;
                try { e = new DeviceEntry { Name = ReadProp(m, "FriendlyName"), Path = ReadProp(m, "DevicePath") ?? "" }; }
                catch (Exception ex)
                {
                    Log.Write("skipping a video device whose properties cannot be read: " + ex.Message);
                    Ole.Release(m);
                    continue;
                }
                yield return (e, m);
            }
            Ole.Release(em);
            Ole.Release(devEnum);
        }

        static string ReadProp(IMoniker m, string name)
        {
            Ole.CreateBindCtx(0, out var ctx);
            var iid = typeof(IPropertyBag).GUID;
            m.BindToStorage(ctx, null, ref iid, out object o);
            var bag = (IPropertyBag)o;
            object v = null;
            int hr = bag.Read(name, ref v, IntPtr.Zero);
            Ole.Release(bag);
            Ole.Release(ctx);
            return hr == 0 ? v as string : null;
        }

        public static List<DeviceEntry> List()
        {
            var r = new List<DeviceEntry>();
            foreach (var (e, m) in Monikers()) { r.Add(e); Ole.Release(m); }
            return r;
        }

        public static UvcDevice Open(string match, int width, int height, Guid subType, double intervalMs, ISampleGrabberCB sink)
        {
            IMoniker found = null;
            var d = new UvcDevice();
            foreach (var (e, m) in Monikers())
            {
                if (found == null && e.Path.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = m; d.Name = e.Name; d.Path = e.Path;
                }
                else Ole.Release(m);
            }
            if (found == null)
                throw new InvalidOperationException("No video device whose path contains '" + match + "'. Devices: " +
                                                    string.Join("; ", List()));
            try { d.Build(found, width, height, subType, intervalMs, sink); }
            catch { d.Dispose(); throw; }
            finally { Ole.Release(found); }
            return d;
        }

        /// <summary>
        /// Formats of the first matching camera's capture pin (no graph, nothing started); empty when no such camera
        /// is present. Usable on any COM thread, also while another instance streams.
        /// </summary>
        public static List<(Guid sub, int width, int height)> ListModes(string match)
        {
            var r = new List<(Guid, int, int)>();
            IMoniker found = null;
            foreach (var (e, m) in Monikers())
            {
                if (found == null && e.Path.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) found = m;
                else Ole.Release(m);
            }
            if (found == null) return r;
            var d = new UvcDevice();
            try
            {
                Ole.CreateBindCtx(0, out var ctx);
                var iid = typeof(IBaseFilter).GUID;
                found.BindToObject(ctx, null, ref iid, out object src);
                Ole.Release(ctx);
                d.source = (IBaseFilter)src;
                foreach (var (pin, name) in d.OutputPins())
                {
                    bool still = name.IndexOf("still", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!still && pin is IAMStreamConfig sc && sc.GetNumberOfCapabilities(out int n, out int size) == 0)
                    {
                        IntPtr caps = Marshal.AllocCoTaskMem(Math.Max(size, 256));
                        for (int i = 0; i < n; i++)
                            if (sc.GetStreamCaps(i, out IntPtr pmt, caps) == 0)
                            {
                                if (FormatOf(pmt, out var sub, out int w, out int h, out _) && !r.Contains((sub, w, h)))
                                    r.Add((sub, w, h));
                                Ole.FreeMediaType(pmt);
                            }
                        Marshal.FreeCoTaskMem(caps);
                    }
                    Ole.Release(pin);
                }
            }
            finally
            {
                Ole.Release(found);
                d.Dispose();
            }
            return r;
        }

        void Build(IMoniker moniker, int width, int height, Guid subType, double intervalMs, ISampleGrabberCB sink)
        {
            Ole.CreateBindCtx(0, out var ctx);
            var iid = typeof(IBaseFilter).GUID;
            moniker.BindToObject(ctx, null, ref iid, out object src);
            Ole.Release(ctx);
            source = (IBaseFilter)src;
            KsControl = source as IKsControl;
            Topology = source as IKsTopologyInfo;
            CameraControl = source as IAMCameraControl;
            ProcAmp = source as IAMVideoProcAmp;

            graph = (IGraphBuilder)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid.FilterGraph));
            control = (IMediaControl)graph;
            Check(graph.AddFilter(source, "camera"), "AddFilter camera");

            var pin = FindCapturePin(width, height, subType, out IntPtr pmt);
            IPin grabIn = null, grabOut = null, nullIn = null;
            try
            {
                if (intervalMs > 0)   // VIDEOINFOHEADER(2).AvgTimePerFrame, 100 ns units
                    Marshal.WriteInt64(Marshal.PtrToStructure<AMMediaType>(pmt).Format, 40, (long)Math.Round(intervalMs * 1e4));
                Check(((IAMStreamConfig)pin).SetFormat(pmt), "SetFormat");
                ReadFormat(pmt);

                Type grabType = Type.GetTypeFromCLSID(Clsid.SampleGrabber);
                try { grabFilter = (IBaseFilter)Activator.CreateInstance(grabType); }
                catch (Exception e)
                {
                    throw new InvalidOperationException("DirectShow Sample Grabber (qedit.dll) not available: " + e.Message);
                }
                var grabber = (ISampleGrabber)grabFilter;
                IntPtr want = AllocMediaType(MediaGuids.Video, subType);
                try { Check(grabber.SetMediaType(want), "SampleGrabber.SetMediaType"); }
                finally { Marshal.FreeCoTaskMem(want); }
                Check(grabber.SetOneShot(0), "SetOneShot");
                Check(grabber.SetBufferSamples(0), "SetBufferSamples");
                Check(grabber.SetCallback(sink, 1), "SetCallback");
                Check(graph.AddFilter(grabFilter, "grabber"), "AddFilter grabber");

                nullFilter = (IBaseFilter)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid.NullRenderer));
                Check(graph.AddFilter(nullFilter, "null"), "AddFilter null");

                grabIn = Pin(grabFilter, 0);
                grabOut = Pin(grabFilter, 1);
                nullIn = Pin(nullFilter, 0);
                Check(graph.ConnectDirect(pin, grabIn, pmt), "connect camera->grabber");
                Check(graph.ConnectDirect(grabOut, nullIn, IntPtr.Zero), "connect grabber->null");
                // No reference clock: usbvideo stamps samples at +listed interval, and with a clock the null renderer
                // waits for each stamp, blocking the capture thread. A mode whose listed interval is longer than the
                // real frame period (1080p YUY2 lists 1 fps on S1) was paced to 1 s and lost frames (2026-10-30).
                int hr = ((IMediaFilter)graph).SetSyncSource(IntPtr.Zero);
                Log.Write($"graph sync source none: hr=0x{hr:X8}");
            }
            finally
            {
                Ole.FreeMediaType(pmt);
                Ole.Release(pin);
                Ole.Release(grabIn);
                Ole.Release(grabOut);
                Ole.Release(nullIn);
            }
        }

        void ReadFormat(IntPtr pmt)
        {
            var mt = Marshal.PtrToStructure<AMMediaType>(pmt);
            SubType = mt.SubType;
            int bmi = mt.FormatType == MediaGuids.FormatVideoInfo2 ? 72 : 48;
            Width = Marshal.ReadInt32(mt.Format, bmi + 4);
            Height = Math.Abs(Marshal.ReadInt32(mt.Format, bmi + 8));
            long avg = Marshal.ReadInt64(mt.Format, 40);
            Fps = avg > 0 ? 1e7 / avg : 0;
        }

        static bool FormatOf(IntPtr pmt, out Guid sub, out int w, out int h, out double fps)
        {
            var mt = Marshal.PtrToStructure<AMMediaType>(pmt);
            sub = mt.SubType; w = h = 0; fps = 0;
            if (mt.Format == IntPtr.Zero) return false;
            int bmi;
            if (mt.FormatType == MediaGuids.FormatVideoInfo && mt.FormatSize >= 88) bmi = 48;
            else if (mt.FormatType == MediaGuids.FormatVideoInfo2 && mt.FormatSize >= 112) bmi = 72;
            else return false;
            w = Marshal.ReadInt32(mt.Format, bmi + 4);
            h = Math.Abs(Marshal.ReadInt32(mt.Format, bmi + 8));
            long avg = Marshal.ReadInt64(mt.Format, 40);
            fps = avg > 0 ? 1e7 / avg : 0;
            return true;
        }

        IEnumerable<(IPin pin, string name)> OutputPins()
        {
            Check(source.EnumPins(out var pins), "EnumPins");
            var one = new IPin[1];
            while (pins.Next(1, one, IntPtr.Zero) == 0)
            {
                one[0].QueryDirection(out int dir);
                if (dir == 1) yield return (one[0], PinName(one[0]));
                else Ole.Release(one[0]);
            }
            Ole.Release(pins);
        }

        static string PinName(IPin pin)
        {
            // PIN_INFO { IBaseFilter* pFilter; PIN_DIRECTION dir; WCHAR achName[128]; }
            IntPtr buf = Marshal.AllocCoTaskMem(IntPtr.Size + 4 + 256 + 8);
            try
            {
                if (pin.QueryPinInfo(buf) != 0) return "";
                IntPtr filter = Marshal.ReadIntPtr(buf);
                if (filter != IntPtr.Zero) Marshal.Release(filter);
                return Marshal.PtrToStringUni(buf + IntPtr.Size + 4);
            }
            finally { Marshal.FreeCoTaskMem(buf); }
        }

        /// <summary>All formats of all output pins, for diagnostics.</summary>
        public List<string> DescribeFormats()
        {
            var r = new List<string>();
            foreach (var (pin, name) in OutputPins())
            {
                if (pin is IAMStreamConfig sc && sc.GetNumberOfCapabilities(out int n, out int size) == 0)
                {
                    IntPtr caps = Marshal.AllocCoTaskMem(Math.Max(size, 256));
                    for (int i = 0; i < n; i++)
                        if (sc.GetStreamCaps(i, out IntPtr pmt, caps) == 0)
                        {
                            // VIDEO_STREAM_CONFIG_CAPS.MinFrameInterval / MaxFrameInterval (100 ns) at offsets 104 / 112
                            if (FormatOf(pmt, out var sub, out int w, out int h, out double fps))
                                r.Add($"pin '{name}' #{i}: {MediaGuids.Name(sub)} {w}x{h} {fps:F1} fps" +
                                      (size >= 120 ? $" (intervals {Marshal.ReadInt64(caps, 104) / 1e4:F1}..{Marshal.ReadInt64(caps, 112) / 1e4:F1} ms)" : ""));
                            Ole.FreeMediaType(pmt);
                        }
                    Marshal.FreeCoTaskMem(caps);
                }
                else r.Add($"pin '{name}': no IAMStreamConfig");
            }
            return r;
        }

        IPin FindCapturePin(int width, int height, Guid subType, out IntPtr chosen)
        {
            IPin best = null;
            chosen = IntPtr.Zero;
            foreach (var (pin, name) in OutputPins())
            {
                bool still = name.IndexOf("still", StringComparison.OrdinalIgnoreCase) >= 0;
                IntPtr match = IntPtr.Zero;
                if (!still && pin is IAMStreamConfig sc && sc.GetNumberOfCapabilities(out int n, out int size) == 0)
                {
                    IntPtr caps = Marshal.AllocCoTaskMem(Math.Max(size, 256));
                    for (int i = 0; i < n && match == IntPtr.Zero; i++)
                    {
                        if (sc.GetStreamCaps(i, out IntPtr pmt, caps) != 0) continue;
                        if (FormatOf(pmt, out var sub, out int w, out int h, out _) && sub == subType && w == width && h == height)
                            match = pmt;
                        else
                            Ole.FreeMediaType(pmt);
                    }
                    Marshal.FreeCoTaskMem(caps);
                }
                if (match != IntPtr.Zero && best == null) { best = pin; chosen = match; }
                else { Ole.FreeMediaType(match); Ole.Release(pin); }
            }
            if (best == null)
                throw new InvalidOperationException($"Camera has no {MediaGuids.Name(subType)} {width}x{height} format. Formats: " +
                                                    string.Join("; ", DescribeFormats()));
            return best;
        }

        static IPin Pin(IBaseFilter f, int wantDir)
        {
            Check(f.EnumPins(out var pins), "EnumPins");
            var one = new IPin[1];
            try
            {
                while (pins.Next(1, one, IntPtr.Zero) == 0)
                {
                    one[0].QueryDirection(out int dir);
                    if (dir == wantDir) return one[0];
                    Ole.Release(one[0]);
                }
            }
            finally { Ole.Release(pins); }
            throw new InvalidOperationException("filter pin not found");
        }

        static IntPtr AllocMediaType(Guid major, Guid sub)
        {
            var mt = new AMMediaType { MajorType = major, SubType = sub, FixedSizeSamples = 0 };
            IntPtr p = Marshal.AllocCoTaskMem(Marshal.SizeOf<AMMediaType>());
            Marshal.StructureToPtr(mt, p, false);
            return p;
        }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed, HRESULT 0x" + hr.ToString("X8"), hr);
        }

        public void Start()
        {
            long t0 = Stopwatch.GetTimestamp();
            int hr = control.Run();
            Check(hr, "graph Run");
            long t1 = Stopwatch.GetTimestamp();
            int gs = control.GetState(2000, out int state);
            Running = true;
            Log.Write($"graph Run {Ms(t0, t1):F0} ms (hr 0x{hr:X8}), GetState {Ms(t1, Stopwatch.GetTimestamp()):F0} ms (state {state}, hr 0x{gs:X8})");
        }

        public void Stop()
        {
            if (control == null) return;
            long t0 = Stopwatch.GetTimestamp();
            int hr = control.Stop();
            long t1 = Stopwatch.GetTimestamp();
            int gs = control.GetState(2000, out int state);
            Running = false;
            Log.Write($"graph Stop {Ms(t0, t1):F0} ms (hr 0x{hr:X8}), GetState {Ms(t1, Stopwatch.GetTimestamp()):F0} ms (state {state}, hr 0x{gs:X8})");
        }

        /// <summary>Running -> Paused: data flow stops, but the graph keeps its buffers and pin connection.</summary>
        public void Pause()
        {
            long t0 = Stopwatch.GetTimestamp();
            int hr = control.Pause();
            Check(hr, "graph Pause");
            long t1 = Stopwatch.GetTimestamp();
            int gs = control.GetState(2000, out int state);
            Running = false;
            Log.Write($"graph Pause {Ms(t0, t1):F0} ms (hr 0x{hr:X8}), GetState {Ms(t1, Stopwatch.GetTimestamp()):F0} ms (state {state}, hr 0x{gs:X8})");
        }

        static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

        public void Dispose()
        {
            try { Stop(); } catch { }
            if (grabFilter is ISampleGrabber g) g.SetCallback(null, 1);
            Ole.Release(nullFilter);
            Ole.Release(grabFilter);
            Ole.Release(source);
            Ole.Release(graph);
            nullFilter = grabFilter = source = null;
            graph = null; control = null;
            KsControl = null; Topology = null; CameraControl = null; ProcAmp = null;
        }
    }
}
