using System;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.GameRuntime.Hello;

/// <summary>Payload hashing per lumio.hello-wire.v1: SHA-256 over the raw payload UTF-8 bytes, lowercase hex.</summary>
internal static class HelloWireHash
{
    /// <summary>Computes the expected payloadSha256 wire value for a payload string.</summary>
    public static string PayloadSha256Hex(string payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
#if NET10_0_OR_GREATER
        byte[] digest = SHA256.HashData(bytes);
#else
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(bytes);
#endif
        return ToLowercaseHex(digest);
    }

    private static string ToLowercaseHex(byte[] digest)
    {
        const string Digits = "0123456789abcdef";
        char[] chars = new char[digest.Length * 2];
        for (int i = 0; i < digest.Length; i++)
        {
            chars[i * 2] = Digits[digest[i] >> 4];
            chars[(i * 2) + 1] = Digits[digest[i] & 0x0F];
        }

        return new string(chars);
    }
}
