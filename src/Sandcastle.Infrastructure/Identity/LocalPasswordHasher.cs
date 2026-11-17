using System.Security.Cryptography;

namespace Sandcastle.Infrastructure.Identity;

/// <summary>
/// SPEC-158 RF-002: hash de senha local — PBKDF2-SHA256, 210k iterações,
/// salt de 16 bytes. Formato:
/// <c>pbkdf2-sha256$&lt;iter&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c>.
/// </summary>
public static class LocalPasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>Gera o hash persistível da senha.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
        return $"pbkdf2-sha256${Iterations}$"
            + $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>Verifica a senha contra o hash; false em formato inválido.</summary>
    public static bool Verify(string password, string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var parts = encoded.Split('$');
        if (parts is not ["pbkdf2-sha256", var iterStr, var saltB64, var keyB64]
            || !int.TryParse(iterStr, out var iter))
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(saltB64);
            expected = Convert.FromBase64String(keyB64);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}