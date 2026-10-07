using System.Security.Cryptography;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Security;

public sealed class CryptoCoupleJoinCodeGenerator : ICoupleJoinCodeGenerator
{
    // No 0/O/1/I: the code is read aloud and typed by hand.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int JoinCodeLength = 8;

    public string Generate()
    {
        Span<char> buffer = stackalloc char[JoinCodeLength];

        for (var i = 0; i < JoinCodeLength; i++)
        {
            buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(buffer);
    }
}