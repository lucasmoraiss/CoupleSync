using System.Security.Cryptography;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Security;

public sealed class CryptoCoupleJoinCodeGenerator : ICoupleJoinCodeGenerator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int JoinCodeLength = 6;

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