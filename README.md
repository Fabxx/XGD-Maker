<p align="center">
  <img src="https://github.com/Fabxx/XGD-Maker/blob/main/XGDM.png?raw=true" alt="XGDM" width="200" height="200"/><br>
  <em>
   Xbox Game Disc Maker (XGDM)
  </em><br><br>
</p>
<p align="center">
<img src="https://github.com/Fabxx/XGD-Maker/blob/main/GUI.png?raw=true" alt="XGDM GUI" width="800" height="600"/><br>
</p>

# Features
- Rebuild/Extract Xbox 360 games following XGD format (no XDK needed).
- On rebuilding, generates the SS, DMI, PFI and .DVD natively and embeds them into the ISO
- Reuses a locally stored SS when one is available (e.g. extracted from a redump disc) instead of the built-in template.

## Requirements

| Dependency | Required | Used for |
|---|---|---|
| Windows 10 or 11, x64 | yes | running the program |
| Free disk space | yes | about 7.6 GB per XGD2 ISO and 8.7 GB per XGD3 ISO |

## Building ISOs

1. Select your **Games folder**, it will scan for both XISOs or raw `default.xex` files.
2. **Output folder** (optional): where the ISOs are written. Leave it empty to write each ISO next to its game folder.
3. Tick the games you want and click **Build checked**.

Games are built one at a time. **Cancel** stops the current game (its partial ISO is deleted) and skips the rest. A summary is shown at the end, and the full log is in the window and in each game's report file.

### Options

| Option | Default | Effect |
|---|---|---|
| Overwrite existing ISOs | off | Replace an existing ISO. When off, games whose ISO already exists are skipped. |
| Cleanup | off | Permanently delete the game folder after the build (see Safety). Asks for confirmation before the batch starts. |

### Output files

| File | Content |
|---|---|
| `<game>.iso` | the disc image |
| `<game>.iso.ss` | the extracted/generated security sector |
| `<game>.dvd` | layer break file for burning (1913760 for XGD2, 2133520 for XGD3) |
| `<game>.dmi` | Disc Manufacturing Information file, made from template |
| `<game>.pfi` | Physical Format Information file |
| `<game>.builder-report.txt` | log of the build and of the native verification |

### Status messages

| Status | Meaning |
|---|---|
| XISO OK – SS | DMI | PFI | .DVD restored | All checks OK |
| ERROR – could not read the Media ID | the `default.xex` has no readable media id |
| ERROR - verification after rebuild FAILED | the image header or `default.xex` could not be confirmed; see the log |
| Skipped: ISO already exists | tick **Overwrite existing ISOs** to rebuild it |
| …, source deleted / source kept | result of the source folder cleanup |

## Documentation

### Disc type

A game is XGD3 when `default.xex` has a Disc Profile ID (optional header `0x00004304`), otherwise XGD2.

### Security sector (SS), DMI and PFI

The SS is a 2048-byte sector placed in front of the game partition. It holds the disc geometry, an encrypted challenge/response table (CCRT), a per-disc replay table, a response table and the 16-byte media id at offset `0x460`. The DMI (2048 bytes) holds the media id at offset `0x20`; the PFI (2048 bytes) is the physical format information and is constant per disc type. These offsets are facts of the XGD format.

When a game is built, XGDM reads the media id from `default.xex` (certificate offset + `0x140`) and resolves the SS/DMI/PFI to embed, in this order:

1. **Local SS** — a matching `SS_<mediaid>.bin` (plus `DMI_`/`PFI_`) in `%APPDATA%\XGDMaker\StealthFiles`, e.g. one extracted from a redump of the same disc.
2. **Built-in template** — an XGD2/XGD3 template is cloned and its media id (SS `0x460`, DMI `0x20`) is patched to the game's media id.

The challenge/response data is independent of the media id, so a template SS still authenticates on RGH/JTAG consoles and in the Xenon emulator; the SS/DMI signatures are not checked there. The image is a full retail layout (SS/DMI/PFI in front of the game partition, video partition left empty).

### Image layout

| | XGD2 | XGD3 |
|---|---|---|
| Game partition offset (with SS) | `0xFD90000` | `0x2080000` |
| SS / DMI / PFI | partition − `0x800` / − `0x1000` / − `0x1800` | partition − `0x8800` / − `0x9000` / − `0x9800` |
| Game partition size | 7,307,001,856 bytes | 8,662,351,872 bytes |

The XDVDFS file system follows the retail layout:
- the volume descriptor is at sector 32;
- directory tables come first, then file data, each starting on a 2048-byte sector;
- directory tables are balanced binary search trees ordered by case-insensitive name;
- files have attribute `0x80`;
- on XGD2, sectors 48–4143 are left empty (on a real disc they hold authentication hashes).

## Safety

- Game folders are never modified.
- Existing ISOs are never replaced unless **Overwrite existing ISOs** is ticked.
- A partially written ISO is deleted when a build fails or is cancelled.
- **Source folder cleanup:** before deleting a game folder, the program reads the new ISO back and compares it with the folder: same folders, same files, same sizes, identical bytes, nothing extra on either side. The folder is deleted (permanently, not to the Recycle Bin) only if everything matches and the build succeeded. Otherwise it is kept and the reason is logged.
- Extraction rejects unsafe file names and entries that point outside the image.

## Limitations

- **Names and sizes:** file names must be plain ASCII (up to 255 characters), files can be at most 4 GB, and two names in one folder cannot differ only by case.
- **Not identical to the original disc:** file placement differs from Microsoft's tools, so the image is not byte-identical to a retail disc.
- **Generated SS:** when no local SS is available, the built-in template's challenge/response table is used and only the media id is patched. This works on RGH/JTAG consoles and Xenon, but the SS/DMI signatures are not valid (they are not checked there). The XGD3 drive angles come from the template, not from a physical measurement.

## Troubleshooting

| Problem | Solution |
|---|---|
| Xenon: "disc unreadable" | check that `<game>.iso.ss` is next to the ISO and that the status shows an embedded SS |
| "You must install .NET" when starting the program | use the single-file release build (see Building from source); a framework-dependent build only runs when its `.runtimeconfig.json` / `.deps.json` files are next to the exe and the **.NET 8 Desktop Runtime x64** (not just the .NET Runtime) is installed |
| Xenon: "Error opening DVD key file" | create `dvdkey.txt` with your console's DVD key |

## Building from source

Requirements: Visual Studio 2022 17.8 or later with the **.NET desktop development** workload, or the .NET 8 SDK.

Release build (single self-contained `XGD Maker.exe`, no .NET installation needed on the user's PC):

```powershell
dotnet publish XGDMaker -p:PublishProfile=SingleFile-win-x64
```

The exe is written to `XGDMaker\bin\publish\`. In Visual Studio: right-click the project → **Publish** → profile **SingleFile-win-x64**. Distribute only that exe, not the contents of `bin\Release`.

## Credits

- [XDVDFS](https://github.com/antangelo/xdvdfs)

- [EXTRACT-XISO](https://github.com/xboxdev/extract-xiso)

- [ABGX360](https://abgx360.hadzz.com/) (open source reference for the SS/DMI/PFI format; no longer a runtime dependency)

## Disclaimer

All code is original. The XDVDFS format was implemented from public documentation and checked against the open-source xdvdfs and extract-xiso projects. Disc offsets, sizes and SS/DMI/PFI field positions are facts of the format, also documented by abgx360 and xboxdevwiki. XGD Maker contains no Microsoft code, XDK tools, keys or game data.

Intended for backups of games you own.

## License

[MIT](LICENSE)
