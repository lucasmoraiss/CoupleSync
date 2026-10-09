using CoupleSync.Application.AppUpdate;

namespace CoupleSync.UnitTests.AppUpdate;

/// <summary>Issue #3 — the tag of a release (and the configured minimum) as the version the app compares.</summary>
[Trait("Category", "AppUpdate")]
public sealed class AppVersionNumberTests
{
    [Theory]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData("v1.1.0", "1.1.0")]
    [InlineData("V1.1.0", "1.1.0")]
    [InlineData("v1.0.0-pit", "1.0.0")]
    [InlineData("1.0.0-pit", "1.0.0")]
    [InlineData("1.2.3+build.7", "1.2.3")]
    [InlineData("  v1.10.0  ", "1.10.0")]
    [InlineData("01.002.0003", "1.2.3")]
    [InlineData("0.0.0", "0.0.0")]
    public void AVersion_IsReducedToItsThreeNumbers(string text, string expected)
        => Assert.Equal(expected, AppVersionNumber.Normalize(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("1.1")]
    [InlineData("1")]
    [InlineData("1.1.0.4")]
    [InlineData("1.1.x")]
    [InlineData("v.1.1.0")]
    [InlineData("vv1.1.0")]
    [InlineData("1.1.0pit")]
    [InlineData("1,1,0")]
    [InlineData("-1.1.0")]
    [InlineData("1.1.0\n2.0.0")]
    [InlineData("release 1.1.0")]
    [InlineData("1.1.99999999999")]
    [InlineData("١.١.٠")]
    public void WhatIsNotAVersion_IsNull_NeverAnException(string? text)
        => Assert.Null(AppVersionNumber.Normalize(text));
}
