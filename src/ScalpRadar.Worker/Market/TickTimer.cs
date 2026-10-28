using Microsoft.Extensions.Options;
using ScalpRadar.Core.Models;
using ScalpRadar.Core.Options;
using ScalpRadar.Worker.Infrastructure;

namespace ScalpRadar.Worker.Market;

/// <summary>Posts a Tick every Engine:TickSeconds. The consumer scores all symbols on each tick.</summary>
public sealed class TickTimer(MarketChannel channel, IOptions<EngineOptions> options, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.TickSeconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                channel.Post(new Tick(time.GetUtcNow().UtcDateTime));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
