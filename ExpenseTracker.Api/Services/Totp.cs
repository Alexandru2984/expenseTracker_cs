using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ExpenseTracker.Api.Services;

public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string NewSecret() => Encode(RandomNumberGenerator.GetBytes(20));
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder(); var bits = 0; var value = 0;
        foreach (var item in bytes)
        {
            value = (value << 8) | item; bits += 8;
            while (bits >= 5) { bits -= 5; output.Append(Alphabet[(value >> bits) & 31]); }
        }
        if (bits > 0) output.Append(Alphabet[(value << (5 - bits)) & 31]);
        return output.ToString();
    }
    private static byte[] Decode(string secret)
    {
        var output = new List<byte>(); var bits = 0; var value = 0;
        foreach (var item in secret)
        {
            var index = Alphabet.IndexOf(item);
            if (index < 0) throw new FormatException("Invalid TOTP secret");
            value = (value << 5) | index; bits += 5;
            if (bits >= 8) { bits -= 8; output.Add((byte)(value >> bits)); }
        }
        return output.ToArray();
    }
    public static string Code(string secret, long step, int digits = 6)
    {
        if (step < 0 || digits is not (6 or 8)) throw new ArgumentOutOfRangeException(nameof(step));
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(counter, (ulong)step);
        var hash = HMACSHA1.HashData(Decode(secret), counter); // RFC 6238 SHA-1 profile, not a password hash.
        var offset = hash[^1] & 15;
        var truncated = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (truncated % (digits == 6 ? 1_000_000u : 100_000_000u)).ToString("D" + digits, CultureInfo.InvariantCulture);
    }
    public static long? Verify(string secret, string code, DateTimeOffset now, long? lastAcceptedStep)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;
        var current = now.ToUnixTimeSeconds() / 30; long? matched = null;
        for (var step = Math.Max(0, current - 1); step <= current + 1; step++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(Code(secret, step)));
            if (equal && (lastAcceptedStep is null || step > lastAcceptedStep)) matched = step;
        }
        return matched;
    }
}
