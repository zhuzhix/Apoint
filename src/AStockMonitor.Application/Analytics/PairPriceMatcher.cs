using AStockMonitor.Domain.Analytics;

namespace AStockMonitor.Application.Analytics;

/// <summary>使用 decimal 和整数 tick 判断价格是否属于对子尾数。</summary>
public static class PairPriceMatcher
{
    /// <summary>
    /// 将价格除以最小变动单位后转换为整数，并识别 00、11、22、…、99 尾数。
    /// </summary>
    /// <remarks>
    /// 只有价格能被 priceTick 精确表示时才匹配，从而避免 double 取余造成的精度误判。
    /// </remarks>
    /// <param name="price">待判断价格。</param>
    /// <param name="priceTick">最小价格变动单位，A 股默认 0.01。</param>
    /// <param name="includeRound00">是否把 .00 识别为 ROUND_00。</param>
    /// <returns>对子匹配信息；不是对子或参数无效时返回 null。</returns>
    public static PairPriceMatch? Match(
        decimal price,
        decimal priceTick = 0.01m,
        bool includeRound00 = true)
    {
        if (price <= 0 || priceTick <= 0)
        {
            return null;
        }

        // 先转整数 tick 再取末两位，禁止对二进制浮点价格直接取余。
        var ticksDecimal = decimal.Round(price / priceTick, 0, MidpointRounding.AwayFromZero);
        if (ticksDecimal * priceTick != price || ticksDecimal > long.MaxValue)
        {
            return null;
        }

        var ticks = decimal.ToInt64(ticksDecimal);
        var code = (int)(Math.Abs(ticks) % 100);
        if (code == 0)
        {
            return includeRound00
                ? new PairPriceMatch(price, ticks, code, PairPriceKind.Round00)
                : null;
        }

        return code % 11 == 0
            ? new PairPriceMatch(price, ticks, code, PairPriceKind.DoubleDigit)
            : null;
    }
}
