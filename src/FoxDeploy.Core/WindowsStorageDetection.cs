using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FoxDeploy.Core;

internal sealed record StorageHardware(bool Verified, bool External, bool Virtual, string BusType)
{
    public static StorageHardware Unknown { get; } = new(false, false, false, "Unknown");
}

internal static class WindowsStorageDetection
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlStorageGetHotplugInfo = 0x002D0C14;
    private const int DeviceDescriptorMinimumSize = 36;

    public static StorageHardware Inspect(string root)
    {
        if (!OperatingSystem.IsWindows() || root.Length < 2 || root[1] != ':' || !char.IsAsciiLetter(root[0]))
            return StorageHardware.Unknown;

        // Zero desired access: query metadata only, without elevation or locking a volume.
        using var volume = CreateFile(@"\\.\" + root[..2], 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (volume.IsInvalid) return StorageHardware.Unknown;

        // STORAGE_PROPERTY_QUERY { StorageDeviceProperty = 0, PropertyStandardQuery = 0 }.
        // A volume forwards the property request to its physical disk. An ambiguous volume
        // or unsupported driver fails closed instead of showing an unverified target.
        var query = new byte[12];
        var header = new byte[8];
        if (!DeviceIoControl(volume, IoctlStorageQueryProperty, query, query.Length, header, header.Length, out var headerLength, IntPtr.Zero) || headerLength < header.Length)
            return StorageHardware.Unknown;
        var requiredSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (requiredSize < DeviceDescriptorMinimumSize || requiredSize > 1024 * 1024)
            return StorageHardware.Unknown;

        var descriptor = new byte[requiredSize];
        if (!DeviceIoControl(volume, IoctlStorageQueryProperty, query, query.Length, descriptor, descriptor.Length, out var descriptorLength, IntPtr.Zero) || descriptorLength < DeviceDescriptorMinimumSize)
            return StorageHardware.Unknown;
        var bus = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28));
        var removableMedia = descriptor[10] != 0;

        // BOOLEAN fields occupy one byte, after the DWORD Size field.
        var hotplug = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(hotplug, (uint)hotplug.Length);
        var hotplugKnown = DeviceIoControl(volume, IoctlStorageGetHotplugInfo, null, 0, hotplug, hotplug.Length, out var hotplugLength, IntPtr.Zero) && hotplugLength >= hotplug.Length;
        var external = removableMedia || IsExternalBus(bus) ||
            (hotplugKnown && (hotplug[4] != 0 || hotplug[5] != 0 || hotplug[6] != 0));
        var virtualDisk = bus is 14 or 15;
        return new StorageHardware(IsInternalBus(bus) && hotplugKnown, external, virtualDisk, BusName(bus));
    }

    private static bool IsInternalBus(uint bus) => bus is 1 or 2 or 3 or 8 or 10 or 11 or 13 or 17 or 18 or 19;
    private static bool IsExternalBus(uint bus) => bus is 4 or 5 or 6 or 7 or 9 or 12 or 20;
    private static string BusName(uint bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE1394", 5 => "SSA", 6 => "Fibre",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD",
        13 => "MMC", 14 => "Virtual", 15 => "FileBackedVirtual", 16 => "StorageSpaces",
        17 => "NVMe", 18 => "SCM", 19 => "UFS", 20 => "NVMeOf", _ => "Unknown"
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[]? input, int inputSize,
        [Out] byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);
}
