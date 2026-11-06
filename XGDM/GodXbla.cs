using System.Text;
using System.Text.RegularExpressions;

namespace XGDMaker;

/// <summary>A Games-on-Demand (GOD) or Xbox Live Arcade (XBLA) package.</summary>
internal sealed record GodXblaPackage(string Path, string DisplayName, string Kind, string Description);

/// <summary>Detects GOD (Games on Demand) and XBLA (arcade) packages on disk.</summary>
internal static class GodXblaProbe
{
    private static readonly byte[] LiveMagic = Encoding.ASCII.GetBytes("LIVE");

    public static GodXblaPackage? Detect(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var folderName = Path.GetFileName(folder);

        try
        {
            // GOD first: a nested "<media id>.data" folder containing Data0000..DataXXXX.
            foreach (var childDir in Directory.EnumerateDirectories(folder))
            {
                foreach (var subDir in Directory.EnumerateDirectories(childDir))
                {
                    if (IsGodDataDir(subDir))
                        return new GodXblaPackage(folder, CleanName(folderName), "GOD", "GOD (Games on Demand)");
                }
            }

            // XBLA: a direct subfolder containing exactly one file that starts with the LIVE magic
            // (e.g. 000D0000/<hash>) and no .data folder.
            foreach (var childDir in Directory.EnumerateDirectories(folder))
            {
                if (Directory.EnumerateDirectories(childDir).Any(d => d.EndsWith(".data", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var files = Directory.EnumerateFiles(childDir, "*", SearchOption.TopDirectoryOnly).ToList();
                if (files.Count == 1 && StartsWithLive(files[0]))
                    return new GodXblaPackage(folder, CleanName(folderName), "XBLA", "XBLA (arcade)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool IsGodDataDir(string folder) =>
        Path.GetFileName(folder).EndsWith(".data", StringComparison.OrdinalIgnoreCase) &&
        Directory.EnumerateFiles(folder, "Data*", SearchOption.TopDirectoryOnly)
            .Any(f => Regex.IsMatch(Path.GetFileName(f), @"^Data\d{4,}$", RegexOptions.IgnoreCase));

    private static bool StartsWithLive(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16, FileOptions.RandomAccess);
            if (stream.Length < LiveMagic.Length) return false;
            Span<byte> magic = stackalloc byte[LiveMagic.Length];
            stream.ReadExactly(magic);
            return magic.SequenceEqual(LiveMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CleanName(string name)
    {
        var cleaned = Regex.Replace(
            name.Trim(), @"[\s._-]*(god|gods? on demand|xbla|arcade)[\s._-]*$", "", RegexOptions.IgnoreCase).Trim();
        return cleaned.Length > 0 ? cleaned : name.Trim();
    }
}

/// <summary>Extracts the game files out of a GOD (SVOD) or XBLA (STFS) package.</summary>
internal static class GodXblaExtractor
{
    private const byte AttributeDirectory = 0x10;

    // SVOD (GOD) constants.
    private const long SvodBlockSize = 0x800;
    private const long SvodHashBlockSize = 0x1000;
    private const long SvodBlocksPerL0Hash = 0x198;
    private const long SvodHashesPerL1Hash = 0xA1C4;
    private const long SvodBlocksPerFile = 0x14388;
    private const long SvodMaxFileSize = 0xA290000;

    // STFS (XBLA) constants.
    private const long StfsBlockSize = 0x1000;
    private const uint StfsEndOfChain = 0xFFFFFF;
    private static readonly uint[] StfsBlocksPerHashLevel = { 170, 28900, 4913000 };

    public static void Extract(GodXblaPackage pkg, string destination, Action<string> log, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        var (headerPath, dataPaths) = ResolveFiles(pkg);
        var header = ReadAll(headerPath);
        var volumeType = ReadBe32(header, 0x3A9);

        if (volumeType == 1)
            ExtractSvod(header, dataPaths, destination, log, token);
        else
            ExtractStfs(header, headerPath, destination, log, token);
    }

    private static (string header, string[] data) ResolveFiles(GodXblaPackage pkg)
    {
        foreach (var childDir in Directory.EnumerateDirectories(pkg.Path))
        {
            foreach (var subDir in Directory.EnumerateDirectories(childDir))
            {
                if (!Path.GetFileName(subDir).EndsWith(".data", StringComparison.OrdinalIgnoreCase)) continue;
                var data = Directory.EnumerateFiles(subDir, "Data*", SearchOption.TopDirectoryOnly)
                    .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^Data\d{4,}$", RegexOptions.IgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (data.Length == 0) continue;
                var master = subDir[..^".data".Length];
                if (!File.Exists(master)) master = Directory.EnumerateFiles(childDir).First(StartsWithLive);
                return (master, data);
            }
        }

        foreach (var childDir in Directory.EnumerateDirectories(pkg.Path))
        {
            var files = Directory.EnumerateFiles(childDir).Where(StartsWithLive).ToArray();
            if (files.Length == 1) return (files[0], new[] { files[0] });
        }

        throw new InvalidOperationException("Could not resolve the package files.");
    }

    private static bool StartsWithLive(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16, FileOptions.RandomAccess);
            Span<byte> m = stackalloc byte[4];
            if (s.Read(m) != 4) return false;
            return m[0] == 'L' && m[1] == 'I' && m[2] == 'V' && m[3] == 'E';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] ReadAll(string path) => File.ReadAllBytes(path);

    private static uint ReadBe32(byte[] d, int off) =>
        (uint)(d[off] << 24 | d[off + 1] << 16 | d[off + 2] << 8 | d[off + 3]);

    private static uint ReadLe24(byte[] d, int off) =>
        (uint)(d[off + 2] << 16 | d[off + 1] << 8 | d[off]);

    private static ushort ReadBe16(byte[] d, int off) => (ushort)(d[off] << 8 | d[off + 1]);

    private static long RoundUp(long value, long align) => (value + align - 1) / align * align;

    // ---- SVOD (GOD) ----

    private static void ExtractSvod(byte[] header, string[] dataPaths, string destination, Action<string> log, CancellationToken token)
    {
        var enhancedGdf = (header[0x391] & 0x40) != 0;
        var startDataBlock = ReadLe24(header, 0x395);

        var streams = dataPaths.Select(p =>
            new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess)).ToArray();
        try
        {

        long magicOffset = -1;
        foreach (var probe in new long[] { 0x2000, 0x12000, 0xD000 })
        {
            if (streams[0].Length < probe + 0x18) continue;
            var buf = new byte[0x18];
            streams[0].Seek(probe, SeekOrigin.Begin);
            streams[0].Read(buf, 0, buf.Length);
            if (Encoding.ASCII.GetString(buf, 0, 20) == "MICROSOFT*XBOX*MEDIA") { magicOffset = probe; break; }
        }
        if (magicOffset < 0) throw new InvalidOperationException("No XDVDFS volume descriptor found in the GOD data files.");

        var desc = new byte[0x20];
        streams[0].Seek(magicOffset, SeekOrigin.Begin);
        streams[0].Read(desc, 0, desc.Length);
        var rootBlock = BitConverter.ToUInt32(desc, 0x14);
        var rootSize = BitConverter.ToUInt32(desc, 0x18);

        (long File, long Offset) BlockToOffset(long block)
        {
            var trueBlock = block - (long)startDataBlock * 2;
            if (enhancedGdf) trueBlock += 2;
            var fileBlock = trueBlock % SvodBlocksPerFile;
            var fileIndex = trueBlock / SvodBlocksPerFile;
            long offset = 0;
            var l0 = fileBlock / SvodBlocksPerL0Hash + 1;
            offset += l0 * SvodHashBlockSize;
            var l1 = l0 / SvodHashesPerL1Hash + 1;
            offset += l1 * SvodHashBlockSize;
            var blockAddress = fileBlock * SvodBlockSize + offset;
            if (blockAddress >= SvodMaxFileSize)
            {
                fileIndex += 1;
                blockAddress %= SvodMaxFileSize;
                blockAddress += 0x2000;
            }
            return (fileIndex, blockAddress);
        }

        byte[] ReadBlock(long block)
        {
            var (file, offset) = BlockToOffset(block);
            if (file < 0 || file >= streams.Length) return Array.Empty<byte>();
            var s = streams[file];
            s.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[SvodBlockSize];
            var total = 0;
            while (total < buf.Length)
            {
                var read = s.Read(buf, total, buf.Length - total);
                if (read == 0) break;
                total += read;
            }
            return total == buf.Length ? buf : Array.Empty<byte>();
        }

        void WriteFile(long block, uint size, string target)
        {
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            long remaining = size;
            long currentBlock = block;
            var buffer = new byte[1024 * 1024];
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var (file, offset) = BlockToOffset(currentBlock);
                if (file < 0 || file >= streams.Length) throw new EndOfStreamException($"Truncated file {target}.");
                var s = streams[file];
                s.Seek(offset, SeekOrigin.Begin);
                var chunk = (int)Math.Min(buffer.Length, Math.Min(remaining, SvodBlockSize));
                var read = s.Read(buffer, 0, chunk);
                if (read == 0) throw new EndOfStreamException($"Truncated file {target}.");
                output.Write(buffer, 0, read);
                remaining -= read;
                currentBlock += read / SvodBlockSize;
            }
        }

        int files = 0, dirs = 0;
        var pending = new Stack<(long Block, uint Size, string Path)>();
        pending.Push((rootBlock, rootSize, destination));
        var visited = new HashSet<long>();

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (block, size, folder) = pending.Pop();
            if (size == 0 || !visited.Add(block)) continue;
            var table = ReadBlock(block);
            if (table.Length == 0) continue;

            foreach (var entry in WalkTable(table, folder))
            {
                var target = Path.Combine(folder, entry.Name);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    dirs++;
                    pending.Push((entry.Block, entry.Size, target));
                    continue;
                }
                WriteFile(entry.Block, entry.Size, target);
                files++;
            }
        }

        log($"Extracted {files:N0} files in {dirs:N0} folders from the GOD package to {destination}.");
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    // ---- STFS (XBLA) ----

    private static void ExtractStfs(byte[] header, string headerPath, string destination, Action<string> log, CancellationToken token)
    {
        var headerSize = ReadBe32(header, 0x340);
        var readOnlyFormat = (header[0x37B] & 0x01) != 0;
        var fileTableBlockCount = ReadBe16(header, 0x37C);
        var fileTableBlockNumber = ReadLe24(header, 0x37E);
        var blocksPerHashTable = readOnlyFormat ? 1 : 2;
        var blockStep0 = StfsBlocksPerHashLevel[0] + (uint)blocksPerHashTable;
        var blockStep1 = StfsBlocksPerHashLevel[1] + ((StfsBlocksPerHashLevel[0] + 1) * (uint)blocksPerHashTable);

        using var stream = new FileStream(headerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);

        long BlockToOffset(uint blockIndex)
        {
            var baseCount = StfsBlocksPerHashLevel[0];
            long block = blockIndex;
            for (var i = 0; i < 3; i++)
            {
                block += (blockIndex + baseCount) / baseCount * blocksPerHashTable;
                if (blockIndex < baseCount) break;
                baseCount *= StfsBlocksPerHashLevel[0];
            }
            return RoundUp(headerSize, StfsBlockSize) + block * StfsBlockSize;
        }

        byte[] ReadAt(long offset, int count)
        {
            var buf = new byte[count];
            stream.Seek(offset, SeekOrigin.Begin);
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buf, total, count - total);
                if (read == 0) break;
                total += read;
            }
            return total == count ? buf : Array.Empty<byte>();
        }

        long BlockToHashBlockOffset(uint blockIndex, uint hashLevel)
        {
            uint block;
            if (hashLevel == 0)
            {
                if (blockIndex < StfsBlocksPerHashLevel[0]) block = 0;
                else
                {
                    block = blockIndex / StfsBlocksPerHashLevel[0] * blockStep0;
                    block += (blockIndex / StfsBlocksPerHashLevel[1] + 1) * (uint)blocksPerHashTable;
                    if (blockIndex >= StfsBlocksPerHashLevel[1]) block += (uint)blocksPerHashTable;
                }
            }
            else if (hashLevel == 1)
            {
                if (blockIndex < StfsBlocksPerHashLevel[1]) block = blockStep0;
                else
                {
                    block = blockIndex / StfsBlocksPerHashLevel[1] * blockStep1;
                    block += (uint)blocksPerHashTable;
                }
            }
            else
            {
                block = blockStep1;
            }
            return RoundUp(headerSize, StfsBlockSize) + (long)block * StfsBlockSize;
        }

        uint NextBlock(uint blockIndex)
        {
            var record = blockIndex % StfsBlocksPerHashLevel[0];
            var hashOffset = BlockToHashBlockOffset(blockIndex, 0);
            var table = ReadAt(hashOffset, (int)StfsBlockSize);
            if (table.Length == 0) return StfsEndOfChain;
            var info = ReadBe32(table, (int)(record * 0x18 + 0x14));
            return info & 0xFFFFFF;
        }

        var entries = new List<(string Name, uint Block, uint Size, bool IsDir, ushort Parent)>();
        var tableBlockIndex = fileTableBlockNumber;
        for (var n = 0; n < fileTableBlockCount; n++)
        {
            token.ThrowIfCancellationRequested();
            var table = ReadAt(BlockToOffset(tableBlockIndex), (int)StfsBlockSize);
            if (table.Length == 0) break;
            for (var m = 0; m < 64; m++)
            {
                var e = m * 0x40;
                if (table[e] == 0) break;
                var nameLen = table[e + 0x28] & 0x3F;
                var isDir = (table[e + 0x28] & 0x80) != 0;
                var name = Encoding.ASCII.GetString(table, e, nameLen);
                var startBlock = ReadLe24(table, e + 0x2F);
                var length = ReadBe32(table, e + 0x34);
                var parent = ReadBe16(table, e + 0x32);
                entries.Add((name, startBlock, length, isDir, parent));
            }
            tableBlockIndex = NextBlock(tableBlockIndex);
            if (tableBlockIndex == StfsEndOfChain) break;
        }

        // Build the directory tree. directory_index (parent) indexes the flat entry list,
        // so the parent directory is always read before its children.
        var paths = new List<string>(entries.Count);
        var fileRecords = new List<(string FullPath, uint Block, uint Size)>();
        foreach (var (name, block, size, isDir, parent) in entries)
        {
            var parentPath = parent == 0xFFFF ? destination : paths[parent];
            var fullPath = Path.Combine(parentPath, name);
            paths.Add(fullPath);
            if (isDir)
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                fileRecords.Add((fullPath, block, size));
            }
        }

        int fileCount = 0;
        foreach (var (fullPath, block, size) in fileRecords)
        {
            token.ThrowIfCancellationRequested();
            using var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            uint blockIndex = block;
            long remaining = size;
            while (remaining > 0 && blockIndex != StfsEndOfChain)
            {
                token.ThrowIfCancellationRequested();
                var chunk = (int)Math.Min(StfsBlockSize, remaining);
                var data = ReadAt(BlockToOffset(blockIndex), chunk);
                if (data.Length == 0) throw new EndOfStreamException($"Truncated STFS file {Path.GetFileName(fullPath)}.");
                output.Write(data, 0, data.Length);
                remaining -= data.Length;
                blockIndex = NextBlock(blockIndex);
            }
            fileCount++;
        }

        log($"Extracted {fileCount:N0} files from the XBLA package to {destination}.");
    }

    // ---- shared XDVDFS directory walking (SVOD) ----

    private sealed record Entry(string Name, long Block, uint Size, bool IsDirectory);

    private static List<Entry> WalkTable(byte[] table, string folder)
    {
        var entries = new List<Entry>();
        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (!visited.Add(o) || o + 14 > table.Length) continue;
            var left = BitConverter.ToUInt16(table, o);
            var right = BitConverter.ToUInt16(table, o + 2);
            if (left == 0xFFFF && right == 0xFFFF) continue;
            var block = BitConverter.ToUInt32(table, o + 4);
            var size = BitConverter.ToUInt32(table, o + 8);
            var attributes = table[o + 12];
            var nameLength = table[o + 13];
            if (nameLength == 0 || o + 14 + nameLength > table.Length)
                throw new InvalidDataException($"Damaged directory entry in {folder}.");
            var name = Encoding.Latin1.GetString(table, o + 14, nameLength);
            if (!IsSafeName(name)) throw new InvalidDataException($"Unsafe name: \"{name}\".");
            entries.Add(new Entry(name, block, size, (attributes & AttributeDirectory) != 0));
            if (left != 0) stack.Push(left * 4);
            if (right != 0) stack.Push(right * 4);
        }
        return entries;
    }

    private static bool IsSafeName(string name) =>
        name is not ("." or "..") &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
        name.Trim().Length > 0;
}
