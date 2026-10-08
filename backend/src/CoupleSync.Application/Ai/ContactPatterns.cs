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
}
