using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace YakuzaDeadSouls.Ps3;

/// <summary>Discovery of the running game process.</summary>
/// <remarks>
/// Process listing deliberately goes through webMAN's web GUI rather than
/// PS3MAPI's <c>PROCESS GETALLPID</c>. Over the HTTP JSON bridge that command
/// is <b>ambiguous</b>: the emitter drops the comma after every hex value, so
/// two live PIDs came back glued together as <c>0x10102000x10003000</c> when
/// the real values were 0x1010200 and 0x1000300. The GUI's option list is
/// unambiguous and carries process names, which <c>PROCESS GETNAME</c> does
/// not - it returns empty even for the XMB.
/// </remarks>
public static partial class Ps3Console
{
    [GeneratedRegex("""<option\s+value="(0x[0-9a-fA-F]+)"\s*/?>([^<]*)""")]
    private static partial Regex OptionPattern();

    public readonly record struct ProcessEntry(uint Pid, string Name)
    {
        /// <summary>The XMB, which is always running and is never the game.</summary>
        public bool IsXmb => Name.Contains("vsh.self", StringComparison.OrdinalIgnoreCase);
        public bool IsGame => !IsXmb && Name.Contains("EBOOT", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<IReadOnlyList<ProcessEntry>> ListAsync(string host)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var html = await http.GetStringAsync($"http://{host}/getmem.ps3mapi");

        var found = new List<ProcessEntry>();
        foreach (Match m in OptionPattern().Matches(html))
        {
            // Entries with plain numeric values are pseudo-targets (LV1 Memory,
            // LV2 Memory, Flash, /dev_hdd0 ...), not processes. Requiring the
            // 0x prefix filters them out.
            var raw = m.Groups[1].Value;
            if (!raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            if (uint.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber,
                              null, out var pid))
                found.Add(new ProcessEntry(pid, m.Groups[2].Value.Trim()));
        }
        return found;
    }

    /// <summary>
    /// The game's PID. Never cache this across launches - it changes every
    /// time the game starts (observed 0x1010200 -> 0x1030200), and a stale PID
    /// reads as <b>zeros rather than erroring</b>, which looks exactly like
    /// "all the addresses moved".
    /// </summary>
    public static async Task<uint?> FindGameAsync(string host)
    {
        var procs = await ListAsync(host);
        foreach (var p in procs)
            if (p.IsGame) return p.Pid;
        return null;
    }
}

/// <summary>
/// Typed access to one process's memory. Every read is <b>big-endian</b> - the
/// PPU is, and reading little-endian yields a plausible wrong answer rather
/// than an error.
/// </summary>
public sealed class GameProcess(Ps3MapiClient client, uint pid, string name = "")
{
    public uint Pid { get; } = pid;
    public string Name { get; } = name;
    public Ps3MapiClient Client { get; } = client;

    public byte[] Read(uint address, int size) => Client.ReadMemory(Pid, address, size);
    public void Write(uint address, ReadOnlySpan<byte> data) => Client.WriteMemory(Pid, address, data);

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

    /// <summary>
    /// Cheapest proof that the PID is really the game and the transport is not
    /// corrupting bytes: the EBOOT's ELF header is mapped at 0x00010000.
    /// </summary>
    public bool LooksLikeGame()
    {
        var head = Read(Addresses.EbootBase, 8);
        return head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E'
               && head[2] == (byte)'L' && head[3] == (byte)'F';
    }
}

/// <summary>A span fetched in one round trip, sliced locally and big-endian.</summary>
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
