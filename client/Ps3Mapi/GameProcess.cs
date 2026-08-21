using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace YakuzaDeadSouls.Ps3;

public static partial class Ps3Console
{
    [GeneratedRegex("""<option\s+value="(0x[0-9a-fA-F]+)"\s*/?>([^<]*)""")]
    private static partial Regex OptionPattern();

    public readonly record struct ProcessEntry(uint Pid, string Name)
    {
        public bool IsXmb => Name.Contains("vsh.self", StringComparison.OrdinalIgnoreCase);
        public bool IsGame => !IsXmb && Name.Contains("EBOOT", StringComparison.OrdinalIgnoreCase);
    }

    // Scraped from webMAN's GUI rather than PROCESS GETALLPID: that command's
    // JSON glues adjacent PIDs together, and PROCESS GETNAME returns empty.
    public static async Task<IReadOnlyList<ProcessEntry>> ListAsync(string host)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var html = await http.GetStringAsync($"http://{host}/getmem.ps3mapi");

        var found = new List<ProcessEntry>();
        foreach (Match m in OptionPattern().Matches(html))
        {
            // Plain numeric values are pseudo-targets (LV1/LV2 Memory, Flash,
            // /dev_hdd0), not processes.
            var raw = m.Groups[1].Value;
            if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            if (uint.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber,
                              null, out var pid))
                found.Add(new ProcessEntry(pid, m.Groups[2].Value.Trim()));
        }
        return found;
    }

    // Never cache the result: the PID changes every launch, and a stale one
    // reads as zeros rather than erroring.
    public static async Task<uint?> FindGameAsync(string host)
    {
        var procs = await ListAsync(host);
        foreach (var p in procs)
            if (p.IsGame) return p.Pid;
        return null;
    }
}

public sealed class GameProcess(Ps3MapiClient client, uint pid, string name = "") : IMemoryTarget
{
    public uint Pid { get; } = pid;
    public string Name { get; } = name;
    public Ps3MapiClient Client { get; } = client;

    public byte[] Read(uint address, int size) => Client.ReadMemory(Pid, address, size);
    public void Write(uint address, ReadOnlySpan<byte> data) => Client.WriteMemory(Pid, address, data);

    byte[] IMemoryTarget.ReadMemory(uint address, int size) => Read(address, size);
    void IMemoryTarget.WriteMemory(uint address, ReadOnlySpan<byte> payload) => Write(address, payload);

    public byte ReadU8(uint address) => Read(address, 1)[0];
    public ushort ReadU16(uint address) => BinaryPrimitives.ReadUInt16BigEndian(Read(address, 2));
    public uint ReadU32(uint address) => BinaryPrimitives.ReadUInt32BigEndian(Read(address, 4));
    public ulong ReadU64(uint address) => BinaryPrimitives.ReadUInt64BigEndian(Read(address, 8));
    public float ReadF32(uint address) => BinaryPrimitives.ReadSingleBigEndian(Read(address, 4));

    public void WriteU8(uint address, byte value) => Write(address, [value]);

    public void WriteU16(uint address, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        Write(address, buf);
    }

    public void WriteU32(uint address, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        Write(address, buf);
    }

    public void WriteF32(uint address, float value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buf, value);
        Write(address, buf);
    }

    public string ReadString(uint address, int size = 64)
    {
        var raw = Read(address, size);
        var end = Array.IndexOf(raw, (byte)0);
        return Encoding.UTF8.GetString(raw, 0, end < 0 ? raw.Length : end);
    }

    // Catches both a wrong PID (reads zeros) and a corrupting transport.
    public bool LooksLikeGame()
    {
        var head = Read(Addresses.EbootBase, 8);
        return head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E'
               && head[2] == (byte)'L' && head[3] == (byte)'F';
    }
}

public readonly struct MemoryBlock(uint baseAddress, byte[] data)
{
    public uint Base { get; } = baseAddress;
    public byte[] Data { get; } = data;

    private ReadOnlySpan<byte> At(uint address, int size)
    {
        var offset = (int)(address - Base);
        if (offset < 0 || offset + size > Data.Length)
            throw new ArgumentOutOfRangeException(nameof(address),
                $"{address:X8} outside block {Base:X8}+{Data.Length}");
        return Data.AsSpan(offset, size);
    }

    public byte U8(uint address) => At(address, 1)[0];
    public ushort U16(uint address) => BinaryPrimitives.ReadUInt16BigEndian(At(address, 2));
    public uint U32(uint address) => BinaryPrimitives.ReadUInt32BigEndian(At(address, 4));
    public float F32(uint address) => BinaryPrimitives.ReadSingleBigEndian(At(address, 4));
}
