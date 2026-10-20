using Flagpole.Engine;

namespace Flagpole.Engine.Tests;

public class ClientTests
{
    private readonly TestClock _clock = new();
    private readonly FlagStore _store;
    private readonly FlakySource _source;
    private readonly FlagClient _client;

    private sealed class FlakySource(IFlagSource inner) : IFlagSource
    {
        public bool Down { get; set; }
        public int Calls { get; private set; }

        public FlagSnapshot? Fetch(long? knownVersion)
        {
            Calls++;
            return Down ? throw new InvalidOperationException("flag service unavailable") : inner.Fetch(knownVersion);
        }
    }

    public ClientTests()
    {
        _store = new FlagStore(_clock);
        _source = new FlakySource(new StoreFlagSource(_store));
        _client = new FlagClient(_source, "prod", _clock, TimeSpan.FromSeconds(30));
    }

    private void Publish(string key = "new-checkout", string variant = "on") =>
        _store.UpsertFlag(F.Make(key, F.Env(fallthrough: F.Fixed(variant))), "ava");

    [Fact]
    public void Before_any_flags_arrive_every_call_returns_the_default_and_never_throws()
    {
        Assert.False(_client.IsEnabled("new-checkout", new Context("u")));
        Assert.True(_client.IsEnabled("new-checkout", new Context("u"), defaultValue: true));
        Assert.Equal("fallback", _client.GetString("x", new Context("u"), "fallback"));
        Assert.Equal(7, _client.GetInt("x", new Context("u"), 7));
        Assert.Equal(Reason.Error, _client.Evaluate("x", new Context("u")).Reason);
    }

    [Fact]
    public void After_a_refresh_the_client_evaluates_locally()
    {
        Publish();

        Assert.Equal(RefreshResult.Updated, _client.Refresh());

        Assert.True(_client.IsEnabled("new-checkout", new Context("u")));
        Assert.Equal(1, _client.Version);
    }

    [Fact]
    public void Refreshing_again_with_nothing_new_changes_nothing()
    {
        Publish();
        _client.Refresh();

        Assert.Equal(RefreshResult.Unchanged, _client.Refresh());
        Assert.Equal(1, _client.Version);
    }

    [Fact]
    public void A_change_is_picked_up_on_the_next_refresh_and_not_before()
    {
        Publish(variant: "off");
        _client.Refresh();

        Publish(variant: "on");
        Assert.False(_client.IsEnabled("new-checkout", new Context("u")));
        _client.Refresh();

        Assert.True(_client.IsEnabled("new-checkout", new Context("u")));
        Assert.Equal(2, _client.Version);
    }

    [Fact]
    public void When_the_flag_service_goes_down_the_client_keeps_serving_the_last_known_flags()
    {
        Publish();
        _client.Refresh();
        _source.Down = true;

        var result = _client.Refresh();

        Assert.Equal(RefreshResult.Failed, result);
        Assert.True(_client.IsEnabled("new-checkout", new Context("u")));
        Assert.Equal(1, _client.Failures);
        Assert.Equal("flag service unavailable", _client.LastError);
    }

    [Fact]
    public void The_client_reports_when_its_flags_have_gone_stale_and_recovers_when_the_service_returns()
    {
        Publish();
        _client.Refresh();
        Assert.False(_client.IsStale);

        _source.Down = true;
        _clock.Advance(TimeSpan.FromSeconds(89));
        Assert.False(_client.IsStale);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(_client.IsStale);

        _source.Down = false;
        _client.Refresh();
        Assert.False(_client.IsStale);
        Assert.Null(_client.LastError);
    }

    [Fact]
    public void A_client_that_never_reached_the_service_is_stale_but_still_safe()
    {
        _source.Down = true;
        _client.Refresh();

        Assert.True(_client.IsStale);
        Assert.False(_client.IsEnabled("x", new Context("u")));
    }

    [Fact]
    public void Tick_only_contacts_the_service_once_per_poll_interval()
    {
        Publish();

        Assert.Equal(RefreshResult.Updated, _client.Tick());
        Assert.Null(_client.Tick());
        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(_client.Tick());
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RefreshResult.Unchanged, _client.Tick());
        Assert.Equal(2, _source.Calls);
    }

    [Fact]
    public void A_failed_attempt_still_waits_for_the_next_interval_so_an_outage_is_not_hammered()
    {
        _source.Down = true;

        _client.Tick();
        _client.Tick();
        _client.Tick();

        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public void An_older_snapshot_never_replaces_a_newer_one()
    {
        Publish();
        Publish(variant: "off");
        _client.Refresh();
        var stale = new OldSource(_store.Snapshot with { Version = 1 });
        var client = new FlagClient(stale, "prod", _clock);
        client.Refresh();
        stale.Next = _store.Snapshot with { Version = 0 };

        client.Refresh();

        Assert.Equal(1, client.Version);
    }

    private sealed class OldSource(FlagSnapshot first) : IFlagSource
    {
        public FlagSnapshot Next { get; set; } = first;
        public FlagSnapshot? Fetch(long? knownVersion) => Next;
    }

    [Fact]
    public void Typed_helpers_fall_back_to_the_default_when_a_value_has_the_wrong_type()
    {
        var variants = new Variant[] { new("v", "not-a-number"), new("off", "false") };
        _store.UpsertFlag(F.Make("limit", F.Env(fallthrough: F.Fixed("v")), variants: variants), "a");
        _client.Refresh();

        Assert.Equal(42, _client.GetInt("limit", new Context("u"), 42));
        Assert.False(_client.IsEnabled("limit", new Context("u")));
        Assert.Equal("not-a-number", _client.GetString("limit", new Context("u"), "x"));
    }

    [Fact]
    public void Numbers_and_strings_come_through_when_they_are_well_formed()
    {
        var variants = new Variant[] { new("big", "500"), new("small", "50"), new("off", "0") };
        _store.UpsertFlag(F.Make("rate-limit", F.Env(fallthrough: F.Fixed("big")), variants: variants), "a");
        _client.Refresh();

        Assert.Equal(500, _client.GetInt("rate-limit", new Context("u"), 1));
    }

    [Fact]
    public void An_unknown_flag_returns_the_default()
    {
        Publish();
        _client.Refresh();

        Assert.True(_client.IsEnabled("ghost", new Context("u"), defaultValue: true));
    }

    [Fact]
    public void The_client_evaluates_for_its_own_environment()
    {
        _store.UpsertFlag(F.Make("a", prod: F.Env(fallthrough: F.Fixed("off")), dev: F.Env(fallthrough: F.Fixed("on"))), "x");
        var dev = new FlagClient(new StoreFlagSource(_store), "dev", _clock);
        dev.Refresh();
        _client.Refresh();

        Assert.True(dev.IsEnabled("a", new Context("u")));
        Assert.False(_client.IsEnabled("a", new Context("u")));
    }

    [Fact]
    public async Task Evaluating_while_another_thread_refreshes_never_throws_or_returns_a_half_updated_answer()
    {
        Publish();
        _client.Refresh();
        var stop = false;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 400 && !stop; i++)
            {
                Publish(variant: i % 2 == 0 ? "off" : "on");
                _client.Refresh();
            }
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var seen = new HashSet<string?>();
            for (var i = 0; i < 20_000; i++)
            {
                seen.Add(_client.Evaluate("new-checkout", new Context("u")).Variant);
            }

            return seen;
        })));
        stop = true;
        await writer;

        Assert.All(results, seen => Assert.True(seen.IsSubsetOf(new HashSet<string?> { "on", "off" })));
    }
}
