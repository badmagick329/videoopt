using System.Collections.ObjectModel;
using System.Reflection;
using FluentAssertions;
using VideoOptimiser.Domain.Jobs;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// Failure categories are stored in <c>jobs.failure_category</c> and matched later, so renaming one strands the jobs that
/// already carry it. Each value is pinned here, and a new constant without a pin fails.
/// </summary>
public sealed class FailureCategoriesTests
{
    [Theory]
    [InlineData(FailureCategories.SourceMissing, "SourceMissing")]
    [InlineData(FailureCategories.SourceNotReady, "SourceNotReady")]
    [InlineData(FailureCategories.SourceOutsideWatchRoot, "SourceOutsideWatchRoot")]
    [InlineData(FailureCategories.Ineligible, "Ineligible")]
    [InlineData(FailureCategories.SourceChanged, "SourceChanged")]
    [InlineData(FailureCategories.ProcessingFailed, "ProcessingFailed")]
    [InlineData(FailureCategories.StagingFailed, "StagingFailed")]
    [InlineData(FailureCategories.CrfSearchFailed, "CrfSearchFailed")]
    [InlineData(FailureCategories.EncodeFailed, "EncodeFailed")]
    [InlineData(FailureCategories.DownloadFailed, "DownloadFailed")]
    [InlineData(FailureCategories.Interrupted, "Interrupted")]
    [InlineData(FailureCategories.RemoteUnavailable, "RemoteUnavailable")]
    [InlineData(FailureCategories.RemoteConfigurationChanged, "RemoteConfigurationChanged")]
    [InlineData(FailureCategories.Cancelled, "Cancelled")]
    [InlineData(FailureCategories.NotWorthConverting, "NotWorthConverting")]
    [InlineData(FailureCategories.UnsupportedAudio, "UnsupportedAudio")]
    [InlineData(FailureCategories.ValidationFailed, "ValidationFailed")]
    [InlineData(FailureCategories.StageStalled, "StageStalled")]
    [InlineData(FailureCategories.Rejected, "Rejected")]
    [InlineData(FailureCategories.FinalizationIncomplete, "FinalizationIncomplete")]
    [InlineData(FailureCategories.FinalizationFailed, "FinalizationFailed")]
    [InlineData(FailureCategories.ManualInterventionRequired, "ManualInterventionRequired")]
    public void AStoredCategoryKeepsItsValue(string category, string stored) => category.Should().Be(stored);

    [Fact]
    public void EveryCategoryIsPinned()
    {
        var constants = typeof(FailureCategories).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);
        // The pinned values read straight off the Theory's rows, so this cannot drift from what the Theory checks.
        var pinned = typeof(FailureCategoriesTests).GetMethod(nameof(AStoredCategoryKeepsItsValue))!.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(InlineDataAttribute))
            .Select(attribute => (string)((ReadOnlyCollection<CustomAttributeTypedArgument>)attribute.ConstructorArguments[0].Value!)[1].Value!);

        constants.Should().BeEquivalentTo(pinned);
    }
}
