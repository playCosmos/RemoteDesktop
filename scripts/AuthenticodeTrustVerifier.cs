using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RemoteDesktop.Build;

public static class AuthenticodeTrustVerifier
{
    private const uint UiNone = 2;
    private const uint ChoiceFile = 1;
    private const uint StateVerify = 1;
    private const uint StateClose = 2;
    private const uint CacheOnlyUrlRetrieval = 0x00001000;
    private static readonly Guid GenericVerifyAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateHandle;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    public static uint VerifyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var pathMemory = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        var fileMemory = IntPtr.Zero;
        var data = default(WinTrustData);
        var stateOpened = false;
        try
        {
            var file = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                Path = pathMemory
            };
            fileMemory = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, fileMemory, false);

            data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                UnionChoice = ChoiceFile,
                File = fileMemory,
                StateAction = StateVerify,
                ProviderFlags = CacheOnlyUrlRetrieval
            };
            var action = GenericVerifyAction;
            stateOpened = true;
            return unchecked((uint)WinVerifyTrust(IntPtr.Zero, ref action, ref data));
        }
        finally
        {
            if (stateOpened)
            {
                data.StateAction = StateClose;
                var action = GenericVerifyAction;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            if (fileMemory != IntPtr.Zero) Marshal.FreeHGlobal(fileMemory);
            Marshal.FreeCoTaskMem(pathMemory);
        }
    }
}
