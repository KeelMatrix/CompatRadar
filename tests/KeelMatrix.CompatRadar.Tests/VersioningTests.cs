using System.Globalization;

using NuGet.Versioning;

namespace KeelMatrix.CompatRadar.Tests;

/// <summary>
/// Candidate semantics are NuGet semantics. These tests pin the behaviors that a hand-written
/// ordinal parser got wrong: SemVer 2 build metadata, case-insensitive prerelease labels, and
/// NuGet-normalized equivalence.
/// </summary>
public sealed class VersioningTests
{
    [Fact]
    public void OrdersNuGetCandidatesAndRejectsAmbiguousInputs()
    {
        var result = Versioning.Validate(["1.0.4-preview.1", "1.0.2", "1.0.3-preview.1", "1.0.1"]);

        Assert.True(result.Accepted, result.Error);
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
    [InlineData("11.0.100-preview.7.26381.103")]
    [InlineData("1.0.0+build.20260912")]
    public void AcceptsSupportedCandidateShapes(string value)
    {
        Assert.True(Versioning.TryParse(value, out _));
    }

    [Fact]
    public void SemVer2BuildMetadataIsAcceptedPreservedAndPrecedenceNeutral()
    {
        var accepted = Versioning.Validate(["2.0.0+build.9", "1.0.0+build.1"]);

        Assert.True(accepted.Accepted, accepted.Error);
        Assert.Equal(["1.0.0+build.1", "2.0.0+build.9"], accepted.OrderedCandidates);

        // Build metadata does not change precedence, so it cannot make two candidates distinct
        // participants in a first-bad boundary.
        var equivalent = Versioning.Validate(["1.0.0", "1.0.0+build.1"]);
        Assert.False(equivalent.Accepted);
        Assert.Contains("1.0.0+build.1", equivalent.RejectedCandidates);
        Assert.Contains("1.0.0", equivalent.OrderedCandidates);
    }

    [Fact]
    public void PrereleaseLabelsCompareCaseInsensitivelyAndKeepTheOriginalString()
    {
        var result = Versioning.Validate(["1.0.0-B", "1.0.0-a"]);

        Assert.True(result.Accepted, result.Error);
        Assert.Equal(["1.0.0-a", "1.0.0-B"], result.OrderedCandidates);

        var equivalent = Versioning.Validate(["1.0.0-RC.1", "1.0.0-rc.1"]);
        Assert.False(equivalent.Accepted);
        Assert.Contains("1.0.0-rc.1", equivalent.RejectedCandidates);
    }

    [Fact]
    public void NuGetNormalizedEquivalentVersionsAreRejectedAsDuplicates()
    {
        var twoPart = Versioning.Validate(["1.0", "1.0.0"]);
        Assert.False(twoPart.Accepted);
        Assert.Contains("1.0.0", twoPart.RejectedCandidates);

        var revision = Versioning.Validate(["1.0.0.0", "1.0.0"]);
        Assert.False(revision.Accepted);
        Assert.Contains("1.0.0", revision.RejectedCandidates);
    }

    [Fact]
    public void CandidateOrderingIsCultureIndependent()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var invariant = Versioning.Validate(["1.0.0-I2", "1.0.0-i", "1.0.0-1"]);

            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

            var turkish = Versioning.Validate(["1.0.0-I2", "1.0.0-i", "1.0.0-1"]);

            Assert.True(invariant.Accepted, invariant.Error);
            Assert.Equal(["1.0.0-1", "1.0.0-i", "1.0.0-I2"], invariant.OrderedCandidates);
            Assert.Equal(invariant.OrderedCandidates, turkish.OrderedCandidates);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void BuildMetadataIsIgnoredByThePrecedenceComparerUsedForOrdering()
    {
        Assert.Equal(
            0,
            VersionComparer.VersionRelease.Compare(
                NuGetVersion.Parse("1.0.0+build.1"),
                NuGetVersion.Parse("1.0.0+build.2")));
        Assert.True(
            VersionComparer.VersionRelease.Compare(
                NuGetVersion.Parse("1.0.0-B"),
                NuGetVersion.Parse("1.0.0-a")) > 0);
    }
}
