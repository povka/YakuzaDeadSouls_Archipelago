using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YakuzaDeadSouls.Ps3;

public sealed partial class Rpcs3Target : IMemoryTarget, IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessVmWrite = 0x0020;
    private const int ProcessVmOperation = 0x0008;
    private const int ProcessQueryInformation = 0x0400;

    private const int MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(
        IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(
        IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualQueryEx(
        IntPtr handle, IntPtr address, out MemoryBasicInformation info, IntPtr length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public int Alignment1;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public int Alignment2;
    }

    private readonly IntPtr _handle;

    public int ProcessId { get; }
    public ulong GuestBase { get; }

    private Rpcs3Target(IntPtr handle, int pid, ulong guestBase)
    {
        _handle = handle;
        ProcessId = pid;
        GuestBase = guestBase;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }

    public static Process? FindProcess() =>
        Process.GetProcessesByName("rpcs3").FirstOrDefault();

    public static Rpcs3Target? Attach(int? pid = null)
    {
        var process = pid is null ? FindProcess() : Process.GetProcessById(pid.Value);
        if (process is null) return null;

        var handle = OpenProcess(
            ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation,
            false, process.Id);
        if (handle == IntPtr.Zero) return null;

        foreach (var candidate in CandidateBases(handle))
        {
            if (!LooksLikeEboot(handle, candidate)) continue;
            return new Rpcs3Target(handle, process.Id, candidate);
        }

        CloseHandle(handle);
        return null;
    }

    private static bool LooksLikeEboot(IntPtr handle, ulong guestBase)
    {
        var buffer = new byte[4];
        var at = (IntPtr)(guestBase + Addresses.EbootBase);
        if (!ReadProcessMemory(handle, at, buffer, 4, out var read) || read != 4) return false;
        return buffer[0] == 0x7F && buffer[1] == (byte)'E'
               && buffer[2] == (byte)'L' && buffer[3] == (byte)'F';
    }

    private static IEnumerable<ulong> CandidateBases(IntPtr handle)
    {
        // RPCS3's usual guest base on 64-bit Windows.
        yield return 0x300000000UL;

        IntPtr address = 0;
        var size = (IntPtr)Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQueryEx(handle, address, out var info, size) != IntPtr.Zero)
        {
            var regionSize = (ulong)info.RegionSize;
            if (regionSize == 0) break;

            var usable = info.State == MemCommit
                         && (info.Protect & PageNoAccess) == 0
                         && (info.Protect & PageGuard) == 0;

            if (usable && regionSize >= 0x10000)
                yield return (ulong)info.BaseAddress;

            var next = (ulong)info.BaseAddress + regionSize;
            if (next > long.MaxValue) break;
            address = (IntPtr)next;
        }
    }

    public IEnumerable<(uint Guest, ulong Size, uint Protect)> MappedGuestRegions()
    {
        var address = (IntPtr)GuestBase;
        var limit = GuestBase + 0x100000000UL;
        var size = (IntPtr)Marshal.SizeOf<MemoryBasicInformation>();

        while ((ulong)address < limit &&
               VirtualQueryEx(_handle, address, out var info, size) != IntPtr.Zero)
        {
            var regionSize = (ulong)info.RegionSize;
            if (regionSize == 0) break;

            var readable = info.State == MemCommit
                           && (info.Protect & PageNoAccess) == 0
                           && (info.Protect & PageGuard) == 0;
            if (readable)
                yield return ((uint)((ulong)info.BaseAddress - GuestBase), regionSize, info.Protect);

            var next = (ulong)info.BaseAddress + regionSize;
            if (next > long.MaxValue) break;
            address = (IntPtr)next;
        }
    }

    public byte[] ReadMemory(uint address, int size)
    {
        var buffer = new byte[size];
        if (!ReadProcessMemory(_handle, (IntPtr)(GuestBase + address), buffer, size, out var read)
            || (int)read != size)
            throw new Ps3Exception($"RPCS3 read failed at guest {address:X8} ({read} of {size})");
        return buffer;
    }

    public void WriteMemory(uint address, ReadOnlySpan<byte> payload)
    {
        var buffer = payload.ToArray();
        if (!WriteProcessMemory(_handle, (IntPtr)(GuestBase + address), buffer,
                                buffer.Length, out var written)
            || (int)written != buffer.Length)
            throw new Ps3Exception($"RPCS3 write failed at guest {address:X8}");
    }
}
