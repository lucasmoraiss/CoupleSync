using System.Text.RegularExpressions;

namespace CoupleSync.Application.Ai;

/// <summary>
/// How documents and phones are actually typed in Brazil — with dots, spaces, hyphens or nothing, with or without
/// area code — shared by the privacy filter (what goes out) and the output validator (what comes back).
/// Every pattern refuses to start in the middle of a number or right after a separator of an amount or a date, so
/// "R$ 1.234.567,89", "05/10/2026", "03/10" and "2025-2026" are left alone.
/// </summary>
internal static partial class ContactPatterns
{
    // Not preceded by a digit nor by what glues digits in amounts, dates and installments.
    private const string Start = @"(?<![\d.,/-])";

    // Not followed by a digit nor by a "/" (a date or an installment goes on).
    private const string End = @"(?![\d/])";

    [GeneratedRegex(Start + @"\d{2}[.\s]?\d{3}[.\s]?\d{3}[/\s]?\d{4}[-\s]?\d{2}" + End, RegexOptions.CultureInvariant)]
    private static partial Regex Cnpj();

    [GeneratedRegex(Start + @"\d{3}[.\s]?\d{3}[.\s]?\d{3}[-\s]?\d{2}" + End, RegexOptions.CultureInvariant)]
    private static partial Regex Cpf();

    // Country code, area code (with or without parentheses and leading zero), the detached 9, two blocks of four.
    [GeneratedRegex(@"(?<![\d.,/-])(?:\+?55[\s.-]?)?\(?0?\d{2}\)?[\s.-]?(?:9[\s.-]?)?\d{4}[\s.-]?\d{4}" + End, RegexOptions.CultureInvariant)]
    private static partial Regex PhoneWithAreaCode();

    // Mobile without area code: 9 and eight digits.
    [GeneratedRegex(Start + @"9[\s.]?\d{4}[\s.-]?\d{4}" + End, RegexOptions.CultureInvariant)]
    private static partial Regex MobileWithoutAreaCode();

    // Landline without area code, only in its usual spelling (0000-0000) and never when it reads as two years.
    [GeneratedRegex(Start + @"(?!(?:19|20)\d{2}-(?:19|20)\d{2})[2-5]\d{3}-\d{4}(?![\d/-])", RegexOptions.CultureInvariant)]
    private static partial Regex LandlineWithoutAreaCode();

    [GeneratedRegex(@"\b0800\b", RegexOptions.CultureInvariant)]
    private static partial Regex TollFree();

    /// <summary>Documents first (a CNPJ contains what looks like a CPF, a CPF what looks like a phone), then phones.</summary>
    private static readonly Func<Regex>[] InOrder = [Cnpj, Cpf, PhoneWithAreaCode, MobileWithoutAreaCode, LandlineWithoutAreaCode];

    public static string Replace(string text, string replacement)
    {
        foreach (var pattern in InOrder) text = pattern().Replace(text, replacement);
        return text;
    }

    public static bool HasDocument(string text) => Cnpj().IsMatch(text) || Cpf().IsMatch(text);

    public static bool HasPhone(string text)
        => PhoneWithAreaCode().IsMatch(text) || MobileWithoutAreaCode().IsMatch(text) || LandlineWithoutAreaCode().IsMatch(text) || TollFree().IsMatch(text);

    // ---------------------------------------------------------------- what a number in a statement line is

    [GeneratedRegex(@"(?<!\d)(?:\d{11}|\d{14})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex RawDocumentLength();

    /// <summary>True when any of the patterns above matches: a document or a phone in any spelling, valid or not.</summary>
    public static bool HasAny(string text) => InOrder.Any(pattern => pattern().IsMatch(text));

    /// <summary>
    /// True when a document or a phone is written the way people write them — with its dots, hyphen, slash,
    /// parentheses or spaces ("123.456.789-09", "(11) 91234-5678"), whatever the digits.
    /// </summary>
    public static bool HasFormatted(string text)
        => InOrder.Any(pattern => pattern().Matches(text).Any(match => match.Value.Any(ch => !char.IsAsciiDigit(ch))));

    /// <summary>True when a run of exactly 11 or 14 digits has the check digits of a CPF or of a CNPJ.</summary>
    public static bool HasValidRawDocument(string text)
        => RawDocumentLength().Matches(text).Any(match => match.Length == 11 ? IsCpf(match.ValueSpan) : IsCnpj(match.ValueSpan));

    private static bool IsCpf(ReadOnlySpan<char> digits)
    {
        if (digits.Length != 11 || AllTheSame(digits)) return false;
        return CheckDigit(digits[..9], 10) == digits[9] - '0' && CheckDigit(digits[..10], 11) == digits[10] - '0';

        static int CheckDigit(ReadOnlySpan<char> part, int firstWeight)
        {
            var sum = 0;
            for (var i = 0; i < part.Length; i++) sum += (part[i] - '0') * (firstWeight - i);
            var rest = sum * 10 % 11;
            return rest == 10 ? 0 : rest;
        }
    }

    private static bool IsCnpj(ReadOnlySpan<char> digits)
    {
        if (digits.Length != 14 || AllTheSame(digits)) return false;
        return CheckDigit(digits[..12]) == digits[12] - '0' && CheckDigit(digits[..13]) == digits[13] - '0';

        static int CheckDigit(ReadOnlySpan<char> part)
        {
            // Weights 2 to 9 from the right, starting again at 2.
            var sum = 0;
            for (var i = 0; i < part.Length; i++) sum += (part[part.Length - 1 - i] - '0') * (2 + i % 8);
            var rest = sum % 11;
            return rest < 2 ? 0 : 11 - rest;
        }
    }

    private static bool AllTheSame(ReadOnlySpan<char> digits)
    {
        foreach (var digit in digits)
        {
            if (digit != digits[0]) return false;
        }

        return true;
    }
}
