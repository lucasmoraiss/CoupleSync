using CoupleSync.Infrastructure.Security;

namespace CoupleSync.UnitTests.Security;

public sealed class CryptoCoupleJoinCodeGeneratorTests
{
    [Fact]
    public void Generate_ReturnsEightCharactersWithoutAmbiguousOnes()
    {
        var generator = new CryptoCoupleJoinCodeGenerator();

        for (var i = 0; i < 2000; i++)
        {
            var code = generator.Generate();

            Assert.Equal(8, code.Length);
            Assert.All(code, c =>
            {
                Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c), $"'{c}' in {code}");
                Assert.DoesNotContain(c, "0O1I");
            });
        }
    }
}
