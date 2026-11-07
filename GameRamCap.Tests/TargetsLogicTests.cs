using GameRamCap;
using Xunit;

public class TargetsLogicTests
{
    [Theory]
    [InlineData("250", 1, 250)]
    [InlineData(" 7 ", 1, 7)]
    [InlineData("0", 64, 64)]
    [InlineData("-5", 64, 64)]
    [InlineData("abc", 64, 64)]
    [InlineData("", 64, 64)]
    [InlineData(null, 64, 64)]
    public void Num_returns_the_number_or_the_fallback(object? input, int fallback, int expected) =>
        Assert.Equal(expected, TargetsLogic.Num(input, fallback));

    [Theory]
    [InlineData("12", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("0", true)]
    [InlineData("-1", true)]
    [InlineData("x", true)]
    public void BadNum_only_flags_text_that_is_not_a_positive_whole_number(object? input, bool bad) =>
        Assert.Equal(bad, TargetsLogic.BadNum(input));

    [Fact]
    public void ParseAlso_splits_trims_and_drops_exe()
    {
        var r = TargetsLogic.ParseAlso(" steamwebhelper.EXE; steam ,, ");
        Assert.Equal(new[] { "steamwebhelper", "steam" }, r);
    }

    [Fact]
    public void Build_keeps_good_values_and_reports_nothing()
    {
        var p = new List<string>();
        var v = TargetsLogic.Build("discord", 200, 64, 600, true, false, 0, "", p);
        Assert.Equal((200, 64, 600), (v.CeilingMB, v.MinMB, v.MaxMB));
        Assert.Empty(p);
    }

    [Fact]
    public void Build_raises_max_to_min_and_says_so()
    {
        var p = new List<string>();
        var v = TargetsLogic.Build("app", 100, 300, 200, false, false, 0, null, p);
        Assert.Equal(300, v.MaxMB);
        Assert.Equal(300, v.CeilingMB);   // the ceiling is clamped into [min, max]
        Assert.Contains(p, m => m.Contains("Max (200) was below Min (300)"));
    }

    [Fact]
    public void Build_clamps_the_ceiling_into_the_range()
    {
        var p = new List<string>();
        var v = TargetsLogic.Build("app", 9999, 64, 600, true, false, 0, null, p);
        Assert.Equal(600, v.CeilingMB);
    }

    [Fact]
    public void Build_reports_bad_text_and_uses_defaults()
    {
        var p = new List<string>();
        var v = TargetsLogic.Build("app", "lots", "", "-3", true, true, "x", null, p);
        Assert.Equal((200, 64, 600), (v.CeilingMB, v.MinMB, v.MaxMB));
        Assert.Equal(0, v.BudgetMB);
        Assert.Equal(3, p.Count);   // Ceiling, Max and Budget (an empty Min is not an error)
        Assert.True(v.Own);
    }
}
