namespace Talon.Interop;

/// <summary>Describes how a signature-bound member is used.</summary>
[Flags]
public enum SignatureUseFlags
{
    /// <summary>Infers the use from the member type.</summary>
    Auto = 0,
    /// <summary>Initializes a native pointer or delegate.</summary>
    Pointer = 1,
    /// <summary>Creates a hook for the resolved function.</summary>
    Hook = 2,
    /// <summary>Applies the configured offset.</summary>
    Offset = 4,
}

/// <summary>Selects how a signature match is resolved.</summary>
public enum SignatureScanType
{
    /// <summary>Returns the matching executable address.</summary>
    Text,
    /// <summary>Returns the static address referenced by the matching instruction.</summary>
    StaticAddress,
}

/// <summary>Initializes a field or property from a game-code signature.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SignatureAttribute(string signature) : Attribute
{
    /// <summary>Gets the space-separated byte pattern. Use <c>??</c> as a wildcard.</summary>
    public string Signature { get; } = signature;
    /// <summary>Gets how the initialized member is used.</summary>
    public SignatureUseFlags UseFlags { get; init; } = SignatureUseFlags.Auto;
    /// <summary>Gets how the matching address is resolved.</summary>
    public SignatureScanType ScanType { get; init; } = SignatureScanType.Text;
    /// <summary>Gets the detour method name for a hook member.</summary>
    public string? DetourName { get; init; }
    /// <summary>Gets the byte offset applied to the match.</summary>
    public int Offset { get; init; }
    /// <summary>Gets whether a failed scan leaves the member uninitialized.</summary>
    public bool Fallibility { get; init; }
}
