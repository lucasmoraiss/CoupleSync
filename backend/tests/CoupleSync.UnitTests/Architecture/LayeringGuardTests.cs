using System.Text.RegularExpressions;
using CoupleSync.Application.Common.Exceptions;

namespace CoupleSync.UnitTests.Architecture;

/// <summary>Guards for the "Application without EF Core" rule: the Application layer never sees raw EF exceptions.</summary>
public sealed class LayeringGuardTests
{
    [Fact]
    public void ApplicationAssembly_ReferencesNoEntityFrameworkCoreAssembly()
    {
        var references = typeof(DataStoreException).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(references);
    }

    // Any call of SaveChanges/SaveChangesAsync on anything: "_dbContext.SaveChangesAsync(", "db.SaveChanges ()",
    // or the call on its own line after the receiver.
    private static readonly Regex SaveCall = new(@"\.SaveChanges(Async)?\s*\(", RegexOptions.Compiled);

    // Files of the Infrastructure project that may contain a save call, and why. Everything else must go through
    // DbSaveTranslator.SaveAsync, which turns EF Core failures into the application's typed exceptions.
    private static readonly IReadOnlyDictionary<string, string> AllowedSaveCalls = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Persistence/Seeders/CategoryRulesSeeder.cs"] = "start-up seeder: no request is waiting for a typed error",
        ["BackgroundJobs/NotificationDispatcherJob.cs"] = RepositoryCaller,
        ["BackgroundJobs/OcrBackgroundJob.cs"] = RepositoryCaller,
    };

    private const string RepositoryCaller = "calls SaveChangesAsync of a repository interface (which uses the translator)";

    [Theory]
    [InlineData("await _dbContext.SaveChangesAsync(ct);", true)]
    [InlineData("db.SaveChanges();", true)]
    [InlineData("context.SaveChangesAsync (cancellationToken)", true)]
    [InlineData("await context\n    .SaveChangesAsync(ct);", true)]
    [InlineData("return DbSaveTranslator.SaveAsync(_dbContext, ct);", false)]
    [InlineData("public Task SaveChangesAsync(CancellationToken ct)", false)]
    public void SaveCallPattern_RecognisesEverySpellingOfADirectSave(string code, bool expected)
        => Assert.Equal(expected, SaveCall.IsMatch(code));

    [Fact]
    public void Infrastructure_SavesOnlyThroughDbSaveTranslator()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "CoupleSync.Infrastructure")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var infrastructure = Path.Combine(root!, "src", "CoupleSync.Infrastructure");
        var files = Directory
            .EnumerateFiles(infrastructure, "*.cs", SearchOption.AllDirectories)
            .Select(file => (Full: file, Relative: Path.GetRelativePath(infrastructure, file).Replace('\\', '/')))
            .Where(f => !f.Relative.StartsWith("obj/", StringComparison.Ordinal)
                        && !f.Relative.StartsWith("bin/", StringComparison.Ordinal)
                        && !f.Relative.StartsWith("Persistence/Migrations/", StringComparison.Ordinal)
                        && f.Relative != "Persistence/DbSaveTranslator.cs")
            .ToList();

        // The scan really saw the project: the repositories are there, and so is every allow-listed file.
        Assert.True(files.Count(f => f.Relative.EndsWith("Repository.cs", StringComparison.Ordinal)) >= 10,
            $"only {files.Count} files scanned under {infrastructure}");
        Assert.All(AllowedSaveCalls.Keys, allowed => Assert.Contains(files, f => f.Relative == allowed));
        Assert.True(SaveCall.IsMatch(File.ReadAllText(Path.Combine(infrastructure, "Persistence", "DbSaveTranslator.cs"))),
            "the translator itself should be the one place that saves");

        var offenders = files
            .Where(f => SaveCall.IsMatch(File.ReadAllText(f.Full)) && !AllowedSaveCalls.ContainsKey(f.Relative))
            .Select(f => f.Relative)
            .ToList();
        Assert.Empty(offenders);

        // A file allowed because it only calls repositories must not hold a DbContext at all.
        var repositoryCallersWithAContext = AllowedSaveCalls
            .Where(a => a.Value == RepositoryCaller)
            .Select(a => a.Key)
            .Where(relative => File.ReadAllText(Path.Combine(infrastructure, relative)).Contains("DbContext", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(repositoryCallersWithAContext);
    }
}
