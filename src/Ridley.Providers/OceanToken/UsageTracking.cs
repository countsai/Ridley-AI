namespace Ridley.Providers.OceanToken;

public enum CostSource { Header, DerivedFromSpendDelta, Unknown }

/// <summary>
/// One gateway call. Contains no prompt, transcript, audio or image content: only counts, timings and money.
/// </summary>
public sealed record UsageRecord(
    DateTimeOffset At,
    string Operation,
    string Model,
    int Status,
    int Attempts,
    TimeSpan Latency,
    int? PromptTokens,
    int? CompletionTokens,
    int? ReasoningTokens,
    double? AudioSecondsIn,
    int? InputCharacters,
    decimal? CostUsd,
    CostSource CostSource,
    decimal? KeySpendUsd,
    decimal? KeyBudgetUsd,
    string? RequestId);

public sealed record UsageTotals(int Calls, decimal KnownCostUsd, int CallsWithUnknownCost, long PromptTokens, long CompletionTokens, double AudioSeconds, decimal UnattributedSpendUsd)
{
    /// <summary>Best available estimate of what this process has spent: attributed cost plus spend the gateway reported that no response claimed.</summary>
    public decimal EstimatedSpendUsd => KnownCostUsd + UnattributedSpendUsd;
}

public interface IUsageTracker
{
    void Record(UsageRecord record);
    UsageTotals Totals { get; }
    IReadOnlyList<UsageRecord> Recent(int count);

    /// <summary>Last key spend reported by the gateway, used to derive cost when a response omits its cost header.</summary>
    decimal? LastKeySpendUsd { get; }

    /// <summary>Spend that appeared on the key between calls but was not claimed by any response (e.g. streamed calls report no cost).</summary>
    void RecordUnattributedSpend(decimal amountUsd);
}

public sealed class InMemoryUsageTracker : IUsageTracker
{
    private readonly object _gate = new();
    private readonly List<UsageRecord> _records = [];
    private decimal? _lastSpend;
    private decimal _unattributed;

    public void RecordUnattributedSpend(decimal amountUsd)
    {
        lock (_gate)
        {
            _unattributed += amountUsd;
        }
    }

    public decimal? LastKeySpendUsd { get { lock (_gate) { return _lastSpend; } } }

    public void Record(UsageRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (record.KeySpendUsd is { } spend)
            {
                _lastSpend = spend;
            }
        }
    }

    public UsageTotals Totals
    {
        get
        {
            lock (_gate)
            {
                return new UsageTotals(
                    _records.Count,
                    _records.Where(r => r.CostUsd is not null).Sum(r => r.CostUsd!.Value),
                    _records.Count(r => r.CostUsd is null),
                    _records.Sum(r => (long)(r.PromptTokens ?? 0)),
                    _records.Sum(r => (long)(r.CompletionTokens ?? 0)),
                    _records.Sum(r => r.AudioSecondsIn ?? 0),
                    _unattributed);
            }
        }
    }

    public IReadOnlyList<UsageRecord> Recent(int count)
    {
        lock (_gate)
        {
            return _records.TakeLast(count).ToList();
        }
    }
}
