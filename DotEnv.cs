namespace SkailSubscriptionCycle;

/// <summary>
/// Loads KEY=VALUE lines from the first .env found in the current directory or its parents.
/// Variables already set in the environment win, so a real environment overrides the file.
/// </summary>
public static class DotEnv
{
    public static string? Load(string fileName = ".env")
    {
        var path = Find(fileName);
        if (path is null) return null;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }

        return path;
    }

    private static string? Find(string fileName)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
