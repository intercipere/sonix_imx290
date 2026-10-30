// SxFlash (experimental, see README): patches ONE 4 KB sector (0x10000-0x10FFF: parameters, USB
// descriptors, CRC table, mode table) of the SN9C292's SPI flash. The patch is applied to the user's own backup of the
// flash (never a shipped firmware image) and only if that backup is a known firmware; dry run unless --write.
// Raw SPI through the ASIC's flash controller (0x1080 host control, 0x1091 chip select, 0x1082 tx byte,
// 0x1081 = 1 send / 2 receive, 0x1083 rx byte, 0x1084 = 1 ready) with standard SPI NOR commands; the controller
// sequence is the flash controller's register protocol. The stream is never run.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Sonix290;

static class Program
{
    const int Sector = Patch.Sector, SectorLen = Patch.SectorLen, ImageLen = Patch.ImageLen, PageLen = 256;
    static readonly byte[] ExpectedId = { 0x5E, 0x32, 0x13 };   // Zbit 4 Mbit, read from both boards

    static readonly Settings S = new Settings();
    internal static readonly Dictionary<string, string> Opt = new Dictionary<string, string>();
    internal static readonly List<string> Args = new List<string>();

    static readonly string Usage = @"sxflash - patch the camera's SPI flash (one 4 KB sector) for the " + Patch.PatchTitle + @"

  probe                          camera, read-only: opens without running the stream, reads 16 bytes of the sector,
                                 logs the flash controller (0x1080/0x1091) for 3 s
  backup FILE                    camera, read-only: reads the whole 128 KB twice (~90 s), compares, saves FILE (never
                                 overwrites), says whether it is a known firmware. Do this first; keep the file.
  info FILE                      offline: is the backup a known original, a known patch version, or unsupported;
                                 lists the bytes the patch changes
  patch FILE [--write]           camera: FILE is a backup of this camera in any known state (original or an older
                                 patch). The camera must hold a known state; it gets the newest patch version in one
                                 write. Without --write: all checks and a raw read, nothing written. With --write:
                                 erase + program the sector, read back, verify the whole 128 KB.
  restore FILE [--write] [--current any]
                                 camera: as patch, but the camera gets the original firmware back (rebuilt from FILE in
                                 any known state). --current any: recovery after a failed write only (the sector's
                                 present content is not checked, the rest of the flash still is).
  verify FILE                    camera, read-only: reads the whole 128 KB and compares it with FILE
Options: --settle MS (default 1500)  --quiet  --set Name=Value[,..] (recovery only: DeviceMatch=vid_0c45&pid_6362 for the ROM fallback ID)
After a write: replug the camera. If a write fails: do NOT replug, keep it powered, run restore with --current any.";

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] argv)
    {
        if (argv.Length == 0)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
        AttachConsole(-1);   // the parent's console (cmd, PowerShell); harmless without one
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
        if (Args.Count < 1 || (Args[0] != "probe" && Args.Count < 2)) { Console.WriteLine(Usage); return 2; }
        if (Opt.TryGetValue("set", out var sets))
            foreach (var kv in sets.Split(','))
            {
                var p = kv.Split(new[] { '=' }, 2);
                if (p.Length != 2 || !S.TrySet(p[0], p[1])) { Console.WriteLine("bad setting: " + kv); return 2; }
            }
        Log.ToConsole = !Opt.ContainsKey("quiet");
        Log.Enabled = true;
        Log.Open("sxflash");
        Note("sxflash: " + string.Join(" ", argv));
        Note("log file: " + Log.FilePath);
        int rc = Execute(Args[0]);
        if (rc == 2 && !new[] { "info", "probe", "backup", "verify", "patch", "restore" }.Contains(Args[0])) Console.WriteLine(Usage);
        return rc;
    }

    /// <summary>One command with Args/Opt already set (shared by the command line and the window).</summary>
    internal static int Execute(string cmd)
    {
        try
        {
            switch (cmd)
            {
                case "info": return Info() ? 0 : 1;
                case "probe": return WithDevice(Probe);
                case "backup": return WithDevice(Backup);
                case "verify": return WithDevice(Verify);
                case "patch": return WithDevice((d, x) => WriteSector(d, x, restore: false));
                case "restore": return WithDevice((d, x) => WriteSector(d, x, restore: true));
            }
            return 2;
        }
        catch (OperationCanceledException)
        {
            Note("cancelled; nothing was written");
            return 3;
        }
        catch (Exception e)
        {
            Note("FAILED: " + e);
            return 1;
        }
    }

    // hooks for the window: log lines, read progress, and cancelling a read (only before anything is written)
    internal static Action<string> NoteHook;
    internal static Action<int, int> ProgressHook;
    internal static volatile bool CancelRequested;
    internal static volatile bool CanCancel;
    /// <summary>The window: the camera's flash as read by a backup in this session, the camera never unplugged since
    /// (the firmware does not write its own flash), so the write needs no second 128 KB read. Null = read it.</summary>
    internal static byte[] KnownFlash;
    /// <summary>The window: asked after all checks passed, before anything is written, with what will happen
    /// ("update the patch from v1 to v2"); false = nothing is written.</summary>
    internal static Func<string, bool> ConfirmHook;
    /// <summary>Set when the erase is about to start: from here a failure may leave the sector changed.</summary>
    internal static volatile bool WriteStarted;

    static void Note(string s)
    {
        try { Console.WriteLine("# " + s); } catch { }
        Log.Write(s);
        NoteHook?.Invoke(s);
    }

    /// <summary>
    /// Opens the camera WITHOUT running the stream (graph built, never Run): the firmware re-reads the descriptor
    /// sector at stream start and reads the flash right after a stream stop, both bad during or after a write.
    /// The body runs on the device thread.
    /// </summary>
    static int WithDevice(Func<UvcDevice, SonixXu, int> body)
    {
        using (var com = new ComThread("sxflash"))
        {
            UvcDevice dev = null;
            SonixXu x = null;
            try
            {
                return com.Run(() =>
                {
                    var cams = UvcDevice.List().Where(e => e.Path.IndexOf("vid_0c45", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (cams.Count != 1 && !Opt.ContainsKey("set"))
                    {
                        Note(cams.Count == 0 ? "REFUSED: no Sonix camera (USB 0C45) found" : $"REFUSED: {cams.Count} Sonix cameras connected; unplug all but the one to flash");
                        return 1;
                    }
                    var sub = S.Format.Equals("YUY2", StringComparison.OrdinalIgnoreCase) ? MediaGuids.Yuy2 : MediaGuids.Mjpg;
                    dev = UvcDevice.Open(S.DeviceMatch, S.Width, S.Height, sub, S.FrameIntervalMs, new FrameBuffer());
                    x = SonixXu.Find(dev.KsControl, dev.Topology);
                    Note($"opened '{dev.Name}' (stream never run), chip id 0x{x.AsicRead(0x101F):X2}");
                    return body(dev, x);
                });
            }
            finally
            {
                com.Run(() => { x?.Dispose(); dev?.Dispose(); });
            }
        }
    }

    /// <summary>
    /// Running programs that Windows reports as using a camera now (the privacy indicator's record: HKCU
    /// ...\CapabilityAccessManager\ConsentStore\webcam, LastUsedTimeStop = 0), this tool excluded. Another program
    /// starting or stopping the stream makes the firmware read the flash and take the controller (2026-10-30: a test
    /// program did that during a check; the check stopped and nothing was written).
    /// </summary>
    static List<string> OtherCameraUsers()
    {
        var r = new List<string>();
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam"))
            {
                if (root == null) return r;
                string self = Process.GetCurrentProcess().MainModule.FileName;
                bool InUse(RegistryKey k) => k.GetValue("LastUsedTimeStart") is long start && start != 0 && k.GetValue("LastUsedTimeStop") is long stop && stop == 0;
                using (var np = root.OpenSubKey("NonPackaged"))
                    if (np != null)
                        foreach (var name in np.GetSubKeyNames())
                            using (var k = np.OpenSubKey(name))
                            {
                                if (k == null || !InUse(k)) continue;
                                string path = name.Replace('#', '\\');
                                if (string.Equals(path, self, StringComparison.OrdinalIgnoreCase)) continue;
                                // a record left by a program that is gone (crash) does not count
                                if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)).Length == 0) continue;
                                r.Add(Path.GetFileName(path));
                            }
                foreach (var name in root.GetSubKeyNames())
                    if (name != "NonPackaged")
                        using (var k = root.OpenSubKey(name))
                            if (k != null && InUse(k)) r.Add(name.Split('_')[0]);   // a Store app (package name)
            }
        }
        catch (Exception e) { Note("could not read Windows' camera usage record: " + e.Message); }
        return r;
    }

    /// <summary>False (and a note) when another program uses a camera.</summary>
    static bool NoOtherCameraUsers(string when)
    {
        var users = OtherCameraUsers();
        if (users.Count == 0) return true;
        Note($"REFUSED {when}: other programs are using a camera now: {string.Join(", ", users.Distinct())}. Close them and " +
             "try again (a program that starts or stops the camera's stream makes its firmware use the flash). Nothing was written.");
        return false;
    }

    static string Sha(byte[] b, int off, int len)
    {
        using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b, off, len)).Replace("-", "").Substring(0, 16);
    }

    // ---------- offline checks ----------

    static byte[] ReadBackup()
    {
        var b = File.ReadAllBytes(Args[1]);
        if (b.Length != ImageLen) { Note($"{Args[1]} must be {ImageLen} bytes (a backup made by 'backup')"); return null; }
        return b;
    }

    /// <summary>Offline: what the backup is, and (for a known firmware) the checked target image.</summary>
    static bool Info() => Plan(ReadBackup(), restore: false, out _, out _, out _);

    /// <summary>Patch.StateName, or "unknown" for -1.</summary>
    static string StateName(int v) => v < 0 ? "unknown content" : Patch.StateName(v);

    /// <summary>
    /// backup = the user's backup in any known state of a known firmware; original = rebuilt from it (hash checked);
    /// image = the target (restore: the original; patch: the newest version); target = its version (0 = original).
    /// What the camera holds is checked later against every known state.
    /// </summary>
    static bool Plan(byte[] backup, bool restore, out byte[] original, out byte[] image, out Patch.Firmware fw)
    {
        original = image = null;
        fw = null;
        if (backup == null) return false;
        var state = Patch.Identify(backup, out fw, out int version);
        Note($"backup {Path.GetFileName(Args[1])}: sha256 {Patch.Sha(backup)}");
        if (state == Patch.State.Unknown)
        {
            Note("UNSUPPORTED: this flash content is not a firmware this patch is known for. Nothing will be written.");
            Note("Send the backup file and the log to the project (the list of known firmwares grows with each report).");
            return false;
        }
        Note($"known firmware: {fw.Name}; the backup holds {StateName(version)}; this tool: patch v{Patch.Version}");
        if (!Patch.CrcsValid(backup)) { Note("REFUSED: the CRC table of the backup is not valid"); return false; }
        int target = restore ? 0 : Patch.Version;
        try
        {
            original = version == 0 ? backup : Patch.Revert(backup, fw, version);
            image = target == 0 ? original : Patch.Apply(original, fw, target);
        }
        catch (InvalidOperationException e) { Note("REFUSED: " + e.Message); return false; }
        // the bytes that change from the backup's state to the target (CRC bytes included)
        byte[] from = backup, to = image;
        var diff = Enumerable.Range(0, ImageLen).Where(i => from[i] != to[i]).ToList();
        if (diff.Any(i => i < Sector || i >= Sector + SectorLen)) { Note("REFUSED: changed bytes outside the sector"); return false; }
        if (!Patch.CrcsValid(image)) { Note("REFUSED: CRC table of the target image is not valid"); return false; }
        Note($"from the backup's state ({StateName(version)}) to the target ({StateName(target)}): {diff.Count} bytes, all inside " +
             $"0x{Sector:X5}-0x{Sector + SectorLen - 1:X5}, all ten CRCs valid");
        foreach (var r in Runs(diff))
            Note($"  0x{r.Item1:X5}: {Hex(from, r.Item1, r.Item2)} -> {Hex(to, r.Item1, r.Item2)}");
        Note($"  sector sha256 {Sha(from, Sector, SectorLen)} -> {Sha(to, Sector, SectorLen)}");
        return true;
    }

    static IEnumerable<Tuple<int, int>> Runs(List<int> idx)
    {
        for (int i = 0; i < idx.Count;)
        {
            int j = i;
            while (j + 1 < idx.Count && idx[j + 1] - idx[j] <= 4) j++;
            yield return Tuple.Create(idx[i], idx[j]);
            i = j + 1;
        }
    }

    static string Hex(byte[] b, int from, int to) => string.Join(" ", Enumerable.Range(from, to - from + 1).Select(i => b[i].ToString("X2")));

    // ---------- XU access ----------

    static void XuTransfer(UvcDevice d, SonixXu x, int selector, byte[] buf, bool set)
    {
        IntPtr prop = Marshal.AllocCoTaskMem(32), data = Marshal.AllocCoTaskMem(buf.Length);
        try
        {
            Marshal.Copy(SonixXu.SystemXu.ToByteArray(), 0, prop, 16);
            Marshal.WriteInt32(prop, 16, selector);
            Marshal.WriteInt32(prop, 20, (set ? Ks.TypeSet : Ks.TypeGet) | Ks.TypeTopology);
            Marshal.WriteInt32(prop, 24, x.Node);
            Marshal.WriteInt32(prop, 28, 0);
            Marshal.Copy(buf, 0, data, buf.Length);
            int hr = d.KsControl.KsProperty(prop, 32, data, buf.Length, out _);
            if (hr != 0) throw new XuException($"XU selector {selector} {(set ? "SET" : "GET")}_CUR failed, HRESULT 0x{hr:X8}");
            if (!set) Marshal.Copy(data, buf, 0, buf.Length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(prop);
            Marshal.FreeCoTaskMem(data);
        }
    }

    /// <summary>Reads flash through the firmware (selector 3, 8 bytes per packet, read flag 0x80 checked).</summary>
    static byte[] FirmwareRead(UvcDevice d, SonixXu x, int from, int len)
    {
        var r = new byte[len];
        for (int off = 0; off < len; off += 8)
        {
            int a = from + off, n = Math.Min(8, len - off);
            var b = new byte[11];
            b[0] = (byte)a;
            b[1] = (byte)(a >> 8);
            b[2] = (byte)(0x80 | (a >= 0x10000 ? 0x10 : 0) | n);
            if ((b[2] & 0x80) == 0) throw new InvalidOperationException("refused: not a read packet");
            XuTransfer(d, x, 3, b, true);
            Array.Clear(b, 0, b.Length);
            XuTransfer(d, x, 3, b, false);
            Array.Copy(b, 3, r, off, n);
            if ((off & 0x3FF) == 0)
            {
                if (CancelRequested && CanCancel) throw new OperationCanceledException();
                ProgressHook?.Invoke(off, len);
            }
        }
        ProgressHook?.Invoke(len, len);
        return r;
    }

    // ---------- raw SPI ----------

    sealed class ControllerLost : Exception
    {
        public ControllerLost(string m) : base(m) { }
    }

    enum Kind { Read, Control, Erase, Program }

    /// <summary>
    /// Watches the flash controller for at least ms (floor 1000) and until it has been idle (0x1080 = 00,
    /// 0x1091 = 01) for 500 ms; logs every change with its time. False if it never got quiet within ms + 10 s.
    /// </summary>
    static bool Settle(SonixXu x, int ms)
    {
        ms = Math.Max(1000, ms);
        var sw = Stopwatch.StartNew();
        long quietSince = 0;
        int last = -1, changes = 0;
        while (sw.ElapsedMilliseconds < ms + 10000)
        {
            int v = x.AsicRead(0x1080) << 8 | x.AsicRead(0x1091);
            if (v != last)
            {
                if (last >= 0 || v != 0x0001) Note($"  settle {sw.ElapsedMilliseconds,5} ms: 0x1080={v >> 8:X2} 0x1091={v & 0xFF:X2}");
                if (last >= 0) changes++;
                last = v;
                quietSince = sw.ElapsedMilliseconds;
            }
            if (v == 0x0001 && sw.ElapsedMilliseconds >= ms && sw.ElapsedMilliseconds - quietSince >= 500)
            {
                Note($"controller idle after {sw.ElapsedMilliseconds} ms of watching ({changes} changes)");
                return true;
            }
        }
        Note($"controller never idle for 500 ms within {ms + 10000} ms ({changes} changes)");
        return false;
    }

    /// <summary>
    /// Raw SPI through the flash controller, held from construction to Dispose (0x1080 = 1; only chip select
    /// toggles per command). Every command checks that we still hold it before, before the chip-select rise of an
    /// erase/program (the chip executes on that rise), and after. Use on the device thread, stream never run.
    /// </summary>
    sealed class Spi : IDisposable
    {
        readonly SonixXu x;
        bool held;

        public Spi(SonixXu x)
        {
            this.x = x;
            byte ctl = x.AsicRead(0x1080), cs = x.AsicRead(0x1091);
            if (ctl != 0x00 || cs != 0x01) throw new ControllerLost($"flash controller not idle (0x1080={ctl:X2}, 0x1091={cs:X2}; expected 00/01)");
            x.AsicWrite(0x1080, 1);
            held = true;
        }

        /// <summary>Write disable (if we still hold the controller), then release chip select and the controller.</summary>
        public void Dispose()
        {
            if (!held) return;
            held = false;
            try
            {
                if (x.AsicRead(0x1080) == 1 && x.AsicRead(0x1091) == 1)
                {
                    RawCommand(new byte[] { 0x04 }, 0, Kind.Control);
                    byte s = RawCommand(new byte[] { 0x05 }, 1, Kind.Read)[0];
                    Note($"write disable sent, status 0x{s:X2}");
                }
                else Note("controller not ours at release: write disable skipped");
            }
            catch (Exception e) { Note("write disable failed: " + e.Message); }
            try { x.AsicWrite(0x1091, 1); }
            catch (Exception e) { Note("chip select release failed: " + e.Message); }
            try { x.AsicWrite(0x1080, 0); }
            catch (Exception e) { Note("controller release failed: " + e.Message); }
        }

        void Ready()
        {
            for (int i = 0; i < 200; i++)
            {
                if (x.AsicRead(0x1084) == 1) return;
                Thread.Sleep(1);
            }
            throw new XuException("flash controller not ready (0x1084)");
        }

        void Send(byte b)
        {
            x.AsicWrite(0x1082, b);
            x.AsicWrite(0x1081, 1);
            Ready();
        }

        byte Receive()
        {
            x.AsicWrite(0x1083, 0);
            x.AsicWrite(0x1081, 2);
            Ready();
            return x.AsicRead(0x1083);
        }

        public byte[] Command(IList<byte> tx, int rxLen, Kind kind)
        {
            if (!held) throw new ControllerLost("controller already released");
            return RawCommand(tx, rxLen, kind);
        }

        byte[] RawCommand(IList<byte> tx, int rxLen, Kind kind)
        {
            byte ctl = x.AsicRead(0x1080), cs = x.AsicRead(0x1091);
            if (ctl != 1 || cs != 1) throw new ControllerLost($"lost the flash controller before the command (0x1080={ctl:X2}, 0x1091={cs:X2})");
            x.AsicWrite(0x1091, 0);
            byte[] r;
            try
            {
                foreach (var b in tx) Send(b);
                r = new byte[rxLen];
                for (int i = 0; i < rxLen; i++) r[i] = Receive();
                // checked for every command, so reads (dry run) prove this readback before an erase relies on it
                ctl = x.AsicRead(0x1080);
                cs = x.AsicRead(0x1091);
                if (ctl != 1 || cs != 0)
                {
                    // a sector erase only executes if chip select rises right after bit 32: one more byte cancels it
                    if (kind == Kind.Erase) Send(0x00);
                    throw new ControllerLost($"lost the flash controller before chip select rose (0x1080={ctl:X2}, 0x1091={cs:X2})" +
                                             (kind == Kind.Erase ? "; erase cancelled by an extra byte" : ""));
                }
            }
            catch
            {
                try { x.AsicWrite(0x1091, 1); }
                catch (Exception e) { Note("chip select release failed: " + e.Message); }
                throw;
            }
            x.AsicWrite(0x1091, 1);
            ctl = x.AsicRead(0x1080);
            if (ctl != 1) throw new ControllerLost($"lost the flash controller during the command (0x1080={ctl:X2})");
            return r;
        }

        public byte Status() => Command(new byte[] { 0x05 }, 1, Kind.Read)[0];
        public byte[] Read(int addr, int len) => Command(new byte[] { 0x03, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr }, len, Kind.Read);

        /// <summary>Strict: while we hold the controller the first ID read must be right.</summary>
        public void RequireId()
        {
            var id = Command(new byte[] { 0x9F }, 3, Kind.Read);
            Note($"flash ID {id[0]:X2} {id[1]:X2} {id[2]:X2}");
            if (!id.SequenceEqual(ExpectedId)) throw new ControllerLost("flash ID not 5E 32 13 while holding the controller: interference");
        }

        /// <summary>Busy poll; status must end at exactly 0x00. Returns whether busy (WIP) was seen at all: a short
        /// operation can finish before the first poll, so callers decide from their own read-back.</summary>
        public bool WaitIdle(int timeoutMs, string what, out long ms)
        {
            var sw = Stopwatch.StartNew();
            bool sawBusy = false;
            byte s;
            while (((s = Status()) & 1) != 0)
            {
                sawBusy = true;
                if (sw.ElapsedMilliseconds > timeoutMs) throw new InvalidOperationException($"{what}: still busy after {timeoutMs} ms (status 0x{s:X2})");
                Thread.Sleep(2);
            }
            if (s != 0x00) throw new InvalidOperationException($"{what}: status 0x{s:X2} after completion (expected 0x00)");
            ms = sw.ElapsedMilliseconds;
            return sawBusy;
        }

        /// <summary>Write enable; the status must then read exactly 0x02 (WREN is ignored while busy).</summary>
        public void WriteEnable()
        {
            Command(new byte[] { 0x06 }, 0, Kind.Control);
            byte s = Status();
            if (s != 0x02) throw new InvalidOperationException($"write enable not confirmed (status 0x{s:X2}, expected 0x02)");
        }

        /// <summary>Write disable; the status must then read exactly 0x00.</summary>
        public void WriteDisable()
        {
            Command(new byte[] { 0x04 }, 0, Kind.Control);
            byte s = Status();
            if (s != 0x00) throw new InvalidOperationException($"write disable not confirmed (status 0x{s:X2}, expected 0x00)");
        }

        /// <summary>
        /// Status 0x00, write enable, erase, then a raw read of the whole sector must be all FF. The blank check
        /// decides, not the busy flag: the erase may end before the first poll (time never measured on this chip),
        /// and a lost 0x20 opcode (address bytes taken as WRSR, which clears WEL) leaves the old data. If busy was
        /// never seen, three more status reads must be 0x00. beforeErase runs right before the 0x20 command.
        /// </summary>
        public void SectorErase(int addr, Action beforeErase)
        {
            byte s = Status();
            if (s != 0x00) throw new InvalidOperationException($"status 0x{s:X2} before the erase (expected 0x00)");
            WriteEnable();
            beforeErase();
            Command(new byte[] { 0x20, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr }, 0, Kind.Erase);
            bool sawBusy = WaitIdle(3000, "sector erase", out long ms);
            if (!sawBusy)
            {
                var more = Enumerable.Range(0, 3).Select(_ => Status()).ToList();
                Note($"WARNING: the erase never showed busy (finished before the first poll?); status {string.Join(" ", more.Select(v => v.ToString("X2")))}");
                if (more.Any(v => v != 0x00)) throw new InvalidOperationException("status not 0x00 after an erase that never showed busy");
            }
            Note("erase finished; reading the sector to check that it is blank (about 30 s)...");
            var blank = Read(addr, SectorLen);
            int notFF = blank.Count(b => b != 0xFF);
            Note($"erase: busy {(sawBusy ? $"for ~{ms} ms" : "never seen")}; blank check: {notFF} bytes not FF");
            if (notFF > 0) throw new InvalidOperationException($"sector not blank after erase: {notFF} bytes are not FF");
        }

        /// <summary>Write enable, program, then read the page back raw.</summary>
        public void PageProgram(int addr, byte[] src, int off)
        {
            WriteEnable();
            var tx = new List<byte> { 0x02, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr };
            for (int i = 0; i < PageLen; i++) tx.Add(src[off + i]);
            Command(tx, 0, Kind.Program);
            WaitIdle(500, $"page program 0x{addr:X5}", out _);
            var back = Read(addr, PageLen);
            int bad = Enumerable.Range(0, PageLen).Count(i => back[i] != src[off + i]);
            if (bad > 0) throw new InvalidOperationException($"page 0x{addr:X5}: {bad} bytes differ after programming");
        }
    }

    // ---------- camera commands ----------

    static double ReadMs(SonixXu x)
    {
        var t = Stopwatch.StartNew();
        for (int i = 0; i < 6; i++) x.AsicRead(0x101F);
        return t.Elapsed.TotalMilliseconds / 6;
    }

    static int Probe(UvcDevice d, SonixXu x)
    {
        Note($"ASIC read {ReadMs(x):F1} ms");
        Note("sector 0x10000: " + Hex(FirmwareRead(d, x, Sector, 16), 0, 15));
        var sw = Stopwatch.StartNew();
        int last = -1;
        while (sw.ElapsedMilliseconds < 3000)
        {
            int v = x.AsicRead(0x1080) << 8 | x.AsicRead(0x1091);
            if (v != last) Note($"  {sw.ElapsedMilliseconds,5} ms: 0x1080={v >> 8:X2} 0x1091={v & 0xFF:X2}");
            last = v;
        }
        return 0;
    }

    static int Verify(UvcDevice d, SonixXu x)
    {
        var image = File.ReadAllBytes(Args[1]);
        if (image.Length != ImageLen) { Note($"IMAGE must be {ImageLen} bytes"); return 2; }
        var cam = FirmwareRead(d, x, 0, ImageLen);
        int inside = Enumerable.Range(Sector, SectorLen).Count(i => cam[i] != image[i]);
        int outside = Enumerable.Range(0, ImageLen).Count(i => cam[i] != image[i]) - inside;
        Note(inside == 0 && outside == 0 ? $"camera flash (128 KB) EQUALS {Path.GetFileName(Args[1])} (sector sha256 {Sha(cam, Sector, SectorLen)})"
                                         : $"camera flash differs from {Path.GetFileName(Args[1])}: {inside} bytes inside the sector, {outside} outside");
        return inside == 0 && outside == 0 ? 0 : 1;
    }

    static int Backup(UvcDevice d, SonixXu x)
    {
        string path = Args[1];
        if (File.Exists(path)) { Note($"REFUSED: {path} exists (a backup is never overwritten)"); return 2; }
        Note($"ASIC read {ReadMs(x):F1} ms");
        Note("reading the camera's flash, first pass (128 KB, ~45 s)");
        var a = FirmwareRead(d, x, 0, ImageLen);
        Note("second pass");
        var b = FirmwareRead(d, x, 0, ImageLen);
        if (!a.SequenceEqual(b)) { Note("REFUSED: the two reads differ; nothing saved (bad USB connection? try another port or cable)"); return 1; }
        File.WriteAllBytes(path, a);
        Note($"saved {path}: sha256 {Patch.Sha(a)}");
        var state = Patch.Identify(a, out var fw, out int version);
        Note(state == Patch.State.Unknown ? "this flash content is NOT a known firmware: the patch is not available for it"
                                          : $"known firmware: {fw.Name} ({(state == Patch.State.Original ? "original, can be patched" : $"patched with v{version}")})");
        return 0;
    }

    static int WriteSector(UvcDevice d, SonixXu x, bool restore)
    {
        if (!Plan(ReadBackup(), restore, out var original, out var image, out var fw)) return 1;
        bool write = Opt.ContainsKey("write");
        int target = restore ? 0 : Patch.Version;
        // 'any' only for a recovery of an unknown, half-written sector (the rest of the flash is still checked)
        bool anyCurrent = false;
        if (Opt.TryGetValue("current", out var cn))
        {
            if (!restore) { Note("--current is for restore only"); return 2; }
            if (cn == "any") anyCurrent = true;
            else if (cn != "patched") { Note("--current must be any"); return 2; }
        }
        var states = Patch.KnownStates(original, fw);
        var pages = Enumerable.Range(0, SectorLen / PageLen).Where(p => image.Skip(Sector + p * PageLen).Take(PageLen).Any(b => b != 0xFF)).ToList();
        Note($"plan: checks (controller idle, flash ID, status, raw read of the sector, write enable), then erase 0x{Sector:X5} + blank check, " +
             $"program {pages.Count} pages with read-back, raw read of the sector, release, firmware read of 128 KB");

        double readMs = ReadMs(x);
        Note($"ASIC read {readMs:F1} ms");
        if (readMs > 8) { Note("REFUSED: slow USB port (> 8 ms per register read); use a port on the CPU's controller"); return 1; }
        if (!NoOtherCameraUsers("before the checks")) return 1;

        // the right board in the expected state: the whole low 128 KB as the firmware reads it
        byte[] cam;
        if (KnownFlash != null && KnownFlash.Length == ImageLen)
        {
            cam = KnownFlash;
            Note("camera flash: the backup read from this camera in this session (not unplugged since); no second read");
        }
        else
        {
            Note("reading the camera's flash (128 KB, ~45 s)");
            cam = FirmwareRead(d, x, 0, ImageLen);
        }
        int outside = 0;
        for (int i = 0; i < ImageLen; i++)
            if ((i < Sector || i >= Sector + SectorLen) && cam[i] != original[i]) outside++;
        if (outside > 0) { Note($"REFUSED: the camera's flash differs from the original in {outside} bytes outside the sector (other board or firmware)"); return 1; }
        // the whole 128 KB must be one known state (original or a released patch version)
        int camState = -1;
        foreach (var known in states) if (known.image.SequenceEqual(cam)) { camState = known.version; break; }
        string what;
        if (anyCurrent)
        {
            Note($"--current any: the camera's sector is NOT checked (camera sha256 {Sha(cam, Sector, SectorLen)}, {StateName(camState)}); " +
                 "the rest equals the original");
            what = $"write {StateName(target)} (recovery: the sector's present content is not checked)";
        }
        else
        {
            if (camState < 0)
            {
                Note($"REFUSED: the camera's sector is not a known state of this firmware (camera sha256 {Sha(cam, Sector, SectorLen)}). " +
                     "After a failed write only: restore with --current any");
                return 1;
            }
            Note($"camera flash: {StateName(camState)} (equals the known image in all 128 KB)");
            if (camState == target) { Note($"the camera already holds {StateName(target)}: nothing to do, nothing written"); return 4; }
            what = target == 0 ? $"restore the original firmware (the camera has {StateName(camState)})"
                 : camState == 0 ? $"patch the original firmware to v{target}"
                 : $"update the patch from v{camState} to v{target}";
        }
        Note("plan: " + what);

        CanCancel = false;   // from here on the sector may change: the operation always runs to its end
        int settle = Opt.TryGetValue("settle", out var st) && int.TryParse(st, out int sv) ? sv : 1500;
        if (!Settle(x, settle)) { Note("REFUSED: flash controller not idle"); return 1; }

        // the checks run before every write (alone they are the dry run); the raw read also confirms that the sector
        // still holds what the comparison above expects
        try
        {
            using (var spi = new Spi(x))
            {
                var hold = Stopwatch.StartNew();
                spi.RequireId();
                byte s = spi.Status();
                Note($"status 0x{s:X2}");
                if (s != 0x00) { Note("REFUSED: status register not 0x00 (busy, write enabled or protected)"); return 1; }
                // raw read of the whole sector, holding the controller about as long as erase + blank check would
                Note("reading the sector directly from the flash chip to compare it (about 30 s)...");
                var raw = spi.Read(Sector, SectorLen);
                int rawBad = Enumerable.Range(0, SectorLen).Count(i => raw[i] != cam[Sector + i]);
                Note(rawBad == 0 ? $"raw SPI read of the sector equals the firmware read ({hold.Elapsed.TotalSeconds:F1} s holding the controller)"
                                 : $"raw SPI read differs from the firmware read in {rawBad} bytes");
                if (rawBad > 0) return 1;
                // the one status value the write depends on: WREN -> 0x02, WRDI -> 0x00 (WREN alone writes nothing)
                spi.WriteEnable();
                Note("write enable confirmed (status 0x02)");
                spi.WriteDisable();
                Note("write disable confirmed (status 0x00)");
            }
            if (!write) { Note("dry run: nothing written (add --write)"); return 0; }
            Note("all checks passed");
        }
        catch (ControllerLost e)
        {
            Note($"check stopped: {e.Message}. Nothing was written. Another program probably used the camera: close every " +
                 "program that uses it and try again.");
            return 1;
        }
        if (ConfirmHook != null && !ConfirmHook(what)) { Note("not confirmed: nothing was written"); return 3; }
        // the question may have been open for a while: someone may have started a camera program meanwhile
        if (!NoOtherCameraUsers("before the write")) return 1;
        if (!Settle(x, settle)) { Note("REFUSED: flash controller not idle; nothing was written"); return 1; }

        WriteStarted = true;
        var sw = Stopwatch.StartNew();
        bool erased = false, done = false;
        for (int attempt = 1; attempt <= 3 && !done; attempt++)
        {
            Spi spi = null;
            try
            {
                if (attempt > 1 && !Settle(x, settle)) throw new ControllerLost("controller not idle before the retry");
                spi = new Spi(x);
                spi.RequireId();
                Note($"attempt {attempt}: ERASE sector 0x{Sector:X5}");
                spi.SectorErase(Sector, () => erased = true);
                foreach (int p in pages)
                {
                    int a = Sector + p * PageLen;
                    spi.PageProgram(a, image, a);
                    Note($"page 0x{a:X5} programmed and read back ({sw.Elapsed.TotalSeconds:F1} s)");
                }
                Note("all pages written; reading the whole sector back from the flash chip (about 30 s)...");
                var raw = spi.Read(Sector, SectorLen);
                int rawBad = Enumerable.Range(0, SectorLen).Count(i => raw[i] != image[Sector + i]);
                if (rawBad > 0) throw new InvalidOperationException($"raw read of the whole sector: {rawBad} bytes differ from the image");
                Note($"raw read of the whole sector equals the image ({sw.Elapsed.TotalSeconds:F1} s)");
                done = true;
            }
            catch (Exception ex)
            {
                Note($"attempt {attempt} FAILED: {ex.Message}");
                if (!erased)
                {
                    spi?.Dispose();
                    Note("nothing was erased or written");
                    return 1;
                }
            }
            finally
            {
                spi?.Dispose();
            }
        }
        if (!done)
        {
            Note("WRITE FAILED after 3 attempts. SECTOR STATE UNKNOWN. Do NOT replug; keep the camera powered, read the log, then rerun with --current any.");
            return 1;
        }

        // independent of the raw path: the firmware's own reads, the whole 128 KB, controller released
        Note("reading the whole flash back through the camera's firmware for the final check (about 40 s)...");
        var back = FirmwareRead(d, x, 0, ImageLen);
        int inside = Enumerable.Range(Sector, SectorLen).Count(i => back[i] != image[i]);
        int outsideBad = Enumerable.Range(0, ImageLen).Count(i => back[i] != image[i]) - inside;
        Note(inside == 0 && outsideBad == 0
            ? $"VERIFIED in {sw.Elapsed.TotalSeconds:F1} s: the whole 128 KB equals the image (sector sha256 {Sha(back, Sector, SectorLen)}). Replug the camera."
            : $"FIRMWARE READ-BACK DIFFERS: {inside} bytes inside the sector, {outsideBad} outside. Do NOT replug; keep the camera powered and report.");
        return inside == 0 && outsideBad == 0 ? 0 : 1;
    }
}
