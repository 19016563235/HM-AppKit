using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FoxDeploy.Core;

namespace FoxDeploy.App;

internal static class BuiltInSupportReceiver
{
    internal const string ResourceName = "FoxDeploy.support-receiver.bin";
    internal const int MaximumBundleBytes = 16 * 1024;
    private const string InvalidBundleMessage = "内置日志接收配置不可用，可保存诊断报告后联系 IT 支持。";

    internal static SupportUploadOptions? Load()
    {
        using var resource = typeof(BuiltInSupportReceiver).Assembly.GetManifestResourceStream(ResourceName);
        return Load(resource);
    }

    internal static SupportUploadOptions? Load(Stream? resource)
    {
        if (resource is null) return null;
        byte[]? bundle = null;
        byte[]? plaintext = null;
        try
        {
            // A bounded read also protects against non-seekable or misleading streams.
            bundle = new byte[MaximumBundleBytes + 1];
            var length = 0;
            while (length < bundle.Length)
            {
                var read = resource.Read(bundle, length, bundle.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length is <= 65 or > MaximumBundleBytes || !bundle.AsSpan(0, 5).SequenceEqual("HMFB1"u8))
                throw new InvalidDataException();
            plaintext = new byte[length - 65];
            // This random per-build key is intentionally shipped with the ciphertext.
            // It obscures plain text at rest; a public binary can still be reversed.
            using var aes = new AesGcm(bundle.AsSpan(5, 32), 16);
            aes.Decrypt(bundle.AsSpan(37, 12), bundle.AsSpan(65, length - 65), bundle.AsSpan(49, 16), plaintext, "HMFB1"u8);
            var config = JsonSerializer.Deserialize<Destination>(plaintext);
            if (config is null || config.Mode != nameof(SupportUploadMode.FeishuWebhook) ||
                !Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) ||
                !SupportUploadClient.IsValidEndpoint(endpoint, SupportUploadMode.FeishuWebhook) ||
                string.IsNullOrWhiteSpace(config.SigningSecret) || config.SigningSecret.Length > 4096)
                throw new InvalidDataException();
            return new SupportUploadOptions("IT 支持", endpoint, SupportUploadMode.FeishuWebhook, config.SigningSecret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or InvalidOperationException)
        {
            // Never attach the original exception or private JSON to diagnostics.
            throw new InvalidDataException(InvalidBundleMessage);
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (bundle is not null) CryptographicOperations.ZeroMemory(bundle);
        }
    }

    private sealed class Destination
    {
        public string? Endpoint { get; set; }
        public string? Mode { get; set; }
        public string? SigningSecret { get; set; }
    }
}
