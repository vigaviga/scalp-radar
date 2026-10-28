namespace ScalpRadar.Core.Models;

public enum Direction { Long, Short }

public enum Tier
{
    /// <summary>✅ 2 of 3 confirmations.</summary>
    Alert,

    /// <summary>🔥 3 of 3 confirmations.</summary>
    Strong,
}

public static class DirectionExtensions
{
    public static string Label(this Direction d) => d == Direction.Long ? "LONG" : "SHORT";
    public static string Label(this Tier t) => t == Tier.Strong ? "STRONG" : "ALERT";
    public static string Emoji(this Tier t) => t == Tier.Strong ? "🔥" : "✅";
}
