namespace YakuzaDeadSouls.Ps3;

public static class Ps3Config
{
    public const string EnvironmentVariable = "YDS_PS3_HOST";
    public const string FileName = "console.txt";

    public static string? Resolve(string? explicitHost = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitHost))
            return explicitHost.Trim();

        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        foreach (var dir in SearchPaths())
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) continue;
            var line = File.ReadLines(path)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (!string.IsNullOrWhiteSpace(line)) return line;
        }
        return null;
    }

    public static string Require(string? explicitHost = null) =>
        Resolve(explicitHost) ?? throw new Ps3Exception(HelpText);

    public static string HelpText =>
        $"""
         No PS3 address configured. Set one of:

           1. pass it as an argument     ydsprobe 192.168.1.50
           2. set an env var             setx {EnvironmentVariable} 192.168.1.50
           3. write a file               echo 192.168.1.50 > {FileName}

         {FileName} is searched next to the executable and in the repo root,
         and is git-ignored so it stays a local setting.

         The address is your console's IP on the LAN. webMAN shows it on the
         XMB, or check your router's client list.
         """;

    private static IEnumerable<string> SearchPaths()
    {
        var dir = AppContext.BaseDirectory;
        yield return dir;
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (dir is not null) yield return dir;
        }
    }
}
