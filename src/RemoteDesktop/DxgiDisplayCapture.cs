using System.Runtime.InteropServices;

namespace RemoteDesktop;

internal readonly record struct DxgiImage(byte[] Bgra, int Width, int Height, int Left, int Top, bool Changed);

internal static class DxgiDisplayCapture
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiFormatBgra = 87;
    private static IntPtr device;
    private static IntPtr context;
    private static IntPtr duplication;
    private static IntPtr staging;
    private static string outputName = "";
    private static byte[]? pixels;
    private static int width;
    private static int height;
    private static int originLeft;
    private static int originTop;
    private static DateTime retryAfter;

    public static void Invalidate() => ReleaseDuplication();

    public static bool TryAcquire(string? displayId, int timeoutMs, out DxgiImage image)
    {
        image = default;
        if (DateTime.UtcNow < retryAfter || displayId == "all") return false;
        try
        {
            var name = string.IsNullOrEmpty(displayId) ? System.Windows.Forms.Screen.PrimaryScreen?.DeviceName : displayId;
            if (string.IsNullOrEmpty(name) || !Ensure(name)) return false;
            var acquired = Acquire(duplication, timeoutMs, out _, out var resource);
            if (acquired == WaitTimeout)
            {
                if (pixels is null) return false;
                image = new DxgiImage(pixels, width, height, originLeft, originTop, false);
                return true;
            }
            if (acquired < 0 || resource == IntPtr.Zero)
            {
                ReleaseDuplication();
                return false;
            }
            try
            {
                var changed = HasDesktopChanges();
                if (!changed && pixels is not null)
                {
                    image = new DxgiImage(pixels, width, height, originLeft, originTop, false);
                    return true;
                }
                if (!CopyToBuffer(resource)) return false;
                image = new DxgiImage(pixels!, width, height, originLeft, originTop, true);
                return true;
            }
            finally
            {
                Release(resource);
                ReleaseFrame(duplication);
            }
        }
        catch (Exception)
        {
            ReleaseAll();
            retryAfter = DateTime.UtcNow.AddSeconds(2);
            return false;
        }
    }

    private static bool Ensure(string name)
    {
        if (duplication != IntPtr.Zero && outputName.Equals(name, StringComparison.OrdinalIgnoreCase) && device != IntPtr.Zero) return true;
        ReleaseAll();
        var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref factoryId, out var factory) < 0 || factory == IntPtr.Zero) return false;
        try
        {
            for (uint adapterIndex = 0; adapterIndex < 8; adapterIndex++)
            {
                if (EnumAdapters(factory, adapterIndex, out var adapter) < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    for (uint outputIndex = 0; outputIndex < 8; outputIndex++)
                    {
                        if (EnumOutputs(adapter, outputIndex, out var output) < 0 || output == IntPtr.Zero) break;
                        try
                        {
                            if (GetOutputDesc(output, out var desc) < 0 || !desc.DeviceName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                            if (D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out _, out context) < 0 || device == IntPtr.Zero) return false;
                            var output1Id = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
                            var output1 = Query(output, output1Id);
                            try
                            {
                                if (DuplicateOutput(output1, device, out duplication) < 0 || duplication == IntPtr.Zero) return false;
                            }
                            finally { Release(output1); }
                            outputName = desc.DeviceName;
                            originLeft = desc.Left;
                            originTop = desc.Top;
                            return true;
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        ReleaseAll();
        retryAfter = DateTime.UtcNow.AddSeconds(2);
        return false;
    }

    private static bool HasDesktopChanges()
    {
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            const int moreData = unchecked((int)0x887A0003);
            var dirty = GetDirty(duplication, 4096, buffer, out var dirtyBytes);
            var moved = GetMoved(duplication, 4096, buffer, out var movedBytes);
            if ((dirty < 0 && dirty != moreData) || (moved < 0 && moved != moreData)) return true;
            return pixels is null || dirtyBytes > 0 || movedBytes > 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool CopyToBuffer(IntPtr resource)
    {
        var textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        var texture = Query(resource, textureId);
        try
        {
            GetTextureDesc(texture, out var desc);
            if (desc.Width <= 0 || desc.Height <= 0 || desc.Width > 16384 || desc.Height > 16384) return false;
            if (staging == IntPtr.Zero || desc.Width != width || desc.Height != height)
            {
                if (staging != IntPtr.Zero) { Release(staging); staging = IntPtr.Zero; }
                var stagingDesc = new TextureDesc
                {
                    Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = DxgiFormatBgra,
                    SampleCount = 1, Usage = 3, CpuAccess = 0x20000
                };
                if (CreateTexture(device, ref stagingDesc, IntPtr.Zero, out staging) < 0 || staging == IntPtr.Zero) return false;
                width = desc.Width;
                height = desc.Height;
                pixels = new byte[width * height * 4];
            }
            CopyResource(context, staging, texture);
            if (Map(context, staging, 0, 1, 0, out var mapped) < 0 || mapped.Data == IntPtr.Zero) return false;
            try
            {
                var rowBytes = width * 4;
                for (var y = 0; y < height; y++)
                    Marshal.Copy(mapped.Data + y * (int)mapped.RowPitch, pixels!, y * rowBytes, rowBytes);
            }
            finally { Unmap(context, staging, 0); }
            return true;
        }
        finally { Release(texture); }
    }

    private static void ReleaseDuplication()
    {
        if (duplication != IntPtr.Zero) { Release(duplication); duplication = IntPtr.Zero; }
        outputName = "";
        pixels = null;
    }

    private static void ReleaseAll()
    {
        ReleaseDuplication();
        if (staging != IntPtr.Zero) { Release(staging); staging = IntPtr.Zero; }
        if (context != IntPtr.Zero) { Release(context); context = IntPtr.Zero; }
        if (device != IntPtr.Zero) { Release(device); device = IntPtr.Zero; }
        width = height = 0;
    }

    private static IntPtr Query(IntPtr com, Guid iid)
    {
        var hr = Delegate<QiDelegate>(com, 0)(com, ref iid, out var result);
        if (hr < 0 || result == IntPtr.Zero) throw new InvalidOperationException("DXGI interface is unavailable.");
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int Attached;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public int Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced, ProtectedContentMaskedOut, PointerX, PointerY, PointerVisible;
        public uint TotalMetadata, PointerShapeBytes;
    }

    private delegate int QiDelegate(IntPtr self, ref Guid iid, out IntPtr result);
    private delegate uint ReleaseDelegate(IntPtr self);
    private delegate int EnumAdaptersDelegate(IntPtr self, uint index, out IntPtr adapter);
    private delegate int EnumOutputsDelegate(IntPtr self, uint index, out IntPtr output);
    private delegate int GetDescDelegate(IntPtr self, out OutputDesc desc);
    private delegate int DuplicateDelegate(IntPtr self, IntPtr device, out IntPtr duplication);
    private delegate int AcquireDelegate(IntPtr self, uint timeout, out FrameInfo info, out IntPtr resource);
    private delegate int RectsDelegate(IntPtr self, uint size, IntPtr buffer, out uint required);
    private delegate int ReleaseFrameDelegate(IntPtr self);
    private delegate int GetTextureDescDelegate(IntPtr self, out TextureDesc desc);
    private delegate int CreateTextureDelegate(IntPtr self, ref TextureDesc desc, IntPtr data, out IntPtr texture);
    private delegate int MapDelegate(IntPtr self, IntPtr resource, uint subresource, int type, uint flags, out Mapped mapped);
    private delegate void UnmapDelegate(IntPtr self, IntPtr resource, uint subresource);
    private delegate void CopyDelegate(IntPtr self, IntPtr destination, IntPtr source);

    private static void Release(IntPtr com) { if (com != IntPtr.Zero) Delegate<ReleaseDelegate>(com, 2)(com); }
    private static int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter) => Delegate<EnumAdaptersDelegate>(factory, 7)(factory, index, out adapter);
    private static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output) => Delegate<EnumOutputsDelegate>(adapter, 7)(adapter, index, out output);
    private static int GetOutputDesc(IntPtr output, out OutputDesc desc) => Delegate<GetDescDelegate>(output, 7)(output, out desc);
    private static int DuplicateOutput(IntPtr output, IntPtr devicePointer, out IntPtr result) => Delegate<DuplicateDelegate>(output, 22)(output, devicePointer, out result);
    private static int Acquire(IntPtr duplicationPointer, int timeout, out FrameInfo info, out IntPtr resource) => Delegate<AcquireDelegate>(duplicationPointer, 8)(duplicationPointer, (uint)timeout, out info, out resource);
    private static int GetDirty(IntPtr duplicationPointer, uint size, IntPtr buffer, out uint required) => Delegate<RectsDelegate>(duplicationPointer, 9)(duplicationPointer, size, buffer, out required);
    private static int GetMoved(IntPtr duplicationPointer, uint size, IntPtr buffer, out uint required) => Delegate<RectsDelegate>(duplicationPointer, 10)(duplicationPointer, size, buffer, out required);
    private static void ReleaseFrame(IntPtr duplicationPointer) => Delegate<ReleaseFrameDelegate>(duplicationPointer, 14)(duplicationPointer);
    private static void GetTextureDesc(IntPtr texture, out TextureDesc desc) => Delegate<GetTextureDescDelegate>(texture, 10)(texture, out desc);
    private static int CreateTexture(IntPtr devicePointer, ref TextureDesc desc, IntPtr data, out IntPtr texture) => Delegate<CreateTextureDelegate>(devicePointer, 5)(devicePointer, ref desc, data, out texture);
    private static int Map(IntPtr contextPointer, IntPtr resource, uint subresource, int type, uint flags, out Mapped mapped) => Delegate<MapDelegate>(contextPointer, 14)(contextPointer, resource, subresource, type, flags, out mapped);
    private static void Unmap(IntPtr contextPointer, IntPtr resource, uint subresource) => Delegate<UnmapDelegate>(contextPointer, 15)(contextPointer, resource, subresource);
    private static void CopyResource(IntPtr contextPointer, IntPtr destination, IntPtr source) => Delegate<CopyDelegate>(contextPointer, 47)(contextPointer, destination, source);

    private static T Delegate<T>(IntPtr com, int slot) where T : Delegate
    {
        var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
}
