using System.Globalization;

namespace FoxDeploy.Core;

/// <summary>Stable release versions, including the original two-part v0.5 tag.</summary>
public readonly record struct SelfUpdateVersion(int Major, int Minor, int Patch = 0, int Revision = 0) : IComparable<SelfUpdateVersion>
{
    public static bool TryParse(string? text, out SelfUpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var metadata = text.IndexOf('+');
        if (metadata >= 0) text = text[..metadata];
        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        Span<int> numbers = stackalloc int[4];
        numbers.Clear();
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0 || parts[index].Any(character => character is < '0' or > '9') ||
                !int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index])) return false;
        }
        version = new(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    public int CompareTo(SelfUpdateVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : Revision.CompareTo(other.Revision);
    }

    public override string ToString() => Revision == 0
        ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}.{Revision}";
}

public sealed record SelfUpdateRelease
{
    public required string Version { get; init; }
    public required string Tag { get; init; }
    public required Uri ReleasePage { get; init; }
    public string Notes { get; init; } = "";
    public required Uri PackageUri { get; init; }
    public required Uri ChecksumUri { get; init; }
    public required long PackageBytes { get; init; }
    public string? ApiSha256 { get; init; }
}

public sealed record SelfUpdatePackage
{
    public required SelfUpdateRelease Release { get; init; }
    public required string StageDirectory { get; init; }
    public required string ExtractedDirectory { get; init; }
    public required string ExecutablePath { get; init; }
    public required string ZipPath { get; init; }
    public required string Sha256 { get; init; }
    public required IReadOnlyList<string> RelativeFiles { get; init; }
}

public sealed record SelfUpdateProgress(long ReceivedBytes, long? TotalBytes, string Message)
{
    public double? Percentage => TotalBytes is > 0 ? Math.Min(100, ReceivedBytes * 100d / TotalBytes.Value) : null;
}

public sealed class SelfUpdateOptions
{
    public string StagingRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HM AppKit", "Updates");
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public long MaximumPackageBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumExtractedBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaximumArchiveEntries { get; init; } = 2048;
}
