namespace XGDMaker;

internal sealed record FoundGame(string Folder, string DisplayName, DiscTypeDetector.Result Detection);

internal static class GameScanner
{
    public static List<FoundGame> Scan(string root, Action<string> log, CancellationToken token)
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
        var names = UniqueNames(folders);
        var games = new List<FoundGame>(folders.Count);
        for (var i = 0; i < folders.Count; i++)
        {
            games.Add(new FoundGame(folders[i], names[i], DiscTypeDetector.Detect(folders[i])));
        }
        log($"Found {games.Count} game folder(s) under {root}" + (skipped > 0 ? $" ({skipped} unreadable folder(s) skipped)." : "."));
        return games;
    }

    private static bool ContainsDefaultXex(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Any(path => string.Equals(Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase));

    private static readonly System.Text.RegularExpressions.Regex ExtractionSuffix = new(
        @"[\s._-]*[\(\[]?extracted[\)\]]?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string CleanName(string folderName)
    {
        var cleaned = ExtractionSuffix.Replace(folderName.Trim(), "").Trim();
        return cleaned.Length > 0 ? cleaned : folderName.Trim();
    }

    private static string[] UniqueNames(List<string> folders)
    {
        var simple = folders.Select(f => CleanName(new DirectoryInfo(f).Name)).ToArray();
        var result = new string[folders.Count];
        for (var i = 0; i < folders.Count; i++)
        {
            var duplicate = simple.Count(n => string.Equals(n, simple[i], StringComparison.OrdinalIgnoreCase)) > 1;
            var parent = Directory.GetParent(folders[i])?.Name;
            result[i] = duplicate && !string.IsNullOrEmpty(parent) ? $"{CleanName(parent)} - {simple[i]}" : simple[i];
        }
        return result;
    }

    public static string OutputPathFor(FoundGame game, string? outputFolder)
    {
        var directory = string.IsNullOrWhiteSpace(outputFolder)
            ? Directory.GetParent(game.Folder)?.FullName ?? game.Folder
            : outputFolder;
        var name = $"{game.DisplayName}.iso";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return Path.Combine(directory, name);
    }
}
