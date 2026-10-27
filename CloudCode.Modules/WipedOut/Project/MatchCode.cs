using System.Security.Cryptography;
using System.Text;

namespace WipedOut;

/// <summary>
/// Códigos de partida derivados del requestId (patrón del PoC TurnMatch del curso):
/// si el código saliera de un generador aleatorio, reintentar "crear partida"
/// produciría una sala distinta y el jugador acabaría con dos.
/// </summary>
public static class MatchCode
{
    /// <summary>Sin I, O, 0 ni 1: se confunden al dictarlos.</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int Length = 4;

    public static string FromRequestId(string requestId, int attempt = 0)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId}#{attempt}"));
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            // 256 es múltiplo de 32, así que el módulo no sesga el reparto.
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }

    public static string Normalize(string? input) => (input ?? string.Empty).Trim().ToUpperInvariant();

    public static bool IsWellFormed(string? code)
    {
        var normalized = Normalize(code);
        return normalized.Length == Length && normalized.All(Alphabet.Contains);
    }
}
