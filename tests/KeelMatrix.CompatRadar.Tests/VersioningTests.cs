namespace KeelMatrix.CompatRadar.Tests;

public sealed class VersioningTests
{
    [Fact]
    public void OrdersNuGetCandidatesAndRejectsAmbiguousInputs()
    {
        var result = Versioning.Validate(["1.0.4-preview.1", "1.0.2", "1.0.3-preview.1", "1.0.1"]);

        Assert.True(result.Accepted);
        Assert.Equal(["1.0.1", "1.0.2", "1.0.3-preview.1", "1.0.4-preview.1"], result.OrderedCandidates);

        var rejected = Versioning.Validate(["1.0.0", "1.0.0.0", "not-a-version"]);
        Assert.False(rejected.Accepted);
        Assert.Contains("1.0.0.0", rejected.RejectedCandidates);
        Assert.Contains("not-a-version", rejected.RejectedCandidates);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("9.0.120")]
    [InlineData("17.14.0-preview-25107-01")]
    public void AcceptsSupportedCandidateShapes(string value)
    {
        Assert.True(Versioning.TryParse(value, out _));
    }
}
