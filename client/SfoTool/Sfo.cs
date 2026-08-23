using System.Buffers.Binary;
using System.Text;

namespace YakuzaDeadSouls.Saves;

public sealed class Sfo
{
    public const ushort FormatUtf8Special = 0x0004;
    public const ushort FormatUtf8 = 0x0204;
    public const ushort FormatUInt32 = 0x0404;

    public readonly record struct Entry(
        string Key, ushort Format, uint Length, uint MaxLength, uint DataOffset, int IndexAt);

    private readonly byte[] _raw;
    private readonly uint _dataTable;
    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries => _entries;

    private Sfo(byte[] raw)
    {
        _raw = raw;
        if (raw.Length < 20 || raw[0] != 0 || raw[1] != 'P' || raw[2] != 'S' || raw[3] != 'F')
            throw new InvalidDataException("not a PARAM.SFO");

        var keyTable = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
        _dataTable = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16));

        for (var i = 0; i < count; i++)
        {
            var at = 20 + i * 16;
            var keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at));
            _entries.Add(new Entry(
                ReadKey(keyTable + keyOffset),
                BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 2)),
                BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at + 12)),
                at));
        }
    }

    public static Sfo Load(string path) => new(File.ReadAllBytes(path));

    public void Save(string path) => File.WriteAllBytes(path, _raw);

    private string ReadKey(uint at)
    {
        var end = (int)at;
        while (end < _raw.Length && _raw[end] != 0) end++;
        return Encoding.UTF8.GetString(_raw, (int)at, end - (int)at);
    }

    public Entry Find(string key)
    {
        foreach (var e in _entries)
            if (e.Key == key) return e;
        throw new KeyNotFoundException($"no '{key}' in this PARAM.SFO");
    }

    public bool Has(string key) => _entries.Any(e => e.Key == key);

    public string Read(Entry e)
    {
        var at = (int)(_dataTable + e.DataOffset);
        if (e.Format == FormatUInt32)
            return BinaryPrimitives.ReadUInt32LittleEndian(_raw.AsSpan(at)).ToString();
        return Encoding.UTF8.GetString(_raw, at, (int)e.Length).TrimEnd('\0');
    }

    public string ReadString(string key) => Read(Find(key));

    public void SetString(string key, string value)
    {
        var entry = Find(key);
        if (entry.Format == FormatUInt32)
            throw new InvalidOperationException($"'{key}' is an integer field, not a string");

        var bytes = Encoding.UTF8.GetBytes(value);
        var needed = entry.Format == FormatUtf8 ? bytes.Length + 1 : bytes.Length;

        if (needed > entry.MaxLength)
            throw new InvalidOperationException($"'{key}' cannot be set to '{value}' because it exceeds the maximum length of {entry.MaxLength} bytes.");

        var at = (int)(_dataTable + entry.DataOffset);
        Array.Clear(_raw, at, (int)entry.MaxLength);
        bytes.CopyTo(_raw, at);
        BinaryPrimitives.WriteUInt32LittleEndian(_raw.AsSpan(entry.IndexAt + 4), (uint)needed);
    }
}
