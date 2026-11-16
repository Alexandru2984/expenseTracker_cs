using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace ExpenseTracker.Api.Services;

public sealed class MfaSecrets(IConfiguration config)
{
    private byte[]? Master()
    {
        try
        {
            var key = Convert.FromBase64String(config["Mfa:EncryptionKey"] ?? "");
            return key.Length == 32 ? key : null;
        }
        catch (FormatException) { return null; }
    }
    public bool KeyConfigured => Master() is not null;
    public bool Available => config.GetValue("Mfa:AllowEnrollment", true) && KeyConfigured;
    private byte[] Key() => HKDF.DeriveKey(HashAlgorithmName.SHA256,
        Master() ?? throw new CryptographicException("MFA key unavailable"), 32, info: Encoding.UTF8.GetBytes("expense-tracker:mfa-encryption:v1"));
    private static byte[] Context(Guid userId) => Encoding.UTF8.GetBytes("expense-tracker:mfa-secret:v1:" + userId.ToString("N"));
    public string Protect(Guid userId, string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret); var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(Key(), 16); aes.Encrypt(nonce, plain, cipher, tag, Context(userId));
        return "v1." + WebEncoders.Base64UrlEncode(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public string Unprotect(Guid userId, string envelope)
    {
        if (!envelope.StartsWith("v1.", StringComparison.Ordinal)) throw new CryptographicException("Invalid MFA envelope");
        var bytes = WebEncoders.Base64UrlDecode(envelope[3..]);
        if (bytes.Length < 29) throw new CryptographicException("Invalid MFA envelope");
        var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(Key(), 16); aes.Decrypt(bytes.AsSpan(0,12), bytes.AsSpan(28), bytes.AsSpan(12,16), plain, Context(userId));
        return Encoding.UTF8.GetString(plain);
    }
    public static string NormalizeRecovery(string input) => input.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
    public static string RecoveryHash(Guid userId, string code) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("mfa-recovery:v1:" + userId.ToString("N") + ":" + NormalizeRecovery(code))));
    public static string NewRecovery()
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        return string.Join("-", Enumerable.Range(0,4).Select(i => hex.Substring(i*5,5)));
    }
}
