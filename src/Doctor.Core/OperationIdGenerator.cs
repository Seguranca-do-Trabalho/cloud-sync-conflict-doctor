namespace Doctor.Core;

using System.Security.Cryptography;

/// <summary>
/// Gerador de operation_id baseado em 128 bits CSPRNG (T-09/R9).
/// 
/// O id é independente de relógio e nunca colide entre lotes simultâneos.
/// Forma: hex minúsculo de 32 caracteres (128 bits = 16 bytes).
/// </summary>
internal static class OperationIdGenerator
{
    private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

    /// <summary>
    /// Gera um operation_id único baseado em entropia criptográfica.
    /// </summary>
    public static string Generate()
    {
        var bytes = new byte[16]; // 128 bits
        _rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
