using System.Security.Cryptography;
using System.Text;

namespace mediq.Utilities.Common;

public static class EncryptionHelper
{
    private const int DerivationIterations = 100_000;
    private const int SaltSize = 32;   // bytes
    private const int IvSize = 16;     // bytes (AES block size)
    private const int KeySize = 32;    // bytes (AES-256)

    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    public static string Encrypt(string plainText, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var key = DeriveKey(passphrase, salt);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor(key, iv);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var output = new byte[SaltSize + IvSize + cipherBytes.Length];
        Buffer.BlockCopy(salt, 0, output, 0, SaltSize);
        Buffer.BlockCopy(iv, 0, output, SaltSize, IvSize);
        Buffer.BlockCopy(cipherBytes, 0, output, SaltSize + IvSize, cipherBytes.Length);

        return Convert.ToBase64String(output);
    }

    public static string Decrypt(string cipherText, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(cipherText);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        var payload = Convert.FromBase64String(cipherText);
        if (payload.Length < SaltSize + IvSize)
            throw new ArgumentException("Cipher text payload is too short.", nameof(cipherText));

        var salt = new byte[SaltSize];
        var iv = new byte[IvSize];
        var cipherBytes = new byte[payload.Length - SaltSize - IvSize];

        Buffer.BlockCopy(payload, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(payload, SaltSize, iv, 0, IvSize);
        Buffer.BlockCopy(payload, SaltSize + IvSize, cipherBytes, 0, cipherBytes.Length);

        var key = DeriveKey(passphrase, salt);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor(key, iv);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, DerivationIterations, HashAlgorithm, KeySize);
}
