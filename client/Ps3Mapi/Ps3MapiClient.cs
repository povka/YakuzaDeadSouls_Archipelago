using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YakuzaDeadSouls.Ps3;

/// <summary>
/// PS3MAPI over the binary TCP server on port 7887. Reads and writes the
/// memory of a process on a jailbroken PS3 across the network.
/// </summary>
/// <remarks>
/// <para>
/// Measured against a Slim CECH-25xx running Evilnat 4.93, webMAN MOD 1.47.48,
/// PS3MAPI server 0x125: 64 KB per read, ~61 ms per round trip, ~1 MB/sec. The
/// round trip dominates completely - a 64 KB read costs the same as a 4-byte
/// one - so read one span and slice it rather than issuing many small reads.
/// </para>
/// <para>
/// The server is <b>off by default</b>. It is enabled in webMAN's <i>Setup</i>
/// page (not the PS3MAPI page): the VSH MENU section, on the DEL CFW SYSCALLS
/// line, a dropdown named <c>sc8</c>. It only binds the port at boot, so the
/// console must be rebooted afterwards.
/// </para>
/// <para>
/// <b>Never</b> read memory through webMAN's <c>/ps3mapi.ps3?MEMORY GET</c>
/// JSON bridge. On 1.47.48 it zeroes the high nibble of every byte returned,
/// producing data that looks structured and is wrong. The tell is that every
/// byte comes back &lt;= 0x0F.
/// </para>
/// </remarks>
public sealed class Ps3MapiClient : IDisposable
{
    public const int DefaultPort = 7887;

    /// <summary>No cap was found; this is a sane transfer unit.</summary>
    public const int MaxRead = 65536;

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _control;
    private NetworkStream? _stream;
    private StringBuilder _buffer = new();
    private bool _binary;

    public Ps3MapiClient(string host, int port = DefaultPort)
    {
        _host = host;
        _port = port;
    }

    public bool Connected => _control?.Connected == true;

    /// <summary>Is anything listening on the PS3MAPI port?</summary>
    public static bool IsAvailable(string host, int port = DefaultPort, int timeoutMs = 1500)
    {
        try
        {
            using var probe = new TcpClient();
            return probe.ConnectAsync(host, port).Wait(timeoutMs) && probe.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Connect()
    {
        Dispose();
        _control = new TcpClient();
        if (!_control.ConnectAsync(_host, _port).Wait(5000))
            throw new Ps3Exception($"timed out connecting to {_host}:{_port}");

        _stream = _control.GetStream();
        _stream.ReadTimeout = 15000;
        _stream.WriteTimeout = 15000;
        _buffer = new StringBuilder();
        _binary = false;

        // Greeting is 220, then 230 once the server will take commands. Read
        // until the 230 rather than assuming a line count - builds differ in
        // how much banner they send.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var (code, text) = ReadResponse();
            if (code == 230) return;
            if (code >= 400) throw new Ps3Exception($"console refused connection: {code} {text}");
        }
        throw new Ps3Exception("no 230 ready response from PS3MAPI");
    }

    public void Dispose()
    {
        if (_control is not null)
        {
            try { Send("DISCONNECT"); } catch (Exception) { /* closing anyway */ }
            try { _control.Close(); } catch (Exception) { /* closing anyway */ }
        }
        _control = null;
        _stream = null;
    }

    // -- control channel ----------------------------------------------------

    private void Send(string line)
    {
        if (_stream is null) throw new Ps3Exception("not connected");
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        _stream.Write(bytes, 0, bytes.Length);
    }

    private string ReadLine()
    {
        if (_stream is null) throw new Ps3Exception("not connected");
        var chunk = new byte[512];
        while (true)
        {
            var text = _buffer.ToString();
            var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                _buffer = new StringBuilder(text[(idx + 2)..]);
                return text[..idx];
            }
            var read = _stream.Read(chunk, 0, chunk.Length);
            if (read <= 0) throw new Ps3Exception("control connection closed by console");
            _buffer.Append(Encoding.ASCII.GetString(chunk, 0, read));
        }
    }

    private (int Code, string Text) ReadResponse()
    {
        var line = ReadLine();
        if (line.Length < 3 || !int.TryParse(line[..3], out var code))
            return (0, line);
        return (code, line.Length > 4 ? line[4..] : string.Empty);
    }

    /// <summary>Read responses until a final one, skipping continuation lines.</summary>
    private (int Code, string Text) Await(params int[] accept)
    {
        while (true)
        {
            var (code, text) = ReadResponse();
            if (code == 0) continue;
            if (code >= 400) throw new Ps3Exception($"{code} {text}");
            if (accept.Length == 0 || accept.Contains(code) || (code >= 200 && code < 300))
                return (code, text);
        }
    }

    public string Command(string line)
    {
        Send(line);
        return Await().Text;
    }

    // -- data channel -------------------------------------------------------

    private void EnsureBinary()
    {
        if (_binary) return;
        Command("TYPE I");
        _binary = true;
    }

    /// <summary>Open the PASV data connection a memory transfer needs.</summary>
    private TcpClient OpenDataConnection()
    {
        Send("PASV");
        var (_, text) = Await(227);

        var open = text.LastIndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close < 0 || close <= open)
            throw new Ps3Exception($"cannot parse PASV response: {text}");

        var parts = text[(open + 1)..close].Split(',');
        if (parts.Length != 6)
            throw new Ps3Exception($"cannot parse PASV response: {text}");

        var nums = parts.Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
        var host = string.Join('.', nums.Take(4));
        // Some builds report 0.0.0.0; fall back to the control host.
        if (host == "0.0.0.0") host = _host;
        var port = nums[4] * 256 + nums[5];

        var data = new TcpClient();
        if (!data.ConnectAsync(IPAddress.Parse(host), port).Wait(5000))
            throw new Ps3Exception($"timed out opening PASV data connection to {host}:{port}");
        data.GetStream().ReadTimeout = 15000;
        data.GetStream().WriteTimeout = 15000;
        return data;
    }

    // -- memory -------------------------------------------------------------

    /// <summary>Read process memory. Splits requests larger than MaxRead.</summary>
    public byte[] ReadMemory(uint pid, uint address, int size)
    {
        if (size <= 0) return [];
        var result = new byte[size];
        var written = 0;

        while (written < size)
        {
            var want = Math.Min(MaxRead, size - written);
            var at = address + (uint)written;

            using var data = OpenDataConnection();
            Send($"MEMORY GET {pid} {at:X8} {want}");
            Await(125, 150);

            var stream = data.GetStream();
            var got = 0;
            while (got < want)
            {
                var n = stream.Read(result, written + got, want - got);
                if (n <= 0) break;
                got += n;
            }
            data.Close();
            Await(226, 250);

            if (got != want)
                throw new Ps3Exception($"short read at {at:X8}: {got} of {want}");
            written += want;
        }
        return result;
    }

    /// <summary>Write process memory.</summary>
    public void WriteMemory(uint pid, uint address, ReadOnlySpan<byte> payload)
    {
        EnsureBinary();
        using var data = OpenDataConnection();
        Send($"MEMORY SET {pid} {address:X8}");
        Await(125, 150, 350);
        data.GetStream().Write(payload);
        data.Close();
        Await(226, 250);
    }

    /// <summary>Load an SPRX into a running process - the stage-2 injection path.</summary>
    public string LoadModule(uint pid, string path) => Command($"MODULE LOAD {pid} {path}");
}

public class Ps3Exception : Exception
{
    public Ps3Exception(string message) : base(message) { }
}
