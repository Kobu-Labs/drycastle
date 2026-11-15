using System.Security.Cryptography;
using System.Text;

namespace Drycastle.LauncherCore;

/// <summary>The private asset pack could not be installed. The game itself is installed and playable without it.</summary>
public sealed class PackException : Exception
{
    /// <summary>The pack password is wrong (the download was fine); any other failure is a download or file problem.</summary>
    public bool WrongPassword { get; }
    public PackException(string message, bool wrongPassword, Exception? inner = null) : base(message, inner) => WrongPassword = wrongPassword;
}

/// <summary>
/// The encryption of the private asset pack: the file <c>openssl enc -aes-256-cbc -pbkdf2 -md sha256 -iter N -salt</c> writes
/// ("Salted__", 8 bytes of salt, then AES-256-CBC with PKCS7 padding; key and IV are the 48 bytes of PBKDF2-HMAC-SHA256 over the password).
/// The password is only ever used on the player's machine: the file is served as it is, over plain HTTP, and is useless without it.
/// CBC has no integrity check of its own, so the caller compares the sha256 of the result with the manifest (<see cref="PackRef.PlainSha256"/>).
/// </summary>
public static class PackCrypto
{
    public const int DefaultIterations = 600_000;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("Salted__");

    static Aes Cipher(string password, byte[] salt, int iterations)
    {
        var kiv = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 48);
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = kiv[..32];
        aes.IV = kiv[32..];
        return aes;
    }

    /// <summary>Decrypts <paramref name="src"/> into <paramref name="dst"/>. A wrong password usually fails here (bad padding); when it does not, the result is garbage and the sha256 check catches it.</summary>
    public static void Decrypt(string src, string dst, string password, int iterations)
    {
        using var input = File.OpenRead(src);
        var head = new byte[16];
        if (input.ReadAtLeast(head, 16, false) < 16 || !head.AsSpan(0, 8).SequenceEqual(Magic))
            throw new PackException("the asset pack file is not an encrypted pack", false);
        using var aes = Cipher(password, head[8..], iterations);
        try
        {
            using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var output = File.Create(dst);
            crypto.CopyTo(output);
        }
        catch (CryptographicException e)
        {
            throw new PackException("wrong asset pack password", true, e);
        }
    }

    /// <summary>The inverse of <see cref="Decrypt"/> (the publish script uses openssl; this is for the tests).</summary>
    public static void Encrypt(string src, string dst, string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        using var aes = Cipher(password, salt, iterations);
        using var output = File.Create(dst);
        output.Write(Magic);
        output.Write(salt);
        using var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write);
        using var input = File.OpenRead(src);
        input.CopyTo(crypto);
    }
}
