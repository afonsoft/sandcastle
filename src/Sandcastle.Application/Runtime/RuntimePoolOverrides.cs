using System.Collections.Concurrent;

namespace Sandcastle.Application.Runtime;

/// <inheritdoc />
public sealed class RuntimePoolOverrides : IRuntimePoolOverrides
{
    private readonly ConcurrentDictionary<string, int> _desired = new(StringComparer.Ordinal);
    /// <inheritdoc />
    public IReadOnlyDictionary<string, int> Snapshot() => _desired;
    /// <inheritdoc />
    public void Set(string image, int desired) => _desired[image] = desired;
}