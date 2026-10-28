using ScalpRadar.Core.Models;

namespace ScalpRadar.Worker.Market;

public sealed partial class BackfillService
{
    private partial Task<IReadOnlyList<OiSample>> FetchOiHistoryAsync(string symbol, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OiSample>>([]);
}
