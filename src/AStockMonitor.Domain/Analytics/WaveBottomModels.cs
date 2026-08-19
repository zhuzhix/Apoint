using System.Text.Json;

namespace AStockMonitor.Domain.Analytics;

public sealed record WaveBottomOptions
{
    public const string CurrentAlgorithmVersion = "pair-wave-bottom-v2";

    public string AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;
    public int RequiredDailyBars { get; init; } = 120;
    public int MinimumDailyBars { get; init; } = 60;
    public int CandidateThreshold { get; init; } = 70;
    public int StrongThreshold { get; init; } = 85;

    public void Validate()
    {
        if (RequiredDailyBars < MinimumDailyBars || MinimumDailyBars < 35)
            throw new ArgumentOutOfRangeException(nameof(RequiredDailyBars));
        if (CandidateThreshold is < 1 or > 100 || StrongThreshold is < 1 or > 100 ||
            CandidateThreshold >= StrongThreshold)
            throw new ArgumentOutOfRangeException(nameof(CandidateThreshold));
    }
}

public sealed record WaveBottomComponent(
    string Code,
    string Label,
    int Score,
    bool Matched,
    string Evidence);

public sealed record WaveBottomEvaluation(
    string CalculationStatus,
    string Signal,
    int Score,
    bool TrendGatePassed,
    DateTime? DataAsOf,
    int DailyBarCount,
    string AlgorithmVersion,
    string InputHash,
    IReadOnlyList<WaveBottomComponent> Components)
{
    public string ComponentsJson => JsonSerializer.Serialize(Components);
}
