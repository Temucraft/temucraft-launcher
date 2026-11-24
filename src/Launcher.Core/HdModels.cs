using System.Text;

namespace Launcher.Core;

/// <summary>
/// Character models: HD per race. The server sends the HD models cut into one archive per race
/// (Data\TemucraftHD\hd-&lt;part&gt;.MPQ, never loaded by the game as they are). From the races the player wants in HD
/// this builds:
///   Data\patch-K.MPQ   the HD models of those races (each race is all HD or all Classic, nothing is mixed)
///   Data\patch-_.MPQ   the skin, hair, emote and NPC face data (6 DBC files): HD rows for the HD races, Classic rows
///                      for the rest. "_" sorts after every letter, so it outranks all other patches: the HD data
///                      can't be hidden by another patch's copy of the same files.
/// No HD race: both files are removed. The archives' file data is copied as it is (compressed, not encrypted).
/// </summary>
public static class HdModels
{
    /// <summary>The parts: a player race each, plus the NPC-only races. "common" always comes with any HD race.</summary>
    public static readonly string[] Parts = ["human", "orc", "dwarf", "nightelf", "scourge", "tauren", "gnome", "troll",
                                             "goblin", "bloodelf", "draenei", "npcs"];

    public static readonly IReadOnlyDictionary<string, string> PartNames = new Dictionary<string, string>
    {
        ["human"] = "Human", ["orc"] = "Orc", ["dwarf"] = "Dwarf", ["nightelf"] = "Night Elf", ["scourge"] = "Undead",
        ["tauren"] = "Tauren", ["gnome"] = "Gnome", ["troll"] = "Troll", ["goblin"] = "Goblin", ["bloodelf"] = "Blood Elf",
        ["draenei"] = "Draenei", ["npcs"] = "Other NPCs",
    };

    static readonly Dictionary<int, string> RacePart = new()
    {
        [1] = "human", [2] = "orc", [3] = "dwarf", [4] = "nightelf", [5] = "scourge", [6] = "tauren", [7] = "gnome",
        [8] = "troll", [9] = "goblin", [10] = "bloodelf", [11] = "draenei",
    };

    /// <summary>The DBC files merged per race: race column, string columns (-1: no race column, taken whole), and the
    /// columns the rows must be sorted by (empty: by id).</summary>
    /// <remarks>Two tables need their rows in order, not by id. The 3.3.5a client sizes its skin and hair lists from
    /// runs of CharSections rows (BuildComponentArray, Wow.exe 0x004F3DD0): with the rows of one race, sex and section
    /// split up, a list gets the size of its last run and the next pass writes past it (heap damage that crashes
    /// later, anywhere). So CharSections goes by race, sex, section, variation, color. EmotesTextSound goes by emote
    /// (SI2::InitEmoteSounds 0x004CECB0 keeps pointers into a list that moves as it grows, and a row for an emote
    /// made earlier is then written through an old pointer).</remarks>
    static readonly (string Name, int Race, int[] Strings, int[] Order)[] Dbcs =
    [
        ("CharSections", 1, [4, 5, 6], [1, 2, 3, 8, 9]),
        ("CharHairGeosets", 1, [], []),
        ("CharacterFacialHairStyles", 0, [], []),
        ("CreatureDisplayInfoExtra", 1, [20], []),
        ("EmotesTextSound", 2, [], [1, 2, 3]),
        ("HelmetGeosetVisData", -1, [], []),
    ];

    public const string PieceFolder = "Data/TemucraftHD";
    /// <summary>The files this builds (never in the manifest, never "untracked").</summary>
    public static readonly string[] Generated = ["Data/patch-K.MPQ", "Data/patch-_.MPQ"];
    public static string PiecePath(string wowFolder, string part) => Path.Combine(wowFolder, "Data", "TemucraftHD", $"hd-{part}.MPQ");
    static string ModelsPath(string wowFolder) => Path.Combine(wowFolder, "Data", "patch-K.MPQ");
    static string DataPath(string wowFolder) => Path.Combine(wowFolder, "Data", "patch-_.MPQ");
    static string StampPath(string wowFolder) => Path.Combine(wowFolder, "Data", "TemucraftHD", "built.txt");

    /// <summary>The part an NPC/character race id belongs to; null for races the HD pack doesn't touch (custom races).</summary>
    static string? PartOf(int race) => RacePart.TryGetValue(race, out string? p) ? p : race >= 100 ? "npcs" : null;

    /// <summary>What a build from these pieces would be: changes when the pieces or the races change.</summary>
    /// <remarks>"rows2|" marks a patch-_ with its rows in the client's order (see Dbcs); an older build is made again.</remarks>
    static string Stamp(string wowFolder, IReadOnlyCollection<string> hd) =>
        "rows2|" + string.Join(",", hd.OrderBy(p => p)) + "|" + string.Join(",", hd.Append("common").OrderBy(p => p)
            .Select(p => new FileInfo(PiecePath(wowFolder, p))).Select(f => f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-"));

    /// <summary>True when the build for these races is already in place.</summary>
    public static bool UpToDate(string wowFolder, IReadOnlyCollection<string> hd)
    {
        if (hd.Count == 0) return !File.Exists(ModelsPath(wowFolder)) && !File.Exists(DataPath(wowFolder));
        string stamp = StampPath(wowFolder);
        return File.Exists(stamp) && File.Exists(ModelsPath(wowFolder)) && File.Exists(DataPath(wowFolder))
               && File.ReadAllText(stamp) == Stamp(wowFolder, hd);
    }

    /// <summary>The pieces present for HD (common plus at least one race).</summary>
    public static bool HavePieces(string wowFolder) => File.Exists(PiecePath(wowFolder, "common"));

    /// <summary>Builds (or removes) patch-K and patch-_ for the races in <paramref name="hd"/>. Returns a line for the log,
    /// or null when nothing changed.</summary>
    public static string? Sync(string wowFolder, IReadOnlyCollection<string> hd, Action<string>? status = null)
    {
        hd = [.. hd.Where(p => Parts.Contains(p) && File.Exists(PiecePath(wowFolder, p)))];
        if (UpToDate(wowFolder, hd)) return null;
        if (hd.Count == 0 || !HavePieces(wowFolder))
        {
            foreach (string f in new[] { ModelsPath(wowFolder), DataPath(wowFolder), StampPath(wowFolder) })
                if (File.Exists(f)) File.Delete(f);
            return "HD character models: all Classic";
        }
        status?.Invoke("Building the HD character models…");
        BuildModels(wowFolder, hd);
        status?.Invoke("Merging the HD skin and hair data…");
        BuildData(wowFolder, hd);
        File.WriteAllText(StampPath(wowFolder), Stamp(wowFolder, hd));
        return "HD character models: " + string.Join(", ", Parts.Where(hd.Contains).Select(p => PartNames[p]))
               + (hd.Count < Parts.Length ? "; Classic: " + string.Join(", ", Parts.Where(p => !hd.Contains(p)).Select(p => PartNames[p])) : "");
    }

    static void BuildModels(string wowFolder, IReadOnlyCollection<string> hd)
    {
        string target = ModelsPath(wowFolder), temp = target + ".building";
        List<MpqArchive> pieces = [];
        try
        {
            foreach (string part in hd.Append("common"))
                pieces.Add(MpqArchive.Open(PiecePath(wowFolder, part)));
            using (MpqWriter writer = new(temp, pieces[0].SectorShift))
                foreach (MpqArchive piece in pieces)
                    foreach (string name in piece.Names)
                        if (!name.StartsWith("Temucraft\\", StringComparison.OrdinalIgnoreCase))
                            piece.CopyRaw(name, writer);
        }
        finally { foreach (MpqArchive p in pieces) p.Dispose(); }
        File.Move(temp, target, overwrite: true);
    }

    static void BuildData(string wowFolder, IReadOnlyCollection<string> hd)
    {
        string target = DataPath(wowFolder), temp = target + ".building";
        using (MpqArchive common = MpqArchive.Open(PiecePath(wowFolder, "common")))
        using (MpqWriter writer = new(temp, common.SectorShift))
        {
            HashSet<string> hdParts = [.. hd];
            foreach ((string name, int race, int[] strings, int[] order) in Dbcs)
            {
                byte[] classic = common.ReadPlain($"Temucraft\\Classic\\{name}.dbc");
                byte[] hdData = common.ReadPlain($"Temucraft\\HD\\{name}.dbc");
                byte[] merged = race < 0
                    ? (hdParts.Count > 0 ? hdData : classic)
                    : Dbc.Merge(classic, hdData, race, strings, r => PartOf(r) is { } p && hdParts.Contains(p), order);
                writer.AddPlain($"DBFilesClient\\{name}.dbc", merged);
            }
        }
        File.Move(temp, target, overwrite: true);
    }
}

/// <summary>Reading and writing DBC tables (3.3.5 WDBC: 4-byte fields, a string block at the end).</summary>
public static class Dbc
{
    sealed record Table(int Fields, int RecordSize, List<byte[]> Rows, byte[] Strings);

    static Table Read(byte[] data)
    {
        if (data.Length < 20 || Encoding.ASCII.GetString(data, 0, 4) != "WDBC") throw new InvalidDataException("not a DBC file");
        int count = BitConverter.ToInt32(data, 4), fields = BitConverter.ToInt32(data, 8), size = BitConverter.ToInt32(data, 12),
            strings = BitConverter.ToInt32(data, 16);
        List<byte[]> rows = new(count);
        for (int i = 0; i < count; i++) rows.Add(data.AsSpan(20 + i * size, size).ToArray());
        return new Table(fields, size, rows, data.AsSpan(20 + count * size, strings).ToArray());
    }

    static string StringAt(byte[] block, int offset)
    {
        if (offset <= 0 || offset >= block.Length) return "";
        int end = Array.IndexOf(block, (byte)0, offset);
        return Encoding.UTF8.GetString(block, offset, (end < 0 ? block.Length : end) - offset);
    }

    /// <summary>Rows of <paramref name="hd"/> whose race is wanted in HD, the other rows from <paramref name="classic"/>.
    /// String columns are re-written into one new string block. The rows are sorted by <paramref name="order"/>'s
    /// columns, then by id (column 0); without <paramref name="order"/> by id only (and not at all for a table whose
    /// column 0 is the race).</summary>
    public static byte[] Merge(byte[] classic, byte[] hd, int raceColumn, int[] stringColumns, Func<int, bool> useHd, int[]? order = null)
    {
        Table a = Read(classic), b = Read(hd);
        if (a.Fields != b.Fields || a.RecordSize != b.RecordSize) throw new InvalidDataException("the Classic and HD tables differ in layout");
        List<(byte[] Row, byte[] Strings)> rows = [];
        foreach (byte[] row in a.Rows) if (!useHd(BitConverter.ToInt32(row, raceColumn * 4))) rows.Add((row, a.Strings));
        foreach (byte[] row in b.Rows) if (useHd(BitConverter.ToInt32(row, raceColumn * 4))) rows.Add((row, b.Strings));
        if (order is { Length: > 0 })
        {
            int Compare((byte[] Row, byte[] Strings) x, (byte[] Row, byte[] Strings) y)
            {
                foreach (int column in order)
                {
                    int c = BitConverter.ToInt32(x.Row, column * 4).CompareTo(BitConverter.ToInt32(y.Row, column * 4));
                    if (c != 0) return c;
                }
                return BitConverter.ToInt32(x.Row, 0).CompareTo(BitConverter.ToInt32(y.Row, 0));
            }
            rows.Sort(Compare);
        }
        else if (raceColumn != 0) rows.Sort((x, y) => BitConverter.ToInt32(x.Row, 0).CompareTo(BitConverter.ToInt32(y.Row, 0)));

        using MemoryStream block = new();
        block.WriteByte(0);
        Dictionary<string, int> offsets = new() { [""] = 0 };
        List<byte[]> outRows = new(rows.Count);
        foreach ((byte[] source, byte[] strings) in rows)
        {
            byte[] row = (byte[])source.Clone();
            foreach (int column in stringColumns)
            {
                string text = StringAt(strings, BitConverter.ToInt32(row, column * 4));
                if (!offsets.TryGetValue(text, out int offset))
                {
                    offset = (int)block.Length;
                    offsets[text] = offset;
                    block.Write(Encoding.UTF8.GetBytes(text));
                    block.WriteByte(0);
                }
                BitConverter.TryWriteBytes(row.AsSpan(column * 4, 4), offset);
            }
            outRows.Add(row);
        }
        // tables without string columns keep their string block as it is (it holds at least the empty string)
        byte[] stringBlock = stringColumns.Length > 0 ? block.ToArray() : a.Strings;
        using MemoryStream output = new();
        output.Write("WDBC"u8);
        output.Write(BitConverter.GetBytes(outRows.Count));
        output.Write(BitConverter.GetBytes(a.Fields));
        output.Write(BitConverter.GetBytes(a.RecordSize));
        output.Write(BitConverter.GetBytes(stringBlock.Length));
        foreach (byte[] row in outRows) output.Write(row);
        output.Write(stringBlock);
        return output.ToArray();
    }
}

/// <summary>Just enough of the MPQ format (version 1 tables) to read our HD pieces and copy their files.</summary>
public sealed class MpqArchive : IDisposable
{
    readonly FileStream _file;
    readonly long _base;
    readonly uint[] _hash;     // 4 uints per entry: name A, name B, locale/platform, block
    readonly uint[] _block;    // 4 uints per entry: offset, compressed size, size, flags
    public int SectorShift { get; }
    public IReadOnlyList<string> Names { get; }

    MpqArchive(FileStream file, long @base, int shift, uint[] hash, uint[] block)
    {
        _file = file; _base = @base; SectorShift = shift; _hash = hash; _block = block;
        byte[]? list = TryReadPlain("(listfile)");
        Names = list is null ? [] : Encoding.Latin1.GetString(list).Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(n => !n.Equals("(listfile)", StringComparison.OrdinalIgnoreCase) && Find(n) is not null).ToList();
    }

    public static MpqArchive Open(string path)
    {
        FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            byte[] header = new byte[32];
            long @base = 0;
            while (true)
            {
                file.Position = @base;
                if (file.Read(header, 0, 32) < 32) throw new InvalidDataException($"{Path.GetFileName(path)} is not an MPQ archive");
                if (header[0] == 'M' && header[1] == 'P' && header[2] == 'Q' && header[3] == 0x1A) break;
                @base += 512;
            }
            int shift = BitConverter.ToUInt16(header, 14);
            uint hashPos = BitConverter.ToUInt32(header, 16), blockPos = BitConverter.ToUInt32(header, 20);
            int hashCount = BitConverter.ToInt32(header, 24), blockCount = BitConverter.ToInt32(header, 28);
            uint[] hash = MpqCrypto.ReadTable(file, @base + hashPos, hashCount, MpqCrypto.Hash("(hash table)", 3));
            uint[] block = MpqCrypto.ReadTable(file, @base + blockPos, blockCount, MpqCrypto.Hash("(block table)", 3));
            return new MpqArchive(file, @base, shift, hash, block);
        }
        catch { file.Dispose(); throw; }
    }

    int? Find(string name)
    {
        int size = _hash.Length / 4;
        uint a = MpqCrypto.Hash(name, 1), b = MpqCrypto.Hash(name, 2);
        int start = (int)(MpqCrypto.Hash(name, 0) % (uint)size);
        for (int n = 0; n < size; n++)
        {
            int i = (start + n) % size;
            uint blk = _hash[i * 4 + 3];
            if (blk == 0xFFFFFFFF) return null;
            if (_hash[i * 4] == a && _hash[i * 4 + 1] == b && blk != 0xFFFFFFFE) return (int)blk;
        }
        return null;
    }

    /// <summary>A file stored without compression or encryption (what our build writes for data files).</summary>
    public byte[] ReadPlain(string name) => TryReadPlain(name) ?? throw new FileNotFoundException($"{name} isn't in the archive");

    byte[]? TryReadPlain(string name)
    {
        if (Find(name) is not int blk) return null;
        uint flags = _block[blk * 4 + 3];
        if ((flags & 0x0001FF00) != 0) throw new InvalidDataException($"{name} is compressed or encrypted");
        byte[] data = new byte[_block[blk * 4 + 2]];
        _file.Position = _base + _block[blk * 4];
        _file.ReadExactly(data);
        return data;
    }

    /// <summary>Copies a file's stored bytes into another archive unchanged.</summary>
    public void CopyRaw(string name, MpqWriter writer)
    {
        if (Find(name) is not int blk) return;
        uint flags = _block[blk * 4 + 3];
        if ((flags & 0x00010000) != 0) throw new InvalidDataException($"{name} is encrypted and can't be moved");
        _file.Position = _base + _block[blk * 4];
        writer.AddRaw(name, _file, _block[blk * 4 + 1], _block[blk * 4 + 2], flags);
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Writes a version 1 MPQ: files as given (raw blocks or plain data), then the hash and block tables.</summary>
public sealed class MpqWriter : IDisposable
{
    readonly FileStream _out;
    readonly int _shift;
    readonly List<(string Name, uint Offset, uint CSize, uint Size, uint Flags)> _files = [];
    readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly byte[] _buffer = new byte[1 << 20];

    public MpqWriter(string path, int sectorShift)
    {
        _out = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        _shift = sectorShift;
        _out.Write(new byte[32]);
    }

    public void AddRaw(string name, Stream source, uint csize, uint size, uint flags)
    {
        if (!_names.Add(name)) return;   // the first piece with a name wins (names don't repeat between pieces)
        if (_out.Position > uint.MaxValue - csize) throw new IOException("the archive would be larger than 4 GB");
        uint offset = (uint)_out.Position;
        long left = csize;
        while (left > 0)
        {
            int n = source.Read(_buffer, 0, (int)Math.Min(_buffer.Length, left));
            if (n <= 0) throw new EndOfStreamException();
            _out.Write(_buffer, 0, n);
            left -= n;
        }
        _files.Add((name, offset, csize, size, flags));
    }

    public void AddPlain(string name, byte[] data)
    {
        if (!_names.Add(name)) return;
        uint offset = (uint)_out.Position;
        _out.Write(data);
        _files.Add((name, offset, (uint)data.Length, (uint)data.Length, 0x80000000));
    }

    public void Dispose()
    {
        byte[] list = Encoding.Latin1.GetBytes(string.Join("\r\n", _files.Select(f => f.Name)) + "\r\n");
        AddPlain("(listfile)", list);
        int size = 16;
        while (size < _files.Count * 2) size *= 2;
        uint[] hash = Enumerable.Repeat(0xFFFFFFFFu, size * 4).ToArray();
        for (int i = 0; i < _files.Count; i++)
        {
            string name = _files[i].Name;
            int pos = (int)(MpqCrypto.Hash(name, 0) % (uint)size);
            while (hash[pos * 4 + 3] != 0xFFFFFFFF) pos = (pos + 1) % size;
            hash[pos * 4] = MpqCrypto.Hash(name, 1);
            hash[pos * 4 + 1] = MpqCrypto.Hash(name, 2);
            hash[pos * 4 + 2] = 0;
            hash[pos * 4 + 3] = (uint)i;
        }
        uint[] block = new uint[_files.Count * 4];
        for (int i = 0; i < _files.Count; i++)
            (block[i * 4], block[i * 4 + 1], block[i * 4 + 2], block[i * 4 + 3]) = (_files[i].Offset, _files[i].CSize, _files[i].Size, _files[i].Flags);
        uint hashPos = (uint)_out.Position;
        MpqCrypto.WriteTable(_out, hash, MpqCrypto.Hash("(hash table)", 3));
        uint blockPos = (uint)_out.Position;
        MpqCrypto.WriteTable(_out, block, MpqCrypto.Hash("(block table)", 3));
        uint end = (uint)_out.Position;
        _out.Position = 0;
        using (BinaryWriter header = new(_out, Encoding.ASCII, leaveOpen: true))
        {
            header.Write("MPQ\x1a"u8.ToArray());
            header.Write(32u); header.Write(end); header.Write((ushort)0); header.Write((ushort)_shift);
            header.Write(hashPos); header.Write(blockPos); header.Write((uint)size); header.Write((uint)_files.Count);
        }
        _out.Dispose();
    }
}

static class MpqCrypto
{
    static readonly uint[] Table = Build();

    static uint[] Build()
    {
        uint[] t = new uint[0x500];
        uint seed = 0x00100001;
        for (int i = 0; i < 0x100; i++)
            for (int j = 0; j < 5; j++)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB; uint a = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB; t[i + j * 0x100] = a | (seed & 0xFFFF);
            }
        return t;
    }

    public static uint Hash(string text, int kind)
    {
        uint s1 = 0x7FED7FED, s2 = 0xEEEEEEEE;
        foreach (byte c in Encoding.Latin1.GetBytes(text.ToUpperInvariant().Replace('/', '\\')))
        {
            s1 = Table[(kind << 8) + c] ^ (s1 + s2);
            s2 = c + s1 + s2 + (s2 << 5) + 3;
        }
        return s1;
    }

    public static uint[] ReadTable(Stream file, long position, int entries, uint key)
    {
        byte[] raw = new byte[entries * 16];
        file.Position = position;
        file.ReadExactly(raw);
        uint[] data = new uint[entries * 4];
        uint seed = 0xEEEEEEEE;
        for (int i = 0; i < data.Length; i++)
        {
            seed += Table[0x400 + (key & 0xFF)];
            uint v = BitConverter.ToUInt32(raw, i * 4) ^ (key + seed);
            key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            seed = v + seed + (seed << 5) + 3;
            data[i] = v;
        }
        return data;
    }

    public static void WriteTable(Stream output, uint[] data, uint key)
    {
        byte[] raw = new byte[data.Length * 4];
        uint seed = 0xEEEEEEEE;
        for (int i = 0; i < data.Length; i++)
        {
            seed += Table[0x400 + (key & 0xFF)];
            uint v = data[i];
            BitConverter.TryWriteBytes(raw.AsSpan(i * 4, 4), v ^ (key + seed));
            key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            seed = v + seed + (seed << 5) + 3;
        }
        output.Write(raw);
    }
}
