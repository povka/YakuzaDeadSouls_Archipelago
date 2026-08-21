namespace YakuzaDeadSouls.Ps3;

public interface IMemoryTarget
{
    byte[] ReadMemory(uint address, int size);
    void WriteMemory(uint address, ReadOnlySpan<byte> payload);
}
