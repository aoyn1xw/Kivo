using System.IO;

namespace Kivo.Windows.Bootstrap;

/// <summary>Parses the small command-line surface (used for diagnostics/smoke runs).</summary>
public sealed record CliOptions
{
    public bool SmokeTest { get; private init; }

    public int SmokeSeconds { get; private init; } = 10;

    public string? Folder { get; private init; }

    public int? Port { get; private init; }

    public bool Background { get; private init; }

    /// <summary>Isolates diagnostic runs from the user's normal settings.</summary>
    public string? DataDirectory { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--background":
                    options = options with { Background = true };
                    break;
                case "--data-directory" when i + 1 < args.Length:
                    options = options with { DataDirectory = Path.GetFullPath(args[++i]) };
                    break;
                case "--smoke":
                    options = options with { SmokeTest = true };
                    break;
                case "--smoke-seconds" when i + 1 < args.Length && int.TryParse(args[++i], out var seconds):
                    options = options with { SmokeSeconds = Math.Clamp(seconds, 1, 300) };
                    break;
                case "--folder" when i + 1 < args.Length:
                    options = options with { Folder = args[++i] };
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out var port):
                    options = options with { Port = Math.Clamp(port, 0, 65535) };
                    break;
            }
        }

        return options;
    }
}
