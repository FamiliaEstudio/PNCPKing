using System.Runtime.InteropServices;
using PNCPKing.Core.Models;

namespace PNCPKing.Infrastructure.Services;

public interface ISystemResourceProbe
{
    SystemResourceSnapshot GetSnapshot();
}

public sealed class SystemResourceProbe : ISystemResourceProbe
{
    private const long Mebibyte = 1024L * 1024;
    private const long Gibibyte = 1024L * Mebibyte;

    public SystemResourceSnapshot GetSnapshot()
    {
        var processors = Environment.ProcessorCount;
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                return CreateSnapshot(
                    SaturatingLong(status.TotalPhysical),
                    SaturatingLong(status.AvailablePhysical),
                    (int)Math.Min(100, status.MemoryLoad),
                    processors);
            }
        }

        var gcLimit = Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        return CreateSnapshot(gcLimit, gcLimit, 0, processors);
    }

    public static SystemResourceSnapshot CreateSnapshot(
        long totalPhysicalMemoryBytes,
        long availablePhysicalMemoryBytes,
        int memoryLoadPercent,
        int logicalProcessors)
    {
        var total = Math.Max(0, totalPhysicalMemoryBytes);
        var available = Math.Clamp(availablePhysicalMemoryBytes, 0, total > 0 ? total : long.MaxValue);
        var freePercent = total <= 0 ? 100d : available * 100d / total;
        var pressure = available < 512 * Mebibyte || freePercent < 5d
            ? SystemResourcePressure.Critical
            : available < 1536 * Mebibyte || freePercent < 15d || logicalProcessors <= 4 || total <= 8 * Gibibyte
                ? SystemResourcePressure.Constrained
                : SystemResourcePressure.Normal;
        return new SystemResourceSnapshot
        {
            TotalPhysicalMemoryBytes = total,
            AvailablePhysicalMemoryBytes = available,
            MemoryLoadPercent = Math.Clamp(memoryLoadPercent, 0, 100),
            LogicalProcessors = Math.Max(1, logicalProcessors),
            Pressure = pressure
        };
    }

    private static long SaturatingLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
