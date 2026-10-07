using System.Text.RegularExpressions;

namespace CoupleSync.UnitTests.Validation;

/// <summary>Guard: user-facing exception messages in the API/Application sources must not be English.</summary>
public sealed class ErrorMessageLanguageTests
{
    private static readonly Regex AppExceptionCall = new(
        @"new (Unauthorized|Forbidden|NotFound|Conflict|BadRequest|UnprocessableEntity)Exception\(\s*""[A-Z_]+"",\s*\$?""([^""]*)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex English = new(
        @"\b(Invalid|expired|not found|must|already|required|cannot|Cannot|You |The )\b", RegexOptions.Compiled);

    [Fact]
    public void ApiAndApplicationExceptionMessages_AreNotEnglish()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "CoupleSync.Api")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var offenders = new List<string>();
        foreach (var project in new[] { "CoupleSync.Api", "CoupleSync.Application" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "src", project), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in AppExceptionCall.Matches(File.ReadAllText(file)))
                if (English.IsMatch(m.Groups[2].Value))
                    offenders.Add($"{Path.GetFileName(file)}: {m.Groups[2].Value}");
        }

        Assert.Empty(offenders);
    }
}
