using System.Text.RegularExpressions;

namespace XGDMaker;

internal sealed record ScanEntry(string Path, string DisplayName, bool IsIso, string Type, string Status, string? GodXblaKind = null);

internal static class GameScanner
{
    public static List<ScanEntry> Scan(string root, Action<string> log, CancellationToken token)
    {
        var folders = FindGameFolders(root, log, token);
        var isos = FindIsoFiles(root, log, token);
        var packages = FindGodXblaPackages(root, log, token);

        var entries = new List<ScanEntry>(folders.Count + isos.Count + packages.Count);

        var folderNames = UniqueNames(folders, f => CleanName(new DirectoryInfo(f).Name));
        for (var i = 0; i < folders.Count; i++)
        {
            var detection = DiscTypeDetector.Detect(folders[i]);
            entries.Add(new ScanEntry(
                folders[i],
                folderNames[i],
                false,
                detection.DiscType ?? "?",
                detection.DiscType is null ? detection.Description : "Ready"));
        }

        var isoNames = UniqueNames(isos, f => CleanName(Path.GetFileNameWithoutExtension(f)));
        for (var i = 0; i < isos.Count; i++)
        {
            entries.Add(new ScanEntry(isos[i], isoNames[i], true, DetectIsoType(isos[i], log), "Ready"));
        }

        var packagePaths = packages.Select(p => p.Path).ToList();
        var packageNames = UniqueNames(packagePaths, p => CleanName(new DirectoryInfo(p).Name));
        for (var i = 0; i < packages.Count; i++)
        {
            entries.Add(new ScanEntry(packages[i].Path, packageNames[i], false, packages[i].Kind, packages[i].Description, packages[i].Kind));
        }

        entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        log($"Found {folders.Count} extracted game(s), {isos.Count} ISO file(s) and {packages.Count} GOD/XBLA package(s) under {root}.");
        return entries;
    }

    private static List<GodXblaPackage> FindGodXblaPackages(string root, Action<string> log, CancellationToken token)
    {
        var result = new List<GodXblaPackage>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            var pkg = GodXblaProbe.Detect(folder);
            if (pkg is not null)
            {
                result.Add(pkg);
                continue; // do not descend into a package
            }
            try
            {
                foreach (var child in new DirectoryInfo(folder).EnumerateDirectories()
                             .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                             .Select(d => d.FullName))
                    pending.Push(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Skipped {folder}: {ex.Message}");
            }
        }
        return result;
    }

    private static string DetectIsoType(string iso, Action<string> log)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var xex = XdvdfsProbe.ReadRootFile(iso, "default.xex", 0x20000);
                if (xex is null)
                {
                    log($"  {Path.GetFileName(iso)}: no default.xex in the game partition.");
                    return "ISO";
                }
                return XexInspector.HasDiscProfileId(xex) switch
                {
                    true => "XISO (XGD3)",
                    false => "XISO (XGD2)",
                    null => "ISO"
                };
            }
            catch (IOException ex)
            {
                // The ISO may still be locked by the writer (or Xenon) for a moment: retry a few times.
                if (attempt >= 3)
                {
                    log($"  {Path.GetFileName(iso)}: could not open the ISO ({ex.Message}).");
                    return "ISO";
                }
                Thread.Sleep(300);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataException)
            {
                log($"  {Path.GetFileName(iso)}: {ex.Message}");
                return "ISO";
            }
        }
    }

    private static List<string> FindGameFolders(string root, Action<string> log, CancellationToken token)
    {
        var folders = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        var skipped = 0;

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                if (ContainsDefaultXex(folder))
                {
                    folders.Add(folder);
                    continue;
                }

                var children = new DirectoryInfo(folder).EnumerateDirectories()
                    .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                    .Select(d => d.FullName)
                    .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase);
                foreach (var child in children) pending.Push(child);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped++;
                log($"Skipped {folder}: {ex.Message}");
            }
        }

        folders.Sort(StringComparer.OrdinalIgnoreCase);
        if (skipped > 0) log($"({skipped} unreadable folder(s) skipped.)");
        return folders;
    }

    private static List<string> FindIsoFiles(string root, Action<string> log, CancellationToken token)
    {
        var result = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(Path.GetFullPath(root)));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var entry in folder.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push((DirectoryInfo)entry);
                    }
                    else if (entry.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Skipped {folder.FullName}: {ex.Message}");
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool ContainsDefaultXex(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Any(path => string.Equals(Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase));

    private static readonly Regex ExtractionSuffix = new(
        @"[\s._-]*[\(\[]?extracted[\)\]]?$",
        RegexOptions.IgnoreCase);

    public static string CleanName(string folderName)
    {
        var cleaned = ExtractionSuffix.Replace(folderName.Trim(), "").Trim();
        return cleaned.Length > 0 ? cleaned : folderName.Trim();
    }

    private static string[] UniqueNames(List<string> paths, Func<string, string> simpleName)
    {
        var simple = paths.Select(simpleName).ToArray();
        var result = new string[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            var duplicate = simple.Count(n => string.Equals(n, simple[i], StringComparison.OrdinalIgnoreCase)) > 1;
            var parent = Directory.GetParent(paths[i])?.Name;
            result[i] = duplicate && !string.IsNullOrEmpty(parent) ? $"{CleanName(parent)} - {simple[i]}" : simple[i];
        }
        return result;
    }

    public static string OutputPathFor(ScanEntry entry, string? outputFolder)
    {
        if (entry.IsIso)
        {
            var parent = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.GetDirectoryName(entry.Path) ?? entry.Path
                : outputFolder;
            return Path.Combine(parent, Path.GetFileNameWithoutExtension(entry.Path));
        }

        var directory = string.IsNullOrWhiteSpace(outputFolder)
            ? Directory.GetParent(entry.Path)?.FullName ?? entry.Path
            : outputFolder;
        var name = $"{entry.DisplayName}.iso";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return Path.Combine(directory, name);
    }
}
