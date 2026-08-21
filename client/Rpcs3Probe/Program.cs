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

Console.WriteLine("\ninventory:");
var raw = target.ReadMemory(Inventory.Base, Inventory.Slots * Inventory.Stride);
var any = false;
for (var i = 0; i < Inventory.Slots; i++)
{
    var span = raw.AsSpan(i * Inventory.Stride, Inventory.Stride);
    var id = BinaryPrimitives.ReadUInt16BigEndian(span[..2]);
    var qty = BinaryPrimitives.ReadUInt32BigEndian(span[4..8]);
    if (id == 0 && qty == 0) continue;
    var name = Inventory.KnownItems.TryGetValue(id, out var known) ? known : "?";
    Console.WriteLine($"  slot {i}: id={id} x{qty}  {name}");
    any = true;
}
if (!any) Console.WriteLine("  (empty)");
return 0;
