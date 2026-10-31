using System.Diagnostics;
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

internal sealed record BuildRequest(string SourceFolder, string OutputIso, string Abgx, bool RandomFill, bool EmbedSs, bool Overwrite);
internal sealed record BuildResult(
    string OutputIso, bool AbgxRecognizedDefaultXex, uint? RegionCode, string DefaultXex,
    bool SsEmbedded, bool SsIsV2, string? SsFile, string DvdFile);
internal sealed record PatchOutcome(int ExitCode, bool SsEmbedded, string? SsFile, bool SsIsV2);

internal static class IsoBuilder
{
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

        var defaultXex = Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase));
        if (defaultXex is null)
            throw new InvalidOperationException("default.xex is not directly inside the selected folder.");

        var detection = DiscTypeDetector.Detect(source);
        var discType = detection.DiscType ?? throw new InvalidOperationException(detection.Description);
        var xgd3 = discType == "XGD3";

        var outputDirectory = Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Invalid output path.");
        Directory.CreateDirectory(outputDirectory);

        var reportPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(output) + ".builder-report.txt");
        var report = new StringBuilder();

        void Record(string line)
        {
            report.AppendLine(line);
            log(line);
        }

        var gamePartitionSize = xgd3 ? XdvdfsProbe.Xgd3GamePartitionSize : XdvdfsProbe.Xgd2GamePartitionSize;
        var outputCreated = false;
        var completed = false;
        try
        {
            Record($"Source: {source}");
            Record($"Found: {defaultXex}");
            Record($"Disc type (auto-detected): {detection.Description}");
            Record($"Output: {output}");

            AbgxStealthFiles.SsChoice? ss = null;
            if (request.EmbedSs)
            {
                ss = await AbgxStealthFiles.FindBestSsAsync(defaultXex, Record, cancellationToken);
                if (ss is null) Record("No verified SS found: writing a bare game partition without SS.");
            }

            var partitionOffset = ss is null ? 0L : (xgd3 ? XdvdfsProbe.Xgd3VideoOffset : XdvdfsProbe.Xgd2VideoOffset);
            var stealth = new List<KeyValuePair<long, byte[]>>();
            if (ss is not null)
            {
                stealth.Add(new(partitionOffset - (xgd3 ? 0x8800 : 0x800), ss.Data));
                if (ss.Dmi is not null) stealth.Add(new(partitionOffset - (xgd3 ? 0x9000 : 0x1000), ss.Dmi));
                if (ss.Pfi is not null) stealth.Add(new(partitionOffset - (xgd3 ? 0x9800 : 0x1800), ss.Pfi));
                Record($"Embedding {ss.Name} (SS v{(ss.IsV2 ? 2 : 1)})" +
                       (ss.Dmi is not null ? " + DMI" : "") + (ss.Pfi is not null ? " + PFI" : "") +
                       $" in the full {discType} layout (game partition at 0x{partitionOffset:X}; video partition left empty).");
            }

            Record(request.RandomFill
                ? "Writing the XDVDFS game partition; unused sectors get random data..."
                : "Writing the XDVDFS game partition; unused sectors stay zero...");
            var size = await XdvdfsImageWriter.WriteAsync(source, output, gamePartitionSize, !xgd3, request.RandomFill,
                partitionOffset, stealth, Record, cancellationToken);
            outputCreated = true;
            Record($"Image written: {size:N0} bytes.");

            var gameOffset = XdvdfsProbe.FindGamePartitionOffset(output);
            if (gameOffset != partitionOffset)
                throw new InvalidOperationException("Internal error: the written image does not have the expected layout.");
            Record($"XDVDFS header found; game partition starts at 0x{gameOffset.Value:X} ({XdvdfsProbe.Describe(gameOffset.Value)}).");

            var expectedSize = partitionOffset + gamePartitionSize;
            if (size < expectedSize)
            {
                using (var iso = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    iso.SetLength(expectedSize);
                }
                Record($"Extended the {discType} image with zero padding to {expectedSize:N0} bytes, the size ABGX360 expects.");
            }

            WriteDvdDescriptor(output, discType);
            Record($"Created {Path.GetFileName(DvdFileFor(output))}.");
            string? ssFile = null;
            if (ss is not null)
            {
                ssFile = SsFileFor(output);
                await File.WriteAllBytesAsync(ssFile, ss.Data, cancellationToken);
                Record($"Created {Path.GetFileName(ssFile)} (copy of the embedded SS).");
            }

            var abgxRecognized = false;
            if (!string.IsNullOrWhiteSpace(request.Abgx) && File.Exists(request.Abgx))
            {
                AbgxConfig.EnsureIni(Record);
                Record("Running ABGX360 in read-only mode...");
                var abgxOutput = new StringBuilder();
                var abgxExit = await RunProcessAsync(
                    request.Abgx,
                    new[] { "-vws", "--af0", "--noverify", "--noupdate", "--", output },
                    outputDirectory,
                    line =>
                    {
                        abgxOutput.AppendLine(line);
                        Record(line);
                    },
                    cancellationToken);

                var text = abgxOutput.ToString();
                var foundXex =
                    !text.Contains("default.xex was not found", StringComparison.OrdinalIgnoreCase) &&
                    (text.Contains("default.xex sector:", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("Checking default.xex", StringComparison.OrdinalIgnoreCase));
                var tooShort =
                    text.Contains("This ISO is short", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("ISO size is too small", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("filesize too small", StringComparison.OrdinalIgnoreCase);
                abgxRecognized = foundXex && !tooShort;
                if (abgxRecognized)
                    Record("PASS: ABGX360 found default.xex and accepted the image size.");
                else if (foundXex)
                    Record($"FAIL: ABGX360 found default.xex but reports the ISO is too small for {discType}.");
                else
                    Record($"FAIL: ABGX360 did not confirm default.xex (exit code {abgxExit}).");
            }
            else
            {
                Record("ABGX360 was not found. The ISO was built but not checked.");
            }

            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            completed = true;
            return new BuildResult(output, abgxRecognized, XexInspector.ReadRegionCode(defaultXex), defaultXex,
                ss is not null, ss?.IsV2 ?? false, ssFile, DvdFileFor(output));
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

    private static void WriteDvdDescriptor(string isoPath, string discType)
    {
        var layerBreak = discType == "XGD3" ? "2133520" : "1913760";
        var dvdPath = DvdFileFor(isoPath);
        File.WriteAllText(dvdPath, $"LayerBreak={layerBreak}{Environment.NewLine}{Path.GetFileName(isoPath)}{Environment.NewLine}", new UTF8Encoding(false));
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> output,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(fileName)}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool IsInside(string filePath, string directory)
    {
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedFile = Path.GetFullPath(filePath);
        return normalizedFile.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class AbgxConfig
{
    private static readonly (string Key, string Comment, string Value)[] Entries =
    {
        ("web_inidir", "http directory that contains verified ini files", "http://abgx360.hadzz.com/verified/"),
        ("web_unverifiedinidir", "http directory that contains unverified ini files", "http://abgx360.hadzz.com/unverified/"),
        ("web_csv", "http path to GameNameLookup.csv", "http://abgx360.hadzz.com/GameNameLookup.csv"),
        ("web_dat", "http path to abgx360.dat", "http://abgx360.hadzz.com/abgx360.dat"),
        ("web_topology", "http path to topology.php", "http://abgx360.hadzz.com/topology.php"),
        ("web_stealthdir", "http directory that contains SS/DMI/PFI/Video stealth files", "http://abgx360.hadzz.com/StealthFiles/"),
        ("web_autoupload", "http form for submitting AutoUploads", "http://abgx360.hadzz.com/Control/AutoUpload.php"),
    };

    public static string IniPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "abgx360", "abgx360.ini");

    public static void EnsureIni(Action<string> log)
    {
        try
        {
            var path = IniPath;
            if (File.Exists(path))
            {
                var current = ParseValues(File.ReadAllLines(path));
                var wrong = Entries
                    .Where(e => !current.TryGetValue(e.Key, out var value) || !string.Equals(value, e.Value, StringComparison.Ordinal))
                    .Select(e => e.Key)
                    .ToList();
                if (wrong.Count == 0)
                {
                    log($"abgx360.ini is up to date ({path}).");
                    return;
                }
                var backup = path + ".bak";
                for (var i = 2; File.Exists(backup); i++) backup = $"{path}.bak{i}";
                File.Copy(path, backup);
                log($"abgx360.ini had outdated or missing server addresses ({string.Join(", ", wrong)}); original saved as {backup}.");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                log($"abgx360.ini not found; creating {path}.");
            }

            var content = new StringBuilder();
            foreach (var (key, comment, value) in Entries)
            {
                if (content.Length > 0) content.Append("\r\n");
                content.Append("# ").Append(comment).Append("\r\n");
                content.Append(key).Append(": ").Append(value).Append("\r\n");
            }
            File.WriteAllText(path, content.ToString(), new UTF8Encoding(false));
            log("abgx360.ini written with the current database server addresses.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Warning: could not update abgx360.ini: {ex.Message}");
        }
    }

    public static IReadOnlyDictionary<string, string> CurrentAddresses()
    {
        var result = Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        try
        {
            if (File.Exists(IniPath))
                foreach (var (key, value) in ParseValues(File.ReadAllLines(IniPath)))
                    if (result.ContainsKey(key) && value.Length > 0) result[key] = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    private static Dictionary<string, string> ParseValues(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.StartsWith('#')) continue;
            var key = line.Substring(0, colon);
            if (!key.StartsWith("web_", StringComparison.Ordinal)) continue;
            values[key] = line.Substring(colon + 1).Trim();
        }
        return values;
    }
}

internal static class AbgxPatcher
{
    public static async Task<PatchOutcome> RunAsync(string abgxPath, BuildResult build, Action<string> log, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(abgxPath) || !File.Exists(abgxPath))
            throw new InvalidOperationException("ABGX360 was not found, so AutoFix could not be started.");
        AbgxConfig.EnsureIni(log);

        var regionCode = build.RegionCode is null or 0 ? 0xFFFFFFFFu : build.RegionCode.Value;
        var region = regionCode.ToString("X8");
        var languageText = AbgxLanguageNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
        log($"Region code: {region}{(region == "FFFFFFFF" ? " (region free)" : "")}; display language: {languageText}");

        var exitCode = await RunHiddenAsync(abgxPath, new[]
        {
            "-pc", "--af3",
            "--rgn", region,
            "--lang", languageText,
            "--splitvid", "--patchitanyway",
            "--", build.OutputIso
        }, build.OutputIso, log, token);
        log($"ABGX360 AutoFix exited with code {exitCode}.");

        if (!File.Exists(build.OutputIso))
        {
            log($"{build.OutputIso} no longer exists after AutoFix.");
            return new PatchOutcome(exitCode, false, null, false);
        }

        var embedded = IsoBuilder.ReadEmbeddedSs(build.OutputIso);
        if (embedded is null)
        {
            log("The ISO has no embedded SS (bare game partition).");
            return new PatchOutcome(exitCode, false, null, false);
        }
        var ssFile = IsoBuilder.SsFileFor(build.OutputIso);
        if (!File.Exists(ssFile) || !File.ReadAllBytes(ssFile).AsSpan().SequenceEqual(new ReadOnlySpan<byte>(embedded)))
        {
            if (File.Exists(ssFile) && build.SsFile is null)
            {
                log($"{Path.GetFileName(ssFile)} exists and was not written by this run; it is kept unchanged.");
            }
            else
            {
                await File.WriteAllBytesAsync(ssFile, embedded, token);
                log($"Updated {Path.GetFileName(ssFile)} from the SS now embedded in the ISO.");
            }
        }
        var v2 = SsInspector.IsVersion2(embedded);
        log(v2 ? "Embedded SS: version 2 (usable by Xenon)." : "Embedded SS: version 1 (Xenon needs version 2).");
        return new PatchOutcome(exitCode, true, ssFile, v2);
    }

    private static async Task<int> RunHiddenAsync(string abgxPath, string[] arguments, string iso,
        Action<string> log, CancellationToken token)
    {
        log("Starting ABGX360 AutoFix: abgx360 " + string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var startInfo = new ProcessStartInfo(abgxPath)
        {
            WorkingDirectory = Path.GetDirectoryName(iso) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ABGX360.");
        var stdout = PumpAsync(process.StandardOutput, process, log, token);
        var stderr = PumpAsync(process.StandardError, process, log, token);
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Process process, Action<string> log, CancellationToken token)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var lastProgress = DateTime.MinValue;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    log(line.ToString().TrimEnd('\r'));
                    line.Clear();
                }
                else if (c == '\r' && i + 1 < read && buffer[i + 1] != '\n')
                {
                    if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(5) && line.ToString().Trim().Length > 0)
                    {
                        log(line.ToString().Trim());
                        lastProgress = DateTime.UtcNow;
                    }
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }

            var pending = line.ToString();
            if (pending.Contains("(y/n)", StringComparison.OrdinalIgnoreCase))
            {
                log(pending.Trim() + "  -> answered \"n\" automatically");
                line.Clear();
                try
                {
                    await process.StandardInput.WriteLineAsync("n");
                    await process.StandardInput.FlushAsync();
                }
                catch (IOException)
                {
                }
            }
        }
        if (line.Length > 0) log(line.ToString().TrimEnd('\r'));
    }

    private static int AbgxLanguageNumber()
    {
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        switch (culture.TwoLetterISOLanguageName)
        {
            case "en": return 1;
            case "ja": return 2;
            case "de": return 3;
            case "fr": return 4;
            case "es": return 5;
            case "it": return 6;
            case "ko": return 7;
            case "pt": return 9;
            case "pl": return 11;
            case "ru": return 12;
            case "zh":
                var name = culture.Name;
                return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("HK", StringComparison.OrdinalIgnoreCase) || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase)
                    ? 8 : 10;
            default: return 1;
        }
    }
}

internal static class DiscTypeDetector
{
    public sealed record Result(string? DiscType, string Description);

    public static Result Detect(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new Result(null, "Select a game folder");
        string? defaultXex;
        try
        {
            defaultXex = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(null, $"Cannot read folder: {ex.Message}");
        }
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
        if (header[0] != (byte)'X' || header[1] != (byte)'E' || header[2] != (byte)'X' || header[3] != (byte)'2') return null;

        var codeOffset = ReadUInt32BigEndian(header, 0x08);
        var entryCount = ReadUInt32BigEndian(header, 0x14);
        if (entryCount > 4096 || 0x18L + entryCount * 8L > codeOffset) return null;

        var table = new byte[entryCount * 8];
        if (ReadFully(stream, table) != table.Length) return null;
        for (var i = 0; i < entryCount; i++)
        {
            if (ReadUInt32BigEndian(table, i * 8) == DiscProfileIdKey) return true;
        }
        return false;
    }

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
    public const long Xgd2GamePartitionSize = 7_307_001_856L;
    public const long Xgd3GamePartitionSize = 8_662_351_872L;

    private const long HeaderOffset = 0x10000;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    public static long? FindGamePartitionOffset(string isoPath)
    {
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        foreach (var offset in new[] { 0L, Xgd2VideoOffset, Xgd3VideoOffset })
        {
            if (HasMagic(stream, offset + HeaderOffset)) return offset;
        }
        return null;
    }

    public static string Describe(long offset) => offset switch
    {
        0 => "game partition only, no video partition",
        Xgd2VideoOffset => "full XGD2 layout",
        Xgd3VideoOffset => "full XGD3 layout",
        _ => "unknown layout"
    };

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
