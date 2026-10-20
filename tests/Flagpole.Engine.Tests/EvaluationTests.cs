using Flagpole.Engine;

namespace Flagpole.Engine.Tests;

public class EvaluationTests
{
    [Fact]
    public void An_unknown_flag_or_environment_gives_no_variant_and_says_why()
    {
        var snapshot = F.Snapshot([F.Make()]);

        var noFlag = F.Eval(snapshot, "nope");
        var noEnv = F.Eval(snapshot, "new-checkout", env: "staging");

        Assert.Equal(Reason.FlagNotFound, noFlag.Reason);
        Assert.Null(noFlag.Variant);
        Assert.Equal(Reason.EnvironmentNotFound, noEnv.Reason);
        Assert.Null(noEnv.Value);
    }

    [Fact]
    public void A_flag_that_is_switched_off_serves_its_off_variant_whatever_the_rules_say()
    {
        var rules = new[] { F.When("everyone", F.Fixed("on"), F.Is("plan", "pro")) };
        var snapshot = F.Snapshot([F.Make(prod: F.Env(enabled: false, fallthrough: F.Fixed("on"), rules: rules))]);

        var result = F.Eval(snapshot, "new-checkout", attributes: ("plan", "pro"));

        Assert.Equal(Reason.Off, result.Reason);
        Assert.Equal("off", result.Variant);
        Assert.Equal("false", result.Value);
    }

    [Fact]
    public void Environments_are_independent()
    {
        var snapshot = F.Snapshot([F.Make(prod: F.Env(false), dev: F.Env(true, F.Fixed("on")))]);

        Assert.Equal("off", F.Eval(snapshot, "new-checkout", env: "prod").Variant);
        Assert.Equal("on", F.Eval(snapshot, "new-checkout", env: "dev").Variant);
    }

    [Fact]
    public void The_default_applies_when_no_rule_matches()
    {
        var snapshot = F.Snapshot([F.Make(prod: F.Env(fallthrough: F.Fixed("on")))]);

        var result = F.Eval(snapshot, "new-checkout");

        Assert.Equal(Reason.Fallthrough, result.Reason);
        Assert.Equal("on", result.Variant);
    }

    [Fact]
    public void The_first_matching_rule_wins_and_later_rules_are_not_consulted()
    {
        var rules = new[]
        {
            F.When("pro users", F.Fixed("on"), F.Is("plan", "pro")),
            F.When("everyone in the US", F.Fixed("off"), F.Is("country", "US")),
        };
        var snapshot = F.Snapshot([F.Make(prod: F.Env(rules: rules))]);

        var both = F.Eval(snapshot, "new-checkout", attributes: [("plan", "pro"), ("country", "US")]);
        var onlyUs = F.Eval(snapshot, "new-checkout", attributes: ("country", "US"));

        Assert.Equal("on", both.Variant);
        Assert.Equal(0, both.RuleIndex);
        Assert.Contains("pro users", both.Detail);
        Assert.Equal("off", onlyUs.Variant);
        Assert.Equal(1, onlyUs.RuleIndex);
    }

    [Fact]
    public void A_rule_needs_all_of_its_conditions()
    {
        var rules = new[] { F.When("US pros", F.Fixed("on"), F.Is("plan", "pro"), F.Is("country", "US")) };
        var snapshot = F.Snapshot([F.Make(prod: F.Env(rules: rules))]);

        Assert.Equal("on", F.Eval(snapshot, "new-checkout", attributes: [("plan", "pro"), ("country", "US")]).Variant);
        Assert.Equal("off", F.Eval(snapshot, "new-checkout", attributes: ("plan", "pro")).Variant);
    }

    [Fact]
    public void The_value_comes_from_the_variant_and_non_boolean_values_work()
    {
        var variants = new Variant[] { new("blue", "#0044ff"), new("green", "#00aa44"), new("off", "none") };
        var snapshot = F.Snapshot([F.Make("button-color", F.Env(fallthrough: F.Fixed("green")), variants: variants)]);

        var result = F.Eval(snapshot, "button-color");

        Assert.Equal("green", result.Variant);
        Assert.Equal("#00aa44", result.Value);
    }

    [Fact]
    public void A_scheduled_flag_is_off_before_its_start_live_inside_the_window_and_off_again_after_it()
    {
        var start = F.Now.AddDays(1);
        var end = F.Now.AddDays(3);
        var snapshot = F.Snapshot([F.Make(prod: F.Env(fallthrough: F.Fixed("on"), enableAt: start, disableAt: end))]);

        var before = F.Eval(snapshot, "new-checkout", now: F.Now);
        var at = F.Eval(snapshot, "new-checkout", now: start);
        var inside = F.Eval(snapshot, "new-checkout", now: F.Now.AddDays(2));
        var atEnd = F.Eval(snapshot, "new-checkout", now: end);

        Assert.Equal((Reason.Scheduled, "off"), (before.Reason, before.Variant));
        Assert.Equal("on", at.Variant);
        Assert.Equal("on", inside.Variant);
        Assert.Equal((Reason.Scheduled, "off"), (atEnd.Reason, atEnd.Variant));
    }

    [Fact]
    public void A_flag_whose_prerequisite_is_not_met_serves_off_and_names_the_prerequisite()
    {
        var parent = F.Make("payments-v2", F.Env(fallthrough: F.Fixed("off")));
        var child = F.Make("apple-pay", F.Env(fallthrough: F.Fixed("on"), prerequisites: [new Prerequisite("payments-v2", "on")]));
        var snapshot = F.Snapshot([parent, child]);

        var result = F.Eval(snapshot, "apple-pay");

        Assert.Equal(Reason.PrerequisiteFailed, result.Reason);
        Assert.Equal("off", result.Variant);
        Assert.Contains("payments-v2", result.Detail);
    }

    [Fact]
    public void A_flag_whose_prerequisite_is_met_is_evaluated_normally()
    {
        var parent = F.Make("payments-v2", F.Env(fallthrough: F.Fixed("on")));
        var child = F.Make("apple-pay", F.Env(fallthrough: F.Fixed("on"), prerequisites: [new Prerequisite("payments-v2", "on")]));

        Assert.Equal("on", F.Eval(F.Snapshot([parent, child]), "apple-pay").Variant);
    }

    [Fact]
    public void Prerequisites_are_evaluated_for_the_same_user()
    {
        var parent = F.Make("payments-v2", F.Env(rules: [F.When("staff", F.Fixed("on"), F.Is("team", "staff"))]));
        var child = F.Make("apple-pay", F.Env(fallthrough: F.Fixed("on"), prerequisites: [new Prerequisite("payments-v2", "on")]));
        var snapshot = F.Snapshot([parent, child]);

        Assert.Equal("on", F.Eval(snapshot, "apple-pay", attributes: ("team", "staff")).Variant);
        Assert.Equal("off", F.Eval(snapshot, "apple-pay", attributes: ("team", "sales")).Variant);
    }

    [Fact]
    public void A_missing_prerequisite_flag_counts_as_not_met()
    {
        var child = F.Make("apple-pay", F.Env(fallthrough: F.Fixed("on"), prerequisites: [new Prerequisite("ghost", "on")]));

        var result = F.Eval(F.Snapshot([child]), "apple-pay");

        Assert.Equal(Reason.PrerequisiteFailed, result.Reason);
        Assert.Contains("unavailable", result.Detail);
    }

    [Fact]
    public void A_prerequisite_loop_that_slipped_in_cannot_hang_evaluation()
    {
        var a = F.Make("a", F.Env(fallthrough: F.Fixed("on"), prerequisites: [new Prerequisite("b", "on")]));
        var b = F.Make("b", F.Env(fallthrough: F.Fixed("on"), prerequisites: [new Prerequisite("a", "on")]));

        var result = F.Eval(F.Snapshot([a, b]), "a");

        Assert.Equal("off", result.Variant);
        Assert.Contains(result.Reason, new[] { Reason.Error, Reason.PrerequisiteFailed });
    }

    [Fact]
    public void A_segment_rule_serves_members_and_others_fall_through()
    {
        var beta = new Segment("beta", ["ava", "ben"], [], []);
        var rules = new[] { F.When("beta testers", F.Fixed("on"), new Condition("", Operator.InSegment, ["beta"])) };
        var snapshot = F.Snapshot([F.Make(prod: F.Env(rules: rules))], [beta]);

        Assert.Equal("on", F.Eval(snapshot, "new-checkout", "ava").Variant);
        Assert.Equal("off", F.Eval(snapshot, "new-checkout", "cara").Variant);
    }

    [Fact]
    public void A_percentage_rule_reports_the_bucket_it_used()
    {
        var rules = new[] { F.When("pros get half", F.Percent(50), F.Is("plan", "pro")) };
        var snapshot = F.Snapshot([F.Make(prod: F.Env(rules: rules))]);

        var result = F.Eval(snapshot, "new-checkout", attributes: ("plan", "pro"));

        Assert.Equal(Reason.Rollout, result.Reason);
        Assert.InRange(result.Bucket!.Value, 0, 9999);
        Assert.Contains($"bucket {result.Bucket}", result.Detail);
    }
}
