using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flagpole.Engine;

/// <summary>Where a client gets flags from. Returns null when nothing has changed since <paramref name="knownVersion"/>.</summary>
public interface IFlagSource
{
    FlagSnapshot? Fetch(long? knownVersion);
}

public sealed class StoreFlagSource(FlagStore store) : IFlagSource
{
    public FlagSnapshot? Fetch(long? knownVersion) =>
        store.Snapshot is var snapshot && snapshot.Version == knownVersion ? null : snapshot;
}

public enum RefreshResult { Updated, Unchanged, Failed }

/// <summary>
/// What an application embeds. It keeps the last flags it received, so an outage of the flag service never takes the
/// application down: it keeps serving the last known values, and before any flags have arrived it serves the
/// defaults the caller supplied. It never throws from an evaluation.
/// </summary>
public sealed class FlagClient
{
    private readonly IFlagSource _source;
    private readonly string _environment;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private FlagSnapshot? _snapshot;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSuccess;

    public FlagClient(IFlagSource source, string environment, TimeProvider time, TimeSpan? pollInterval = null)
    {
        _source = source;
        _environment = environment;
        _time = time;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
    }

    public int Failures { get; private set; }
    public string? LastError { get; private set; }
    public long? Version => Locked(() => _snapshot?.Version);

    /// <summary>True when the last successful refresh is older than three poll intervals, or there has been none.</summary>
    public bool IsStale => Locked(() => _lastSuccess is null || _time.GetUtcNow() - _lastSuccess > _pollInterval * 3);

    private T Locked<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }

    public RefreshResult Refresh()
    {
        long? known;
        lock (_gate)
        {
            known = _snapshot?.Version;
            _lastAttempt = _time.GetUtcNow();
        }

        try
        {
            var fetched = _source.Fetch(known);
            lock (_gate)
            {
                _lastSuccess = _time.GetUtcNow();
                LastError = null;
                if (fetched is null || (_snapshot is not null && fetched.Version < _snapshot.Version))
                {
                    return RefreshResult.Unchanged;
                }

                _snapshot = fetched;
                return RefreshResult.Updated;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                Failures++;
                LastError = ex.Message;
            }

            return RefreshResult.Failed;
        }
    }

    /// <summary>Refreshes only if the poll interval has passed since the last attempt. Call it as often as you like.</summary>
    public RefreshResult? Tick() => Locked(() => _time.GetUtcNow() - _lastAttempt >= _pollInterval) ? Refresh() : null;

    public Evaluation Evaluate(string key, Context context)
    {
        FlagSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
        }

        if (snapshot is null)
        {
            return new Evaluation(key, _environment, null, null, Reason.Error, "No flags have been received yet.");
        }

        try
        {
            return Evaluator.Evaluate(snapshot, _environment, key, context, _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            return new Evaluation(key, _environment, null, null, Reason.Error, ex.Message);
        }
    }

    public bool IsEnabled(string key, Context context, bool defaultValue = false) =>
        Evaluate(key, context).Value is { } v && bool.TryParse(v, out var b) ? b : defaultValue;

    public string GetString(string key, Context context, string defaultValue) =>
        Evaluate(key, context).Value ?? defaultValue;

    public int GetInt(string key, Context context, int defaultValue) =>
        Evaluate(key, context).Value is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : defaultValue;
}

/// <summary>Flags as JSON, for export, import and review in pull requests.</summary>
public static class FlagSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(FlagSnapshot snapshot) =>
        JsonSerializer.Serialize(new Document(snapshot.Flags.Values.OrderBy(f => f.Key).ToList(), snapshot.Segments.Values.OrderBy(s => s.Key).ToList()), Options);

    public static (IReadOnlyList<Flag> Flags, IReadOnlyList<Segment> Segments) FromJson(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<Document>(json, Options) ?? throw new FlagValidationException(["The file is empty."]);
            return (document.Flags ?? [], document.Segments ?? []);
        }
        catch (JsonException ex)
        {
            throw new FlagValidationException([$"The file is not valid flag JSON: {ex.Message}"]);
        }
    }

    private sealed record Document(List<Flag>? Flags, List<Segment>? Segments);
}
