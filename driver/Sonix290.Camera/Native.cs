// COM interop for the parts of DirectShow and Kernel Streaming the driver uses.
// Declared from the Windows SDK headers (strmif.h, qedit.h, vidcap.h, ks.h); methods are listed
// in vtable order up to the last one used.
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Sonix290
{
    static class Clsid
    {
        public static readonly Guid SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        public static readonly Guid VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        public static readonly Guid FilterGraph = new Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770");
        public static readonly Guid SampleGrabber = new Guid("C1F400A0-3F08-11d3-9F0B-006008039E37");
        public static readonly Guid NullRenderer = new Guid("C1F400A4-3F08-11d3-9F0B-006008039E37");
    }

    static class MediaGuids
    {
        public static readonly Guid Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid Mjpg = new Guid("47504A4D-0000-0010-8000-00AA00389B71");
        public static readonly Guid Yuy2 = new Guid("32595559-0000-0010-8000-00AA00389B71");
        public static readonly Guid FormatVideoInfo = new Guid("05589f80-c356-11ce-bf01-00aa0055595a");
        public static readonly Guid FormatVideoInfo2 = new Guid("f72a76a0-eb0a-11d0-ace4-0000c0cc16ba");

        public static string Name(Guid sub)
        {
            if (sub == Mjpg) return "MJPG";
            if (sub == Yuy2) return "YUY2";
            var b = sub.ToByteArray();
            return System.Text.Encoding.ASCII.GetString(b, 0, 4);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AMMediaType
    {
        public Guid MajorType;
        public Guid SubType;
        public int FixedSizeSamples;
        public int TemporalCompression;
        public int SampleSize;
        public Guid FormatType;
        public IntPtr Unk;
        public int FormatSize;
        public IntPtr Format;
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker enumerator, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value);
    }

    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IBaseFilter
    {
        [PreserveSig] int GetClassID(out Guid clsid);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Run(long start);
        [PreserveSig] int GetState(int timeoutMs, out int state);
        [PreserveSig] int SetSyncSource(IntPtr clock);
        [PreserveSig] int GetSyncSource(out IntPtr clock);
        [PreserveSig] int EnumPins(out IEnumPins pins);
        [PreserveSig] int FindPin([MarshalAs(UnmanagedType.LPWStr)] string id, out IPin pin);
        [PreserveSig] int QueryFilterInfo(IntPtr info);
        [PreserveSig] int JoinFilterGraph(IntPtr graph, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int QueryVendorInfo(out IntPtr info);
    }

    [ComImport, Guid("56a86892-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumPins
    {
        [PreserveSig] int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IPin[] pins, IntPtr fetched);
    }

    [ComImport, Guid("56a86891-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPin
    {
        [PreserveSig] int Connect(IPin to, IntPtr mediaType);
        [PreserveSig] int ReceiveConnection(IPin from, IntPtr mediaType);
        [PreserveSig] int Disconnect();
        [PreserveSig] int ConnectedTo(out IPin to);
        [PreserveSig] int ConnectionMediaType(IntPtr mediaType);
        [PreserveSig] int QueryPinInfo(IntPtr info);
        [PreserveSig] int QueryDirection(out int direction); // 0 = input, 1 = output
    }

    [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphBuilder
    {
        [PreserveSig] int AddFilter(IBaseFilter filter, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int RemoveFilter(IBaseFilter filter);
        [PreserveSig] int EnumFilters(out IntPtr filters);
        [PreserveSig] int FindFilterByName([MarshalAs(UnmanagedType.LPWStr)] string name, out IBaseFilter filter);
        [PreserveSig] int ConnectDirect(IPin output, IPin input, IntPtr mediaType);
    }

    [ComImport, Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IMediaControl
    {
        [PreserveSig] int Run();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int GetState(int timeoutMs, out int state);
    }

    /// <summary>The filter graph manager's IMediaFilter (strmif.h), used for SetSyncSource only.</summary>
    [ComImport, Guid("56a86899-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMediaFilter
    {
        [PreserveSig] int GetClassID(out Guid clsid);
        [PreserveSig] int Stop();
        [PreserveSig] int Pause();
        [PreserveSig] int Run(long start);
        [PreserveSig] int GetState(int timeoutMs, out int state);
        [PreserveSig] int SetSyncSource(IntPtr clock);
        [PreserveSig] int GetSyncSource(out IntPtr clock);
    }

    [ComImport, Guid("C6E13340-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAMStreamConfig
    {
        [PreserveSig] int SetFormat(IntPtr mediaType);
        [PreserveSig] int GetFormat(out IntPtr mediaType);
        [PreserveSig] int GetNumberOfCapabilities(out int count, out int size);
        [PreserveSig] int GetStreamCaps(int index, out IntPtr mediaType, IntPtr caps);
    }

    [ComImport, Guid("6B652FFF-11FE-4fce-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISampleGrabber
    {
        [PreserveSig] int SetOneShot(int oneShot);
        [PreserveSig] int SetMediaType(IntPtr mediaType);
        [PreserveSig] int GetConnectedMediaType(IntPtr mediaType);
        [PreserveSig] int SetBufferSamples(int buffer);
        [PreserveSig] int GetCurrentBuffer(ref int size, IntPtr buffer);
        [PreserveSig] int GetCurrentSample(out IntPtr sample);
        [PreserveSig] int SetCallback(ISampleGrabberCB callback, int whichMethod);
    }

    [ComImport, ComVisible(true), Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISampleGrabberCB
    {
        [PreserveSig] int SampleCB(double sampleTime, IntPtr sample);
        [PreserveSig] int BufferCB(double sampleTime, IntPtr buffer, int length);
    }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }

    [ComImport, Guid("C6E13360-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAMVideoProcAmp
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl
    {
        [PreserveSig] int KsProperty(IntPtr property, int propertyLength, IntPtr data, int dataLength, out int returned);
        [PreserveSig] int KsMethod(IntPtr method, int methodLength, IntPtr data, int dataLength, out int returned);
        [PreserveSig] int KsEvent(IntPtr evt, int eventLength, IntPtr data, int dataLength, out int returned);
    }

    [ComImport, Guid("720D4AC0-7533-11D0-A5D6-28DB04C10000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsTopologyInfo
    {
        [PreserveSig] int get_NumCategories(out int count);
        [PreserveSig] int get_Category(int index, out Guid category);
        [PreserveSig] int get_NumConnections(out int count);
        [PreserveSig] int get_ConnectionInfo(int index, IntPtr info);
        [PreserveSig] int get_NodeName(int node, IntPtr name, int bufferSize, out int nameLength);
        [PreserveSig] int get_NumNodes(out int count);
        [PreserveSig] int get_NodeType(int node, out Guid type);
    }

    static class Ks
    {
        public const int TypeGet = 0x1;
        public const int TypeSet = 0x2;
        public const int TypeTopology = 0x10000000;
        // PROPSETID_VIDCAP_CAMERACONTROL, KSPROPERTY_CAMERACONTROL_AUTO_EXPOSURE_PRIORITY
        public static readonly Guid CameraControlSet = new Guid("C6E13370-30AC-11d0-A18C-00A0C9118956");
        public const int AutoExposurePriority = 19;
    }

    static class Ole
    {
        [DllImport("ole32.dll")]
        public static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

        public static void FreeMediaType(IntPtr pmt)
        {
            if (pmt == IntPtr.Zero) return;
            var mt = Marshal.PtrToStructure<AMMediaType>(pmt);
            if (mt.Format != IntPtr.Zero) Marshal.FreeCoTaskMem(mt.Format);
            if (mt.Unk != IntPtr.Zero) Marshal.Release(mt.Unk);
            Marshal.FreeCoTaskMem(pmt);
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }
}
