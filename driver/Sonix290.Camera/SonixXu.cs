// SN9C29x System XU over Kernel Streaming: ASIC register access and the ASIC's I2C master
// (0x10D0..0x10D9), as reconstructed by the community from SONiX's UVC TestAP (see README, Credits). No flash access on purpose.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sonix290
{
    sealed class XuException : Exception
    {
        public XuException(string m) : base(m) { }
    }

    /// <summary>Use on the ComThread that owns the device.</summary>
    sealed class SonixXu : IDisposable
    {
        public static readonly Guid SystemXu = new Guid("28F03370-6311-4A2E-BA2C-6890EB334016");
        const int AsicSelector = 1;
        const int KspNodeSize = 32;   // KSPROPERTY (Guid, Id, Flags) + NodeId + Reserved

        readonly IKsControl ks;
        public readonly int Node;
        readonly IntPtr prop = Marshal.AllocCoTaskMem(KspNodeSize);
        readonly IntPtr data = Marshal.AllocCoTaskMem(16);
        public int Transfers;

        SonixXu(IKsControl ks, int node) { this.ks = ks; Node = node; }

        // usbvideo reports extension units as device-specific nodes
        static readonly Guid DevSpecificNode = new Guid("941C7AC0-C559-11D0-8A2B-00A0C9255AC1");

        /// <summary>
        /// Finds the System XU node: candidates by node type (the XU GUID, then KSNODETYPE_DEV_SPECIFIC, then any),
        /// each confirmed by an ASIC read through the System XU property set.
        /// </summary>
        public static SonixXu Find(IKsControl ks, IKsTopologyInfo topo)
        {
            if (ks == null) throw new XuException("camera filter has no IKsControl");
            int count = 0;
            if (topo != null) topo.get_NumNodes(out count);
            var types = new Guid[count];
            for (int i = 0; i < count; i++)
            {
                topo.get_NodeType(i, out types[i]);
                Log.Write($"topology node {i}: {types[i]}");
            }
            for (int pass = 0; pass < 3; pass++)
                for (int i = 0; i < Math.Max(count, pass == 2 ? 8 : 0); i++)
                {
                    Guid t = i < count ? types[i] : Guid.Empty;
                    bool candidate = pass == 0 ? t == SystemXu : pass == 1 ? t == DevSpecificNode
                                                               : t != SystemXu && t != DevSpecificNode;
                    if (!candidate) continue;
                    var x = new SonixXu(ks, i);
                    try
                    {
                        byte id = x.AsicRead(0x101F);
                        Log.Write($"XU: node {i} (type {t}) answers ASIC reads, chip id 0x{id:X2}");
                        return x;
                    }
                    catch (XuException e)
                    {
                        Log.Write($"XU: node {i} (type {t}): {e.Message}");
                        x.Dispose();
                    }
                }
            throw new XuException($"System XU not found among {count} topology nodes");
        }

        void Transfer(byte[] buf, bool set)
        {
            var g = SystemXu.ToByteArray();
            Marshal.Copy(g, 0, prop, 16);
            Marshal.WriteInt32(prop, 16, AsicSelector);
            Marshal.WriteInt32(prop, 20, (set ? Ks.TypeSet : Ks.TypeGet) | Ks.TypeTopology);
            Marshal.WriteInt32(prop, 24, Node);
            Marshal.WriteInt32(prop, 28, 0);
            Marshal.Copy(buf, 0, data, buf.Length);
            int hr = ks.KsProperty(prop, KspNodeSize, data, buf.Length, out _);
            Transfers++;
            if (hr != 0) throw new XuException($"XU {(set ? "SET" : "GET")}_CUR failed, HRESULT 0x{hr:X8}");
            if (!set) Marshal.Copy(data, buf, 0, buf.Length);
        }

        public void AsicWrite(int addr, byte value) =>
            Transfer(new[] { (byte)addr, (byte)(addr >> 8), value, (byte)0x00 }, true);

        public byte AsicRead(int addr)
        {
            var b = new[] { (byte)addr, (byte)(addr >> 8), (byte)0, (byte)0xFF };
            Transfer(b, true);
            Transfer(b, false);
            return b[2];
        }

        /// <summary>
        /// One transaction on the ASIC's I2C master with up to 5 data bytes (in/out). Returns the final status
        /// register; bit 0x04 = done, 0x08 = failed. The firmware shares this engine, so a "done" here does not
        /// prove the sensor took the write.
        /// </summary>
        public byte I2cTransaction(bool read, int slave, byte[] five, int length)
        {
            AsicWrite(0x10D9, 1);
            AsicWrite(0x10D8, 1);
            AsicWrite(0x10D0, (byte)(0x80 | ((length & 7) << 4) | ((read ? 1 : 0) << 1)));
            AsicWrite(0x10D1, (byte)slave);
            for (int i = 0; i < 5; i++) AsicWrite(0x10D2 + i, five[i]);
            AsicWrite(0x10D7, 0x10);
            Thread.Sleep(5);
            byte status = 0;
            for (int t = 0; t < 30; t++)
            {
                status = AsicRead(0x10D0);
                if ((status & 0x04) != 0) break;
                Thread.Sleep(10);
            }
            if (read && (status & 0x0C) == 0x04)
                for (int i = 0; i < 5; i++) five[i] = AsicRead(0x10D2 + i);
            return status;
        }

        public static bool StatusOk(byte s) => (s & 0x0C) == 0x04;

        /// <summary>Writes 1-3 consecutive sensor registers starting at reg (16-bit address, MSB first).</summary>
        public bool SensorWrite(int slave, int reg, params byte[] values)
        {
            if (values.Length < 1 || values.Length > 3) throw new ArgumentException("1-3 bytes");
            var b = new byte[5];
            b[0] = (byte)(reg >> 8);
            b[1] = (byte)reg;
            Array.Copy(values, 0, b, 2, values.Length);
            return StatusOk(I2cTransaction(false, slave, b, 2 + values.Length));
        }

        /// <summary>Reads one sensor register. Unreliable on this camera (firmware I2C traffic between the two transactions).</summary>
        public int SensorRead(int slave, int reg)
        {
            var b = new byte[] { (byte)(reg >> 8), (byte)reg, 0, 0, 0 };
            if (!StatusOk(I2cTransaction(false, slave, b, 2))) return -1;
            var r = new byte[5];
            if (!StatusOk(I2cTransaction(true, slave, r, 1))) return -1;
            return r[4]; // right-aligned in the 5-byte buffer
        }

        /// <summary>Address-only write: does the slave acknowledge? The sensor only answers while streaming.</summary>
        public bool Probe(int slave) =>
            StatusOk(I2cTransaction(false, slave, new byte[] { 0x30, 0x00, 0, 0, 0 }, 2));

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(prop);
            Marshal.FreeCoTaskMem(data);
        }
    }

    /// <summary>IMX290/291 frame length (VMAX) and shutter (SHS1) for a requested exposure.</summary>
    public struct SensorTiming : IEquatable<SensorTiming>
    {
        public const int VmaxLimit = 262143;   // 18 bits
        public const int LongShs1 = 8;

        public int Vmax, Shs1;

        /// <summary>Exposure = (VMAX - SHS1 - 1) x 1H; frame period = VMAX x 1H.</summary>
        public static SensorTiming For(double seconds, double lineSec, int baseVmax)
        {
            long lines = (long)Math.Round(seconds / lineSec);
            long vmax = lines + LongShs1 + 1;
            if (vmax > baseVmax)
                return new SensorTiming { Vmax = (int)Math.Min(vmax, VmaxLimit), Shs1 = LongShs1 };
            lines = Math.Max(1, lines);
            return new SensorTiming { Vmax = baseVmax, Shs1 = (int)Math.Max(LongShs1, baseVmax - 1 - lines) };
        }

        public static double MaxExposure(double lineSec) => (VmaxLimit - LongShs1 - 1) * lineSec;

        public double Exposure(double lineSec) => (Vmax - Shs1 - 1) * lineSec;
        public double Period(double lineSec) => Vmax * lineSec;

        /// <summary>One REGHOLD-wrapped write of VMAX and SHS1 (LSB first). Returns how many I2C transactions reported success.</summary>
        internal int Write(SonixXu xu, int slave, bool burst, out int transactions)
        {
            byte[] v = { (byte)Vmax, (byte)(Vmax >> 8), (byte)((Vmax >> 16) & 3) };
            byte[] s = { (byte)Shs1, (byte)(Shs1 >> 8), (byte)((Shs1 >> 16) & 1) };
            int ok = 0;
            transactions = 0;
            ok += xu.SensorWrite(slave, 0x3001, 1) ? 1 : 0; transactions++;
            if (burst)
            {
                ok += xu.SensorWrite(slave, 0x3018, v) ? 1 : 0;
                ok += xu.SensorWrite(slave, 0x3020, s) ? 1 : 0;
                transactions += 2;
            }
            else
            {
                for (int i = 0; i < 3; i++) { ok += xu.SensorWrite(slave, 0x3018 + i, v[i]) ? 1 : 0; transactions++; }
                for (int i = 0; i < 3; i++) { ok += xu.SensorWrite(slave, 0x3020 + i, s[i]) ? 1 : 0; transactions++; }
            }
            ok += xu.SensorWrite(slave, 0x3001, 0) ? 1 : 0; transactions++;
            return ok;
        }

        public bool Equals(SensorTiming o) => Vmax == o.Vmax && Shs1 == o.Shs1;
        public override bool Equals(object o) => o is SensorTiming t && Equals(t);
        public override int GetHashCode() => Vmax * 31 + Shs1;
        public override string ToString() => $"VMAX={Vmax} SHS1={Shs1}";
    }
}
