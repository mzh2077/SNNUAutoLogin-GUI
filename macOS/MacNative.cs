using System.Runtime.InteropServices;

namespace SNNUAutoLogin.Mac;

internal static partial class MacNative
{
    private const string Library = "libSnnuMac.dylib";
    [DllImport(Library, EntryPoint = "snnu_request_location")]
    public static extern void RequestLocation();
    [DllImport(Library, EntryPoint = "snnu_wifi_kind")]
    private static extern int WifiKind([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, EntryPoint = "snnu_ssid")]
    private static extern IntPtr Ssid([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, EntryPoint = "snnu_password_read")]
    private static extern IntPtr PasswordRead([MarshalAs(UnmanagedType.LPUTF8Str)] string key, out int status);
    [DllImport(Library, EntryPoint = "snnu_password_write")]
    public static extern int SavePassword([MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password);
    [DllImport(Library, EntryPoint = "snnu_password_delete")]
    public static extern int DeletePassword([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Library, EntryPoint = "snnu_login_item")]
    private static extern int LoginItem(int enabled);
    public static int SetLoginItem(bool enabled) => LoginItem(enabled ? 1 : 0);
    [DllImport(Library, EntryPoint = "snnu_free")]
    private static extern void Free(IntPtr value);

    private static string TakeString(IntPtr value)
    {
        try { return value == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(value) ?? ""; }
        finally { Free(value); }
    }
    public static bool IsWifi(string name) => WifiKind(name) != 0;
    public static string ReadSsid(string name) => TakeString(Ssid(name));
    public static (string Password, int Status) ReadPassword(string key)
    {
        var pointer = PasswordRead(key, out int status);
        return (TakeString(pointer), status);
    }
}
