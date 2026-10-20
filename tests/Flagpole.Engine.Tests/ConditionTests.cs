using Flagpole.Engine;

namespace Flagpole.Engine.Tests;

public class ConditionTests
{
    private static bool Matches(Operator op, string actual, params string[] values) =>
        Evaluator.Matches(FlagSnapshot.Empty, new Condition("plan", op, values), F.Ctx("u1", ("plan", actual)));

    [Theory]
    [InlineData(Operator.Equals, "pro", "pro", true)]
    [InlineData(Operator.Equals, "PRO", "pro", true)]
    [InlineData(Operator.Equals, "free", "pro", false)]
    [InlineData(Operator.NotEquals, "free", "pro", true)]
    [InlineData(Operator.NotEquals, "PRO", "pro", false)]
    [InlineData(Operator.StartsWith, "enterprise-plus", "enter", true)]
    [InlineData(Operator.StartsWith, "pro", "enter", false)]
    [InlineData(Operator.EndsWith, "enterprise-plus", "plus", true)]
    [InlineData(Operator.EndsWith, "enterprise", "plus", false)]
    [InlineData(Operator.Contains, "enterprise-plus", "prise", true)]
    [InlineData(Operator.Contains, "enterprise-plus", "gold", false)]
    public void Single_value_operators_compare_text_ignoring_case(Operator op, string actual, string value, bool expected) =>
        Assert.Equal(expected, Matches(op, actual, value));

    [Fact]
    public void In_matches_any_listed_value_and_NotIn_matches_none_of_them()
    {
        Assert.True(Matches(Operator.In, "pro", "free", "pro"));
        Assert.False(Matches(Operator.In, "team", "free", "pro"));
        Assert.True(Matches(Operator.NotIn, "team", "free", "pro"));
        Assert.False(Matches(Operator.NotIn, "pro", "free", "pro"));
    }

    [Theory]
    [InlineData(Operator.GreaterThan, "10", "5", true)]
    [InlineData(Operator.GreaterThan, "5", "5", false)]
    [InlineData(Operator.GreaterOrEqual, "5", "5", true)]
    [InlineData(Operator.LessThan, "4.5", "5", true)]
    [InlineData(Operator.LessThan, "5", "5", false)]
    [InlineData(Operator.LessOrEqual, "5", "5", true)]
    [InlineData(Operator.GreaterThan, "100", "99", true)]
    [InlineData(Operator.GreaterThan, "9", "10", false)]
    public void Numbers_are_compared_as_numbers_not_as_text(Operator op, string actual, string limit, bool expected) =>
        Assert.Equal(expected, Matches(op, actual, limit));

    [Theory]
    [InlineData(Operator.GreaterThan)]
    [InlineData(Operator.LessOrEqual)]
    public void A_non_numeric_value_never_matches_a_numeric_comparison(Operator op)
    {
        Assert.False(Matches(op, "many", "5"));
        Assert.False(Matches(op, "10", "five"));
    }

    [Theory]
    [InlineData(Operator.Equals)]
    [InlineData(Operator.NotEquals)]
    [InlineData(Operator.In)]
    [InlineData(Operator.NotIn)]
    [InlineData(Operator.StartsWith)]
    [InlineData(Operator.Contains)]
    [InlineData(Operator.GreaterThan)]
    public void A_user_without_the_attribute_never_matches_any_operator(Operator op)
    {
        var condition = new Condition("country", op, ["US"]);

        Assert.False(Evaluator.Matches(FlagSnapshot.Empty, condition, F.Ctx("u1", ("plan", "pro"))));
        Assert.False(Evaluator.Matches(FlagSnapshot.Empty, condition, new Context("u1")));
    }

    [Fact]
    public void The_user_id_can_be_used_as_an_attribute()
    {
        var condition = new Condition("userId", Operator.StartsWith, ["staff-"]);

        Assert.True(Evaluator.Matches(FlagSnapshot.Empty, condition, new Context("staff-ava")));
        Assert.False(Evaluator.Matches(FlagSnapshot.Empty, condition, new Context("ava")));
    }

    private static FlagSnapshot WithSegments(params Segment[] segments) => F.Snapshot([], segments);

    [Fact]
    public void A_named_user_is_in_the_segment_even_without_matching_its_rules()
    {
        var snapshot = WithSegments(new Segment("beta", ["ava"], [], [F.Is("plan", "pro")]));

        Assert.True(Evaluator.Matches(snapshot, new Condition("", Operator.InSegment, ["beta"]), new Context("ava")));
    }

    [Fact]
    public void Someone_matching_every_segment_rule_is_in_it_and_someone_missing_one_is_not()
    {
        var snapshot = WithSegments(new Segment("big-us", [], [], [F.Is("country", "US"), new Condition("seats", Operator.GreaterThan, ["50"])]));
        var inSegment = new Condition("", Operator.InSegment, ["big-us"]);

        Assert.True(Evaluator.Matches(snapshot, inSegment, F.Ctx("u", ("country", "US"), ("seats", "80"))));
        Assert.False(Evaluator.Matches(snapshot, inSegment, F.Ctx("u", ("country", "US"), ("seats", "10"))));
        Assert.False(Evaluator.Matches(snapshot, inSegment, F.Ctx("u", ("country", "CA"), ("seats", "80"))));
    }

    [Fact]
    public void An_excluded_user_is_out_even_if_named_or_matching()
    {
        var snapshot = WithSegments(new Segment("beta", ["ava"], ["ava"], [F.Is("plan", "pro")]), new Segment("pro-only", [], ["ben"], [F.Is("plan", "pro")]));

        Assert.False(Evaluator.Matches(snapshot, new Condition("", Operator.InSegment, ["beta"]), new Context("ava")));
        Assert.False(Evaluator.Matches(snapshot, new Condition("", Operator.InSegment, ["pro-only"]), F.Ctx("ben", ("plan", "pro"))));
    }

    [Fact]
    public void A_segment_with_no_named_users_and_no_rules_matches_nobody()
    {
        var snapshot = WithSegments(new Segment("empty", [], [], []));

        Assert.False(Evaluator.Matches(snapshot, new Condition("", Operator.InSegment, ["empty"]), new Context("ava")));
    }

    [Fact]
    public void Several_segments_match_if_the_user_is_in_any_and_a_missing_segment_matches_nobody()
    {
        var snapshot = WithSegments(new Segment("a", ["ava"], [], []), new Segment("b", ["ben"], [], []));
        var either = new Condition("", Operator.InSegment, ["a", "b", "ghost"]);

        Assert.True(Evaluator.Matches(snapshot, either, new Context("ben")));
        Assert.False(Evaluator.Matches(snapshot, either, new Context("cara")));
    }
}
