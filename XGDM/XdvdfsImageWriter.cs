using System.Text;

namespace XGDMaker;

internal static class XdvdfsImageWriter
{
    public const int SectorSize = 2048;
    private const long VolumeDescriptorOffset = 32L * SectorSize;
    private const uint FirstFreeSector = 33;
    private const long Xgd2ReservedStart = 48;
    private const long Xgd2ReservedEnd = 4144;
    private const byte AttributeDirectory = 0x10;
    private const byte AttributeNormal = 0x80;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    private sealed class Node
    {
        public required string Name { get; init; }
        public required byte[] NameBytes { get; init; }
        public required string FullPath { get; init; }
        public required bool IsDirectory { get; init; }
        public long FileSize { get; init; }
        public List<Node> Children { get; } = new();
        public uint Sector { get; set; }
        public uint TableSize { get; set; }          // directories only, multiple of SectorSize
        public byte[]? Table { get; set; }           // directories only
        public int[] Left { get; set; } = Array.Empty<int>();
        public int[] Right { get; set; } = Array.Empty<int>();
        public int[] Offsets { get; set; } = Array.Empty<int>();
        public uint EntrySize => IsDirectory ? TableSize : (uint)FileSize;
    }

    public static async Task<long> WriteAsync(
        string sourceFolder,
        string outputIso,
        long maxGamePartitionSize,
        bool reserveXgd2HashSectors,
        bool fillUnusedWithRandom,
        bool padToMaxSize,
        long partitionOffset,
        IReadOnlyList<KeyValuePair<long, byte[]>> stealthSectors,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var root = new Node
        {
            Name = "",
            NameBytes = Array.Empty<byte>(),
            FullPath = sourceFolder,
            IsDirectory = true
        };
        var fileCount = 0;
        var dirCount = 0;
        long totalBytes = 0;
        Scan(root, ref fileCount, ref dirCount, ref totalBytes, cancellationToken);
        log($"Scanned {fileCount:N0} files in {dirCount:N0} folders ({totalBytes:N0} bytes).");

        var directories = new List<Node>();
        var queue = new Queue<Node>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            directories.Add(dir);
            LayoutTable(dir);
            foreach (var child in dir.Children)
                if (child.IsDirectory) queue.Enqueue(child);
        }

        long nextSector = FirstFreeSector;
        uint Allocate(long sectors)
        {
            if (reserveXgd2HashSectors && nextSector < Xgd2ReservedEnd && nextSector + sectors > Xgd2ReservedStart)
                nextSector = Xgd2ReservedEnd;
            var start = checked((uint)nextSector);
            nextSector += sectors;
            return start;
        }

        foreach (var dir in directories)
        {
            dir.Sector = Allocate(dir.TableSize / SectorSize);
        }
        var files = new List<Node>(fileCount);
        CollectFiles(root, files);
        foreach (var file in files)
        {
            file.Sector = Allocate(SectorsFor(file.FileSize));
        }

        var dataSize = nextSector * SectorSize;
        if (dataSize > maxGamePartitionSize)
            throw new InvalidOperationException(
                $"The game needs {dataSize:N0} bytes but the selected disc type holds at most {maxGamePartitionSize:N0} bytes.");

        foreach (var dir in directories) SerializeTable(dir);

        var finalSize = partitionOffset + (fillUnusedWithRandom && padToMaxSize ? maxGamePartitionSize : dataSize);
        var filler = new HoleFiller(fillUnusedWithRandom, reserveXgd2HashSectors, partitionOffset);
        foreach (var sector in stealthSectors)
        {
            if (sector.Key < 0 || sector.Key + sector.Value.Length > partitionOffset || sector.Key % SectorSize != 0)
                throw new InvalidOperationException("Internal error: stealth data must lie in front of the game partition.");
        }

        var buffer = new byte[1024 * 1024];
        var lastPercent = -5;
        void Progress(long written)
        {
            var percent = (int)(written * 100 / Math.Max(1, finalSize)) / 5 * 5;
            if (percent > lastPercent)
            {
                lastPercent = percent;
                log($"Writing image: {percent}%");
            }
        }

        var output = new FileStream(outputIso, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            foreach (var sector in stealthSectors.OrderBy(s => s.Key))
            {
                await filler.FillToAsync(output, sector.Key, Progress, cancellationToken);
                await output.WriteAsync(sector.Value, cancellationToken);
            }
            await filler.FillToAsync(output, partitionOffset + VolumeDescriptorOffset, Progress, cancellationToken);
            await output.WriteAsync(BuildVolumeDescriptor(root), cancellationToken);

            foreach (var dir in directories)
            {
                await filler.FillToAsync(output, partitionOffset + (long)dir.Sector * SectorSize, Progress, cancellationToken);
                await output.WriteAsync(dir.Table!, cancellationToken);
            }
            Progress(output.Position);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await filler.FillToAsync(output, partitionOffset + (long)file.Sector * SectorSize, Progress, cancellationToken);
                if (file.FileSize == 0) continue;

                long copied = 0;
                await using (var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        copied += read;
                        Progress(output.Position);
                    }
                }
                if (copied != file.FileSize)
                    throw new InvalidOperationException($"{file.FullPath} changed size while the image was being written.");

                var padding = SectorsFor(file.FileSize) * SectorSize - file.FileSize;
                if (padding > 0) await output.WriteAsync(new byte[padding], cancellationToken);
            }

            await filler.FillToAsync(output, finalSize, Progress, cancellationToken);
            if (output.Position != finalSize)
                throw new InvalidOperationException("Internal layout error: unexpected image size.");
            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
        }
        catch
        {
            await output.DisposeAsync();
            try { File.Delete(outputIso); } catch { /* best effort */ }
            throw;
        }

        Progress(finalSize);
        return finalSize;
    }

    private sealed class HoleFiller
    {
        private readonly bool _random;
        private readonly bool _xgd2Reserved;
        private readonly long _base;
        private readonly byte[] _zeros = new byte[1024 * 1024];
        private readonly byte[] _noise = new byte[1024 * 1024];
        private readonly Random _rng = new();

        public HoleFiller(bool random, bool xgd2Reserved, long partitionOffset)
        {
            _random = random;
            _xgd2Reserved = xgd2Reserved;
            _base = partitionOffset;
        }

        public async Task FillToAsync(FileStream output, long target, Action<long> progress, CancellationToken token)
        {
            var position = output.Position;
            if (target < position) throw new InvalidOperationException("Internal layout error: overlapping sectors.");
            var reservedStart = _base + Xgd2ReservedStart * SectorSize;
            var reservedEnd = _base + Xgd2ReservedEnd * SectorSize;
            while (position < target)
            {
                token.ThrowIfCancellationRequested();
                var beforePartition = position < _base;
                var inReserved = _xgd2Reserved && position >= reservedStart && position < reservedEnd;
                var segmentEnd = target;
                if (beforePartition) segmentEnd = Math.Min(segmentEnd, _base);
                else if (_xgd2Reserved && position < reservedStart) segmentEnd = Math.Min(segmentEnd, reservedStart);
                if (inReserved) segmentEnd = Math.Min(segmentEnd, reservedEnd);

                var chunk = (int)Math.Min(segmentEnd - position, _zeros.Length);
                byte[] source;
                if (_random && !inReserved && !beforePartition)
                {
                    _rng.NextBytes(_noise.AsSpan(0, chunk));
                    source = _noise;
                }
                else
                {
                    source = _zeros;
                }
                await output.WriteAsync(source.AsMemory(0, chunk), token);
                position += chunk;
                progress(position);
            }
        }
    }

    private static void Scan(Node dir, ref int fileCount, ref int dirCount, ref long totalBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new DirectoryInfo(dir.FullPath);
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Links/junctions are not supported: {entry.FullName}");

            long size = 0;
            if (!isDirectory)
            {
                size = ((FileInfo)entry).Length;
                if (size > uint.MaxValue)
                    throw new InvalidOperationException($"{entry.FullName} is larger than 4 GB, which XDVDFS cannot store.");
            }

            var child = new Node
            {
                Name = entry.Name,
                NameBytes = EncodeName(entry.Name, entry.FullName),
                FullPath = entry.FullName,
                IsDirectory = isDirectory,
                FileSize = size
            };
            dir.Children.Add(child);
            if (isDirectory)
            {
                dirCount++;
                Scan(child, ref fileCount, ref dirCount, ref totalBytes, token);
            }
            else
            {
                fileCount++;
                totalBytes += size;
            }
        }

        dir.Children.Sort((a, b) => CompareNames(a.NameBytes, b.NameBytes));
        for (var i = 1; i < dir.Children.Count; i++)
        {
            if (CompareNames(dir.Children[i - 1].NameBytes, dir.Children[i].NameBytes) == 0)
                throw new InvalidOperationException(
                    $"\"{dir.Children[i - 1].Name}\" and \"{dir.Children[i].Name}\" differ only by case in {dir.FullPath}.");
        }
    }

    private static byte[] EncodeName(string name, string fullPath)
    {
        if (name.Length is 0 or > 255)
            throw new InvalidOperationException($"File name length must be 1-255 characters: {fullPath}");
        var bytes = new byte[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c < 0x20 || c > 0x7E)
                throw new InvalidOperationException($"Only plain ASCII file names are supported: {fullPath}");
            bytes[i] = (byte)c;
        }
        return bytes;
    }

    internal static int CompareNames(byte[] a, byte[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var x = ToUpper(a[i]);
            var y = ToUpper(b[i]);
            if (x != y) return x < y ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static byte ToUpper(byte c) => c is >= (byte)'a' and <= (byte)'z' ? (byte)(c - 32) : c;

    private static long SectorsFor(long bytes) => bytes == 0 ? 1 : (bytes + SectorSize - 1) / SectorSize;

    private static int EntryLength(Node entry) => (0xE + entry.NameBytes.Length + 3) & ~3;

    private static void LayoutTable(Node dir)
    {
        var count = dir.Children.Count;
        dir.Left = new int[count];
        dir.Right = new int[count];
        dir.Offsets = new int[count];
        if (count == 0)
        {
            dir.TableSize = SectorSize;
            return;
        }

        var preorder = new List<int>(count);
        BuildBalanced(0, count - 1, dir.Left, dir.Right, preorder);

        var offset = 0;
        foreach (var index in preorder)
        {
            var length = EntryLength(dir.Children[index]);
            var room = SectorSize - offset % SectorSize;
            if (length > room) offset += room;
            dir.Offsets[index] = offset;
            offset += length;
        }

        foreach (var index in preorder)
        {
            if (dir.Offsets[index] / 4 > ushort.MaxValue)
                throw new InvalidOperationException($"Too many entries in one folder: {dir.FullPath}");
        }
        dir.TableSize = checked((uint)((offset + SectorSize - 1) / SectorSize * SectorSize));
    }

    private static int BuildBalanced(int low, int high, int[] left, int[] right, List<int> preorder)
    {
        if (low > high) return -1;
        var middle = low + (high - low) / 2;
        preorder.Add(middle);
        left[middle] = BuildBalanced(low, middle - 1, left, right, preorder);
        right[middle] = BuildBalanced(middle + 1, high, left, right, preorder);
        return middle;
    }

    private static void SerializeTable(Node dir)
    {
        var table = new byte[dir.TableSize];
        Array.Fill(table, (byte)0xFF);
        for (var i = 0; i < dir.Children.Count; i++)
        {
            var child = dir.Children[i];
            var o = dir.Offsets[i];
            WriteUInt16(table, o + 0, dir.Left[i] < 0 ? (ushort)0 : (ushort)(dir.Offsets[dir.Left[i]] / 4));
            WriteUInt16(table, o + 2, dir.Right[i] < 0 ? (ushort)0 : (ushort)(dir.Offsets[dir.Right[i]] / 4));
            WriteUInt32(table, o + 4, child.Sector);
            WriteUInt32(table, o + 8, child.EntrySize);
            table[o + 12] = child.IsDirectory ? AttributeDirectory : AttributeNormal;
            table[o + 13] = (byte)child.NameBytes.Length;
            Buffer.BlockCopy(child.NameBytes, 0, table, o + 14, child.NameBytes.Length);
        }
        dir.Table = table;
    }

    private static void CollectFiles(Node dir, List<Node> files)
    {
        foreach (var child in dir.Children)
        {
            if (child.IsDirectory) CollectFiles(child, files);
            else files.Add(child);
        }
    }

    private static byte[] BuildVolumeDescriptor(Node root)
    {
        var sector = new byte[SectorSize];
        Buffer.BlockCopy(Magic, 0, sector, 0, Magic.Length);
        WriteUInt32(sector, 0x14, root.Sector);
        WriteUInt32(sector, 0x18, root.TableSize);
        var fileTime = DateTime.UtcNow.ToFileTimeUtc();
        for (var i = 0; i < 8; i++) sector[0x1C + i] = (byte)(fileTime >> (8 * i));
        Buffer.BlockCopy(Magic, 0, sector, 0x7EC, Magic.Length);
        return sector;
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
