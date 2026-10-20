namespace Flagpole.Engine;

public enum Operator
{
    Equals,
    NotEquals,
    In,
    NotIn,
    StartsWith,
    EndsWith,
    Contains,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,

    /// <summary>The user is in one of the named segments.</summary>
    InSegment,
}

/// <summary>A test on one attribute of the user. A condition on an attribute the user does not have never matches.</summary>
public sealed record Condition(string Attribute, Operator Op, IReadOnlyList<string> Values);

/// <summary>A share of traffic, in basis points (10000 = 100%), for one variant.</summary>
public sealed record WeightedVariant(string Variant, int Weight);

/// <summary>What to hand out: one fixed variant, or a percentage split of several.</summary>
public sealed record Serve(string? Variant = null, IReadOnlyList<WeightedVariant>? Rollout = null, string BucketBy = "userId");

public sealed record Rule(string Name, IReadOnlyList<Condition> Conditions, Serve Serve);

/// <summary>This flag only applies when another flag currently serves the given variant.</summary>
public sealed record Prerequisite(string Flag, string Variant);

public sealed record Variant(string Key, string Value);

public sealed record EnvironmentConfig(
    bool Enabled,
    string OffVariant,
    Serve Fallthrough,
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<Prerequisite> Prerequisites,
    DateTimeOffset? EnableAt = null,
    DateTimeOffset? DisableAt = null);

public sealed record Flag(
    string Key,
    string Description,
    IReadOnlyList<Variant> Variants,
    IReadOnlyDictionary<string, EnvironmentConfig> Environments,
    string Salt,
    IReadOnlyList<string> Tags,
    int Version);

/// <summary>A reusable group of users: named ones, plus anyone matching every condition, minus excluded ones.</summary>
public sealed record Segment(string Key, IReadOnlyList<string> Included, IReadOnlyList<string> Excluded, IReadOnlyList<Condition> Conditions);

public sealed record Context(string UserId, IReadOnlyDictionary<string, string>? Attributes = null)
{
    public string? Get(string attribute) =>
        string.Equals(attribute, "userId", StringComparison.OrdinalIgnoreCase) ? UserId
        : Attributes is not null && Attributes.TryGetValue(attribute, out var v) ? v : null;
}

public enum Reason
{
    FlagNotFound,
    EnvironmentNotFound,
    Off,
    Scheduled,
    PrerequisiteFailed,
    RuleMatch,
    Rollout,
    Fallthrough,
    Error,
}

public sealed record Evaluation(string Flag, string Environment, string? Variant, string? Value, Reason Reason, string Detail, int? RuleIndex = null, int? Bucket = null);

/// <summary>A complete, immutable view of every flag at one version. An evaluation never sees half of an update.</summary>
public sealed record FlagSnapshot(long Version, IReadOnlyDictionary<string, Flag> Flags, IReadOnlyDictionary<string, Segment> Segments)
{
    public static FlagSnapshot Empty { get; } = new(0, new Dictionary<string, Flag>(), new Dictionary<string, Segment>());
}

public sealed record AuditEntry(long Version, DateTimeOffset At, string Actor, string Action, string Target, string Summary);

public sealed class FlagValidationException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
