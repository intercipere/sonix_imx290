// ASCOM ICameraV3 driver, exposed to COM clients by late binding (IDispatch), which is how PHD2,
// N.I.N.A., SharpCap and ConformU talk to camera drivers. Written from the ASCOM interface specification.
using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SxTest")]

namespace Sonix290
{
    /// <summary>ASCOM error codes travel to COM clients as the exception's HRESULT.</summary>
    public class AscomException : Exception
    {
        public const int NotImplemented = unchecked((int)0x80040400);
        public const int InvalidValue = unchecked((int)0x80040401);
        public const int ValueNotSet = unchecked((int)0x80040402);
        public const int NotConnected = unchecked((int)0x80040407);
        public const int InvalidOperation = unchecked((int)0x8004040B);
        public const int ActionNotImplemented = unchecked((int)0x8004040C);
        public const int DriverError = unchecked((int)0x80040500);

        public AscomException(int hr, string message) : base(message) { HResult = hr; }
    }

    [ComVisible(true)]
    [Guid("e47f8cb6-ed19-4267-aba5-837b7d353c88")]
    [ProgId(ProgIdName)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class Camera : IDisposable
    {
        public const string ProgIdName = "ASCOM.Sonix290.Camera";
        const string FriendlyName = "Sonix IMX290";

        // ASCOM CameraStates
        const int StateIdle = 0, StateExposing = 2, StateReading = 3, StateError = 5;

        Settings settings = Settings.Load();
        CameraEngine engine;
        readonly object gate = new object();

        int binX = 1, binY = 1, startX, startY, numX = -1, numY = -1;
        int state = StateIdle;
        CancellationTokenSource cancel;
        Task exposure;
        double requested, lastDuration = double.NaN;
        DateTime exposureStartUtc, lastStartUtc;
        double expectedSeconds;
        bool imageReady;
        Picture image;
        int imgStartX, imgStartY, imgNumX, imgNumY, imgBin;
        Array imageArray;
        string error;

        public Camera()
        {
            Log.Enabled = settings.Trace;
            if (settings.Trace) Log.Open("driver");
        }

        static AscomException NotImpl(string what) => new AscomException(AscomException.NotImplemented, what + " is not implemented by this driver");
        static AscomException Invalid(string what) => new AscomException(AscomException.InvalidValue, what);
        static AscomException InvalidOp(string what) => new AscomException(AscomException.InvalidOperation, what);

        void RequireConnected(string member)
        {
            if (engine == null) throw new AscomException(AscomException.NotConnected, member + ": camera not connected");
        }

        // ---------------- common members ----------------

        public bool Connected
        {
            get => engine != null;
            set
            {
                if (!value)
                {
                    if (engine == null) return;
                    AbortRunning();   // waits for the exposure task, which needs the gate: not under it
                    lock (gate)
                    {
                        if (engine == null) return;
                        engine = null;
                        CameraEngine.ReleaseShared();
                    }
                    return;
                }
                lock (gate)
                {
                    if (engine != null) return;
                    {
                        settings = Settings.Load();
                        Log.Enabled = settings.Trace;
                        try { engine = CameraEngine.Acquire(settings); }
                        catch (Exception e)
                        {
                            Log.Write("connect failed: " + e);
                            throw new AscomException(AscomException.DriverError, "Connect failed: " + e.Message);
                        }
                        if (numX < 0) { numX = engine.Width; numY = engine.Height; }
                        state = StateIdle;
                    }
                }
            }
        }

        public string Description => engine != null && engine.SlowPort
            ? FriendlyName + " (slow USB port: image may be grainier, see README)"
            : FriendlyName;
        public string DriverInfo => "Sonix SN9C292B + IMX290/291 UVC camera; long exposures by writing the sensor frame length over the UVC extension unit. Version " + DriverVersion;
        public string DriverVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor;
            }
        }
        public short InterfaceVersion => 3;
        public string Name => "Sonix IMX290";
        public ArrayList SupportedActions => new ArrayList();

        public string Action(string actionName, string actionParameters) =>
            throw new AscomException(AscomException.ActionNotImplemented, "Action " + actionName + " is not implemented");
        public void CommandBlind(string command, bool raw) => throw NotImpl("CommandBlind");
        public bool CommandBool(string command, bool raw) => throw NotImpl("CommandBool");
        public string CommandString(string command, bool raw) => throw NotImpl("CommandString");

        public void SetupDialog()
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) { ShowSetup(); return; }
            var t = new Thread(ShowSetup);
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
        }

        void ShowSetup()
        {
            using (var f = new SetupForm(Settings.Load(), engine != null))
                if (f.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    f.Result.Save();
                    if (engine == null) settings = f.Result;
                }
        }

        public void Dispose()
        {
            try { Connected = false; } catch { }
        }

        // ---------------- camera geometry and capabilities ----------------

        public int CameraXSize { get { RequireConnected("CameraXSize"); return engine.Width; } }
        public int CameraYSize { get { RequireConnected("CameraYSize"); return engine.Height; } }
        public double PixelSizeX => 2.9;
        public double PixelSizeY => 2.9;
        public string SensorName => "IMX290";
        public int SensorType => settings.Color ? 1 : 0;   // Monochrome / Color (RGB planes)
        public short BayerOffsetX => throw NotImpl("BayerOffsetX");
        public short BayerOffsetY => throw NotImpl("BayerOffsetY");
        public int MaxADU => 255;
        public double ElectronsPerADU => throw NotImpl("ElectronsPerADU");
        public double FullWellCapacity => throw NotImpl("FullWellCapacity");
        public bool HasShutter => false;

        public short MaxBinX => 2;
        public short MaxBinY => 2;
        public bool CanAsymmetricBin => false;

        public short BinX
        {
            get => (short)binX;
            set
            {
                if (value < 1 || value > MaxBinX) throw Invalid("BinX must be 1 or 2");
                binX = value;
            }
        }

        public short BinY
        {
            get => (short)binY;
            set
            {
                if (value < 1 || value > MaxBinY) throw Invalid("BinY must be 1 or 2");
                binY = value;
            }
        }

        public int StartX { get => startX; set => startX = value; }
        public int StartY { get => startY; set => startY = value; }
        public int NumX { get { RequireConnected("NumX"); return numX; } set => numX = value; }
        public int NumY { get { RequireConnected("NumY"); return numY; } set => numY = value; }

        public bool CanAbortExposure => true;
        public bool CanStopExposure => false;
        public bool CanPulseGuide => false;
        public bool CanGetCoolerPower => false;
        public bool CanSetCCDTemperature => false;
        public bool CanFastReadout => false;
        public bool FastReadout { get => throw NotImpl("FastReadout"); set => throw NotImpl("FastReadout"); }

        public double CCDTemperature => throw NotImpl("CCDTemperature");
        public double HeatSinkTemperature => throw NotImpl("HeatSinkTemperature");
        public double SetCCDTemperature { get => throw NotImpl("SetCCDTemperature"); set => throw NotImpl("SetCCDTemperature"); }
        public bool CoolerOn { get => throw NotImpl("CoolerOn"); set => throw NotImpl("CoolerOn"); }
        public double CoolerPower => throw NotImpl("CoolerPower");

        public void PulseGuide(short direction, int duration) => throw NotImpl("PulseGuide");
        public bool IsPulseGuiding => throw NotImpl("IsPulseGuiding");

        public double ExposureMin => 0.0001;
        public double ExposureMax => Math.Floor(SensorTiming.MaxExposure(settings.LineTimeUs * 1e-6) * 100) / 100;
        public double ExposureResolution => 0.0;

        public short ReadoutMode
        {
            get => 0;
            set { if (value != 0) throw Invalid("ReadoutMode must be 0"); }
        }
        public ArrayList ReadoutModes => new ArrayList { "MJPEG 1920x1080 (8-bit, camera-processed)" };

        public double SubExposureDuration { get => throw NotImpl("SubExposureDuration"); set => throw NotImpl("SubExposureDuration"); }

        // Gain: the UVC gain control (numeric mode; Gains is not implemented).
        public short Gain
        {
            get { RequireConnected("Gain"); RequireGain(); return (short)engine.Gain; }
            set
            {
                RequireConnected("Gain"); RequireGain();
                if (value < engine.GainMin || value > engine.GainMax) throw Invalid($"Gain must be {engine.GainMin}..{engine.GainMax}");
                if (state == StateExposing) throw InvalidOp("cannot change gain during an exposure");
                engine.Gain = value;
            }
        }
        public short GainMin { get { RequireConnected("GainMin"); RequireGain(); return (short)engine.GainMin; } }
        public short GainMax { get { RequireConnected("GainMax"); RequireGain(); return (short)engine.GainMax; } }
        public ArrayList Gains => throw NotImpl("Gains");

        void RequireGain()
        {
            if (!engine.HasGain) throw NotImpl("Gain");
        }

        public int Offset { get => throw NotImpl("Offset"); set => throw NotImpl("Offset"); }
        public int OffsetMin => throw NotImpl("OffsetMin");
        public int OffsetMax => throw NotImpl("OffsetMax");
        public ArrayList Offsets => throw NotImpl("Offsets");

        // ---------------- exposure ----------------

        /// <summary>ASCOM CameraStates is a 32-bit enum; a short (VT_I2) fails .NET clients' cast (ConformU 4.5.3).</summary>
        public int CameraState
        {
            get { RequireConnected("CameraState"); return state; }
        }

        public void StartExposure(double duration, bool light)
        {
            RequireConnected("StartExposure");
            if (duration < ExposureMin || duration > ExposureMax)
                throw Invalid($"Duration {duration.ToString(CultureInfo.InvariantCulture)} s is outside {ExposureMin}..{ExposureMax} s");
            if (binX != binY) throw Invalid("BinX and BinY must be equal");
            int w = engine.Width / binX, h = engine.Height / binY;
            if (numX < 1 || numY < 1 || numX > w || numY > h) throw Invalid($"NumX/NumY must be 1..{w}/1..{h} at bin {binX}");
            if (startX < 0 || startY < 0 || startX + numX > w || startY + numY > h)
                throw Invalid("StartX+NumX / StartY+NumY exceed the binned sensor size");

            lock (gate)
            {
                if (state == StateExposing || state == StateReading) throw InvalidOp("an exposure is already running");
                imageReady = false;
                imageArray = null;
                image = null;
                error = null;
                requested = duration;
                imgStartX = startX; imgStartY = startY; imgNumX = numX; imgNumY = numY; imgBin = binX;
                exposureStartUtc = DateTime.UtcNow;
                expectedSeconds = Math.Max(duration, 0.033) * 2;
                state = StateExposing;
                cancel = new CancellationTokenSource();
                var ct = cancel.Token;
                var eng = engine;
                bool color = settings.Color;
                Log.Write($"StartExposure {duration} s, light={light}, bin {binX}, roi {startX},{startY} {numX}x{numY}");
                exposure = Task.Run(() => RunExposure(eng, duration, color, ct));
            }
        }

        void RunExposure(CameraEngine eng, double duration, bool color, CancellationToken ct)
        {
            try
            {
                var r = eng.Expose(duration, ct);
                lock (gate) { if (ct.IsCancellationRequested) return; state = StateReading; }
                var pic = eng.Decode(r.Frame, color);
                if (pic.Width != eng.Width || pic.Height != eng.Height)
                    throw new InvalidOperationException($"decoded frame is {pic.Width}x{pic.Height}, expected {eng.Width}x{eng.Height}");
                Log.Write($"exposure done: {r.Timing}, period {r.MeasuredPeriod * 1000:F1} ms, {r.Applies} writes, {r.FramesSkipped} frames skipped, {r.TotalSeconds:F2} s, mean {pic.Mean():F1}");
                lock (gate)
                {
                    if (ct.IsCancellationRequested) return;
                    image = pic;
                    lastStartUtc = r.StartUtc;
                    lastDuration = r.Timing.Exposure(eng.LineSec);
                    imageReady = true;
                    state = StateIdle;
                }
            }
            catch (OperationCanceledException)
            {
                Log.Write("exposure aborted");
            }
            catch (Exception e)
            {
                Log.Write("exposure failed: " + e);
                lock (gate)
                {
                    if (ct.IsCancellationRequested) return;
                    error = e.Message;
                    state = StateError;
                }
            }
        }

        void AbortRunning()
        {
            Task t;
            lock (gate)
            {
                cancel?.Cancel();
                t = exposure;
                if (state != StateError) state = StateIdle;
            }
            try { t?.Wait(10000); } catch { }
            lock (gate) state = StateIdle;
        }

        public void AbortExposure()
        {
            RequireConnected("AbortExposure");
            Log.Write("AbortExposure");
            AbortRunning();
        }

        public void StopExposure() => throw NotImpl("StopExposure");

        public bool ImageReady
        {
            get
            {
                RequireConnected("ImageReady");
                lock (gate)
                {
                    if (state == StateError) throw new AscomException(AscomException.DriverError, "Exposure failed: " + error);
                    return imageReady;
                }
            }
        }

        public short PercentCompleted
        {
            get
            {
                RequireConnected("PercentCompleted");
                lock (gate)
                {
                    if (state == StateIdle) return (short)(imageReady ? 100 : 0);
                    if (state == StateError) throw InvalidOp("no exposure in progress");
                    double done = (DateTime.UtcNow - exposureStartUtc).TotalSeconds / expectedSeconds;
                    return (short)Math.Max(0, Math.Min(99, done * 100));
                }
            }
        }

        public double LastExposureDuration
        {
            get
            {
                RequireConnected("LastExposureDuration");
                if (double.IsNaN(lastDuration)) throw InvalidOp("no exposure has been taken");
                return lastDuration;
            }
        }

        public string LastExposureStartTime
        {
            get
            {
                RequireConnected("LastExposureStartTime");
                if (double.IsNaN(lastDuration)) throw InvalidOp("no exposure has been taken");
                return lastStartUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
            }
        }

        public object ImageArray
        {
            get
            {
                RequireConnected("ImageArray");
                lock (gate)
                {
                    if (!imageReady || image == null) throw InvalidOp("no image available");
                    if (imageArray == null)
                        imageArray = Imaging.ToAscom(image, imgStartX, imgStartY, imgNumX, imgNumY, imgBin);
                    return imageArray;
                }
            }
        }

        public object ImageArrayVariant
        {
            get
            {
                var a = (Array)ImageArray;
                if (a.Rank == 2)
                {
                    var src = (int[,])a;
                    var v = new object[src.GetLength(0), src.GetLength(1)];
                    for (int x = 0; x < src.GetLength(0); x++)
                        for (int y = 0; y < src.GetLength(1); y++) v[x, y] = src[x, y];
                    return v;
                }
                var s3 = (int[,,])a;
                var v3 = new object[s3.GetLength(0), s3.GetLength(1), s3.GetLength(2)];
                for (int x = 0; x < s3.GetLength(0); x++)
                    for (int y = 0; y < s3.GetLength(1); y++)
                        for (int p = 0; p < s3.GetLength(2); p++) v3[x, y, p] = s3[x, y, p];
                return v3;
            }
        }

        // ---------------- COM registration: ASCOM profile entry for the Chooser ----------------

        const string ProfileKey = @"SOFTWARE\ASCOM\Camera Drivers\" + ProgIdName;

        [ComRegisterFunction]
        static void RegisterAscom(Type t)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var k = hklm.CreateSubKey(ProfileKey))
                k.SetValue("", FriendlyName);
        }

        [ComUnregisterFunction]
        static void UnregisterAscom(Type t)
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                hklm.DeleteSubKeyTree(ProfileKey, false);
        }
    }
}
