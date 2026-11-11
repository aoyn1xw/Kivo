using Kivo.Core.Models;
using Kivo.Core.Settings;

namespace Kivo.Core.Clips;

/// <summary>Prevents a root's reconciliation from removing another root's catalog entries.</summary>
public sealed class ScopedClipStore : IClipStore
{
    private readonly IClipStore _inner;
    private readonly string _root;

    public ScopedClipStore(IClipStore inner, string root)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public void AddOrUpdate(ClipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Contains(entry.FullPath))
        {
            throw new ArgumentException("The recording is outside this configured folder.", nameof(entry));
        }

        _inner.AddOrUpdate(entry);
    }

    public bool RemoveByPath(string fullPath) => Contains(fullPath) && _inner.RemoveByPath(fullPath);

    public ClipEntry? FindByPath(string fullPath) => Contains(fullPath) ? _inner.FindByPath(fullPath) : null;

    public IReadOnlyList<ClipEntry> Snapshot() => _inner.Snapshot().Where(entry => Contains(entry.FullPath)).ToArray();

    public void ReplaceAll(IEnumerable<ClipEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var replacement = entries.ToArray();
        if (replacement.Any(entry => !Contains(entry.FullPath)))
        {
            throw new ArgumentException("The recording is outside this configured folder.", nameof(entries));
        }

        foreach (var existing in Snapshot())
        {
            _inner.RemoveByPath(existing.FullPath);
        }

        foreach (var entry in replacement)
        {
            _inner.AddOrUpdate(entry);
        }
    }

    private bool Contains(string fullPath) => RecordingConfiguration.IsWithinRoot(fullPath, _root);
}
