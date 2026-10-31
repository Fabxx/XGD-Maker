using System.Net;
using System.Net.Http.Headers;

namespace XGDMaker;

internal static class AbgxPaths
{
    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "abgx360");
    public static string StealthFiles => Path.Combine(Home, "StealthFiles");
}

internal static class AbgxDatabase
{
    public static async Task CheckForUpdatesAsync(Action<string> log, CancellationToken token)
    {
        AbgxConfig.EnsureIni(log);
        var addresses = AbgxConfig.CurrentAddresses();
        Directory.CreateDirectory(AbgxPaths.Home);

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XGDMaker/1.0");

        log("Checking the ABGX360 database for updates...");
        var reachable = await UpdateFileAsync(http, addresses["web_dat"], Path.Combine(AbgxPaths.Home, "abgx360.dat"), log, token);
        if (!reachable)
        {
            log("Warning: the ABGX360 database server did not answer. AutoFix will fail until it is reachable " +
                $"(server address in {AbgxConfig.IniPath}).");
            return;
        }
        await UpdateFileAsync(http, addresses["web_csv"], Path.Combine(AbgxPaths.Home, "GameNameLookup.csv"), log, token);
    }

    private static async Task<bool> UpdateFileAsync(HttpClient http, string url, string path, Action<string> log, CancellationToken token)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (File.Exists(path)) request.Headers.IfModifiedSince = new DateTimeOffset(File.GetLastWriteTimeUtc(path));

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                log($"{name}: already up to date.");
                return true;
            }
            if (!response.IsSuccessStatusCode)
            {
                log($"{name}: server answered {(int)response.StatusCode} {response.ReasonPhrase}; local copy kept.");
                return (int)response.StatusCode < 500 && (int)response.StatusCode is not (>= 300 and < 400);
            }

            var temp = path + ".download";
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(output, token);
            }
            var size = new FileInfo(temp).Length;
            if (size == 0)
            {
                File.Delete(temp);
                log($"{name}: server sent an empty file; local copy kept.");
                return true;
            }
            File.Move(temp, path, overwrite: true);
            log($"{name}: updated from the server ({size:N0} bytes).");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            if (token.IsCancellationRequested) throw;
            log($"{name}: update check failed ({ex.Message}); local copy kept.");
            return false;
        }
    }
}

internal static class AbgxStealthFiles
{
    public sealed record SsChoice(byte[] Data, string Name, bool IsV2, byte[]? Dmi, byte[]? Pfi);

    public static async Task<SsChoice?> FindBestSsAsync(string defaultXex, Action<string> log, CancellationToken token)
    {
        try
        {
            var xex = await File.ReadAllBytesAsync(defaultXex, token);
            var xexCrc = Crc32(xex).ToString("X8");
            var xexMediaId = XexMediaId(xex);
            if (xexMediaId is null)
            {
                log("SS lookup: could not read the Media ID from default.xex.");
                return null;
            }

            var addresses = AbgxConfig.CurrentAddresses();
            Directory.CreateDirectory(AbgxPaths.StealthFiles);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("XGDMaker/1.0");

            var xexIniName = $"Xex_{xexCrc}.ini";
            var xexIni = await GetFileAsync(http, addresses["web_inidir"], xexIniName, log, token);
            if (xexIni is null)
            {
                log($"SS lookup: the verified database has no entry for XEX CRC {xexCrc}.");
                return null;
            }

            var ssCrcs = System.Text.Encoding.ASCII.GetString(xexIni)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length == 8 && l.All(Uri.IsHexDigit))
                .Select(l => l.ToUpperInvariant())
                .Distinct()
                .ToList();
            if (ssCrcs.Count == 0)
            {
                log($"SS lookup: {xexIniName} lists no SS.");
                return null;
            }
            log($"SS lookup: {xexIniName} lists {ssCrcs.Count} verified SS ({string.Join(", ", ssCrcs)}).");

            SsChoice? fallbackV1 = null;
            foreach (var ssCrc in ssCrcs)
            {
                var name = $"SS_{ssCrc}.bin";
                var ss = await GetFileAsync(http, addresses["web_stealthdir"], name, log, token);
                if (ss is null) continue;
                if (ss.Length != 2048 || !ss.AsSpan(0x460, 16).SequenceEqual(new ReadOnlySpan<byte>(xexMediaId)))
                {
                    log($"SS lookup: ignored {name} (not a 2048-byte SS with this game's Media ID).");
                    continue;
                }
                var v2 = SsInspector.IsVersion2(ss);
                log($"SS lookup: {name} matches the game, SS version {(v2 ? 2 : 1)}.");
                if (!v2 && fallbackV1 is not null) continue;
                var (dmi, pfi) = await GetDmiPfiAsync(http, addresses, ssCrc, xexCrc, log, token);
                var choice = new SsChoice(ss, name, v2, dmi, pfi);
                if (v2) return choice;
                fallbackV1 = choice;
            }
            return fallbackV1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"SS lookup failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<(byte[]? Dmi, byte[]? Pfi)> GetDmiPfiAsync(HttpClient http, IReadOnlyDictionary<string, string> addresses,
        string ssCrc, string xexCrc, Action<string> log, CancellationToken token)
    {
        var ini = await GetFileAsync(http, addresses["web_inidir"], $"{ssCrc}{xexCrc}.ini", log, token);
        if (ini is null) return (null, null);
        string? Value(string key) => System.Text.Encoding.ASCII.GetString(ini)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Substring(key.Length + 1).Trim().ToUpperInvariant())
            .FirstOrDefault(v => v.Length == 8 && v.All(Uri.IsHexDigit));

        async Task<byte[]?> Fetch(string kind)
        {
            var crc = Value(kind);
            if (crc is null) return null;
            var data = await GetFileAsync(http, addresses["web_stealthdir"], $"{kind}_{crc}.bin", log, token);
            return data is { Length: 2048 } ? data : null;
        }

        return (await Fetch("DMI"), await Fetch("PFI"));
    }

    private static async Task<byte[]?> GetFileAsync(HttpClient http, string webDirectory, string name, Action<string> log, CancellationToken token)
    {
        var local = Path.Combine(AbgxPaths.StealthFiles, name);
        if (File.Exists(local)) return await File.ReadAllBytesAsync(local, token);
        try
        {
            using var response = await http.GetAsync(webDirectory.TrimEnd('/') + "/" + name, token);
            if (!response.IsSuccessStatusCode) return null;
            var data = await response.Content.ReadAsByteArrayAsync(token);
            if (data.Length == 0) return null;
            await File.WriteAllBytesAsync(local, data, token);
            log($"Downloaded {name} from the ABGX360 database.");
            return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            log($"Could not download {name}: {ex.Message}");
            return null;
        }
    }

    private static byte[]? XexMediaId(byte[] xex)
    {
        if (xex.Length < 0x18 || xex[0] != 'X' || xex[1] != 'E' || xex[2] != 'X' || xex[3] != '2') return null;
        var certOffset = (long)((uint)xex[0x10] << 24 | (uint)xex[0x11] << 16 | (uint)xex[0x12] << 8 | xex[0x13]);
        if (certOffset + 0x150 > xex.Length) return null;
        return xex.AsSpan((int)certOffset + 0x140, 16).ToArray();
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    internal static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

internal static class SsInspector
{
    private static readonly byte[] Xgd2Header =
        { 0xE1, 0x0F, 0x31, 0x10, 0x00, 0x04, 0xFB, 0x20, 0x00, 0xFB, 0x04, 0xDF, 0x00, 0x20, 0x33, 0x9F };

    public static bool IsVersion2(byte[] ss)
    {
        if (ss.Length < 2048) return false;
        var replayOffset = ss.AsSpan(0, 16).SequenceEqual(new ReadOnlySpan<byte>(Xgd2Header)) ? 0x200 : 0x20;
        for (var i = 0; i < 21; i++)
        {
            var responseType = ss[0x730 + i * 9];
            if (responseType != 0x05 && responseType != 0x07) continue;
            var angle2 = ss[replayOffset + i * 9 + 8] << 8 | ss[replayOffset + i * 9 + 7];
            if (angle2 != 0) return true;
        }
        return false;
    }
}
