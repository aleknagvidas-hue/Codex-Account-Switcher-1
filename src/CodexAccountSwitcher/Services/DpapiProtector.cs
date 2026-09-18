using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountSwitcher.Services;

internal interface ICredentialProtector
{
    byte[] Protect(ReadOnlySpan<byte> plain);
    byte[] Unprotect(ReadOnlySpan<byte> encrypted);
}

internal sealed class WindowsCredentialProtector : ICredentialProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plain) => DpapiProtector.Protect(plain);
    public byte[] Unprotect(ReadOnlySpan<byte> encrypted) => DpapiProtector.Unprotect(encrypted);
}

public static class DpapiProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexAccountSwitcher/auth/v1");

    public static byte[] Protect(ReadOnlySpan<byte> plain) => Transform(plain, protect: true);

    public static byte[] Unprotect(ReadOnlySpan<byte> encrypted) => Transform(encrypted, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> source, bool protect)
    {
        var input = CreateBlob(source);
        var entropy = CreateBlob(Entropy);
        DataBlob output = default;

        try
        {
            var success = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);

            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not process the credential data.");
            }

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            FreeInputBlob(ref input);
            FreeInputBlob(ref entropy);
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    private static DataBlob CreateBlob(ReadOnlySpan<byte> data)
    {
        var blob = new DataBlob { Length = data.Length };
        if (data.IsEmpty)
        {
            return blob;
        }

        blob.Data = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data.ToArray(), 0, blob.Data, data.Length);
        return blob;
    }

    private static void FreeInputBlob(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
