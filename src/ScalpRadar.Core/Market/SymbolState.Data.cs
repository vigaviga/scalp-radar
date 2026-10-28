using ScalpRadar.Core.Models;

namespace ScalpRadar.Core.Market;

// Mark price, funding and open interest.
public sealed partial class SymbolState
{
    private OiHistory? _oi;

    public OiHistory Oi => _oi ??= new OiHistory(TimeSpan.FromHours(_opt.KeepOiHours));

    public double? MarkPrice { get; private set; }
    public double? FundingRate { get; private set; }
    public DateTime? NextFundingTime { get; private set; }
    public int FundingIntervalHours { get; set; } = 8;
    public DateTime? LastMarkReceivedUtc { get; private set; }
    public DateTime? LastOiReceivedUtc { get; private set; }

    public void ApplyMark(MarkPriceUpdate u, DateTime receivedUtc)
    {
        MarkPrice = u.MarkPrice;
        if (u.FundingRate is { } f) FundingRate = f;
        NextFundingTime = u.NextFundingTime;
        LastMarkReceivedUtc = receivedUtc;
    }

    /// <summary>A live OI poll. Notional uses the mark price (or last trade) because the endpoint returns contracts only.</summary>
    public void ApplyOi(OiEvent e)
    {
        var price = MarkPrice ?? LastPrice ?? 0;
        Oi.Add(new OiSample(e.Time, e.OpenInterest, e.OpenInterest * price));
        LastOiReceivedUtc = e.ReceivedUtc;
    }

    /// <summary>Funding rate normalised to an 8h interval, in percent.</summary>
    public double? Rate8hPct => FundingRate is { } r ? Indicators.Funding.Rate8hPct(r, FundingIntervalHours) : null;

    partial void OnBackfillMergedCore(BackfillEvent e)
    {
        Oi.AddRange(e.OiHistory);
    }
}
