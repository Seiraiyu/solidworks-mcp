using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DialogAllowListTests
{
    [Fact]
    public void The_shipped_list_is_empty_until_a_prompt_is_measured()
    {
        Assert.Empty(DialogAllowList.Default.Rules);
    }

    [Fact]
    public void A_rule_matches_its_title_and_text_and_needs_its_button()
    {
        var list = new DialogAllowList(new[] { new DialogRule("rebuild", "^Rebuild$", "happened", "Continue") });
        Assert.Equal("rebuild", list.Match(Dialogs.Box(1, "Rebuild", "Stop", "&Continue"))!.Name);
        Assert.Null(list.Match(Dialogs.Box(2, "Rebuild errors", "Continue")));   // title
        Assert.Null(list.Match(Dialogs.Box(3, "Rebuild", "Stop")));              // no such button
    }

    [Fact]
    public void A_rule_without_a_text_pattern_matches_any_text()
    {
        var list = new DialogAllowList(new[] { new DialogRule("any", "^Q$", null, "1") });
        Assert.NotNull(list.Match(Dialogs.Box(4, "q", "OK")));                   // case-insensitive title; button by id
    }
}
