namespace FoxDeploy.Core;

public static class DriveSelectionPolicy
{
    internal static IReadOnlyList<string> DefaultDrivePriority { get; } =
        Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => ((char)value).ToString()).Concat(["A", "B", "C"]).ToArray();

    public static bool IsEligibleTarget(DriveSnapshot drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        return drive.IsReady && drive.Type == DriveType.Fixed && drive.HardwareVerified &&
            !drive.IsExternal && !drive.IsVirtual && NormalizeDriveLetter(drive.Name) is not null;
    }

    public static IReadOnlyList<DriveSnapshot> OrderDrives(IReadOnlyList<DriveSnapshot> drives)
    {
        ArgumentNullException.ThrowIfNull(drives);
        return drives.Where(IsEligibleTarget)
            .OrderBy(drive => DriveRank(NormalizeDriveLetter(drive.Name)![0]))
            .ToArray();
    }

    public static string? ResolveDefaultDrive(IReadOnlyList<DriveSnapshot> drives) =>
        OrderDrives(drives).Select(drive => NormalizeDriveLetter(drive.Name)).FirstOrDefault();

    private static int DriveRank(char letter) => letter == 'C' ? 25 : letter is 'A' or 'B' ? 23 + letter - 'A' : letter - 'D';

    private static string? NormalizeDriveLetter(string? value)
    {
        var letter = value?.Trim().TrimEnd('\\', ':');
        return letter is { Length: 1 } && char.IsAsciiLetter(letter[0]) ? letter.ToUpperInvariant() : null;
    }
}
