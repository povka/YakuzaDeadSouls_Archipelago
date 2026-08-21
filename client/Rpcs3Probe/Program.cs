using System.Buffers.Binary;
using YakuzaDeadSouls.Ps3;

var proc = Rpcs3Target.FindProcess();
if (proc is null) { Console.WriteLine("RPCS3 is not running."); return 1; }
Console.WriteLine($"rpcs3 pid {proc.Id}, {proc.WorkingSet64 / 1024.0 / 1024 / 1024:F2} GB resident");

Console.WriteLine("locating the guest address space ...");
using var target = Rpcs3Target.Attach();
if (target is null)
{
    Console.WriteLine("FAIL: could not find the guest base.");
    Console.WriteLine("Is a game actually loaded and running in RPCS3?");
    return 1;
}
Console.WriteLine($"guest base 0x{target.GuestBase:X}  (guest 0x{Addresses.EbootBase:X8} -> host 0x{target.GuestBase + Addresses.EbootBase:X})");

var head = target.ReadMemory(Addresses.EbootBase, 20);
Console.WriteLine($"\nELF header: {Convert.ToHexString(head[..8])}");
Console.WriteLine($"  class {head[4]} (2=ELF64), data {head[5]} (2=big-endian), " +
                  $"machine {BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18, 2))} (21=PPC64)");

uint U32(uint a) => BinaryPrimitives.ReadUInt32BigEndian(target.ReadMemory(a, 4));
ushort U16(uint a) => BinaryPrimitives.ReadUInt16BigEndian(target.ReadMemory(a, 2));

Console.WriteLine("\nhardware addresses, read from the emulator:");
Console.WriteLine($"  money  0x{Addresses.Money:X8}  {U32(Addresses.Money),8}");
Console.WriteLine($"  hp     0x{Addresses.HealthCurrent:X8}  {U16(Addresses.HealthCurrent),8} / {U16(Addresses.HealthMax)}");
Console.WriteLine($"  exp    0x{Addresses.Exp:X8}  {U32(Addresses.Exp),8}");

Console.WriteLine("\ninventory region, 8-byte records from 0x01534DE4:");
const int show = 40;
var raw = target.ReadMemory(Inventory.Base, show * Inventory.Stride);
for (var i = 0; i < show; i++)
{
    var span = raw.AsSpan(i * Inventory.Stride, Inventory.Stride);
    var id = BinaryPrimitives.ReadUInt16BigEndian(span[..2]);
    var pad = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);
    var qty = BinaryPrimitives.ReadUInt32BigEndian(span[4..8]);
    var addr = Inventory.Base + (uint)(i * Inventory.Stride);
    Console.WriteLine($"  {i,2}  0x{addr:X8}  id={id,-5} pad={pad,-5} qty={qty,-10}  {Convert.ToHexString(span)}");
}

// Slot 12 is the first locked slot (id=1). Clearing it to zeros should turn
// it into an unlocked empty slot if the marker is what gates availability.
var lockedSlot = Inventory.Base + 12 * Inventory.Stride;
Console.WriteLine($"\nunlock test on slot 12 (0x{lockedSlot:X8}):");
Console.WriteLine($"  before  {Convert.ToHexString(target.ReadMemory(lockedSlot, 8))}");
target.WriteMemory(lockedSlot, new byte[8]);
Console.WriteLine($"  after   {Convert.ToHexString(target.ReadMemory(lockedSlot, 8))}  (zeroed)");
Console.WriteLine("  -> check the in-game inventory: 13 slots usable now?");

foreach (var (label, where, size) in new[]
{
    ("exp mirror (inert, in RW data)", Addresses.ExpMirror, 4),
})
{
    Console.WriteLine($"\nwrite test - {label} at 0x{where:X8}:");
    var before = target.ReadMemory(where, size);
    Console.WriteLine($"  before   {Convert.ToHexString(before)}");
    var pattern = Convert.FromHexString("DEADBEEFCAFEBABE")[..size];
    try
    {
        target.WriteMemory(where, pattern);
        var readback = target.ReadMemory(where, size);
        Console.WriteLine($"  readback {Convert.ToHexString(readback)}");
        Console.WriteLine($"  {(readback.AsSpan().SequenceEqual(pattern) ? "MATCH - writes work here" : "MISMATCH")}");
        target.WriteMemory(where, before);
        Console.WriteLine($"  restored {Convert.ToHexString(target.ReadMemory(where, size))}");
    }
    catch (Ps3Exception ex)
    {
        Console.WriteLine($"  WRITE REFUSED: {ex.Message}");
    }
}
return 0;
