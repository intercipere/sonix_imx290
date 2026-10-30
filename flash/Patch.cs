// The patch versions and the list of firmware images they are known to apply to. No firmware is stored here: each
// version is a list of changed bytes against the original firmware, applied to the user's own backup of the camera's
// flash (low 128 KB). After the change the ten CRC-16/MODBUS values of the table at 0x10B00 are recomputed, and the
// result must hash to the value recorded for that firmware and version, so a wrong patch or a wrong CRC computation
// cannot be written. Every version is defined against the original, so any known state (original or any released
// version) can be turned back into the original exactly and from there into any other state: one write per change.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

static class Patch
{
    public const int Sector = 0x10000, SectorLen = 0x1000, ImageLen = 0x20000;

    public sealed class Run
    {
        public int At;
        public byte[] Old, New;
        public Run(int at, string old, string @new) { At = at; Old = Hex(old); New = Hex(@new); }
    }

    public sealed class Firmware
    {
        /// <summary>SHA-256 of the whole 128 KB: the original, and the result of each released patch version
        /// (Patched[0] = v1, ...). A new patch version adds its hash and keeps the older ones, so the tool still
        /// recognises a camera patched with an older version (no marker is written to the flash).</summary>
        public string Name, Original;
        public string[] Patched;
    }

    public sealed class PatchVersion
    {
        public int Number;
        public string Name;
        /// <summary>Data bytes only, Old = the original's bytes (the CRCs that cover them are recomputed).</summary>
        public Run[] Runs;
    }

    // CRC-16/MODBUS table at 0x10B00 (big-endian): (start, end, crc offset)
    public static readonly (int start, int end, int at)[] Crcs =
    {
        (0x0000, 0x6000, 0x10B00), (0x0000, 0x2000, 0x10B02), (0x6000, 0x8000, 0x10B04), (0x8000, 0xA000, 0x10B06),
        (0xA000, 0xC000, 0x10B08), (0xC000, 0xE000, 0x10B0A), (0xE000, 0x10000, 0x10B0C), (0x10C00, 0x12100, 0x10B12),
        (0x10000, 0x10600, 0x10B14), (0x10600, 0x10B00, 0x10B16),
    };

    /// <summary>
    /// Every released version, oldest first. A released version is never edited: a change is a new entry here plus
    /// its hash in each Firmware.Patched. The last entry is what "patch" writes.
    /// </summary>
    public static readonly PatchVersion[] Versions =
    {
        new PatchVersion
        {
            Number = 1,
            Name = "YUY2 1920x1080 (5 fps) video mode: 1:1 scaler record",
            Runs = new[]
            {
                new Run(0x107A2, "4001F000", "80073804"),
                new Run(0x107A7, "803202", "40E309"),
                new Run(0x107AB, "803202", "40E309"),
                new Run(0x107AF, "5802", "483F"),
                new Run(0x107B2, "151605", "80841E"),
                new Run(0x107B7, "151605", "80841E"),
                new Run(0x10D16, "014000F0", "07800438"),
                new Run(0x10D1B, "0258", "3F48"),
                new Run(0x10D1E, "0516", "1E84"),
                new Run(0x10D22, "0320", "0C00"),
                new Run(0x10F13, "014000F0", "07800438"),
                new Run(0x10F18, "34", "00"),
                new Run(0x10F1F, "12", "04"),
                new Run(0x10F21, "12", "04"),
            },
        },
    };

    public static PatchVersion Current => Versions[Versions.Length - 1];
    public static int Version => Current.Number;
    public static string PatchName => Current.Name;
    public static string PatchTitle => $"{PatchName}, patch v{Version}";

    /// <summary>"the original firmware" or "patch vN".</summary>
    public static string StateName(int version) => version == 0 ? "the original firmware" : $"patch v{version}";

    public static readonly Firmware[] Known =
    {
        new Firmware
        {
            Name = "SONIX_IPC-292B----IMX291 (USB 0C45:6366), first release",
            Original = "79ee98638a329d4af58758175fe7ca5e2f8dc85b14fc6c327e394a0944224fb9",
            Patched = new[] { "a93c7a1acf93f357274b14bbfa7bc37a1624458f55c4f9ac49bad7f2c47aa2d7" },   // v1
        },
    };

    public static byte[] Hex(string s) => Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();

    public static string Sha(byte[] b)
    {
        using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
    }

    public static int Modbus(byte[] b, int start, int end)
    {
        int c = 0xFFFF;
        for (int i = start; i < end; i++)
        {
            c ^= b[i];
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xA001 : c >> 1;
        }
        return c;
    }

    public static void FixCrcs(byte[] image)
    {
        foreach (var c in Crcs)
        {
            int v = Modbus(image, c.start, c.end);
            image[c.at] = (byte)(v >> 8);
            image[c.at + 1] = (byte)v;
        }
    }

    public static bool CrcsValid(byte[] image) => Crcs.All(c => (image[c.at] << 8 | image[c.at + 1]) == Modbus(image, c.start, c.end));

    /// <summary>Patched = this tool's patch version; OlderPatch = an earlier released version of it.</summary>
    public enum State { Original, Patched, OlderPatch, Unknown }

    /// <summary>What the 128 KB are: a known original, a known patched image, or something else (unsupported).</summary>
    public static State Identify(byte[] dump, out Firmware fw) => Identify(dump, out fw, out _);

    /// <summary>As above; version = the patch version found (0 = original or unknown).</summary>
    public static State Identify(byte[] dump, out Firmware fw, out int version)
    {
        fw = null;
        version = 0;
        if (dump == null || dump.Length != ImageLen) return State.Unknown;
        string h = Sha(dump);
        foreach (var k in Known)
        {
            if (h == k.Original) { fw = k; return State.Original; }
            int i = Array.IndexOf(k.Patched, h);
            if (i >= 0) { fw = k; version = i + 1; return version == Version ? State.Patched : State.OlderPatch; }
        }
        return State.Unknown;
    }

    /// <summary>The image of patch version n for a known original (old bytes checked, CRCs recomputed, hash checked).</summary>
    public static byte[] Apply(byte[] original, Firmware fw, int version)
    {
        if (Identify(original, out var f) != State.Original || f != fw) throw new InvalidOperationException("not the original this patch is for");
        var v = Versions[version - 1];
        var image = (byte[])original.Clone();
        foreach (var r in v.Runs)
        {
            if (r.At < Sector || r.At + r.New.Length > Sector + SectorLen) throw new InvalidOperationException("patch run outside the sector");
            for (int i = 0; i < r.Old.Length; i++)
                if (image[r.At + i] != r.Old[i]) throw new InvalidOperationException($"byte 0x{r.At + i:X5} is not the expected 0x{r.Old[i]:X2}");
            Array.Copy(r.New, 0, image, r.At, r.New.Length);
        }
        FixCrcs(image);
        if (Sha(image) != fw.Patched[version - 1]) throw new InvalidOperationException($"patch v{version} image does not hash to the expected value");
        return image;
    }

    /// <summary>The original for an image of patch version n (the reverse of Apply, hash checked).</summary>
    public static byte[] Revert(byte[] patched, Firmware fw, int version)
    {
        if (Identify(patched, out var f, out int found) == State.Unknown || f != fw || found != version)
            throw new InvalidOperationException($"not the patch v{version} image of this firmware");
        var image = (byte[])patched.Clone();
        foreach (var r in Versions[version - 1].Runs)
        {
            for (int i = 0; i < r.New.Length; i++)
                if (image[r.At + i] != r.New[i]) throw new InvalidOperationException($"byte 0x{r.At + i:X5} is not the expected 0x{r.New[i]:X2}");
            Array.Copy(r.Old, 0, image, r.At, r.Old.Length);
        }
        FixCrcs(image);
        if (Sha(image) != fw.Original) throw new InvalidOperationException("reverted image does not hash to the expected value");
        return image;
    }

    /// <summary>Every known state of this firmware, built from its original: (0, original), (1, v1), ...</summary>
    public static List<(int version, byte[] image)> KnownStates(byte[] original, Firmware fw)
    {
        var r = new List<(int, byte[])> { (0, original) };
        foreach (var v in Versions) r.Add((v.Number, Apply(original, fw, v.Number)));
        return r;
    }

}
