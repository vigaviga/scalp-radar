using Microsoft.Extensions.Options;
using ScalpRadar.Core.Options;
using ScalpRadar.Core.Rules;

namespace ScalpRadar.Worker.Market;

/// <summary>The rules objects the market loop uses. Only the loop touches them, so they need no locks.</summary>
public sealed class SignalContext(
    IOptions<RulesOptions> rules,
    IOptions<OrderBookOptions> book,
    IOptions<HeatmapOptions> heatmap,
    IOptions<StateOptions> state,
    IOptions<UniverseOptions> universe)
{
    public RulesOptions Rules { get; } = rules.Value;
    public RulesEngine Engine { get; } = new(rules.Value);
    public CooldownTracker Cooldown { get; } = new(rules.Value);
    public AlertDecider Decider { get; } = new(rules.Value, book.Value);
    public Shortlist Shortlist { get; } = new(book.Value);
    public string Benchmark { get; } = universe.Value.BenchmarkSymbol;
    public string ConfigHash { get; } = ConfigHasher.Compute(rules.Value, heatmap.Value, book.Value, state.Value);
}
