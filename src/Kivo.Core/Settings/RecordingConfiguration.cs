using Kivo.Core.Models;

namespace Kivo.Core.Settings;

/// <summary>Backward-compatible recording roots. Folder validation never writes to recordings.</summary>
public static class RecordingConfiguration
{
    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal)
    {
        "nvidia", "amd", "intel", "xbox", "obs", "custom",
    };

    /// <summary>Reads the new list, or the legacy primary folder without changing saved data.</summary>
    public static IReadOnlyList<RecordingDirectory> GetDirectories(AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.RecordingDirectories is { Count: > 0 })
        {
            return data.RecordingDirectories
                .Select(directory => new RecordingDirectory { Path = directory.Path, Source = directory.Source })
                .ToArray();
        }

        return string.IsNullOrWhiteSpace(data.RecordingsFolder)
            ? []
            : [new RecordingDirectory { Path = data.RecordingsFolder }];
    }

    /// <summary>Canonicalizes roots and rejects duplicate or overlapping recursive watches.</summary>
    public static IReadOnlyList<RecordingDirectory> Normalize(IEnumerable<RecordingDirectory> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var normalized = new List<RecordingDirectory>();
        foreach (var directory in directories)
        {
            if (directory is null || string.IsNullOrWhiteSpace(directory.Path)
                || !Path.IsPathFullyQualified(directory.Path))
            {
                throw new ArgumentException("Choose an absolute recording folder path.", nameof(directories));
            }

            if (!Sources.Contains(directory.Source))
            {
                throw new ArgumentException("Choose a supported recording source or Custom folder.", nameof(directories));
            }

            string path;
            try
            {
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                throw new ArgumentException("That recording folder path is not valid.", nameof(directories), ex);
            }

            if (normalized.Any(existing => IsWithinRoot(path, existing.Path) || IsWithinRoot(existing.Path, path)))
            {
                throw new ArgumentException("Recording folders cannot duplicate or contain one another. Choose separate folders.", nameof(directories));
            }

            normalized.Add(new RecordingDirectory { Path = path, Source = directory.Source });
        }

        return normalized;
    }

    /// <summary>Writes the list and legacy primary together, preserving every other setting.</summary>
    public static void Replace(AppSettingsData data, IEnumerable<RecordingDirectory> directories, bool completeOnboarding = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        var normalized = Normalize(directories);
        data.RecordingDirectories = normalized.ToList();
        data.RecordingsFolder = normalized.Count > 0 ? normalized[0].Path : null;
        if (completeOnboarding)
        {
            data.OnboardingCompleted = true;
        }
    }

    /// <summary>Separator-aware Windows path containment; sibling names do not count as children.</summary>
    public static bool IsWithinRoot(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
