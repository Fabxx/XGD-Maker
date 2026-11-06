using System.Security.Cryptography;
using System.Text;

namespace XGDMaker;

/// <summary>Builds the original Xbox (XBOX1) security sector (SS.bin, "media flag").</summary>
internal static class OgXboxSecurity
{
    // The SS is 2048 bytes in the "complete format" (xboxdevwiki.net/Xbox_Game_Disc):
    //
    //   0x000  PFI (physical format information)
    //   0x300  version of challenge table (always 1)
    //   0x301  number of challenge entries (always 23)
    //   0x302  challenge entries (23 x 11 = 253 bytes), RC4-encrypted
    //   0x41F  timestamp (u64)
    //   0x43B  16 bytes unknown
    //   0x49F  key basis (44 bytes) -> SHA-1 hashed for the RC4 key
    //   0x4CB  SHA-1 hash A (20 bytes)
    //   0x4DF  signature A (256 bytes)  [RSA, not checked on RGH/JTAG]
    //   0x5DF  key basis 2 (44 bytes)
    //   0x60B  SHA-1 hash B (20 bytes)
    //   0x61F  signature B (64 bytes)   [RSA, not checked on RGH/JTAG]
    //   0x660  number of security sector ranges (always 23)
    //   0x661  security sector ranges (23 x 9 = 207 bytes)
    //   0x730  security sector ranges (copy)
    //
    // The challenge entries and the RSA signatures are not verified on RGH/JTAG consoles,
    // so only the layout, the RC4-encrypted challenge table and the SHA-1 hashes are
    // produced here; the signatures stay zero.

    private const int SsSize = 2048;

    public static byte[] Generate()
    {
        var ss = new byte[SsSize];

        // PFI (offset 0): a minimal DVD-ROM layout. Only the fields a drive might read
        // are set; the rest stay zero.
        ss[0] = 0x00;                       // booktype<<4 | part_version  (DVD-ROM, v0)
        ss[1] = 0x0F;                       // disc_size<<4 | maximum_rate (120mm, max)
        ss[2] = 0x00;                       // single layer, PTP, layer_type 0
        ss[5] = 0x03;                       // start PSN of data area = 0x030000
        ss[13] = 0x20; ss[14] = 0x33; ss[15] = 0xAF; // end sector in layer 0 (fixed)

        // Challenge table header.
        ss[768] = 1;                        // version
        ss[769] = 23;                       // number of challenge entries

        // 23 challenge entries (11 bytes each). Two are marked valid; the values are
        // arbitrary because the drive challenge/response is bypassed on RGH/JTAG.
        var entries = new byte[23 * 11];
        for (var i = 0; i < 23; i++)
        {
            var o = i * 11;
            entries[o] = (byte)(i < 2 ? 1 : 0);   // valid flag
            entries[o + 1] = (byte)(i + 1);        // challenge id
            // challenge value (4 bytes), response modifier (1), response value (4):
            // arbitrary, left zero.
        }

        // Key basis (44 bytes) at 0x49F; its SHA-1 feeds the RC4 key.
        var keyBasis = new byte[44];
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTime()).CopyTo(keyBasis, 0);
        for (var i = 8; i < keyBasis.Length; i++) keyBasis[i] = (byte)(0x40 + i);

        var hash = SHA1.HashData(keyBasis);
        // RC4 key: first 7 bytes of the SHA-1 hash (the Sphinx protocol uses the first
        // characters of the hex digest, which only differs when decrypting a stock SS).
        var rc4Key = hash[..7];
        var encrypted = Rc4(rc4Key, entries);
        Array.Copy(encrypted, 0, ss, 770, encrypted.Length);

        // Timestamp and key basis.
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTime()).CopyTo(ss, 1055);
        Array.Copy(keyBasis, 0, ss, 1183, keyBasis.Length);

        // SHA-1 hash A over bytes 0..1226.
        SHA1.HashData(ss.AsSpan(0, 1227)).CopyTo(ss, 1227);
        // Signature A (256 bytes) stays zero.

        // SHA-1 hash B over bytes 0..1546 (includes hash A + zero signature A).
        SHA1.HashData(ss.AsSpan(0, 1547)).CopyTo(ss, 1547);
        // Signature B (64 bytes) stays zero.

        // Security sector ranges: 23 entries; only the first points at the SS sector.
        ss[1632] = 23;
        var ssPsn = 0x182FF800L / 2048; // 0x305FF = 198143 (partition 0x18300000 - 0x800)
        WriteRange(ss, 1633, ssPsn, ssPsn);
        Array.Copy(ss, 1633, ss, 1840, 23 * 9);

        return ss;
    }

    private static void WriteRange(byte[] ss, int offset, long startPsn, long endPsn)
    {
        // 9-byte range: bytes 0..2 unknown (zero), bytes 3..5 start PSN (BE u24),
        // bytes 6..8 end PSN (BE u24).
        ss[offset + 3] = (byte)(startPsn >> 16);
        ss[offset + 4] = (byte)(startPsn >> 8);
        ss[offset + 5] = (byte)startPsn;
        ss[offset + 6] = (byte)(endPsn >> 16);
        ss[offset + 7] = (byte)(endPsn >> 8);
        ss[offset + 8] = (byte)endPsn;
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        var a = 0;
        var b = 0;
        for (var i = 0; i < data.Length; i++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
            output[i] = (byte)(data[i] ^ s[(s[a] + s[b]) & 0xFF]);
        }
        return output;
    }
}
