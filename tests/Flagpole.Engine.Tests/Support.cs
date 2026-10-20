using Flagpole.Engine;

namespace Flagpole.Engine.Tests;

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public static class F
{
    public static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<Variant> OnOff => [new("on", "true"), new("off", "false")];

    public static Serve Fixed(string variant) => new(variant);

    public static Serve Percent(double onPercent, string by = "userId") =>
        new(null, [new WeightedVariant("on", (int)Math.Round(onPercent * 100)), new WeightedVariant("off", 10_000 - (int)Math.Round(onPercent * 100))], by);

    public static Condition Is(string attribute, params string[] values) => new(attribute, Operator.Equals, values);

    public static Rule When(string name, Serve serve, params Condition[] conditions) => new(name, conditions, serve);

    public static EnvironmentConfig Env(bool enabled = true, Serve? fallthrough = null, IReadOnlyList<Rule>? rules = null,
        IReadOnlyList<Prerequisite>? prerequisites = null, DateTimeOffset? enableAt = null, DateTimeOffset? disableAt = null) =>
        new(enabled, "off", fallthrough ?? Fixed("off"), rules ?? [], prerequisites ?? [], enableAt, disableAt);

    public static Flag Make(string key = "new-checkout", EnvironmentConfig? prod = null, EnvironmentConfig? dev = null, IReadOnlyList<Variant>? variants = null, string? salt = null) =>
        new(key, "", variants ?? OnOff, new Dictionary<string, EnvironmentConfig> { ["prod"] = prod ?? Env(), ["dev"] = dev ?? Env() }, salt ?? "", [], 1);

    public static FlagSnapshot Snapshot(IEnumerable<Flag> flags, IEnumerable<Segment>? segments = null) =>
        new(1, flags.ToDictionary(f => f.Key, f => f.Salt == "" ? f with { Salt = f.Key } : f), (segments ?? []).ToDictionary(s => s.Key));

    public static Evaluation Eval(FlagSnapshot snapshot, string key, string user = "u1", string env = "prod", DateTimeOffset? now = null, params (string, string)[] attributes) =>
        Evaluator.Evaluate(snapshot, env, key, Ctx(user, attributes), now ?? Now);

    public static Context Ctx(string user = "u1", params (string, string)[] attributes) =>
        new(user, attributes.ToDictionary(a => a.Item1, a => a.Item2));
}
