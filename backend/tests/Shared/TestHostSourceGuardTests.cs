using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CoupleSync.TestSupport;

/// <summary>
/// Source-level guard of issue #16, compiled into EVERY test project that can reach the API (it needs neither Mvc.Testing
/// nor the API, so even <c>CoupleSync.UnitTests</c> runs it). It reads the sources and project files of ALL projects
/// under backend/tests, so it sees what the reflection guard (<c>TestHostGuardTests</c>: one assembly, only classes)
/// cannot: a host built without a class (<c>new WebApplicationFactory&lt;Program&gt;()</c>, <c>WebApplication.CreateBuilder</c>,
/// <c>Program.Main</c>) and a test project that does not link the shared protection. Only
/// <c>Shared/TestApiFactory.cs</c> (the base) and the files that hold the guards' own examples may build a host.
/// </summary>
public sealed class TestHostSourceGuardTests
{
    private static readonly string[] KnownProjects =
    {
        "CoupleSync.UnitTests", "CoupleSync.IntegrationTests", "CoupleSync.InvariantGlobalizationTests",
        "CoupleSync.PostgresTests", "CoupleSync.E2ETests",
    };

    private static readonly string[] KnownFactories =
    {
        "TransactionWebApplicationFactory", "PostgresApiFactory", "E2EWebApplicationFactory", "InvariantApiFactory",
    };

    [Fact]
    public void NoSourceOfAnyTestProject_BuildsAHostOutsideTheSharedBase()
    {
        var scan = TestSourceScan.Load();

        var violations = TestHostSourceRules.FindHostCreation(scan.Sources);

        Assert.True(
            violations.Count == 0,
            "A test host must go through CoupleSync.TestSupport.TestApiFactory (\"Testing\" environment, unreachable DATABASE_URL). "
            + "Found: " + string.Join("; ", violations));
    }

    [Fact]
    public void EveryTestProjectThatCanReachTheApi_LinksTheSharedProtection()
    {
        var scan = TestSourceScan.Load();

        var problems = TestHostSourceRules.FindProjectProblems(scan.Projects);

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>A scan that finds nothing must fail instead of passing.</summary>
    [Fact]
    public void TheScan_ActuallyFoundTheSourcesAndTheKnownFactories()
    {
        var scan = TestSourceScan.Load();

        foreach (var project in KnownProjects)
        {
            Assert.Contains(scan.Projects, p => p.Path == $"{project}/{project}.csproj");
        }

        Assert.Contains(scan.Sources, s => s.Path == "Shared/TestApiFactory.cs");
        Assert.Contains(scan.Sources, s => s.Path == "Shared/TestDatabaseIsolation.cs");
        Assert.Contains(scan.Sources, s => s.Path == "Shared/TestHostSourceGuardTests.cs");
        Assert.True(scan.Sources.Count > 100, $"Only {scan.Sources.Count} .cs files found under {scan.TestsRoot}.");

        var factories = TestHostSourceRules.FindFactoryClasses(scan.Sources);
        foreach (var known in KnownFactories)
        {
            Assert.Contains(known, factories);
        }

        Assert.True(factories.Count >= 10, $"Only {factories.Count} factories found: {string.Join(", ", factories)}");
    }

    /// <summary>
    /// The no-parallelisation attribute lives in the base's own file, so a project cannot link the base (and start hosts)
    /// without it: the test host code shares process-wide state (JWT__SECRET, DATABASE_URL, ...).
    /// </summary>
    [Fact]
    public void TheBaseFile_CarriesTheNoParallelizationAttribute()
    {
        var baseFile = TestSourceScan.Load().Sources.Single(s => s.Path == "Shared/TestApiFactory.cs");

        Assert.Matches(@"\[assembly:\s*CollectionBehavior\(\s*DisableTestParallelization\s*=\s*true\s*\)\]", baseFile.Content);
    }

    [Theory]
    [InlineData("var f = new WebApplicationFactory<Program>();")]
    [InlineData("using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => { });")]
    [InlineData("var f = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory <Program>();")]
    [InlineData("class Mine : WebApplicationFactory<Program> { }")]
    [InlineData("internal sealed class Mine(int x)\n    : IDisposable, WebApplicationFactory<Program> { }")]
    [InlineData("var b = WebApplication.CreateBuilder(args);")]
    [InlineData("var b = WebApplication.CreateSlimBuilder();")]
    [InlineData("var b = Host.CreateDefaultBuilder();")]
    [InlineData("var b = WebHost.CreateDefaultBuilder();")]
    [InlineData("var b = new HostBuilder();")]
    [InlineData("var b = new WebHostBuilder();")]
    [InlineData("var s = new TestServer(builder);")]
    [InlineData("Program.Main(new string[0]);")]
    [InlineData("var p = new Program();")]
    public void TheRules_FlagAHostBuiltOutsideTheSharedBase(string code)
    {
        var found = TestHostSourceRules.FindHostCreation(new[] { new SourceFile("CoupleSync.UnitTests/Example.cs", code) });

        Assert.Single(found);
    }

    [Theory]
    [InlineData("class Mine : TestApiFactory { }")]
    [InlineData("static WebApplicationFactory<Program> WithSender(TestApiFactory f) => f.WithWebHostBuilder(b => { });")]
    [InlineData("// new WebApplicationFactory<Program>() is what the guard forbids")]
    [InlineData("/* WebApplication.CreateBuilder(args) */ var x = 1;")]
    [InlineData("var assembly = typeof(Program).Assembly;")]
    [InlineData("var host = new Mine().Services; var url = \"http://localhost\";")]
    public void TheRules_AcceptAHostThatGoesThroughTheBase(string code)
    {
        Assert.Empty(TestHostSourceRules.FindHostCreation(new[] { new SourceFile("CoupleSync.UnitTests/Example.cs", code) }));
    }

    [Fact]
    public void TheRules_LetOnlyTheBaseAndTheGuardsOwnExamplesBuildAHost()
    {
        const string code = "class X : WebApplicationFactory<Program> { }";

        var found = TestHostSourceRules.FindHostCreation(new[]
        {
            new SourceFile("Shared/TestApiFactory.cs", code),
            new SourceFile("Shared/TestHostGuardTests.cs", code),
            new SourceFile("Shared/TestHostSourceGuardTests.cs", code),
            new SourceFile("CoupleSync.NewTests/Factory.cs", code),
        });

        Assert.Equal(new[] { "CoupleSync.NewTests/Factory.cs" }, found.Select(f => f.Split(':')[0]));
    }

    [Fact]
    public void TheRules_FlagATestProjectThatReachesTheApiWithoutLinkingTheSharedProtection()
    {
        var unprotectedApiReference = Project("CoupleSync.NewTests/CoupleSync.NewTests.csproj",
            @"<ProjectReference Include=""..\..\src\CoupleSync.Api\CoupleSync.Api.csproj"" />");
        var unprotectedMvcTesting = Project("CoupleSync.OtherTests/CoupleSync.OtherTests.csproj",
            @"<PackageReference Include=""Microsoft.AspNetCore.Mvc.Testing"" Version=""8.0.12"" />",
            Links("TestEmailIsolation.cs", "TestDatabaseIsolation.cs", "TestHostSourceGuardTests.cs"));
        var webSdk = new ProjectFile("CoupleSync.WebTests/CoupleSync.WebTests.csproj", @"<Project Sdk=""Microsoft.NET.Sdk.Web""></Project>");
        var siblingTestProject = Project("CoupleSync.ThirdTests/CoupleSync.ThirdTests.csproj",
            @"<ProjectReference Include=""..\CoupleSync.NewTests\CoupleSync.NewTests.csproj"" />");

        var problems = TestHostSourceRules.FindProjectProblems(new[] { unprotectedApiReference, unprotectedMvcTesting, webSdk, siblingTestProject });

        Assert.Contains(problems, p => p.StartsWith("CoupleSync.NewTests/") && p.Contains("TestDatabaseIsolation.cs"));
        Assert.Contains(problems, p => p.StartsWith("CoupleSync.NewTests/") && p.Contains("TestHostSourceGuardTests.cs"));
        Assert.Contains(problems, p => p.StartsWith("CoupleSync.OtherTests/") && p.Contains("TestApiFactory.cs"));
        Assert.Contains(problems, p => p.StartsWith("CoupleSync.OtherTests/") && p.Contains("TestHostGuardTests.cs"));
        Assert.Contains(problems, p => p.StartsWith("CoupleSync.WebTests/") && p.Contains("TestEmailIsolation.cs"));
        Assert.Contains(problems, p => p.StartsWith("CoupleSync.ThirdTests/") && p.Contains("TestDatabaseIsolation.cs"));
    }

    [Fact]
    public void TheRules_AcceptAProjectThatLinksTheProtection_AndIgnoreOneThatCannotReachTheApi()
    {
        var protectedProject = Project("CoupleSync.NewTests/CoupleSync.NewTests.csproj",
            @"<PackageReference Include=""Microsoft.AspNetCore.Mvc.Testing"" Version=""8.0.12"" />"
            + @"<ProjectReference Include=""..\..\src\CoupleSync.Api\CoupleSync.Api.csproj"" />",
            Links("TestEmailIsolation.cs", "TestDatabaseIsolation.cs", "TestHostSourceGuardTests.cs", "TestApiFactory.cs", "TestHostGuardTests.cs"));
        var apiOnly = Project("CoupleSync.UnitTests/CoupleSync.UnitTests.csproj",
            @"<ProjectReference Include=""..\..\src\CoupleSync.Api\CoupleSync.Api.csproj"" />",
            Links("TestEmailIsolation.cs", "TestDatabaseIsolation.cs", "TestHostSourceGuardTests.cs"));
        var harmless = Project("CoupleSync.PureTests/CoupleSync.PureTests.csproj",
            @"<ProjectReference Include=""..\..\src\CoupleSync.Domain\CoupleSync.Domain.csproj"" />");

        Assert.Empty(TestHostSourceRules.FindProjectProblems(new[] { protectedProject, apiOnly, harmless }));
    }

    private static string Links(params string[] files) =>
        string.Concat(files.Select(f => $@"<Compile Include=""..\Shared\{f}"" Link=""{f}"" />"));

    private static ProjectFile Project(string path, string references, string links = "") =>
        new(path, $@"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup>{references}{links}</ItemGroup></Project>");
}

internal sealed record SourceFile(string Path, string Content);

internal sealed record ProjectFile(string Path, string Content);

internal sealed record TestSourceScan(string TestsRoot, IReadOnlyList<SourceFile> Sources, IReadOnlyList<ProjectFile> Projects)
{
    /// <summary>Walks up from the test binary until it finds backend/CoupleSync.sln with its tests/Shared folder.</summary>
    internal static string FindTestsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var tests = System.IO.Path.Combine(dir.FullName, "tests");
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "CoupleSync.sln"))
                && File.Exists(System.IO.Path.Combine(tests, "Shared", "TestApiFactory.cs")))
            {
                return tests;
            }
        }

        throw new InvalidOperationException(
            $"backend/CoupleSync.sln with tests/Shared not found above {AppContext.BaseDirectory}.");
    }

    internal static TestSourceScan Load()
    {
        var root = FindTestsRoot();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: System.IO.Path.GetRelativePath(root, f).Replace('\\', '/')))
            .Where(f => !f.Relative.Split('/').Any(part => part is "bin" or "obj" or "TestResults"))
            .ToList();

        return new TestSourceScan(
            root,
            files.Where(f => f.Relative.EndsWith(".cs", StringComparison.Ordinal))
                .Select(f => new SourceFile(f.Relative, File.ReadAllText(f.Full))).ToList(),
            files.Where(f => f.Relative.EndsWith(".csproj", StringComparison.Ordinal))
                .Select(f => new ProjectFile(f.Relative, File.ReadAllText(f.Full))).ToList());
    }
}

internal static class TestHostSourceRules
{
    /// <summary>The base, and the two guards that keep deliberately unprotected examples.</summary>
    private static readonly HashSet<string> MayBuildHosts = new(StringComparer.Ordinal)
    {
        "Shared/TestApiFactory.cs", "Shared/TestHostGuardTests.cs", "Shared/TestHostSourceGuardTests.cs",
    };

    private static readonly (string Name, Regex Pattern)[] HostCreation =
    {
        ("new WebApplicationFactory<...>()", new Regex(@"\bnew\s+(?:[\w.]+\.)?WebApplicationFactory\s*<")),
        ("class deriving from WebApplicationFactory<...>", new Regex(@"\bclass\s+\w+[^{;]*:[^{;]*\bWebApplicationFactory\s*<")),
        ("WebApplication.Create*", new Regex(@"\bWebApplication\s*\.\s*Create\w*\s*\(")),
        ("new WebApplicationBuilder", new Regex(@"\bnew\s+WebApplicationBuilder\b")),
        ("Host.Create* / WebHost.Create*", new Regex(@"\b(?:Web)?Host\s*\.\s*Create\w*\s*\(")),
        ("new HostBuilder / WebHostBuilder", new Regex(@"\bnew\s+(?:Web)?HostBuilder\s*\(")),
        ("new TestServer", new Regex(@"\bnew\s+TestServer\s*\(")),
        ("Program.Main", new Regex(@"\bProgram\s*\.\s*Main\b")),
        ("new Program()", new Regex(@"\bnew\s+Program\s*\(")),
    };

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex LineComment = new(@"(^|\s)//[^\r\n]*");
    private static readonly Regex FactoryClass = new(@"\bclass\s+(\w+)\s*(?:\([^)]*\))?\s*:\s*TestApiFactory\b");

    internal static IReadOnlyList<string> FindHostCreation(IEnumerable<SourceFile> sources)
    {
        var found = new List<string>();
        foreach (var source in sources.Where(s => !MayBuildHosts.Contains(s.Path)))
        {
            var code = StripComments(source.Content);
            foreach (var (name, pattern) in HostCreation)
            {
                if (pattern.IsMatch(code))
                {
                    found.Add($"{source.Path}: {name}");
                }
            }
        }

        return found;
    }

    internal static IReadOnlyList<string> FindFactoryClasses(IEnumerable<SourceFile> sources) =>
        sources.SelectMany(s => FactoryClass.Matches(StripComments(s.Content)).Select(m => m.Groups[1].Value)).ToList();

    internal static IReadOnlyList<string> FindProjectProblems(IEnumerable<ProjectFile> projects)
    {
        var problems = new List<string>();
        foreach (var project in projects)
        {
            var xml = XDocument.Parse(project.Content);
            var references = xml.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => Normalize((string?)e.Attribute("Include"))).ToList();
            var packages = xml.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? "").ToList();
            var linked = xml.Descendants().Where(e => e.Name.LocalName == "Compile")
                .Select(e => Normalize((string?)e.Attribute("Include"))).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var usesMvcTesting = packages.Contains("Microsoft.AspNetCore.Mvc.Testing", StringComparer.OrdinalIgnoreCase);
            var reachesTheApi = usesMvcTesting
                || references.Any(r => r.EndsWith("/CoupleSync.Api.csproj", StringComparison.OrdinalIgnoreCase))
                // Anything that is not one of the application projects (another test project, a helper) can carry the API.
                || references.Any(r => !r.Contains("/src/", StringComparison.Ordinal))
                || ((string?)xml.Root?.Attribute("Sdk") ?? "").Contains("Web", StringComparison.OrdinalIgnoreCase)
                || xml.Descendants().Any(e => e.Name.LocalName == "FrameworkReference"
                    && ((string?)e.Attribute("Include") ?? "").StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
            if (!reachesTheApi)
            {
                continue;
            }

            var required = new List<string> { "TestDatabaseIsolation.cs", "TestEmailIsolation.cs", "TestHostSourceGuardTests.cs" };
            if (usesMvcTesting)
            {
                required.AddRange(new[] { "TestApiFactory.cs", "TestHostGuardTests.cs" });
            }

            foreach (var file in required.Where(f => !linked.Contains($"../Shared/{f}")))
            {
                problems.Add($"{project.Path} can reach the API but does not link ..\\Shared\\{file}");
            }
        }

        return problems;
    }

    private static string Normalize(string? path) => (path ?? "").Replace('\\', '/');

    internal static string StripComments(string code) => LineComment.Replace(BlockComment.Replace(code, " "), "$1");
}
