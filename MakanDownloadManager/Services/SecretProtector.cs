using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace MakanDownloadManager.Services;

/// <summary>Windows DPAPI protection for secrets persisted by Makan. Secrets are bound to the current Windows user.</summary>
public static class SecretProtector
{
    const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, out IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var input = Encoding.UTF8.GetBytes(value);
        var inBlob = new DataBlob { cbData = input.Length };
        inBlob.pbData = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            if (!CryptProtectData(ref inBlob, "Epsilon Download Manager secret", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptprotectUiForbidden, out var outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect the secret.");
            try
            {
                var output = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, output, 0, output.Length);
                return "dpapi:v1:" + Convert.ToBase64String(output);
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inBlob.pbData); }
    }

    public static bool TryUnprotect(string? value, out string? plain)
    {
        plain = null;
        if (string.IsNullOrEmpty(value)) { plain = value; return true; }
        if (!value.StartsWith("dpapi:v1:", StringComparison.Ordinal)) { plain = value; return false; }
        try
        {
            var input = Convert.FromBase64String(value[9..]);
            var inBlob = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
            try
            {
                Marshal.Copy(input, 0, inBlob.pbData, input.Length);
                if (!CryptUnprotectData(ref inBlob, out var description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var outBlob)) return false;
                try
                {
                    var output = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, output, 0, output.Length);
                    plain = Encoding.UTF8.GetString(output);
                    return true;
                }
                finally
                {
                    if (description != IntPtr.Zero) LocalFree(description);
                    LocalFree(outBlob.pbData);
                }
            }
            finally { Marshal.FreeHGlobal(inBlob.pbData); }
        }
        catch { return false; }
    }

    public static string? DecryptOrPlain(string? value, out bool wasPlaintext)
    {
        wasPlaintext = false;
        if (string.IsNullOrEmpty(value)) return value;
        if (TryUnprotect(value, out var plain)) return plain;
        // A protected value that cannot be opened (another Windows user or machine) is dropped instead of being sent as a cookie
        // or wrapped a second time.
        if (value.StartsWith("dpapi:v1:", StringComparison.Ordinal)) return null;
        wasPlaintext = true;
        return value;
    }
}
