using System.Text;

namespace XGDMaker;

internal static class XdvdfsExtractor
{
    private const int SectorSize = 2048;
    private const byte AttributeDirectory = 0x10;

    public sealed record ExtractResult(string Destination, int Files, int Directories, long Bytes);

    public static async Task<ExtractResult> ExtractAsync(string iso, string destination, bool overwrite,
        Action<string> log, CancellationToken token)
    {
        var partition = XdvdfsProbe.FindGamePartitionOffset(iso)
            ?? throw new InvalidOperationException("Not an Xbox 360 XDVDFS image (no MICROSOFT*XBOX*MEDIA header found).");
        log($"Game partition at 0x{partition:X} ({XdvdfsProbe.Describe(partition)}).");

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() && !overwrite)
            throw new InvalidOperationException($"{destination} already exists and is not empty (tick \"Overwrite\" to write into it).");
        Directory.CreateDirectory(destination);

        await using var input = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        var length = input.Length;

        var descriptor = await ReadAtAsync(input, partition + 32L * SectorSize, SectorSize, token);
        var rootSector = BitConverter.ToUInt32(descriptor, 0x14);
        var rootSize = BitConverter.ToUInt32(descriptor, 0x18);

        int files = 0, directories = 0;
        long bytes = 0;
        var buffer = new byte[1024 * 1024];
        var lastLog = DateTime.MinValue;
        var pending = new Stack<(uint Sector, uint Size, string Path)>();
        pending.Push((rootSector, rootSize, destination));
        var visitedTables = new HashSet<uint>();

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (sector, size, folder) = pending.Pop();
            if (size == 0 || !visitedTables.Add(sector)) continue;
            CheckRange(partition, sector, size, length, folder);
            var table = await ReadAtAsync(input, partition + (long)sector * SectorSize, (int)size, token);

            foreach (var entry in ReadTable(table, folder))
            {
                var target = Path.Combine(folder, entry.Name);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    directories++;
                    pending.Push((entry.Sector, entry.Size, target));
                    continue;
                }

                CheckRange(partition, entry.Sector, entry.Size, length, target);
                await using (var output = new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew,
                                 FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                {
                    input.Seek(partition + (long)entry.Sector * SectorSize, SeekOrigin.Begin);
                    long remaining = entry.Size;
                    while (remaining > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                        if (read == 0) throw new EndOfStreamException($"The image ends inside {entry.Name}.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        remaining -= read;
                        bytes += read;
                    }
                }
                files++;
                if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(3))
                {
                    log($"Extracting: {files:N0} files, {bytes:N0} bytes...");
                    lastLog = DateTime.UtcNow;
                }
            }
        }

        log($"Extracted {files:N0} files in {directories:N0} folders ({bytes:N0} bytes) to {destination}");
        return new ExtractResult(destination, files, directories, bytes);
    }

    public static async Task<string?> CompareWithFolderAsync(string iso, string folder, Action<string> log, CancellationToken token)
    {
        var partition = XdvdfsProbe.FindGamePartitionOffset(iso);
        if (partition is null) return "the ISO has no XDVDFS file system";

        await using var input = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        var descriptor = await ReadAtAsync(input, partition.Value + 32L * SectorSize, SectorSize, token);
        var pending = new Stack<(uint Sector, uint Size, string Path)>();
        pending.Push((BitConverter.ToUInt32(descriptor, 0x14), BitConverter.ToUInt32(descriptor, 0x18), folder));
        var isoBuffer = new byte[1024 * 1024];
        var fileBuffer = new byte[1024 * 1024];
        int isoFiles = 0, isoDirs = 0;
        var lastLog = DateTime.MinValue;

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (sector, size, path) = pending.Pop();
            if (size == 0) continue;
            var table = await ReadAtAsync(input, partition.Value + (long)sector * SectorSize, (int)size, token);
            foreach (var entry in ReadTable(table, path))
            {
                var target = Path.Combine(path, entry.Name);
                if (entry.IsDirectory)
                {
                    if (!Directory.Exists(target)) return $"folder missing on disk: {target}";
                    isoDirs++;
                    pending.Push((entry.Sector, entry.Size, target));
                    continue;
                }
                var info = new FileInfo(target);
                if (!info.Exists) return $"file missing on disk: {target}";
                if (info.Length != entry.Size) return $"size differs: {target}";

                input.Seek(partition.Value + (long)entry.Sector * SectorSize, SeekOrigin.Begin);
                await using var source = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
                long remaining = entry.Size;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(isoBuffer.Length, remaining);
                    await ReadExactlyAsync(input, isoBuffer, count, token);
                    await ReadExactlyAsync(source, fileBuffer, count, token);
                    if (!isoBuffer.AsSpan(0, count).SequenceEqual(new ReadOnlySpan<byte>(fileBuffer, 0, count)))
                        return $"content differs: {target}";
                    remaining -= count;
                }
                isoFiles++;
                if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(3))
                {
                    log($"Verifying: {isoFiles:N0} files match so far...");
                    lastLog = DateTime.UtcNow;
                }
            }
        }

        var diskFiles = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
        var diskDirs = Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Count();
        if (diskFiles != isoFiles || diskDirs != isoDirs)
            return $"the folder has {diskFiles} files / {diskDirs} folders, the ISO {isoFiles} / {isoDirs}";
        log($"Verified: all {isoFiles:N0} files in {isoDirs:N0} folders are identical in the ISO.");
        return null;
    }

    public sealed record StealthExtractResult(string SsPath, string? DmiPath, string? PfiPath, string DvdPath, bool IsV2);

    /// <summary>
    /// Extracts the SS (and, when present, the DMI and PFI) embedded in a redump/retail
    /// ISO plus a .dvd layer-break descriptor. The SS is also stored under StealthFiles
    /// so later builds can reuse it automatically.
    /// </summary>
    public static async Task<StealthExtractResult> ExtractStealthAsync(string iso, Action<string> log, CancellationToken token)
    {
        var partition = XdvdfsProbe.FindGamePartitionOffset(iso);
        if (partition is null or 0)
            throw new InvalidOperationException("Not an Xbox 360 XGD image, or it has no video/stealth area (no SS to extract).");
        var xgd3 = partition.Value == XdvdfsProbe.Xgd3VideoOffset;
        var ssOffset = partition.Value - (xgd3 ? 0x8800 : 0x800);
        var dmiOffset = partition.Value - (xgd3 ? 0x9000 : 0x1000);
        var pfiOffset = partition.Value - (xgd3 ? 0x9800 : 0x1800);

        await using var stream = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        var ss = await ReadAtAsync(stream, ssOffset, 2048, token);
        if (!SecuritySector.IsValid(ss))
            throw new InvalidOperationException("No valid security sector found in front of the game partition.");

        var dmi = await ReadAtAsync(stream, dmiOffset, 2048, token);
        var pfi = await ReadAtAsync(stream, pfiOffset, 2048, token);
        if (!DmiSector.LooksValid(dmi)) dmi = null;
        if (!PfiSector.LooksValid(pfi)) pfi = null;

        var ssPath = IsoBuilder.SsFileFor(iso);
        var dmiPath = Path.ChangeExtension(iso, ".dmi");
        var pfiPath = Path.ChangeExtension(iso, ".pfi");
        var dvdPath = IsoBuilder.DvdFileFor(iso);

        await File.WriteAllBytesAsync(ssPath, ss, token);
        if (dmi is not null) await File.WriteAllBytesAsync(dmiPath, dmi, token);
        if (pfi is not null) await File.WriteAllBytesAsync(pfiPath, pfi, token);
        var layerBreak = xgd3 ? "2133520" : "1913760";
        await File.WriteAllTextAsync(dvdPath,
            $"LayerBreak={layerBreak}{Environment.NewLine}{Path.GetFileName(iso)}{Environment.NewLine}", new UTF8Encoding(false), token);

        var mediaId = SecuritySector.ReadMediaId(ss);
        if (mediaId.Length == SecuritySector.MediaIdLength)
        {
            var dir = StealthResolver.StealthFilesDir;
            Directory.CreateDirectory(dir);
            var key = Convert.ToHexString(mediaId);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"SS_{key}.bin"), ss, token);
            if (dmi is not null) await File.WriteAllBytesAsync(Path.Combine(dir, $"DMI_{key}.bin"), dmi, token);
            if (pfi is not null) await File.WriteAllBytesAsync(Path.Combine(dir, $"PFI_{key}.bin"), pfi, token);
            log($"Stored the SS in {dir} (keyed by media id {key}) so future builds reuse it.");
        }

        var v2 = SecuritySector.IsVersion2(ss);
        log($"Extracted SS v{(v2 ? 2 : 1)} from {Path.GetFileName(iso)}" +
            (dmi is not null ? " + DMI" : "") + (pfi is not null ? " + PFI" : "") + $" to {ssPath} and {dvdPath}.");
        return new StealthExtractResult(ssPath, dmi is not null ? dmiPath : null, pfi is not null ? pfiPath : null, dvdPath, v2);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), token);
            if (read == 0) throw new EndOfStreamException("Unexpected end of file while comparing.");
            total += read;
        }
    }

    private sealed record Entry(string Name, uint Sector, uint Size, bool IsDirectory);

    private static List<Entry> ReadTable(byte[] table, string folder)
    {
        var entries = new List<Entry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offsets = new Stack<int>();
        var visited = new HashSet<int>();
        offsets.Push(0);
        while (offsets.Count > 0)
        {
            var offset = offsets.Pop();
            if (!visited.Add(offset) || offset + 14 > table.Length) continue;
            var left = BitConverter.ToUInt16(table, offset);
            var right = BitConverter.ToUInt16(table, offset + 2);
            if (left == 0xFFFF && right == 0xFFFF) continue;
            var sector = BitConverter.ToUInt32(table, offset + 4);
            var size = BitConverter.ToUInt32(table, offset + 8);
            var attributes = table[offset + 12];
            var nameLength = table[offset + 13];
            if (nameLength == 0 || offset + 14 + nameLength > table.Length)
                throw new InvalidDataException($"Damaged directory entry in {folder}.");
            var name = Encoding.Latin1.GetString(table, offset + 14, nameLength);
            if (!IsSafeName(name))
                throw new InvalidDataException($"Unsafe file name in the image: \"{name}\".");
            if (!names.Add(name))
                throw new InvalidDataException($"Duplicate name \"{name}\" in {folder}.");
            entries.Add(new Entry(name, sector, size, (attributes & AttributeDirectory) != 0));

            if (left != 0) offsets.Push(left * 4);
            if (right != 0) offsets.Push(right * 4);
        }
        return entries;
    }

    private static bool IsSafeName(string name) =>
        name is not ("." or "..") &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
        name.Trim().Length > 0;

    private static void CheckRange(long partition, uint sector, uint size, long length, string what)
    {
        if (partition + (long)sector * SectorSize + size > length)
            throw new InvalidDataException($"{what} points beyond the end of the image.");
    }

    private static async Task<byte[]> ReadAtAsync(FileStream stream, long position, int count, CancellationToken token)
    {
        var data = new byte[count];
        stream.Seek(position, SeekOrigin.Begin);
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(data.AsMemory(total, count - total), token);
            if (read == 0) throw new EndOfStreamException("The image is truncated.");
            total += read;
        }
        return data;
    }
}
