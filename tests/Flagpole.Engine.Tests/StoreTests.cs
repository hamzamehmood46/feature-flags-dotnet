using Flagpole.Engine;

namespace Flagpole.Engine.Tests;

public class StoreTests
{
    private readonly TestClock _clock = new();
    private readonly FlagStore _store;

    public StoreTests() => _store = new FlagStore(_clock);

    private FlagValidationException Rejected(Action action) => Assert.Throws<FlagValidationException>(action);

    [Fact]
    public void A_valid_flag_is_saved_with_version_one_and_a_salt_defaulting_to_its_key()
    {
        var saved = _store.UpsertFlag(F.Make("new-checkout"), "ava");

        Assert.Equal(1, saved.Version);
        Assert.Equal("new-checkout", saved.Salt);
        Assert.Equal(1, _store.Snapshot.Version);
        Assert.Contains("new-checkout", _store.Snapshot.Flags.Keys);
    }

    [Fact]
    public void Each_change_bumps_the_flag_version_and_the_snapshot_version_and_is_audited()
    {
        _store.UpsertFlag(F.Make("a"), "ava");
        _clock.Advance(TimeSpan.FromMinutes(5));
        _store.UpsertFlag(F.Make("a", F.Env(fallthrough: F.Fixed("on"))), "ben");
        _store.SetEnabled("a", "prod", false, "cara");

        Assert.Equal(3, _store.Snapshot.Flags["a"].Version);
        Assert.Equal(3, _store.Snapshot.Version);
        Assert.Equal(["create", "update", "disable"], _store.Audit.Select(e => e.Action));
        Assert.Equal(["ava", "ben", "cara"], _store.Audit.Select(e => e.Actor));
        Assert.Equal(_clock.GetUtcNow(), _store.Audit[^1].At);
        Assert.Equal([1, 2, 3], _store.Audit.Select(e => e.Version));
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("bad/slash")]
    public void A_bad_key_is_refused(string key) =>
        Assert.Contains("valid flag key", Rejected(() => _store.UpsertFlag(F.Make(key), "ava")).Message);

    [Fact]
    public void A_flag_needs_variants_with_unique_keys_and_at_least_one_environment()
    {
        Assert.Contains("at least one variant", Rejected(() => _store.UpsertFlag(F.Make(variants: []), "a")).Message);
        Assert.Contains("unique", Rejected(() => _store.UpsertFlag(F.Make(variants: [new("on", "1"), new("on", "2")]), "a")).Message);
        var noEnv = F.Make() with { Environments = new Dictionary<string, EnvironmentConfig>() };
        Assert.Contains("at least one environment", Rejected(() => _store.UpsertFlag(noEnv, "a")).Message);
        Assert.Empty(_store.Snapshot.Flags);
    }

    [Fact]
    public void Serving_or_turning_off_to_a_variant_that_does_not_exist_is_refused()
    {
        Assert.Contains("variant 'maybe' does not exist", Rejected(() => _store.UpsertFlag(F.Make(prod: F.Env(fallthrough: F.Fixed("maybe"))), "a")).Message);
        var badOff = F.Env() with { OffVariant = "nothing" };
        Assert.Contains("off variant 'nothing'", Rejected(() => _store.UpsertFlag(F.Make(prod: badOff), "a")).Message);
    }

    [Fact]
    public void A_serve_must_be_exactly_one_of_a_variant_or_a_rollout()
    {
        var both = new Serve("on", [new WeightedVariant("on", 10_000)]);
        var neither = new Serve();

        Assert.Contains("either one variant or a rollout", Rejected(() => _store.UpsertFlag(F.Make(prod: F.Env(fallthrough: both)), "a")).Message);
        Assert.Contains("either one variant or a rollout", Rejected(() => _store.UpsertFlag(F.Make(prod: F.Env(fallthrough: neither)), "a")).Message);
    }

    [Fact]
    public void Rollout_weights_must_add_up_to_a_hundred_percent_with_real_variants_and_no_repeats()
    {
        var short80 = new Serve(null, [new WeightedVariant("on", 3000), new WeightedVariant("off", 5000)]);
        var ghost = new Serve(null, [new WeightedVariant("on", 5000), new WeightedVariant("maybe", 5000)]);
        var negative = new Serve(null, [new WeightedVariant("on", 12_000), new WeightedVariant("off", -2000)]);
        var repeated = new Serve(null, [new WeightedVariant("on", 5000), new WeightedVariant("on", 5000)]);

        Assert.Contains("80%", Rejected(() => _store.UpsertFlag(F.Make(prod: F.Env(fallthrough: short80)), "a")).Message);
        Assert.Contains("existing variants", Rejected(() => _store.UpsertFlag(F.Make(prod: F.Env(fallthrough: ghost)), "a")).Message);
        Assert.Contains("not negative", Rejected(() => _store.UpsertFlag(F.Make(prod: F.Env(fallthrough: negative)), "a")).Message);
        Assert.Contains("appears twice", Rejected(() => _store.UpsertFlag(F.Make(prod: F.Env(fallthrough: repeated)), "a")).Message);
    }

    [Fact]
    public void Rules_need_conditions_with_attributes_and_values_and_numeric_operators_need_one_number()
    {
        EnvironmentConfig WithRule(Rule rule) => F.Env(rules: [rule]);
        var noConditions = new Rule("r", [], F.Fixed("on"));
        var noValue = F.When("r", F.Fixed("on"), new Condition("plan", Operator.Equals, []));
        var noAttribute = F.When("r", F.Fixed("on"), new Condition("", Operator.Equals, ["x"]));
        var notNumber = F.When("r", F.Fixed("on"), new Condition("seats", Operator.GreaterThan, ["many"]));
        var twoNumbers = F.When("r", F.Fixed("on"), new Condition("seats", Operator.LessThan, ["1", "2"]));

        Assert.Contains("at least one condition", Rejected(() => _store.UpsertFlag(F.Make(prod: WithRule(noConditions)), "a")).Message);
        Assert.Contains("needs a value", Rejected(() => _store.UpsertFlag(F.Make(prod: WithRule(noValue)), "a")).Message);
        Assert.Contains("needs an attribute", Rejected(() => _store.UpsertFlag(F.Make(prod: WithRule(noAttribute)), "a")).Message);
        Assert.Contains("exactly one number", Rejected(() => _store.UpsertFlag(F.Make(prod: WithRule(notNumber)), "a")).Message);
        Assert.Contains("exactly one number", Rejected(() => _store.UpsertFlag(F.Make(prod: WithRule(twoNumbers)), "a")).Message);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var bad = F.Make("Bad Key", F.Env(fallthrough: F.Fixed("maybe")), variants: [new("on", "1"), new("on", "2")]);

        var ex = Rejected(() => _store.UpsertFlag(bad, "a"));

        Assert.True(ex.Errors.Count >= 3);
    }

    [Fact]
    public void A_prerequisite_must_exist_have_that_variant_and_not_be_the_flag_itself()
    {
        _store.UpsertFlag(F.Make("parent"), "a");
        EnvironmentConfig Needs(string flag, string variant) => F.Env(prerequisites: [new Prerequisite(flag, variant)]);

        Assert.Contains("does not exist", Rejected(() => _store.UpsertFlag(F.Make("child", Needs("ghost", "on")), "a")).Message);
        Assert.Contains("no variant 'maybe'", Rejected(() => _store.UpsertFlag(F.Make("child", Needs("parent", "maybe")), "a")).Message);
        Assert.Contains("depend on itself", Rejected(() => _store.UpsertFlag(F.Make("child", Needs("child", "on")), "a")).Message);
        _store.UpsertFlag(F.Make("child", Needs("parent", "on")), "a");
    }

    [Fact]
    public void A_prerequisite_loop_is_refused_even_through_several_flags()
    {
        _store.UpsertFlag(F.Make("a"), "x");
        _store.UpsertFlag(F.Make("b", F.Env(prerequisites: [new Prerequisite("a", "on")])), "x");
        _store.UpsertFlag(F.Make("c", F.Env(prerequisites: [new Prerequisite("b", "on")])), "x");

        var ex = Rejected(() => _store.UpsertFlag(F.Make("a", F.Env(prerequisites: [new Prerequisite("c", "on")])), "x"));

        Assert.Contains("loop", ex.Message);
        Assert.Empty(_store.Snapshot.Flags["a"].Environments["prod"].Prerequisites);
    }

    [Fact]
    public void A_refused_change_leaves_everything_exactly_as_it_was()
    {
        _store.UpsertFlag(F.Make("a"), "x");
        var before = _store.Snapshot;

        Rejected(() => _store.UpsertFlag(F.Make("a", F.Env(fallthrough: F.Fixed("maybe"))), "x"));

        Assert.Same(before, _store.Snapshot);
        Assert.Single(_store.Audit);
    }

    [Fact]
    public void A_flag_that_others_depend_on_cannot_be_deleted_until_they_are_changed()
    {
        _store.UpsertFlag(F.Make("parent"), "x");
        _store.UpsertFlag(F.Make("child", F.Env(prerequisites: [new Prerequisite("parent", "on")])), "x");

        Assert.Contains("prerequisite of child", Rejected(() => _store.DeleteFlag("parent", "x")).Message);
        _store.UpsertFlag(F.Make("child"), "x");
        _store.DeleteFlag("parent", "x");

        Assert.DoesNotContain("parent", _store.Snapshot.Flags.Keys);
        Assert.Contains("There is no flag", Rejected(() => _store.DeleteFlag("parent", "x")).Message);
    }

    [Fact]
    public void Turning_a_flag_off_is_one_audited_step_and_unknown_targets_are_refused()
    {
        _store.UpsertFlag(F.Make("a"), "x");

        _store.SetEnabled("a", "prod", false, "oncall");

        Assert.False(_store.Snapshot.Flags["a"].Environments["prod"].Enabled);
        Assert.True(_store.Snapshot.Flags["a"].Environments["dev"].Enabled);
        Assert.Contains("Switched off in prod", _store.Audit[^1].Summary);
        Rejected(() => _store.SetEnabled("a", "staging", true, "x"));
        Rejected(() => _store.SetEnabled("nope", "prod", true, "x"));
    }

    [Fact]
    public void A_switch_off_time_must_be_after_the_switch_on_time()
    {
        var env = F.Env(enableAt: F.Now.AddDays(2), disableAt: F.Now.AddDays(1));

        Assert.Contains("after the switch-on", Rejected(() => _store.UpsertFlag(F.Make(prod: env), "x")).Message);
    }

    [Fact]
    public void A_segment_can_be_saved_used_and_only_deleted_once_nothing_uses_it()
    {
        _store.UpsertSegment(new Segment("beta", ["ava"], [], []), "x");
        var rule = F.When("beta", F.Fixed("on"), new Condition("", Operator.InSegment, ["beta"]));
        _store.UpsertFlag(F.Make("a", F.Env(rules: [rule])), "x");

        Assert.Contains("used by a", Rejected(() => _store.DeleteSegment("beta", "x")).Message);
        _store.UpsertFlag(F.Make("a"), "x");
        _store.DeleteSegment("beta", "x");

        Assert.Empty(_store.Snapshot.Segments);
        Assert.Contains("no segment", Rejected(() => _store.DeleteSegment("beta", "x")).Message);
    }

    [Fact]
    public void A_rule_cannot_use_a_segment_that_does_not_exist()
    {
        var rule = F.When("beta", F.Fixed("on"), new Condition("", Operator.InSegment, ["ghost"]));

        Assert.Contains("segment 'ghost' does not exist", Rejected(() => _store.UpsertFlag(F.Make("a", F.Env(rules: [rule])), "x")).Message);
    }

    [Fact]
    public void A_segment_cannot_be_defined_in_terms_of_another_segment_or_have_bad_conditions()
    {
        _store.UpsertSegment(new Segment("a", [], [], []), "x");

        Assert.Contains("another segment", Rejected(() => _store.UpsertSegment(new Segment("b", [], [], [new Condition("", Operator.InSegment, ["a"])]), "x")).Message);
        Assert.Contains("valid segment key", Rejected(() => _store.UpsertSegment(new Segment("Bad Key", [], [], []), "x")).Message);
        Assert.Contains("needs a value", Rejected(() => _store.UpsertSegment(new Segment("c", [], [], [new Condition("plan", Operator.Equals, [])]), "x")).Message);
    }

    [Fact]
    public void Evaluating_through_the_store_uses_its_clock()
    {
        var env = F.Env(fallthrough: F.Fixed("on"), enableAt: F.Now.AddHours(1));
        _store.UpsertFlag(F.Make("a", env), "x");

        Assert.Equal(Reason.Scheduled, _store.Evaluate("prod", "a", new Context("u")).Reason);
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal("on", _store.Evaluate("prod", "a", new Context("u")).Variant);
    }

    [Fact]
    public void Replacing_everything_is_all_or_nothing()
    {
        _store.UpsertFlag(F.Make("keep"), "x");
        var before = _store.Snapshot;
        var bad = new[] { F.Make("fine"), F.Make("broken", F.Env(fallthrough: F.Fixed("maybe"))) };

        Rejected(() => _store.Replace(bad, [], "x"));

        Assert.Same(before, _store.Snapshot);
        _store.Replace([F.Make("fine"), F.Make("also-fine")], [], "x");
        Assert.Equal(["also-fine", "fine"], _store.Snapshot.Flags.Keys.Order());
    }

    [Fact]
    public void Replacing_everything_validates_prerequisites_across_the_whole_set()
    {
        var parent = F.Make("parent");
        var child = F.Make("child", F.Env(prerequisites: [new Prerequisite("parent", "on")]));

        _store.Replace([child, parent], [], "x");

        Assert.Equal(2, _store.Snapshot.Flags.Count);
        Rejected(() => _store.Replace([child], [], "x"));
    }
}
