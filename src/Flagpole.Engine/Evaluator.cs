using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Flagpole.Engine;

public static class Bucketing
{
    public const int Buckets = 10_000;

    /// <summary>
    /// A stable number from 0 to 9999 for this user and flag. The same user always lands in the same bucket for the same
    /// flag (so a rollout never reshuffles), and different flags use different buckets (so one rollout does not
    /// secretly overlap another).
    /// </summary>
    public static int Bucket(string salt, string flagKey, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}.{flagKey}.{value}"));
        var number = (uint)hash[0] << 24 | (uint)hash[1] << 16 | (uint)hash[2] << 8 | hash[3];
        return (int)(number % Buckets);
    }
}

public static class Evaluator
{
    private const int MaxDepth = 10;

    public static Evaluation Evaluate(FlagSnapshot snapshot, string environment, string flagKey, Context context, DateTimeOffset now) =>
        Evaluate(snapshot, environment, flagKey, context, now, 0);

    private static Evaluation Evaluate(FlagSnapshot snapshot, string environment, string flagKey, Context context, DateTimeOffset now, int depth)
    {
        if (!snapshot.Flags.TryGetValue(flagKey, out var flag))
        {
            return new Evaluation(flagKey, environment, null, null, Reason.FlagNotFound, $"There is no flag '{flagKey}'.");
        }

        if (!flag.Environments.TryGetValue(environment, out var config))
        {
            return new Evaluation(flagKey, environment, null, null, Reason.EnvironmentNotFound, $"The flag has no '{environment}' environment.");
        }

        Evaluation Result(string? variant, Reason reason, string detail, int? rule = null, int? bucket = null) =>
            new(flagKey, environment, variant, variant is null ? null : flag.Variants.FirstOrDefault(v => v.Key == variant)?.Value, reason, detail, rule, bucket);

        if (depth > MaxDepth)
        {
            return Result(config.OffVariant, Reason.Error, "Prerequisites are nested too deeply.");
        }

        if (!config.Enabled)
        {
            return Result(config.OffVariant, Reason.Off, "The flag is switched off in this environment.");
        }

        if (config.EnableAt is { } start && now < start)
        {
            return Result(config.OffVariant, Reason.Scheduled, $"Not live until {start:u}.");
        }

        if (config.DisableAt is { } end && now >= end)
        {
            return Result(config.OffVariant, Reason.Scheduled, $"Switched off automatically at {end:u}.");
        }

        foreach (var prerequisite in config.Prerequisites)
        {
            var required = Evaluate(snapshot, environment, prerequisite.Flag, context, now, depth + 1);
            if (required.Variant != prerequisite.Variant)
            {
                return Result(config.OffVariant, Reason.PrerequisiteFailed, $"Requires '{prerequisite.Flag}' to be '{prerequisite.Variant}', but it is '{required.Variant ?? "unavailable"}'.");
            }
        }

        for (var i = 0; i < config.Rules.Count; i++)
        {
            var rule = config.Rules[i];
            if (rule.Conditions.All(c => Matches(snapshot, c, context)))
            {
                var served = Serve(flag, rule.Serve, context);
                return Result(served.Variant, served.Bucket is null ? Reason.RuleMatch : Reason.Rollout,
                    $"Matched rule {i + 1} ({rule.Name})" + (served.Bucket is { } b ? $"; bucket {b} of {Bucketing.Buckets}." : "."), i, served.Bucket);
            }
        }

        var fallthrough = Serve(flag, config.Fallthrough, context);
        return Result(fallthrough.Variant, fallthrough.Bucket is null ? Reason.Fallthrough : Reason.Rollout,
            fallthrough.Bucket is { } fb ? $"No rule matched; bucket {fb} of {Bucketing.Buckets}." : "No rule matched, so the default applies.", null, fallthrough.Bucket);
    }

    private static (string? Variant, int? Bucket) Serve(Flag flag, Serve serve, Context context)
    {
        if (serve.Variant is not null)
        {
            return (serve.Variant, null);
        }

        var rollout = serve.Rollout!;
        var by = context.Get(serve.BucketBy) ?? context.UserId;
        var bucket = Bucketing.Bucket(flag.Salt, flag.Key, by);
        var cumulative = 0;
        foreach (var share in rollout)
        {
            cumulative += share.Weight;
            if (bucket < cumulative)
            {
                return (share.Variant, bucket);
            }
        }

        return (rollout[^1].Variant, bucket);
    }

    public static bool Matches(FlagSnapshot snapshot, Condition condition, Context context)
    {
        if (condition.Op == Operator.InSegment)
        {
            return condition.Values.Any(key => snapshot.Segments.TryGetValue(key, out var segment) && InSegment(snapshot, segment, context));
        }

        var actual = context.Get(condition.Attribute);
        if (actual is null)
        {
            return false;
        }

        var values = condition.Values;
        switch (condition.Op)
        {
            case Operator.Equals:
                return values.Any(v => string.Equals(actual, v, StringComparison.OrdinalIgnoreCase));
            case Operator.NotEquals:
                return values.All(v => !string.Equals(actual, v, StringComparison.OrdinalIgnoreCase));
            case Operator.In:
                return values.Any(v => string.Equals(actual, v, StringComparison.OrdinalIgnoreCase));
            case Operator.NotIn:
                return values.All(v => !string.Equals(actual, v, StringComparison.OrdinalIgnoreCase));
            case Operator.StartsWith:
                return values.Any(v => actual.StartsWith(v, StringComparison.OrdinalIgnoreCase));
            case Operator.EndsWith:
                return values.Any(v => actual.EndsWith(v, StringComparison.OrdinalIgnoreCase));
            case Operator.Contains:
                return values.Any(v => actual.Contains(v, StringComparison.OrdinalIgnoreCase));
            default:
                if (!decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    || !decimal.TryParse(values.FirstOrDefault(), NumberStyles.Number, CultureInfo.InvariantCulture, out var limit))
                {
                    return false;
                }

                return condition.Op switch
                {
                    Operator.GreaterThan => number > limit,
                    Operator.GreaterOrEqual => number >= limit,
                    Operator.LessThan => number < limit,
                    _ => number <= limit,
                };
        }
    }

    public static bool InSegment(FlagSnapshot snapshot, Segment segment, Context context)
    {
        if (segment.Excluded.Contains(context.UserId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segment.Included.Contains(context.UserId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return segment.Conditions.Count > 0 && segment.Conditions.All(c => c.Op != Operator.InSegment && Matches(snapshot, c, context));
    }
}
