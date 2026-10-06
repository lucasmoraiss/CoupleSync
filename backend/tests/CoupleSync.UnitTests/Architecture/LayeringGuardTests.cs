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

    [Fact]
    public void Repositories_SaveOnlyThroughDbSaveTranslator()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "CoupleSync.Infrastructure")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root!, "src", "CoupleSync.Infrastructure", "Persistence"), "*Repository.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("dbContext.SaveChangesAsync(", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
