using System.Runtime.InteropServices;

namespace DeskLink;

static class TaskbarChecks
{
    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Text;
        [FieldOffset(8)] public short Boolean;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropertyValue value);
    }
    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SHGetPropertyStoreForWindow(nint window, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropertyValue value);

    public static void VerifyIdentity(StreamWindow view)
    {
        var iid = typeof(IPropertyStore).GUID;
        SHGetPropertyStoreForWindow(view.Handle, in iid, out var store);
        try
        {
            var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            store.GetValue(in key, out var id);
            try
            {
                if (id.Type != 31 || Marshal.PtrToStringUni(id.Text) != $"DeskPortal.Shared.{view.Id:N}")
                    throw new InvalidOperationException("The shell grouped this window with the host application.");
            }
            finally { PropVariantClear(ref id); }
            key.Id = 9;
            store.GetValue(in key, out var pinning);
            try
            {
                if (pinning.Type != 11 || pinning.Boolean == 0)
                    throw new InvalidOperationException("An ephemeral stream should not offer a persistent taskbar pin.");
            }
            finally { PropVariantClear(ref pinning); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
}
