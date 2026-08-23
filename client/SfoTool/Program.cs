using YakuzaDeadSouls.Saves;

if (args.Length < 1) { Usage(); return 1; }

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "dump": Dump(args[1]); break;
        case "set": Set(args[1], args[2], args[3]); break;
        case "retarget": Retarget(args[1], args[2], args[3]); break;
        default: Usage(); return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"error: {ex.Message}");
    return 1;
}
return 0;

static string SfoIn(string path) =>
    Directory.Exists(path) ? Path.Combine(path, "PARAM.SFO") : path;

static void Dump(string path)
{
    var sfo = Sfo.Load(SfoIn(path));
    foreach (var e in sfo.Entries)
    {
        var value = sfo.Read(e).Replace("\n", "\\n");
        Console.WriteLine($"  {e.Key,-20} {value}");
        Console.WriteLine($"  {"",-20} fmt=0x{e.Format:X4} len={e.Length} max={e.MaxLength}");
    }
}

static void Set(string path, string key, string value)
{
    var file = SfoIn(path);
    var sfo = Sfo.Load(file);
    Console.WriteLine($"  {key}: {sfo.ReadString(key)} -> {value}");
    sfo.SetString(key, value);
    sfo.Save(file);
}

static void Retarget(string folder, string newName, string accountId)
{
    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
    if (accountId.Length != 16) throw new ArgumentException("ACCOUNT_ID must be 16 hex characters");

    var file = Path.Combine(folder, "PARAM.SFO");
    var sfo = Sfo.Load(file);

    Console.WriteLine($"  {Path.GetFileName(folder)}");
    Console.WriteLine($"    SAVEDATA_DIRECTORY  {sfo.ReadString("SAVEDATA_DIRECTORY")} -> {newName}");
    sfo.SetString("SAVEDATA_DIRECTORY", newName);

    if (sfo.Has("ACCOUNT_ID"))
    {
        Console.WriteLine($"    ACCOUNT_ID          {sfo.ReadString("ACCOUNT_ID")} -> {accountId}");
        sfo.SetString("ACCOUNT_ID", accountId);
    }
    sfo.Save(file);

    var parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
    var target = Path.Combine(parent, newName);
    if (Path.GetFileName(Path.GetFullPath(folder)) == newName)
    {
        Console.WriteLine("    folder already named correctly");
    }
    else if (Directory.Exists(target))
    {
        Console.WriteLine($"    NOT renamed: {newName} already exists here");
    }
    else
    {
        Directory.Move(Path.GetFullPath(folder), target);
        Console.WriteLine($"    folder renamed to {newName}");
    }

    Console.WriteLine("\n  PARAM.PFD still carries the old signature. Resign on the console");
    Console.WriteLine("  (Apollo Save Tool) before the game will accept it.");
}

static void Usage() => Console.WriteLine("""
    ydssfo <command>

      dump <PARAM.SFO|folder>              print every key
      set <PARAM.SFO|folder> <KEY> <value> rewrite one string field
      retarget <folder> <newName> <accountId>
                                           point a save at a new savedata
                                           directory and account, and rename
                                           the folder to match

    Values are patched in place, so a replacement must fit the space the
    original field reserved.
    """);
