using System.Text;

namespace YakuzaDeadSouls.Ps3;

public static class GameText
{
    private static readonly Encoding Sjis;

    static GameText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Sjis = Encoding.GetEncoding(932);
    }

    private static readonly Dictionary<byte, char> Glyphs = new()
    {
        [0xB1] = 'à',
        [0xB2] = 'é',
        [0xB3] = '°',
        [0xB4] = 'ê',
    };

    private static bool IsLeadByte(byte b) =>
        b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xEF;

    // Must walk the span exactly as Decode does. A per-byte check reports the
    // trail byte of a double-byte pair as unmapped.
    public static int FirstUnmappedByte(ReadOnlySpan<byte> raw)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b < 0x20) return b;
            if (b < 0x80) continue;
            if (IsLeadByte(b) && i + 1 < raw.Length) { i++; continue; }
            if (!Glyphs.ContainsKey(b)) return b;
        }
        return -1;
    }

    public static string Decode(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b < 0x80)
            {
                sb.Append((char)b);
            }
            else if (IsLeadByte(b) && i + 1 < raw.Length)
            {
                sb.Append(Sjis.GetString(raw.Slice(i, 2)));
                i++;
            }
            else if (Glyphs.TryGetValue(b, out var glyph))
            {
                sb.Append(glyph);
            }
            else
            {
                sb.Append('?');
            }
        }
        return sb.ToString();
    }
}
