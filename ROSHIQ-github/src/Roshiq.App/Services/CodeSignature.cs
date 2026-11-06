using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Roshiq.App.Services;

/// <summary>Проверка цифровой подписи Authenticode средствами Windows (WinVerifyTrust).</summary>
public static class CodeSignature
{
    /// <summary>Издатель (Subject сертификата) или null, если файл не подписан.</summary>
    public static string? Publisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile — единственный способ прочитать сертификат Authenticode без сторонних библиотек
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch { return null; }
    }

    /// <summary>true, если подпись действительна и цепочка доверена Windows.</summary>
    public static bool IsTrusted(string path)
    {
        var file = new WinTrustFileInfo(path);
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData(filePtr);
            var action = WintrustActionGenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            // закрыть состояние проверки
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        catch { return false; }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    private static Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;            // 2 = WTD_UI_NONE
        public uint RevocationChecks;    // 0 = WTD_REVOKE_NONE
        public uint UnionChoice;         // 1 = WTD_CHOICE_FILE
        public IntPtr FileInfo;
        public uint StateAction;         // 1 = VERIFY, 2 = CLOSE
        public IntPtr StateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? URLReference;
        public uint ProvFlags;           // 0x10 = WTD_CACHE_ONLY_URL_RETRIEVAL
        public uint UIContext;
        public IntPtr SignatureSettings;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SIPClientData = IntPtr.Zero;
            UIChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 1;
            StateData = IntPtr.Zero;
            URLReference = null;
            ProvFlags = 0x10;
            UIContext = 0;
            SignatureSettings = IntPtr.Zero;
        }
    }
}
