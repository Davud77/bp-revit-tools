using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace BotPlus.Eom.Core;

[SupportedOSPlatform("windows")]
internal sealed class FamilyFileStreams : IDisposable
{
    const int ReadExclusive = 0x10;
    readonly IStorage storage;

    internal FamilyFileStreams(string path)
    {
        int result = StgOpenStorage(path, IntPtr.Zero, ReadExclusive, IntPtr.Zero, 0, out var root);
        Marshal.ThrowExceptionForHR(result);
        storage = root;
    }

    internal byte[]? Read(string name, int maxBytes)
    {
        if (storage.OpenStream(name, IntPtr.Zero, ReadExclusive, 0, out var stream) < 0) return null;
        try
        {
            stream.Stat(out var stat, 1);
            if (stat.cbSize <= 0 || stat.cbSize > maxBytes) return null;
            var bytes = new byte[(int)stat.cbSize];
            IntPtr count = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                stream.Read(bytes, bytes.Length, count);
                return Marshal.ReadInt32(count) == bytes.Length ? bytes : null;
            }
            finally { Marshal.FreeHGlobal(count); }
        }
        finally { Marshal.ReleaseComObject(stream); }
    }

    public void Dispose() => Marshal.ReleaseComObject(storage);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int StgOpenStorage(string name, IntPtr priority, int mode, IntPtr exclude, int reserved, out IStorage storage);

    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IStorage
    {
        // First two methods in the native IStorage vtable.
        [PreserveSig] int CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, int mode, int reserved1, int reserved2, out IStream stream);
        [PreserveSig] int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, int mode, int reserved2, out IStream stream);
    }
}
