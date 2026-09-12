namespace Doctor.Core;

using System.Security.Cryptography;

/// <summary>
/// 128-bit CSPRNG-based operation_id generator (T-09/R9).
/// 
/// The id is clock-independent and never collides between simultaneous batches.
/// Format: 32-character lowercase hex (128 bits = 16 bytes).
/// </summary>
internal static class OperationIdGenerator
{
    private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

    /// <summary>
    /// Generates a unique operation_id based on cryptographic entropy.
    /// </summary>
    public static string Generate()
    {
        var bytes = new byte[16]; // 128 bits
        _rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
