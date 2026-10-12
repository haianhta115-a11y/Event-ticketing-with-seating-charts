using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace EventTicketing.Api.Security;

public static class PasswordHasher
{
    public static string HashPassword(string password)
    {
        const int memorySize = 1024;
        const int iterations = 4;
        const int degreeOfParallelism = 1;
        const int saltSize = 16;
        const int hashSize = 32;

        var salt = RandomNumberGenerator.GetBytes(saltSize);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = degreeOfParallelism,
            Iterations = iterations,
            MemorySize = memorySize
        };

        var hash = argon2.GetBytes(hashSize);

        var saltString = EncodeBase64Url(salt);
        var hashString = EncodeBase64Url(hash);

        return $"$argon2id$v=19$m={memorySize},t={iterations},p={degreeOfParallelism}${saltString}${hashString}";
    }

    public static bool VerifyPassword(string phcHash, string password)
    {
        if (string.IsNullOrEmpty(phcHash) || !phcHash.StartsWith("$argon2id$"))
        {
            return false;
        }

        var parts = phcHash.Split('$');
        if (parts.Length != 6) return false;

        // parts[3] contains m=...,t=...,p=...
        var paramsPart = parts[3];
        int memorySize = 1024;
        int iterations = 4;
        int degreeOfParallelism = 1;

        foreach (var p in paramsPart.Split(','))
        {
            if (p.StartsWith("m=")) memorySize = int.Parse(p.Substring(2));
            else if (p.StartsWith("t=")) iterations = int.Parse(p.Substring(2));
            else if (p.StartsWith("p=")) degreeOfParallelism = int.Parse(p.Substring(2));
        }

        var salt = DecodeBase64Url(parts[4]);
        var expectedHash = DecodeBase64Url(parts[5]);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = degreeOfParallelism,
            Iterations = iterations,
            MemorySize = memorySize
        };

        var actualHash = argon2.GetBytes(expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static string EncodeBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string base64)
    {
        // Standard base64 might be used or base64url, so handle standard base64 padding
        string padded = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        padded = padded.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded);
    }
}
