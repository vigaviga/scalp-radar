using ScalpRadar.Core.Options;

namespace ScalpRadar.Core.Tests;

public class ConfigHasherTests
{
    [Fact]
    public void Same_settings_give_the_same_hash()
    {
        var a = ConfigHasher.Compute(new RulesOptions(), new HeatmapOptions(), new OrderBookOptions(), new StateOptions());
        var b = ConfigHasher.Compute(new RulesOptions(), new HeatmapOptions(), new OrderBookOptions(), new StateOptions());
        Assert.Equal(a, b);
        Assert.Equal(12, a.Length);
    }

    [Fact]
    public void Changing_a_threshold_changes_the_hash()
    {
        var a = ConfigHasher.Compute(new RulesOptions(), new HeatmapOptions(), new OrderBookOptions(), new StateOptions());
        var b = ConfigHasher.Compute(new RulesOptions { MinVolRatio = 3.0 }, new HeatmapOptions(), new OrderBookOptions(), new StateOptions());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Leverage_mix_order_does_not_change_the_hash()
    {
        var h1 = new HeatmapOptions { LeverageMix = new() { ["10"] = 0.5, ["25"] = 0.5 } };
        var h2 = new HeatmapOptions { LeverageMix = new() { ["25"] = 0.5, ["10"] = 0.5 } };
        Assert.Equal(
            ConfigHasher.Compute(new RulesOptions(), h1, new OrderBookOptions(), new StateOptions()),
            ConfigHasher.Compute(new RulesOptions(), h2, new OrderBookOptions(), new StateOptions()));
    }
}
