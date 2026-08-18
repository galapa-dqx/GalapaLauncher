namespace Talon.Interop;

/// <summary>Names a byte pattern for a batch scan of executable game code.</summary>
/// <param name="Name">The stable name used to retrieve this query's matches.</param>
/// <param name="Pattern">The space-separated byte pattern. Use <c>??</c> as a wildcard.</param>
public sealed record SignatureQuery(string Name, string Pattern);

/// <summary>Contains the raw candidate addresses produced by a signature batch.</summary>
public sealed class SignatureScanResult
{
    private readonly IReadOnlyDictionary<string, nint[]> matches;

    internal SignatureScanResult(IReadOnlyDictionary<string, nint[]> matches) =>
        this.matches = matches;

    /// <summary>Gets every raw candidate for the named query.</summary>
    /// <exception cref="KeyNotFoundException">The batch did not contain <paramref name="name"/>.</exception>
    public IReadOnlyList<nint> GetMatches(string name) =>
        matches.TryGetValue(name, out var result)
            ? result
            : throw new KeyNotFoundException($"Signature batch has no query named '{name}'.");

    /// <summary>Tries to get every raw candidate for the named query.</summary>
    public bool TryGetMatches(string name, out IReadOnlyList<nint> result)
    {
        if (matches.TryGetValue(name, out var found))
        {
            result = found;
            return true;
        }

        result = [];
        return false;
    }
}
