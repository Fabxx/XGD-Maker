using System.Text;

namespace XGDMaker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed record BuildRequest(string SourceFolder, string OutputIso, bool RandomFill, bool EmbedSs, bool Overwrite);
internal sealed record BuildResult(
    string OutputIso, bool Verified, uint? RegionCode, string DefaultXex,
    bool SsEmbedded, bool SsIsV2, bool IsXbox1);

internal static class IsoBuilder
{
    // Original Xbox (XBOX1) discs are standard DVDs without a fixed partition size;
    // this is just an upper bound (a bit over a dual-layer 8.5 GB disc).
    private const long OgXboxMaxGamePartitionSize = 9L * 1024 * 1024 * 1024;

    public static string SsFileFor(string iso) => iso + ".ss";
    public static string DvdFileFor(string iso) => Path.ChangeExtension(iso, ".dvd");

    public static async Task<BuildResult> BuildAsync(
        BuildRequest request,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(request.SourceFolder);
        var output = Path.GetFullPath(request.OutputIso);

        if (!Directory.Exists(source)) throw new InvalidOperationException("The game folder does not exist.");
        if (!output.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The output filename must end in .iso.");
        if (IsInside(output, source))
            throw new InvalidOperationException("Place the output ISO outside the source game folder.");
        if (File.Exists(output))
        {
            if (!request.Overwrite)
                throw new InvalidOperationException("The output ISO already exists (enable \"Overwrite existing ISOs\" to replace it).");
            foreach (var old in new[] { output, SsFileFor(output), DvdFileFor(output) })
                if (File.Exists(old)) File.Delete(old);
            log($"Overwriting the existing {Path.GetFileName(output)}.");
        }

        var files = Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly).ToList();
        var defaultXex = files.FirstOrDefault(path => string.Equals(Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase));
        var defaultXbe = files.FirstOrDefault(path => string.Equals(Path.GetFileName(path), "default.xbe", StringComparison.OrdinalIgnoreCase));
        if (defaultXex is null && defaultXbe is null)
            throw new InvalidOperationException("default.xex or default.xbe is not directly inside the selected folder.");

        var detection = DiscTypeDetector.Detect(source);
        var discType = detection.DiscType ?? throw new InvalidOperationException(detection.Description);
        var xgd3 = discType == "XGD3";
        var isXbox1 = discType == "Xbox Executable (XBE)";
        var executable = isXbox1 ? defaultXbe! : defaultXex!;

        var outputDirectory = Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Invalid output path.");
        Directory.CreateDirectory(outputDirectory);

        var reportPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(output) + ".builder-report.txt");
        var report = new StringBuilder();

        void Record(string line)
        {
            report.AppendLine(line);
            log(line);
        }

        var gamePartitionSize = isXbox1 ? OgXboxMaxGamePartitionSize
            : xgd3 ? XdvdfsProbe.Xgd3GamePartitionSize : XdvdfsProbe.Xgd2GamePartitionSize;
        var outputCreated = false;
        var completed = false;
        try
        {
            Record($"Source: {source}");
            Record($"Found: {executable}");
            Record($"Disc type (auto-detected): {detection.Description}");
            Record($"Output: {output}");

            StealthData? stealth = null;
            if (request.EmbedSs && !isXbox1)
            {
                var mediaId = XexInspector.ReadMediaId(defaultXex!);
                if (mediaId is null)
                {
                    Record("Could not read the Media ID from default.xex: writing a bare game partition without SS.");
                }
                else
                {
                    Record($"Media ID from default.xex: {Convert.ToHexString(mediaId)}");
                    stealth = StealthResolver.Resolve(discType, mediaId, Record);
                }
            }
            else if (isXbox1)
            {
                Record("Original Xbox disc: rebuilding the full layout (game partition + video partition).");
            }

            var partitionOffset = isXbox1 ? XdvdfsProbe.OgxRedumpOffset
                : stealth is null ? 0L : (xgd3 ? XdvdfsProbe.Xgd3VideoOffset : XdvdfsProbe.Xgd2VideoOffset);
            var sectors = new List<KeyValuePair<long, byte[]>>();
            if (isXbox1)
            {
                // XGD1 (original Xbox) full-disc layout: game partition at 0x18300000,
                // ~387 MB video partition in front (empty here), generated SS before it.
                sectors.Add(new(partitionOffset - 0x800, OgXboxSecurity.Generate()));
                Record($"Original Xbox disc: XGD1 full layout (game partition at 0x{partitionOffset:X}; video partition empty, generated SS at 0x{partitionOffset - 0x800:X}).");
            }
            else if (stealth is not null)
            {
                sectors.Add(new(partitionOffset - (xgd3 ? 0x8800 : 0x800), stealth.Ss));
                if (stealth.Dmi is not null) sectors.Add(new(partitionOffset - (xgd3 ? 0x9000 : 0x1000), stealth.Dmi));
                if (stealth.Pfi is not null) sectors.Add(new(partitionOffset - (xgd3 ? 0x9800 : 0x1800), stealth.Pfi));
                Record($"Embedding {stealth.Source} (SS v{(stealth.IsV2 ? 2 : 1)})" +
                       (stealth.Dmi is not null ? " + DMI" : "") + (stealth.Pfi is not null ? " + PFI" : "") +
                       $" in the full {discType} layout (game partition at 0x{partitionOffset:X}; video partition left empty).");
            }

            Record(request.RandomFill
                ? "Writing the XDVDFS game partition; unused sectors get random data..."
                : "Writing the XDVDFS game partition; unused sectors stay zero...");
            var size = await XdvdfsImageWriter.WriteAsync(source, output, gamePartitionSize, !xgd3 && !isXbox1, request.RandomFill,
                !isXbox1, partitionOffset, sectors, Record, cancellationToken);
            outputCreated = true;
            Record($"Image written: {size:N0} bytes.");

            var gameOffset = XdvdfsProbe.FindGamePartitionOffset(output);
            if (gameOffset != partitionOffset)
                throw new InvalidOperationException("Internal error: the written image does not have the expected layout.");
            Record($"XDVDFS header found; game partition starts at 0x{gameOffset.Value:X} ({XdvdfsProbe.Describe(gameOffset.Value)}).");

            if (!isXbox1)
            {
                var expectedSize = partitionOffset + gamePartitionSize;
                if (size < expectedSize)
                {
                    using (var iso = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        iso.SetLength(expectedSize);
                    }
                    Record($"Extended the {discType} image with zero padding to {expectedSize:N0} bytes.");
                }
            }

            // Native verification (replaces the abgx360 read-only check).
            var rootFile = isXbox1 ? "default.xbe" : "default.xex";
            var verified = XdvdfsProbe.FindGamePartitionOffset(output) == partitionOffset &&
                           XdvdfsProbe.ContainsRootFile(output, rootFile);
            if (verified)
                Record($"PASS: native verification found the XDVDFS header and {rootFile} in the root directory.");
            else
                Record($"FAIL: native verification could not confirm the XDVDFS header and {rootFile}.");

            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            completed = true;
            return new BuildResult(output, verified, isXbox1 ? null : XexInspector.ReadRegionCode(defaultXex!),
                executable, stealth is not null, stealth?.IsV2 ?? false, isXbox1);
        }
        finally
        {
            if (outputCreated && !completed && File.Exists(output))
            {
                try { File.Delete(output); } catch { /* best effort */ }
            }
        }
    }

    public static byte[]? ReadEmbeddedSs(string iso)
    {
        var offset = XdvdfsProbe.FindGamePartitionOffset(iso);
        if (offset is null or 0) return null;
        var ssOffset = offset.Value - (offset.Value == XdvdfsProbe.Xgd3VideoOffset ? 0x8800 : 0x800);
        using var stream = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(ssOffset, SeekOrigin.Begin);
        var ss = new byte[2048];
        var total = 0;
        while (total < ss.Length)
        {
            var read = stream.Read(ss, total, ss.Length - total);
            if (read == 0) return null;
            total += read;
        }
        return ss.Any(b => b != 0) ? ss : null;
    }

    private static bool IsInside(string filePath, string directory)
    {
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedFile = Path.GetFullPath(filePath);
        return normalizedFile.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class DiscTypeDetector
{
    public sealed record Result(string? DiscType, string Description);

    public static Result Detect(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new Result(null, "Select a game folder");
        string? defaultXbe, defaultXex;
        try
        {
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).ToList();
            defaultXbe = files.FirstOrDefault(path => string.Equals(Path.GetFileName(path), "default.xbe", StringComparison.OrdinalIgnoreCase));
            defaultXex = files.FirstOrDefault(path => string.Equals(Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(null, $"Cannot read folder: {ex.Message}");
        }
        if (defaultXbe is not null)
            return new Result("Xbox Executable (XBE)", "Xbox Executable (XBE) (original Xbox)");
        if (defaultXex is null)
            return new Result(null, "default.xex not found directly in this folder");

        bool? hasProfile;
        try
        {
            hasProfile = XexInspector.HasDiscProfileId(defaultXex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(null, $"Cannot read default.xex: {ex.Message}");
        }
        return hasProfile switch
        {
            null => new Result(null, "default.xex is not a valid XEX2 file"),
            true => new Result("XGD3", "XGD3 (default.xex has a Disc Profile ID)"),
            false => new Result("XGD2", "XGD2 (default.xex has no Disc Profile ID)")
        };
    }
}

internal static class XexInspector
{
    private const uint DiscProfileIdKey = 0x00004304;

    public static bool? HasDiscProfileId(string xexPath)
    {
        using var stream = new FileStream(xexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[0x18];
        if (ReadFully(stream, header) != header.Length) return null;
        if (!IsXex2(header)) return null;
        var entryCount = ReadUInt32BigEndian(header, 0x14);
        if (entryCount > 4096) return null;
        var data = new byte[0x18 + (int)entryCount * 8];
        stream.Seek(0, SeekOrigin.Begin);
        if (ReadFully(stream, data) != data.Length) return null;
        return HasDiscProfileId(data);
    }

    public static bool? HasDiscProfileId(byte[] data)
    {
        if (data.Length < 0x18 || !IsXex2(data)) return null;
        var codeOffset = ReadUInt32BigEndian(data, 0x08);
        var entryCount = ReadUInt32BigEndian(data, 0x14);
        if (entryCount > 4096 || 0x18L + entryCount * 8L > codeOffset) return null;
        if (0x18L + entryCount * 8L > data.Length) return null;
        for (var i = 0; i < entryCount; i++)
        {
            if (ReadUInt32BigEndian(data, 0x18 + i * 8) == DiscProfileIdKey) return true;
        }
        return false;
    }

    private static bool IsXex2(byte[] header) =>
        header.Length >= 4 && header[0] == (byte)'X' && header[1] == (byte)'E' && header[2] == (byte)'X' && header[3] == (byte)'2';

    public static uint? ReadRegionCode(string xexPath)
    {
        using var stream = new FileStream(xexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[0x18];
        if (ReadFully(stream, header) != header.Length) return null;
        if (header[0] != (byte)'X' || header[1] != (byte)'E' || header[2] != (byte)'X' || header[3] != (byte)'2') return null;
        var certOffset = ReadUInt32BigEndian(header, 0x10);
        var position = (long)certOffset + 0x178;
        if (position + 4 > stream.Length) return null;
        stream.Seek(position, SeekOrigin.Begin);
        var value = new byte[4];
        if (ReadFully(stream, value) != value.Length) return null;
        return ReadUInt32BigEndian(value, 0);
    }

    /// <summary>Reads the 16-byte media id from the XEX certificate (certOffset + 0x140).</summary>
    public static byte[]? ReadMediaId(string xexPath)
    {
        using var stream = new FileStream(xexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[0x18];
        if (ReadFully(stream, header) != header.Length) return null;
        if (header[0] != (byte)'X' || header[1] != (byte)'E' || header[2] != (byte)'X' || header[3] != (byte)'2') return null;
        var certOffset = ReadUInt32BigEndian(header, 0x10);
        var position = (long)certOffset + 0x140;
        if (position + 16 > stream.Length) return null;
        stream.Seek(position, SeekOrigin.Begin);
        var value = new byte[16];
        if (ReadFully(stream, value) != value.Length) return null;
        return value;
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static uint ReadUInt32BigEndian(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
}

internal static class XdvdfsProbe
{
    public const long Xgd2VideoOffset = 0xFD90000;
    public const long Xgd3VideoOffset = 0x2080000;
    public const long OgxRedumpOffset = 0x18300000; // original Xbox redump full-disc (video partition first)
    public const long Xgd2GamePartitionSize = 7_307_001_856L;
    public const long Xgd3GamePartitionSize = 8_662_351_872L;

    private const long HeaderOffset = 0x10000;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    public static long? FindGamePartitionOffset(string isoPath)
    {
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        foreach (var offset in new[] { 0L, 0xFD90L, 0x30600L, Xgd2VideoOffset, Xgd3VideoOffset, OgxRedumpOffset })
        {
            if (HasMagic(stream, offset + HeaderOffset)) return offset;
        }
        return null;
    }

    public static string Describe(long offset) => offset switch
    {
        0 => "game partition only (XISO / bare partition)",
        0xFD90 => "cracked/rebuilt image layout",
        0x30600 => "redump-style, video partition first",
        Xgd2VideoOffset => "full XGD2 layout",
        Xgd3VideoOffset => "full XGD3 layout",
        OgxRedumpOffset => "XGD1 (original Xbox) full disc",
        _ => "unknown layout"
    };

    /// <summary>Native check that a file exists in the image's root directory.</summary>
    public static bool ContainsRootFile(string isoPath, string fileName)
    {
        var offset = FindGamePartitionOffset(isoPath);
        if (offset is null) return false;
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        var descriptor = ReadAt(stream, offset.Value + HeaderOffset, 0x800);
        var rootSector = BitConverter.ToUInt32(descriptor, 0x14);
        var rootSize = BitConverter.ToUInt32(descriptor, 0x18);
        if (rootSize == 0 || rootSize > 256 * 1024 * 1024) return false;
        var table = ReadAt(stream, offset.Value + (long)rootSector * 0x800, (int)rootSize);

        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0)
        {
            var o = pending.Pop();
            if (!visited.Add(o) || o + 14 > table.Length) continue;
            var left = BitConverter.ToUInt16(table, o);
            var right = BitConverter.ToUInt16(table, o + 2);
            var nameLength = table[o + 13];
            if (nameLength > 0 && o + 14 + nameLength <= table.Length)
            {
                var name = Encoding.Latin1.GetString(table, o + 14, nameLength);
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (left != 0) pending.Push(left * 4);
            if (right != 0) pending.Push(right * 4);
        }
        return false;
    }

    /// <summary>Reads up to maxBytes of a file from the image's root directory (null if not found).</summary>
    public static byte[]? ReadRootFile(string isoPath, string fileName, int maxBytes)
    {
        var offset = FindGamePartitionOffset(isoPath);
        if (offset is null) return null;
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        var descriptor = ReadAt(stream, offset.Value + HeaderOffset, 0x800);
        if (descriptor.Length < 0x20) return null;
        var rootSector = BitConverter.ToUInt32(descriptor, 0x14);
        var rootSize = BitConverter.ToUInt32(descriptor, 0x18);
        if (rootSize == 0 || rootSize > 256 * 1024 * 1024) return null;
        var table = ReadAt(stream, offset.Value + (long)rootSector * 0x800, (int)rootSize);
        if (table.Length == 0) return null;

        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0)
        {
            var o = pending.Pop();
            if (!visited.Add(o) || o + 14 > table.Length) continue;
            var left = BitConverter.ToUInt16(table, o);
            var right = BitConverter.ToUInt16(table, o + 2);
            var sector = BitConverter.ToUInt32(table, o + 4);
            var size = BitConverter.ToUInt32(table, o + 8);
            var attributes = table[o + 12];
            var nameLength = table[o + 13];
            if (nameLength > 0 && o + 14 + nameLength <= table.Length)
            {
                var name = Encoding.Latin1.GetString(table, o + 14, nameLength);
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    if ((attributes & 0x10) != 0) return null; // it is a directory, not a file
                    if (size == 0) return Array.Empty<byte>();
                    var count = (int)Math.Min((long)size, maxBytes);
                    return ReadAt(stream, offset.Value + (long)sector * 0x800, count);
                }
            }
            if (left != 0) pending.Push(left * 4);
            if (right != 0) pending.Push(right * 4);
        }
        return null;
    }

    private static byte[] ReadAt(FileStream stream, long position, int count)
    {
        var data = new byte[count];
        stream.Seek(position, SeekOrigin.Begin);
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(data, total, count - total);
            if (read == 0) break;
            total += read;
        }
        return total == count ? data : Array.Empty<byte>();
    }

    private static bool HasMagic(FileStream stream, long position)
    {
        if (stream.Length < position + Magic.Length) return false;
        stream.Seek(position, SeekOrigin.Begin);
        var buffer = new byte[Magic.Length];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) return false;
            total += read;
        }
        for (var i = 0; i < Magic.Length; i++)
        {
            if (buffer[i] != Magic[i]) return false;
        }
        return true;
    }
}
