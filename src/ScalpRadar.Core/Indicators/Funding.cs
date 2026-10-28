namespace ScalpRadar.Core.Indicators;

public static class Funding
{
    /// <summary>
    /// Normalises a funding rate (a fraction, e.g. 0.0001) paid every <paramref name="intervalHours"/>
    /// to an 8h rate in percent: rate × 8 / intervalHours × 100.
    /// </summary>
    public static double Rate8hPct(double rate, int intervalHours) =>
        intervalHours <= 0 ? rate * 100 : rate * 8.0 / intervalHours * 100;
}
