#Requires -Version 7.2
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$LocalConfigPath
)

$ErrorActionPreference = 'Stop'
$maximumBytes = 16 * 1024
$plain = $null
$key = $null
$bundle = $null
$aes = $null

try {
    # The key travels with the payload. This hides plain text at rest in release
    # artifacts; it does not make a public client resistant to reverse engineering.
    $output = [System.IO.Path]::GetFullPath($OutputPath)
    if ($LocalConfigPath) {
        $inputPath = [System.IO.Path]::GetFullPath($LocalConfigPath)
        if (-not [System.IO.File]::Exists($inputPath) -or ([System.IO.FileInfo]$inputPath).Length -gt $maximumBytes) {
            throw [System.IO.InvalidDataException]::new('Invalid private input.')
        }
        $json = [System.IO.File]::ReadAllText($inputPath, [System.Text.Encoding]::UTF8)
    } else {
        $json = [Environment]::GetEnvironmentVariable('HM_APPKIT_SUPPORT_CONFIG')
    }
    if ([string]::IsNullOrWhiteSpace($json)) {
        # A later build without configuration must never inherit an older bundle.
        if ([System.IO.File]::Exists($output)) { [System.IO.File]::Delete($output) }
        Write-Output 'Built-in support receiver: not configured.'
        return
    }
    if ([System.Text.Encoding]::UTF8.GetByteCount($json) -gt $maximumBytes) {
        throw [System.IO.InvalidDataException]::new('Invalid private input.')
    }
    $config = ConvertFrom-Json -InputObject $json -AsHashtable
    if ($config -isnot [System.Collections.IDictionary] -or $config.Mode -isnot [string] -or
        $config.Mode -ine 'FeishuWebhook' -or $config.Endpoint -isnot [string] -or
        $config.SigningSecret -isnot [string] -or [string]::IsNullOrWhiteSpace($config.SigningSecret) -or
        $config.SigningSecret.Length -gt 4096) {
        throw [System.IO.InvalidDataException]::new('Invalid private input.')
    }
    $endpoint = $null
    if (-not [Uri]::TryCreate($config.Endpoint, [UriKind]::Absolute, [ref]$endpoint) -or
        $endpoint.Scheme -cne 'https' -or $endpoint.IdnHost -ine 'open.feishu.cn' -or
        -not $endpoint.IsDefaultPort -or $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment -or
        $endpoint.AbsolutePath -cnotmatch '^/open-apis/bot/v2/hook/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
        throw [System.IO.InvalidDataException]::new('Invalid private input.')
    }
    $normalized = [ordered]@{
        DisplayName = 'IT 支持'
        Endpoint = $endpoint.AbsoluteUri
        Mode = 'FeishuWebhook'
        SigningSecret = $config.SigningSecret
    } | ConvertTo-Json -Compress
    $plain = [System.Text.Encoding]::UTF8.GetBytes($normalized)
    $header = [System.Text.Encoding]::ASCII.GetBytes('HMFB1')
    if ($plain.Length + 65 -gt $maximumBytes) { throw [System.IO.InvalidDataException]::new('Invalid private input.') }
    $key = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $nonce = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $tag = [byte[]]::new(16)
    $ciphertext = [byte[]]::new($plain.Length)
    $aes = [System.Security.Cryptography.AesGcm]::new($key)
    $aes.Encrypt($nonce, $plain, $ciphertext, $tag, $header)
    $bundle = [byte[]]::new(65 + $ciphertext.Length)
    [Array]::Copy($header, 0, $bundle, 0, 5)
    [Array]::Copy($key, 0, $bundle, 5, 32)
    [Array]::Copy($nonce, 0, $bundle, 37, 12)
    [Array]::Copy($tag, 0, $bundle, 49, 16)
    [Array]::Copy($ciphertext, 0, $bundle, 65, $ciphertext.Length)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($output)) | Out-Null
    [System.IO.File]::WriteAllBytes($output, $bundle)
    Write-Output 'Built-in support receiver bundle generated.'
} catch {
    # Do not emit parser/URI/crypto exceptions: they can contain private input.
    throw [System.IO.InvalidDataException]::new('内置日志接收配置未能生成，请检查私有构建配置；未输出接收地址或签名密钥。')
} finally {
    if ($aes) { $aes.Dispose() }
    if ($plain) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($plain) }
    if ($key) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($key) }
    if ($bundle) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($bundle) }
    $json = $null
    $normalized = $null
    $config = $null
}
