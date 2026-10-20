using System.Globalization;
using System.Text.RegularExpressions;

namespace Flagpole.Engine;

/// <summary>
/// Holds every flag and segment. Each change is validated as a whole and published as a new immutable snapshot with a
/// higher version, and recorded in an audit log, so a mistake is refused up front and an evaluation never observes
/// half of a change.
/// </summary>
public sealed partial class FlagStore
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<AuditEntry> _audit = [];
    private FlagSnapshot _snapshot = FlagSnapshot.Empty;

    public FlagStore(TimeProvider time) => _time = time;

    public FlagSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public IReadOnlyList<AuditEntry> Audit
    {
        get
        {
            lock (_gate)
            {
                return _audit.ToList();
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex KeyPattern();

    public Flag UpsertFlag(Flag flag, string actor)
    {
        lock (_gate)
        {
            var existing = _snapshot.Flags.GetValueOrDefault(flag.Key);
            var next = flag with { Version = (existing?.Version ?? 0) + 1, Salt = string.IsNullOrEmpty(flag.Salt) ? flag.Key : flag.Salt };
            var flags = new Dictionary<string, Flag>(_snapshot.Flags) { [flag.Key] = next };
            Validate(flags, _snapshot.Segments, flag.Key);
            Publish(flags, _snapshot.Segments, actor, existing is null ? "create" : "update", flag.Key,
                existing is null ? $"Created flag with {flag.Variants.Count} variants." : $"Updated to version {next.Version}.");
            return next;
        }
    }

    public void DeleteFlag(string key, string actor)
    {
        lock (_gate)
        {
            if (!_snapshot.Flags.ContainsKey(key))
            {
                throw new FlagValidationException([$"There is no flag '{key}'."]);
            }

            var users = _snapshot.Flags.Values.Where(f => f.Key != key && f.Environments.Values.Any(e => e.Prerequisites.Any(p => p.Flag == key))).Select(f => f.Key).ToList();
            if (users.Count > 0)
            {
                throw new FlagValidationException([$"'{key}' is a prerequisite of {string.Join(", ", users)}."]);
            }

            var flags = new Dictionary<string, Flag>(_snapshot.Flags);
            flags.Remove(key);
            Publish(flags, _snapshot.Segments, actor, "delete", key, "Deleted.");
        }
    }

    public Flag SetEnabled(string key, string environment, bool enabled, string actor)
    {
        lock (_gate)
        {
            if (!_snapshot.Flags.TryGetValue(key, out var flag) || !flag.Environments.TryGetValue(environment, out var config))
            {
                throw new FlagValidationException([$"There is no flag '{key}' with an '{environment}' environment."]);
            }

            var environments = new Dictionary<string, EnvironmentConfig>(flag.Environments) { [environment] = config with { Enabled = enabled } };
            var next = flag with { Environments = environments, Version = flag.Version + 1 };
            var flags = new Dictionary<string, Flag>(_snapshot.Flags) { [key] = next };
            Publish(flags, _snapshot.Segments, actor, enabled ? "enable" : "disable", key, $"{(enabled ? "Switched on" : "Switched off")} in {environment}.");
            return next;
        }
    }

    public void UpsertSegment(Segment segment, string actor)
    {
        lock (_gate)
        {
            var errors = new List<string>();
            if (!KeyPattern().IsMatch(segment.Key))
            {
                errors.Add($"'{segment.Key}' is not a valid segment key.");
            }

            ValidateConditions(segment.Conditions, new Dictionary<string, Segment>(_snapshot.Segments) { [segment.Key] = segment }, $"segment {segment.Key}", errors, allowSegments: false);
            if (errors.Count > 0)
            {
                throw new FlagValidationException(errors);
            }

            var segments = new Dictionary<string, Segment>(_snapshot.Segments) { [segment.Key] = segment };
            Publish(_snapshot.Flags, segments, actor, "segment", segment.Key, $"Saved segment with {segment.Included.Count} named users.");
        }
    }

    public void DeleteSegment(string key, string actor)
    {
        lock (_gate)
        {
            var users = _snapshot.Flags.Values
                .Where(f => f.Environments.Values.Any(e => e.Rules.Any(r => r.Conditions.Any(c => c.Op == Operator.InSegment && c.Values.Contains(key)))))
                .Select(f => f.Key).ToList();
            if (users.Count > 0)
            {
                throw new FlagValidationException([$"Segment '{key}' is used by {string.Join(", ", users)}."]);
            }

            var segments = new Dictionary<string, Segment>(_snapshot.Segments);
            if (!segments.Remove(key))
            {
                throw new FlagValidationException([$"There is no segment '{key}'."]);
            }

            Publish(_snapshot.Flags, segments, actor, "segment-delete", key, "Deleted segment.");
        }
    }

    /// <summary>Replaces everything at once (for import). The new set must be valid as a whole.</summary>
    public void Replace(IEnumerable<Flag> flags, IEnumerable<Segment> segments, string actor)
    {
        lock (_gate)
        {
            var flagMap = flags.ToDictionary(f => f.Key, f => f with { Salt = string.IsNullOrEmpty(f.Salt) ? f.Key : f.Salt });
            var segmentMap = segments.ToDictionary(s => s.Key);
            Validate(flagMap, segmentMap, null);
            Publish(flagMap, segmentMap, actor, "import", "*", $"Imported {flagMap.Count} flags and {segmentMap.Count} segments.");
        }
    }

    public Evaluation Evaluate(string environment, string flagKey, Context context) =>
        Evaluator.Evaluate(Snapshot, environment, flagKey, context, _time.GetUtcNow());

    private void Publish(IReadOnlyDictionary<string, Flag> flags, IReadOnlyDictionary<string, Segment> segments, string actor, string action, string target, string summary)
    {
        var version = _snapshot.Version + 1;
        _snapshot = new FlagSnapshot(version, flags, segments);
        _audit.Add(new AuditEntry(version, _time.GetUtcNow(), actor, action, target, summary));
    }

    private static void Validate(IReadOnlyDictionary<string, Flag> flags, IReadOnlyDictionary<string, Segment> segments, string? only)
    {
        var errors = new List<string>();
        foreach (var flag in flags.Values.Where(f => only is null || f.Key == only))
        {
            ValidateFlag(flag, flags, segments, errors);
        }

        foreach (var flag in flags.Values)
        {
            foreach (var (environment, config) in flag.Environments)
            {
                if (HasCycle(flags, flag.Key, environment))
                {
                    errors.Add($"{flag.Key} ({environment}): prerequisites form a loop.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new FlagValidationException(errors.Distinct().ToList());
        }
    }

    private static void ValidateFlag(Flag flag, IReadOnlyDictionary<string, Flag> flags, IReadOnlyDictionary<string, Segment> segments, List<string> errors)
    {
        if (!KeyPattern().IsMatch(flag.Key))
        {
            errors.Add($"'{flag.Key}' is not a valid flag key (lower case letters, digits, '.', '_' and '-', up to 64).");
        }

        if (flag.Variants.Count == 0)
        {
            errors.Add($"{flag.Key}: a flag needs at least one variant.");
        }

        if (flag.Variants.Select(v => v.Key).Distinct().Count() != flag.Variants.Count || flag.Variants.Any(v => string.IsNullOrWhiteSpace(v.Key)))
        {
            errors.Add($"{flag.Key}: variant keys must be unique and not empty.");
        }

        if (flag.Environments.Count == 0)
        {
            errors.Add($"{flag.Key}: a flag needs at least one environment.");
        }

        var variants = flag.Variants.Select(v => v.Key).ToHashSet();
        foreach (var (environment, config) in flag.Environments)
        {
            var where = $"{flag.Key} ({environment})";
            if (!variants.Contains(config.OffVariant))
            {
                errors.Add($"{where}: the off variant '{config.OffVariant}' does not exist.");
            }

            ValidateServe(config.Fallthrough, variants, $"{where} default", errors);
            for (var i = 0; i < config.Rules.Count; i++)
            {
                var rule = config.Rules[i];
                var label = $"{where} rule {i + 1}";
                if (rule.Conditions.Count == 0)
                {
                    errors.Add($"{label}: a rule needs at least one condition.");
                }

                ValidateConditions(rule.Conditions, segments, label, errors, allowSegments: true);
                ValidateServe(rule.Serve, variants, label, errors);
            }

            foreach (var prerequisite in config.Prerequisites)
            {
                if (prerequisite.Flag == flag.Key)
                {
                    errors.Add($"{where}: a flag cannot depend on itself.");
                }
                else if (!flags.TryGetValue(prerequisite.Flag, out var other))
                {
                    errors.Add($"{where}: prerequisite flag '{prerequisite.Flag}' does not exist.");
                }
                else if (other.Variants.All(v => v.Key != prerequisite.Variant))
                {
                    errors.Add($"{where}: '{prerequisite.Flag}' has no variant '{prerequisite.Variant}'.");
                }
            }

            if (config.EnableAt is { } start && config.DisableAt is { } end && end <= start)
            {
                errors.Add($"{where}: the switch-off time must be after the switch-on time.");
            }
        }
    }

    private static void ValidateServe(Serve serve, HashSet<string> variants, string label, List<string> errors)
    {
        if ((serve.Variant is null) == (serve.Rollout is null))
        {
            errors.Add($"{label}: serve either one variant or a rollout, not both or neither.");
            return;
        }

        if (serve.Variant is not null)
        {
            if (!variants.Contains(serve.Variant))
            {
                errors.Add($"{label}: variant '{serve.Variant}' does not exist.");
            }

            return;
        }

        var rollout = serve.Rollout!;
        if (rollout.Count == 0 || rollout.Any(w => w.Weight < 0 || !variants.Contains(w.Variant)))
        {
            errors.Add($"{label}: a rollout needs existing variants and weights that are not negative.");
        }
        else if (rollout.Sum(w => w.Weight) != Bucketing.Buckets)
        {
            errors.Add($"{label}: rollout weights must add up to 100% (they add up to {rollout.Sum(w => w.Weight) / 100.0:0.##}%).");
        }
        else if (rollout.Select(w => w.Variant).Distinct().Count() != rollout.Count)
        {
            errors.Add($"{label}: a variant appears twice in the rollout.");
        }
    }

    private static void ValidateConditions(IReadOnlyList<Condition> conditions, IReadOnlyDictionary<string, Segment> segments, string label, List<string> errors, bool allowSegments)
    {
        foreach (var condition in conditions)
        {
            if (condition.Op == Operator.InSegment)
            {
                if (!allowSegments)
                {
                    errors.Add($"{label}: a segment cannot be defined in terms of another segment.");
                }

                foreach (var key in condition.Values.Where(v => !segments.ContainsKey(v)))
                {
                    errors.Add($"{label}: segment '{key}' does not exist.");
                }

                if (condition.Values.Count == 0)
                {
                    errors.Add($"{label}: choose at least one segment.");
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(condition.Attribute))
            {
                errors.Add($"{label}: a condition needs an attribute.");
            }

            if (condition.Values.Count == 0 || condition.Values.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{label}: condition on '{condition.Attribute}' needs a value.");
            }
            else if (condition.Op is Operator.GreaterThan or Operator.GreaterOrEqual or Operator.LessThan or Operator.LessOrEqual
                     && (condition.Values.Count != 1 || !decimal.TryParse(condition.Values[0], NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
            {
                errors.Add($"{label}: '{condition.Attribute} {condition.Op}' needs exactly one number.");
            }
        }
    }

    private static bool HasCycle(IReadOnlyDictionary<string, Flag> flags, string start, string environment)
    {
        var visiting = new HashSet<string>();

        bool Visit(string key)
        {
            if (!visiting.Add(key))
            {
                return true;
            }

            if (flags.TryGetValue(key, out var flag) && flag.Environments.TryGetValue(environment, out var config)
                && config.Prerequisites.Any(p => Visit(p.Flag)))
            {
                return true;
            }

            visiting.Remove(key);
            return false;
        }

        return Visit(start);
    }
}
