using System.Buffers.Binary;

namespace YakuzaDeadSouls.Ps3;

// The EBOOT is mapped with its ELF header at 0x00010000, so the segment
// layout can be read straight out of the live process.
public static class ElfMap
{
    public readonly record struct Segment(
        uint Type, uint Flags, uint VirtualAddress, ulong MemorySize, ulong FileSize)
    {
        public bool IsLoad => Type == 1;
        public bool Executable => (Flags & 1) != 0;
        public bool Writable => (Flags & 2) != 0;
        public uint End => VirtualAddress + (uint)MemorySize;

        public string FlagString =>
            $"{((Flags & 4) != 0 ? "R" : "")}{(Writable ? "W" : "")}{(Executable ? "X" : "")}";
    }

    public sealed record Layout(uint Entry, IReadOnlyList<Segment> Segments)
    {
        public Segment? Code => Segments.FirstOrDefault(s => s.IsLoad && s.Executable && s.MemorySize > 0);
        public Segment? Data => Segments.FirstOrDefault(
            s => s.IsLoad && s.Writable && !s.Executable && s.MemorySize > 0);
    }

    public static Layout Read(GameProcess game)
    {
        var header = game.Read(Addresses.EbootBase, 64);
        if (header[0] != 0x7F || header[1] != 'E' || header[2] != 'L' || header[3] != 'F')
            throw new Ps3Exception(
                $"no ELF magic at {Addresses.EbootBase:X8} - got {Convert.ToHexString(header[..8])}");

        // ELF64 big-endian. Entry points at a function descriptor in the data
        // segment, not at code - normal for the PPC64 ELFv1 ABI.
        var entry = (uint)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24, 8));
        var phoff = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(32, 8));
        var phentsize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(54, 2));
        var phnum = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(56, 2));

        var raw = game.Read(Addresses.EbootBase + (uint)phoff, phnum * phentsize);
        var segments = new List<Segment>(phnum);
        for (var i = 0; i < phnum; i++)
        {
            var s = raw.AsSpan(i * phentsize, phentsize);
            segments.Add(new Segment(
                BinaryPrimitives.ReadUInt32BigEndian(s[..4]),
                BinaryPrimitives.ReadUInt32BigEndian(s.Slice(4, 4)),
                (uint)BinaryPrimitives.ReadUInt64BigEndian(s.Slice(16, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(s.Slice(40, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(s.Slice(32, 8))));
        }
        return new Layout(entry, segments);
    }
}
