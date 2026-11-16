using System.Runtime.InteropServices;
using System.Text;

namespace SpotlightSearch.Settings;

/// <summary>
/// DPAPI (CryptProtectData) with the current user scope — the same protection the
/// original used for the AI API key. The ciphertext is only readable by this Windows
/// account on this machine, so settings.json can be copied around without leaking it.
/// </summary>
internal static class ProtectedString
{
    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            return Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), protect: true));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), protect: false));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();
        IntPtr inputPtr = IntPtr.Zero;

        try
        {
            inputPtr = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inputPtr, input.Length);
            inputBlob.Data = inputPtr;
            inputBlob.Size = input.Length;

            bool ok = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob);

            if (!ok) throw new InvalidOperationException("DPAPI call failed.");

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero) Marshal.FreeHGlobal(inputPtr);
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
