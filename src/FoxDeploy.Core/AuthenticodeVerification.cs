using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace FoxDeploy.Core;

public static class AuthenticodeVerification
{
    public static string VerifyPublisher(string path, IReadOnlyList<string> allowedPublishers)
    {
        var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1,
                FileInfo = filePointer, ProviderFlags = 0x10
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) throw new InvalidDataException($"安装包数字签名验证失败 (0x{unchecked((uint)result):X8})。");
            // X509CertificateLoader reads certificate encodings, not signed PE containers.
            // Trust is checked above; this API only obtains that container's signer name.
#pragma warning disable SYSLIB0057
            using var signed = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(signed.Export(X509ContentType.Cert));
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (!allowedPublishers.Any(name => name.Equals(publisher, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"安装包发布者不符合已核实的软件发布者：{publisher}");
            return publisher;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
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
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
