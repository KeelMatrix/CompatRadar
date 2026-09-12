using System.Text;
using System.Text.Json;

namespace KeelMatrix.CompatRadar.Tests;

/// <summary>
/// Records the compatibility outcome each regression test actually observed so an evidence run can
/// recompute detection metrics from observed classifications instead of from test pass/fail alone.
///
/// Recording is inert unless the test run supplies <see cref="OutcomesPathVariable"/>; the
/// variable is read by the test process only and is never used by the product comparison
/// environment to describe a candidate.
/// </summary>
internal static class ObservedOutcomes
{
    public const string OutcomesPathVariable = "COMPATRADAR_TEST_OUTCOMES_PATH";

    private static readonly object SyncRoot = new();

    public static void Record(string caseId, ResultClassification classification)
    {
        var path = Environment.GetEnvironmentVariable(OutcomesPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var line = JsonSerializer.Serialize(new
        {
            @case = caseId,
            classification = classification switch
            {
                ResultClassification.Compatible => "COMPATIBLE",
                ResultClassification.FutureRegression => "FUTURE_REGRESSION",
                ResultClassification.InconclusiveBaselineFailed => "INCONCLUSIVE_BASELINE_FAILED",
                ResultClassification.InconclusiveFlaky => "INCONCLUSIVE_FLAKY",
                ResultClassification.InconclusiveExecution => "INCONCLUSIVE_EXECUTION",
                _ => "UNSUPPORTED"
            }
        });

        lock (SyncRoot)
        {
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
