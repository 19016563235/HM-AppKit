namespace FoxDeploy.Core;

public static class DriveResolver
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static IReadOnlyList<DriveSnapshot> GetDrives()
    {
        var result = new List<DriveSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var ready = drive.IsReady;
                var hardware = ready ? WindowsStorageDetection.Inspect(drive.Name) : StorageHardware.Unknown;
                result.Add(new DriveSnapshot(drive.Name[..1], drive.Name, ready, ready ? drive.AvailableFreeSpace : 0, drive.DriveType)
                {
                    HardwareVerified = hardware.Verified,
                    IsExternal = hardware.External,
                    IsVirtual = hardware.Virtual,
                    BusType = hardware.BusType
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new DriveSnapshot(drive.Name[..1], drive.Name, false, 0, DriveType.Unknown) { HardwareVerified = false });
            }
        }
        return result.AsReadOnly();
    }

    public static string ResolveTarget(AppDefinition app, string? customPath, DeploymentSettings settings, IReadOnlyList<DriveSnapshot>? drives = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        drives ??= GetDrives();
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            var normalized = ValidateAbsolutePath(customPath);
            var letter = normalized[..1];
            if (!drives.Any(d => DriveLetter(d.Root) == letter && DriveSelectionPolicy.IsEligibleTarget(d)))
                throw new InvalidOperationException($"指定目录的 {letter}: 盘不是已就绪的内置磁盘；不支持移动硬盘、U 盘或虚拟磁盘。");
            return normalized;
        }

        ValidateRelativeFolder(settings.BaseFolder, allowNested: true);
        ValidateRelativeFolder(app.FolderName, allowNested: false);
        if (!string.IsNullOrWhiteSpace(settings.SelectedDrive))
        {
            var letter = ParsePriority(settings.SelectedDrive);
            var selected = drives.FirstOrDefault(d => DriveLetter(d.Root) == letter && DriveSelectionPolicy.IsEligibleTarget(d));
            if (selected is null)
                throw new InvalidOperationException($"选择的 {letter}: 盘不是已就绪的内置磁盘，请重新选择存储位置。");
            return ValidateAbsolutePath(Path.Combine(selected.Root, settings.BaseFolder, app.FolderName));
        }
        if (settings.DrivePriority is null || settings.DrivePriority.Count == 0) throw new InvalidOperationException("请至少配置一个优先盘符。");
        foreach (var priority in settings.DrivePriority)
        {
            var letter = ParsePriority(priority);
            var drive = drives.FirstOrDefault(d => DriveLetter(d.Root) == letter && DriveSelectionPolicy.IsEligibleTarget(d));
            if (drive is null) continue;
            return ValidateAbsolutePath(Path.Combine(drive.Root, settings.BaseFolder, app.FolderName));
        }
        throw new InvalidOperationException("未找到可用的内置磁盘；移动硬盘、U 盘和虚拟磁盘不能作为安装位置。");
    }

    public static string ValidateAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("安装目录不能为空。");
        var value = path.Trim().Replace('/', '\\');
        if (value.Length < 4 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\')
            throw new ArgumentException("安装目录必须为本地绝对路径，例如 D:\\Software\\WeChat；不能使用盘符根目录或网络路径。");
        var relative = value[3..].TrimEnd('\\');
        ValidateRelativeFolder(relative, allowNested: true);
        return char.ToUpperInvariant(value[0]) + ":\\" + relative;
    }

    internal static void ValidateRelativeFolder(string? folder, bool allowNested)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("安装文件夹不能为空。");
        if (folder != folder.Trim() || folder.Contains('/') || (!allowNested && folder.Contains('\\')))
            throw new ArgumentException("文件夹名不能包含首尾空格或无效的分隔符。");
        foreach (var part in folder.Split('\\'))
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Any(char.IsControl) ||
                ReservedNames.Contains(part.Split('.')[0]))
                throw new ArgumentException($"无效的安装文件夹：{folder}");
        }
    }

    private static string ParsePriority(string value)
    {
        var trimmed = value?.Trim().TrimEnd('\\', ':');
        if (trimmed is null || trimmed.Length != 1 || !char.IsAsciiLetter(trimmed[0])) throw new ArgumentException($"优先盘符无效：{value}");
        return trimmed.ToUpperInvariant();
    }

    private static string DriveLetter(string root) => root.Length >= 2 && root[1] == ':' ? root[..1].ToUpperInvariant() : "";
}
