using Archivio.Shared;

namespace Archivio.UnitTests;

public sealed class GuardTests
{
    [Fact]
    public void AgainstNullOrWhiteSpace_ReturnsValidValue()
    {
        var result = Guard.AgainstNullOrWhiteSpace("Archivio", "value");
        Assert.Equal("Archivio", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AgainstNullOrWhiteSpace_RejectsInvalidValue(string? value)
    {
        Assert.Throws<ArgumentException>(() => Guard.AgainstNullOrWhiteSpace(value, "value"));
    }
}
