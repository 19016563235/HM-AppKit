namespace FoxDeploy.Core;

/// <summary>A verified release, staged outside the running application's directory.</summary>
public sealed record UpdateApplyPreparation
{
    public required string StageDirectory { get; init; }
    public required string TargetDirectory { get; init; }
    public required string TargetVersion { get; init; }
    public required string ArchiveSha256 { get; init; }
    public string ExecutableRelativePath { get; init; } = "HM AppKit.exe";
    public required IReadOnlyList<string> ManagedFiles { get; init; }
    public IReadOnlyList<string> PreserveRelativePaths { get; init; } = [];
}

public sealed record UpdateApplyResult(
    bool Success,
    bool AlreadyApplied,
    bool RollbackSucceeded,
    bool Canceled,
    string ExecutablePath,
    string Message);

/// <summary>Overrides support deterministic filesystem/process tests. Production uses the defaults.</summary>
public sealed record UpdateApplyOptions
{
    public string? StagingRoot { get; init; }
    public string? CurrentExecutablePath { get; init; }
    public TimeSpan ParentExitTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public Func<string, string?>? ExecutableVersionReader { get; init; }
    public Func<UpdateApplyProcessIdentity, CancellationToken, Task>? WaitForParentExitAsync { get; init; }
}

public sealed record UpdateApplyProcessIdentity(int ProcessId, DateTime StartTimeUtc, string ExecutablePath);

// These documents are persisted before any installed file changes. Their paths and contents are
// checked independently by the replacement executable; a command-line path is never a file list.
internal sealed record UpdateApplyManifest
{
    public int Schema { get; init; } = 1;
    public required string Id { get; init; }
    public required string Nonce { get; init; }
    public required string StageDirectory { get; init; }
    public required string TargetDirectory { get; init; }
    public required string ExecutableRelativePath { get; init; }
    public required string TargetVersion { get; init; }
    public required string ArchiveSha256 { get; init; }
    public required UpdateApplyProcessIdentity Parent { get; init; }
    public required UpdateApplyFile[] Files { get; init; }
    public string[] PreservedFiles { get; init; } = [];
}

internal sealed record UpdateApplyFile(string RelativePath, long Length, string Sha256, string? OriginalSha256);
internal sealed record UpdateApplyOwner(int Schema, string Id, string Nonce, string ManifestSha256);
internal sealed record UpdateApplyJournal(string Id, string Nonce, string State, string Message = "");
