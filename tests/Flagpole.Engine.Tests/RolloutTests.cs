using Flagpole.Engine;

namespace Flagpole.Engine.Tests;

public class RolloutTests
{
    private static FlagSnapshot Rollout(double percent, string key = "new-checkout", string by = "userId") =>
        F.Snapshot([F.Make(key, F.Env(fallthrough: F.Percent(percent, by)))]);

    private static HashSet<string> OnUsers(FlagSnapshot snapshot, string key, int users, string prefix = "user-") =>
        Enumerable.Range(0, users).Select(i => prefix + i).Where(u => F.Eval(snapshot, key, u).Variant == "on").ToHashSet();

    [Fact]
    public void Bucketing_is_pinned_to_known_values_so_it_cannot_change_by_accident()
    {
        Assert.Equal(4531, Bucketing.Bucket("salt", "k", "user-1"));
        Assert.Equal(7640, Bucketing.Bucket("new-checkout", "new-checkout", "user-42"));
    }

    [Fact]
    public void Buckets_are_always_within_range()
    {
        for (var i = 0; i < 5000; i++)
        {
            Assert.InRange(Bucketing.Bucket("s", "k", "user-" + i), 0, Bucketing.Buckets - 1);
        }
    }

    [Fact]
    public void The_same_user_always_gets_the_same_answer()
    {
        var snapshot = Rollout(37);

        var first = Enumerable.Range(0, 500).Select(i => F.Eval(snapshot, "new-checkout", "user-" + i).Variant).ToList();
        var second = Enumerable.Range(0, 500).Select(i => F.Eval(snapshot, "new-checkout", "user-" + i).Variant).ToList();

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(99)]
    public void A_rollout_reaches_close_to_the_requested_share_of_users(double percent)
    {
        const int users = 40_000;

        var share = OnUsers(Rollout(percent), "new-checkout", users).Count * 100.0 / users;

        Assert.InRange(share, percent - 1.0, percent + 1.0);
    }

    [Fact]
    public void Zero_percent_reaches_nobody_and_a_hundred_percent_reaches_everybody()
    {
        Assert.Empty(OnUsers(Rollout(0), "new-checkout", 5000));
        Assert.Equal(5000, OnUsers(Rollout(100), "new-checkout", 5000).Count);
    }

    [Fact]
    public void Raising_a_rollout_only_adds_users_and_never_takes_the_feature_away_from_anyone()
    {
        var previous = new HashSet<string>();
        foreach (var percent in new[] { 5.0, 10, 25, 40, 60, 80, 100 })
        {
            var current = OnUsers(Rollout(percent), "new-checkout", 20_000);

            Assert.True(previous.IsSubsetOf(current), $"going to {percent}% removed someone");
            previous = current;
        }
    }

    [Fact]
    public void Lowering_a_rollout_only_removes_users()
    {
        var wide = OnUsers(Rollout(60), "new-checkout", 20_000);
        var narrow = OnUsers(Rollout(20), "new-checkout", 20_000);

        Assert.True(narrow.IsSubsetOf(wide));
    }

    [Fact]
    public void Two_flags_at_fifty_percent_overlap_by_about_a_quarter_so_they_are_independent()
    {
        const int users = 40_000;
        var snapshot = F.Snapshot([
            F.Make("flag-a", F.Env(fallthrough: F.Percent(50))),
            F.Make("flag-b", F.Env(fallthrough: F.Percent(50))),
        ]);

        var a = OnUsers(snapshot, "flag-a", users);
        var b = OnUsers(snapshot, "flag-b", users);

        var both = a.Intersect(b).Count() * 100.0 / users;
        Assert.InRange(both, 24.0, 26.0);
    }

    [Fact]
    public void A_different_salt_reshuffles_who_is_included()
    {
        var one = F.Snapshot([F.Make("f", F.Env(fallthrough: F.Percent(50)), salt: "v1")]);
        var two = F.Snapshot([F.Make("f", F.Env(fallthrough: F.Percent(50)), salt: "v2")]);

        var a = OnUsers(one, "f", 5000);
        var b = OnUsers(two, "f", 5000);

        Assert.NotEqual(a, b);
        Assert.InRange(a.Intersect(b).Count() * 100.0 / 5000, 22.0, 28.0);
    }

    [Fact]
    public void A_multi_way_split_follows_its_weights()
    {
        var split = new Serve(null, [new("a", 2000), new("b", 3000), new("c", 5000)]);
        var variants = new Variant[] { new("a", "A"), new("b", "B"), new("c", "C"), new("off", "none") };
        var snapshot = F.Snapshot([F.Make("exp", F.Env(fallthrough: split), variants: variants)]);
        const int users = 50_000;

        var counts = Enumerable.Range(0, users).GroupBy(i => F.Eval(snapshot, "exp", "u" + i).Variant).ToDictionary(g => g.Key!, g => g.Count() * 100.0 / users);

        Assert.InRange(counts["a"], 19, 21);
        Assert.InRange(counts["b"], 29, 31);
        Assert.InRange(counts["c"], 49, 51);
        Assert.False(counts.ContainsKey("off"));
    }

    [Fact]
    public void Bucketing_by_company_keeps_everyone_in_a_company_together()
    {
        var snapshot = Rollout(50, by: "company");

        for (var company = 0; company < 100; company++)
        {
            var answers = Enumerable.Range(0, 12)
                .Select(i => F.Eval(snapshot, "new-checkout", $"user-{company}-{i}", attributes: ("company", "co-" + company)).Variant)
                .Distinct().ToList();

            Assert.Single(answers);
        }
    }

    [Fact]
    public void Bucketing_by_company_splits_companies_roughly_in_half()
    {
        var snapshot = Rollout(50, by: "company");

        var on = Enumerable.Range(0, 4000).Count(c => F.Eval(snapshot, "new-checkout", "u", attributes: ("company", "co-" + c)).Variant == "on");

        Assert.InRange(on * 100.0 / 4000, 47, 53);
    }

    [Fact]
    public void A_user_missing_the_bucketing_attribute_is_bucketed_by_user_id_instead()
    {
        var snapshot = Rollout(50, by: "company");

        var withoutCompany = F.Eval(snapshot, "new-checkout", "user-7");
        var byUser = F.Eval(Rollout(50), "new-checkout", "user-7");

        Assert.Equal(byUser.Variant, withoutCompany.Variant);
    }

    [Fact]
    public void Narrowing_a_rollout_to_a_segment_only_considers_that_segment()
    {
        var staff = new Segment("staff", [], [], [new Condition("email", Operator.EndsWith, ["@acme.com"])]);
        var rules = new[] { F.When("staff at 50%", F.Percent(50), new Condition("", Operator.InSegment, ["staff"])) };
        var snapshot = F.Snapshot([F.Make(prod: F.Env(rules: rules))], [staff]);

        var outsiders = Enumerable.Range(0, 2000).Count(i => F.Eval(snapshot, "new-checkout", "o" + i, attributes: ("email", $"o{i}@other.com")).Variant == "on");
        var insiders = Enumerable.Range(0, 4000).Count(i => F.Eval(snapshot, "new-checkout", "i" + i, attributes: ("email", $"i{i}@acme.com")).Variant == "on");

        Assert.Equal(0, outsiders);
        Assert.InRange(insiders * 100.0 / 4000, 47, 53);
    }
}
