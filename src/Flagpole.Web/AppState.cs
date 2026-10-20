using Flagpole.Engine;

namespace Flagpole.Web;

/// <summary>A flag service in the browser, with a client that talks to it and a clock you control.</summary>
public sealed class AppState
{
    public sealed class Source(FlagStore store) : IFlagSource
    {
        public bool Down { get; set; }

        public FlagSnapshot? Fetch(long? knownVersion) =>
            Down ? throw new InvalidOperationException("The flag service is not answering (503).") : new StoreFlagSource(store).Fetch(knownVersion);
    }

    public static readonly DateTimeOffset Start = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    public SimulatedClock Clock { get; private set; } = null!;
    public FlagStore Store { get; private set; } = null!;
    public Source ServiceSource { get; private set; } = null!;
    public FlagClient Client { get; private set; } = null!;

    public string Environment { get; set; } = "prod";
    public string UserId { get; set; } = "user-1";
    public string Plan { get; set; } = "pro";
    public string Country { get; set; } = "US";
    public string Seats { get; set; } = "80";
    public string Company { get; set; } = "acme";

    public string Message { get; private set; } = "";
    public bool MessageOk { get; private set; } = true;

    public AppState() => Reset();

    public DateTimeOffset Now => Clock.GetUtcNow();

    public Context Context => new(UserId, new Dictionary<string, string>
    {
        ["plan"] = Plan, ["country"] = Country, ["seats"] = Seats, ["company"] = Company,
    });

    public void Reset()
    {
        Clock = new SimulatedClock(Start);
        Store = new FlagStore(Clock);
        ServiceSource = new Source(Store);
        Client = new FlagClient(ServiceSource, "prod", Clock, TimeSpan.FromSeconds(30));
        Seed();
        Client.Refresh();
        Message = "";
    }

    private static IReadOnlyList<Variant> OnOff => [new("on", "true"), new("off", "false")];

    private static Serve Percent(double on) =>
        new(null, [new WeightedVariant("on", (int)Math.Round(on * 100)), new WeightedVariant("off", 10_000 - (int)Math.Round(on * 100))]);

    private static EnvironmentConfig Env(bool enabled, Serve fallthrough, IReadOnlyList<Rule>? rules = null, IReadOnlyList<Prerequisite>? prerequisites = null,
        DateTimeOffset? enableAt = null, DateTimeOffset? disableAt = null) =>
        new(enabled, "off", fallthrough, rules ?? [], prerequisites ?? [], enableAt, disableAt);

    private void Seed()
    {
        Store.UpsertSegment(new Segment("staff", [], [], [new Condition("userId", Operator.StartsWith, ["staff-"])]), "seed");
        Store.UpsertSegment(new Segment("beta-testers", ["user-7", "user-8"], ["user-9"], []), "seed");

        Store.UpsertFlag(new Flag("new-checkout", "The redesigned checkout page", OnOff, new Dictionary<string, EnvironmentConfig>
        {
            ["prod"] = Env(true, Percent(20), [
                new Rule("staff and beta testers", [new Condition("", Operator.InSegment, ["staff", "beta-testers"])], new Serve("on")),
                new Rule("small accounts stay on the old page", [new Condition("seats", Operator.LessThan, ["5"])], new Serve("off")),
            ]),
            ["dev"] = Env(true, new Serve("on")),
        }, "", ["checkout"], 1), "seed");

        Store.UpsertFlag(new Flag("payments-v2", "New payments provider", OnOff, new Dictionary<string, EnvironmentConfig>
        {
            ["prod"] = Env(true, Percent(50)),
            ["dev"] = Env(true, new Serve("on")),
        }, "", ["payments"], 1), "seed");

        Store.UpsertFlag(new Flag("apple-pay", "Apple Pay button (needs the new payments provider)", OnOff, new Dictionary<string, EnvironmentConfig>
        {
            ["prod"] = Env(true, new Serve("on"), [], [new Prerequisite("payments-v2", "on")]),
            ["dev"] = Env(true, new Serve("on")),
        }, "", ["payments"], 1), "seed");

        Store.UpsertFlag(new Flag("button-color", "Checkout button experiment", [new("blue", "#3b82f6"), new("green", "#22c55e"), new("orange", "#f97316"), new("off", "#94a3b8")],
            new Dictionary<string, EnvironmentConfig>
            {
                ["prod"] = new(true, "off", new Serve(null, [new WeightedVariant("blue", 2000), new WeightedVariant("green", 3000), new WeightedVariant("orange", 5000)], "company"), [], []),
                ["dev"] = new(true, "off", new Serve("blue"), [], []),
            }, "", ["experiment"], 1), "seed");

        Store.UpsertFlag(new Flag("holiday-banner", "Seasonal banner, live for 3 days starting tomorrow", OnOff, new Dictionary<string, EnvironmentConfig>
        {
            ["prod"] = Env(true, new Serve("on"), enableAt: Start.AddDays(1), disableAt: Start.AddDays(4)),
            ["dev"] = Env(true, new Serve("on")),
        }, "", ["marketing"], 1), "seed");
    }

    public void Act(Action action, string success)
    {
        try
        {
            action();
            Message = success;
            MessageOk = true;
        }
        catch (FlagValidationException ex)
        {
            Message = "Refused: " + string.Join(" ", ex.Errors);
            MessageOk = false;
        }
    }

    public void Wait(TimeSpan by)
    {
        Clock.Advance(by);
        Client.Tick();
        Message = $"Moved time forward {(by.TotalHours >= 1 ? $"{by.TotalHours:0} hour(s)" : $"{by.TotalSeconds:0} seconds")}.";
        MessageOk = true;
    }
}
