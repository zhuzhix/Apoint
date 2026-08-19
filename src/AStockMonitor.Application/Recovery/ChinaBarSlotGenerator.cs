namespace AStockMonitor.Application.Recovery;

/// <summary>生成沪深连续竞价应有K线槽位，不跨午休且不生成空交易日。</summary>
public static class ChinaBarSlotGenerator
{
    private static readonly TimeOnly MorningStart = new(9, 30);
    private static readonly TimeOnly MorningEnd = new(11, 30);
    private static readonly TimeOnly AfternoonStart = new(13, 0);
    private static readonly TimeOnly AfternoonEnd = new(15, 0);

    public static IReadOnlyList<(DateTime Bob, DateTime Eob)> Generate(
        DateOnly tradingDate,
        string frequency)
    {
        if (frequency.Equals("1d", StringComparison.OrdinalIgnoreCase))
        {
            return [(tradingDate.ToDateTime(MorningStart), tradingDate.ToDateTime(AfternoonEnd))];
        }

        var minutes = frequency.ToLowerInvariant() switch
        {
            "1m" => 1,
            "5m" => 5,
            "30m" => 30,
            "60m" => 60,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unsupported frequency")
        };
        var result = new List<(DateTime Bob, DateTime Eob)>();
        AddSession(MorningStart, MorningEnd);
        AddSession(AfternoonStart, AfternoonEnd);
        return result;

        void AddSession(TimeOnly start, TimeOnly end)
        {
            var cursor = tradingDate.ToDateTime(start);
            var sessionEnd = tradingDate.ToDateTime(end);
            while (cursor < sessionEnd)
            {
                var eob = cursor.AddMinutes(minutes);
                result.Add((cursor, eob > sessionEnd ? sessionEnd : eob));
                cursor = eob;
            }
        }
    }
}
