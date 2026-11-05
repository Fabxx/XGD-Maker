using System.Security.Cryptography;

namespace XGDMaker;

/// <summary>
/// Xbox 360 security sector (SS.bin) parsing, validation and generation.
/// The layout is a fact of the XGD format (also documented in abgx360's open source and
/// the xboxdevwiki): a 2048-byte sector holding a disc-geometry header, an encrypted
/// challenge/response table (CCRT), a per-disc replay table, a response table and the
/// 16-byte media id at offset 0x460.
/// </summary>
internal static class SecuritySector
{
    public const int Size = 2048;
    public const int MediaIdOffset = 0x460;
    public const int MediaIdLength = 16;
    public const int ChallengeCount = 21;

    public static readonly byte[] Xgd2Header =
        { 0xE1, 0x0F, 0x31, 0x10, 0x00, 0x04, 0xFB, 0x20, 0x00, 0xFB, 0x04, 0xDF, 0x00, 0x20, 0x33, 0x9F };

    public static readonly byte[] Xgd3Header =
        { 0xE1, 0x0F, 0x31, 0x10, 0x00, 0x03, 0x41, 0x00, 0x00, 0xFC, 0xAE, 0xFF, 0x00, 0x23, 0x8E, 0x0F };

    /// <summary>Global AES key that wraps the CCRT inside the SS. Public knowledge.</summary>
    public static readonly byte[] CcrtKey =
        { 0xD1, 0xE3, 0xB3, 0x3A, 0x6C, 0x1E, 0xF7, 0x70, 0x5F, 0x6D, 0xE9, 0x3B, 0xB6, 0xC0, 0xDC, 0x71 };

    public static bool IsXgd2(ReadOnlySpan<byte> ss) => ss.Length >= 16 && ss[..16].SequenceEqual(Xgd2Header);

    public static bool IsXgd3(ReadOnlySpan<byte> ss) => ss.Length >= 16 && ss[..16].SequenceEqual(Xgd3Header);

    public static bool IsValid(ReadOnlySpan<byte> ss) => ss.Length == Size && (IsXgd2(ss) || IsXgd3(ss));

    public static byte[] ReadMediaId(ReadOnlySpan<byte> ss) =>
        ss.Length >= MediaIdOffset + MediaIdLength ? ss.Slice(MediaIdOffset, MediaIdLength).ToArray() : Array.Empty<byte>();

    public static void PatchMediaId(byte[] ss, ReadOnlySpan<byte> mediaId) =>
        mediaId[..MediaIdLength].CopyTo(ss.AsSpan(MediaIdOffset, MediaIdLength));

    /// <summary>
    /// SS v2 records the drive angle twice for response types 0x07/0x05 (iXtreme v1.6+);
    /// SS v1 records it once. Xenon needs a v2 SS for disc authentication.
    /// </summary>
    public static bool IsVersion2(ReadOnlySpan<byte> ss)
    {
        if (!IsValid(ss)) return false;
        var replay = IsXgd2(ss) ? 0x200 : 0x20;
        for (var i = 0; i < ChallengeCount; i++)
        {
            var rt = ss[0x730 + i * 9];
            if (rt != 0x05 && rt != 0x07) continue;
            var angle2 = ss[replay + i * 9 + 8] << 8 | ss[replay + i * 9 + 7];
            if (angle2 != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Decrypts the 21-entry challenge/response table stored encrypted at 0x304
    /// (AES-128-CBC, zero IV) plus the 12 trailing plaintext bytes at 0x3F4.
    /// Used to prove a template's challenge/response data is intact.
    /// </summary>
    public static byte[] DecryptCcrt(byte[] ss)
    {
        var result = new byte[ChallengeCount * 12];
        using var aes = Aes.Create();
        aes.Key = CcrtKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.IV = new byte[16];
        var cipher = new byte[15 * 16];
        Array.Copy(ss, 0x304, cipher, 0, cipher.Length);
        using var decryptor = aes.CreateDecryptor();
        decryptor.TransformBlock(cipher, 0, cipher.Length, result, 0);
        Array.Copy(ss, 0x3F4, result, 240, 12);
        return result;
    }
}

internal static class DmiSector
{
    public const int MediaIdOffset = 0x20;
    public const int MediaIdLength = 16;
    public const int CatalogOffset = 0x40;
    public const int TimestampOffset = 0x10;

    public static byte[] ReadMediaId(ReadOnlySpan<byte> dmi) =>
        dmi.Length >= MediaIdOffset + MediaIdLength ? dmi.Slice(MediaIdOffset, MediaIdLength).ToArray() : Array.Empty<byte>();

    public static void PatchMediaId(byte[] dmi, ReadOnlySpan<byte> mediaId) =>
        mediaId[..MediaIdLength].CopyTo(dmi.AsSpan(MediaIdOffset, MediaIdLength));

    public static bool LooksValid(ReadOnlySpan<byte> dmi) =>
        dmi.Length == 2048 &&
        dmi[0] == 0x02 &&
        dmi[0x7E8] == (byte)'X' && dmi[0x7E9] == (byte)'B' && dmi[0x7EA] == (byte)'O' && dmi[0x7EB] == (byte)'X';
}

internal static class PfiSector
{
    public static bool LooksValid(ReadOnlySpan<byte> pfi) =>
        pfi.Length == 2048 &&
        pfi[0x05] == 0x03 && pfi[0x06] == 0x00 && pfi[0x07] == 0x00 &&
        pfi[0x04] == 0x00 && pfi[0x08] == 0x00 && pfi[0x0C] == 0x00;
}

internal sealed record StealthData(byte[] Ss, byte[]? Dmi, byte[]? Pfi, bool IsV2, string Source);

/// <summary>
/// Resolves the SS/DMI/PFI to embed. Priority: a locally stored matching SS (extracted
/// from a redump disc), otherwise a built-in template with the game's media id patched in.
/// No abgx360 binary and no network are required.
/// </summary>
internal static class StealthResolver
{
    public static string StealthFilesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XGDMaker", "StealthFiles");

    public static StealthData Resolve(string discType, byte[] mediaId, Action<string> log)
    {
        var local = FindLocal(mediaId, log);
        if (local is not null) return local;
        return GenerateFromTemplate(discType == "XGD3", mediaId, log);
    }

    public static StealthData GenerateFromTemplate(bool xgd3, byte[] mediaId, Action<string> log)
    {
        var ss = (byte[])(xgd3 ? SecurityTemplates.SsXgd3 : SecurityTemplates.SsXgd2).Clone();
        var dmi = (byte[])(xgd3 ? SecurityTemplates.DmiXgd3 : SecurityTemplates.DmiXgd2).Clone();
        var pfi = (byte[])(xgd3 ? SecurityTemplates.PfiXgd3 : SecurityTemplates.PfiXgd2).Clone();

        SecuritySector.PatchMediaId(ss, mediaId);
        DmiSector.PatchMediaId(dmi, mediaId);

        var v2 = SecuritySector.IsVersion2(ss);
        log($"Generated {((xgd3) ? "XGD3" : "XGD2")} SS v{(v2 ? 2 : 1)} + DMI + PFI from the built-in template " +
            $"(media id patched to {Convert.ToHexString(mediaId)}).");
        return new StealthData(ss, dmi, pfi, v2, "built-in template");
    }

    public static StealthData? FindLocal(byte[] mediaId, Action<string> log)
    {
        try
        {
            var dir = StealthFilesDir;
            if (!Directory.Exists(dir)) return null;

            var key = Convert.ToHexString(mediaId);
            var ss = LoadSector(Path.Combine(dir, $"SS_{key}.bin"));
            var ssSource = ss is not null ? $"SS_{key}.bin" : null;
            if (ss is null || !SecuritySector.IsValid(ss) || !ss.AsSpan(SecuritySector.MediaIdOffset, 16).SequenceEqual(mediaId))
            {
                ss = null;
                ssSource = null;
                // fall back to scanning any .ss/.iso.ss/.bin file whose content matches
                foreach (var path in Directory.EnumerateFiles(dir)
                             .Where(p => p.EndsWith(".ss", StringComparison.OrdinalIgnoreCase) ||
                                         p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                {
                    var candidate = LoadSector(path);
                    if (candidate is null || !SecuritySector.IsValid(candidate)) continue;
                    if (!candidate.AsSpan(SecuritySector.MediaIdOffset, 16).SequenceEqual(mediaId)) continue;
                    ss = candidate;
                    ssSource = Path.GetFileName(path);
                    break;
                }
                if (ss is null) return null;
            }

            log($"Using local SS {ssSource} (media id matches).");

            var dmi = LoadSector(Path.Combine(dir, $"DMI_{key}.bin"));
            if (dmi is not null && (!DmiSector.LooksValid(dmi) || !dmi.AsSpan(DmiSector.MediaIdOffset, 16).SequenceEqual(mediaId))) dmi = null;
            var pfi = LoadSector(Path.Combine(dir, $"PFI_{key}.bin"));
            if (pfi is not null && !PfiSector.LooksValid(pfi)) pfi = null;

            return new StealthData(ss, dmi, pfi, SecuritySector.IsVersion2(ss), $"local ({ssSource})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Local SS lookup failed: {ex.Message}");
            return null;
        }
    }

    private static byte[]? LoadSector(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            return data.Length == SecuritySector.Size ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
