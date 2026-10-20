using Flagpole.Engine;

namespace Flagpole.Engine.Tests;

public class SerializerTests
{
    private readonly TestClock _clock = new();

    private FlagStore RichStore()
    {
        var store = new FlagStore(_clock);
        store.UpsertSegment(new Segment("beta", ["ava", "ben"], ["cara"], [new Condition("plan", Operator.In, ["pro", "team"])]), "x");
        store.UpsertFlag(F.Make("payments-v2", F.Env(fallthrough: F.Percent(30))), "x");
        var rules = new[]
        {
            F.When("beta", F.Fixed("on"), new Condition("", Operator.InSegment, ["beta"])),
            F.When("big", F.Percent(25, "company"), new Condition("seats", Operator.GreaterOrEqual, ["50"]), F.Is("country", "US", "CA")),
        };
        var env = F.Env(fallthrough: F.Fixed("off"), rules: rules, prerequisites: [new Prerequisite("payments-v2", "on")], enableAt: F.Now, disableAt: F.Now.AddDays(30));
        store.UpsertFlag(F.Make("apple-pay", env) with { Description = "Apple Pay at checkout", Tags = ["payments", "q4"] }, "x");
        return store;
    }

    [Fact]
    public void Exporting_and_importing_gives_back_the_same_flags_and_segments()
    {
        var original = RichStore().Snapshot;

        var (flags, segments) = FlagSerializer.FromJson(FlagSerializer.ToJson(original));
        var imported = new FlagStore(_clock);
        imported.Replace(flags, segments, "import");

        Assert.Equal(FlagSerializer.ToJson(original), FlagSerializer.ToJson(imported.Snapshot with { Version = original.Version }));
    }

    [Fact]
    public void Evaluations_are_identical_after_a_round_trip()
    {
        var original = RichStore().Snapshot;
        var (flags, segments) = FlagSerializer.FromJson(FlagSerializer.ToJson(original));
        var imported = new FlagStore(_clock);
        imported.Replace(flags, segments, "import");

        for (var i = 0; i < 500; i++)
        {
            var ctx = F.Ctx("user-" + i, ("plan", i % 3 == 0 ? "pro" : "free"), ("seats", (i % 100).ToString()), ("country", i % 2 == 0 ? "US" : "DE"), ("company", "co-" + i % 40));
            foreach (var key in new[] { "apple-pay", "payments-v2" })
            {
                var before = Evaluator.Evaluate(original, "prod", key, ctx, F.Now.AddDays(1));
                var after = Evaluator.Evaluate(imported.Snapshot, "prod", key, ctx, F.Now.AddDays(1));
                Assert.Equal(before, after);
            }
        }
    }

    [Fact]
    public void The_json_is_stable_so_it_diffs_cleanly()
    {
        var a = FlagSerializer.ToJson(RichStore().Snapshot);
        var b = FlagSerializer.ToJson(RichStore().Snapshot);

        Assert.Equal(a, b);
        Assert.True(a.IndexOf("apple-pay", StringComparison.Ordinal) < a.IndexOf("payments-v2", StringComparison.Ordinal));
        Assert.Contains("\"Op\": \"InSegment\"", a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Flags\": [{\"Key\": 5}]}")]
    public void Bad_files_are_refused_with_a_clear_error(string json) =>
        Assert.Throws<FlagValidationException>(() => FlagSerializer.FromJson(json));

    [Fact]
    public void An_import_that_is_well_formed_but_invalid_is_refused_by_the_store()
    {
        const string json = "{\"Flags\":[{\"Key\":\"a\",\"Description\":\"\",\"Variants\":[],\"Environments\":{},\"Salt\":\"\",\"Tags\":[],\"Version\":1}],\"Segments\":[]}";
        var (flags, segments) = FlagSerializer.FromJson(json);

        Assert.Throws<FlagValidationException>(() => new FlagStore(_clock).Replace(flags, segments, "import"));
    }
}

public class PropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Random_valid_flags_always_evaluate_to_an_existing_variant_without_throwing(int seed)
    {
        var random = new Random(seed);
        var store = new FlagStore(new TestClock());
        var keys = new List<string>();
        var variants = new Variant[] { new("a", "1"), new("b", "2"), new("c", "3"), new("off", "0") };
        string[] attributes = ["plan", "country", "seats", "company"];
        Operator[] operators = [Operator.Equals, Operator.NotEquals, Operator.In, Operator.StartsWith, Operator.GreaterThan, Operator.LessOrEqual, Operator.Contains];

        Serve RandomServe()
        {
            if (random.Next(3) == 0)
            {
                var a = random.Next(0, 10_001);
                var b = random.Next(0, 10_001 - a);
                return new Serve(null, [new WeightedVariant("a", a), new WeightedVariant("b", b), new WeightedVariant("c", 10_000 - a - b)], attributes[random.Next(attributes.Length)]);
            }

            return new Serve(variants[random.Next(variants.Length)].Key);
        }

        Condition RandomCondition()
        {
            var op = operators[random.Next(operators.Length)];
            var numeric = op is Operator.GreaterThan or Operator.LessOrEqual;
            return new Condition(attributes[random.Next(attributes.Length)], op, [numeric ? random.Next(0, 100).ToString() : "pro"]);
        }

        for (var i = 0; i < 25; i++)
        {
            var key = "flag-" + i;
            var rules = Enumerable.Range(0, random.Next(4)).Select(r => new Rule("r" + r, Enumerable.Range(0, random.Next(1, 3)).Select(_ => RandomCondition()).ToList(), RandomServe())).ToList();
            var prerequisites = keys.Count > 0 && random.Next(3) == 0 ? new[] { new Prerequisite(keys[random.Next(keys.Count)], "a") } : [];
            var env = new EnvironmentConfig(random.Next(5) != 0, "off", RandomServe(), rules, prerequisites);
            store.UpsertFlag(new Flag(key, "", variants, new Dictionary<string, EnvironmentConfig> { ["prod"] = env }, "", [], 1), "seed");
            keys.Add(key);
        }

        var snapshot = store.Snapshot;
        for (var u = 0; u < 400; u++)
        {
            var ctx = F.Ctx("user-" + u, ("plan", random.Next(2) == 0 ? "pro" : "free"), ("seats", random.Next(0, 200).ToString()), ("country", "US"));
            foreach (var key in keys)
            {
                var result = Evaluator.Evaluate(snapshot, "prod", key, ctx, F.Now);

                Assert.NotNull(result.Variant);
                Assert.Contains(result.Variant, variants.Select(v => v.Key));
                Assert.Equal(variants.Single(v => v.Key == result.Variant).Value, result.Value);
            }
        }
    }
}

public class ConsistencyTests
{
    [Fact]
    public async Task Readers_taking_one_snapshot_never_see_two_flags_that_were_changed_together_disagree()
    {
        var store = new FlagStore(new TestClock());
        FlagSnapshot Pair(string variant) => F.Snapshot([
            F.Make("a", F.Env(fallthrough: F.Fixed(variant))),
            F.Make("b", F.Env(fallthrough: F.Fixed(variant))),
        ]);
        store.Replace(Pair("on").Flags.Values, [], "w");
        var stop = false;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 600 && !stop; i++)
            {
                store.Replace(Pair(i % 2 == 0 ? "off" : "on").Flags.Values, [], "w");
            }
        });

        var disagreements = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var bad = 0;
            for (var i = 0; i < 20_000; i++)
            {
                var snapshot = store.Snapshot;
                var a = Evaluator.Evaluate(snapshot, "prod", "a", new Context("u"), F.Now).Variant;
                var b = Evaluator.Evaluate(snapshot, "prod", "b", new Context("u"), F.Now).Variant;
                bad += a == b ? 0 : 1;
            }

            return bad;
        })));
        stop = true;
        await writer;

        Assert.All(disagreements, d => Assert.Equal(0, d));
    }

    [Fact]
    public async Task Parallel_writers_never_lose_an_update_or_skip_a_version()
    {
        var store = new FlagStore(new TestClock());

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                store.UpsertFlag(F.Make($"flag-{t}-{i}"), "w" + t);
            }
        })));

        Assert.Equal(400, store.Snapshot.Flags.Count);
        Assert.Equal(400, store.Snapshot.Version);
        Assert.Equal(Enumerable.Range(1, 400).Select(i => (long)i), store.Audit.Select(a => a.Version));
    }
}
