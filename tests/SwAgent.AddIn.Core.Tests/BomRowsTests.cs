using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class BomRowsTests
{
    const string Block = @"C:\scratch\block.SLDPRT";
    const string Pin = @"C:\scratch\pin.SLDPRT";
    const string Sub = @"C:\scratch\sub.SLDASM";

    static ComponentInfo C(string name, string path, string? parent = null, string configuration = "Default", ComponentState state = ComponentState.Resolved) =>
        new ComponentInfo { Name = name, Path = path, Configuration = configuration, State = state, Parent = parent, Subassembly = path.EndsWith(".SLDASM") };

    /// <summary>block-1, two pins at the top, and sub-1 holding a third pin and a block of another configuration.</summary>
    static readonly List<ComponentInfo> Assembly = new List<ComponentInfo>
    {
        C("block-1", Block), C("pin-1", Pin), C("pin-2", Pin), C("sub-1", Sub),
        C("sub-1/pin-1", Pin, "sub-1"), C("sub-1/block-1", Block, "sub-1", "Long"),
    };

    [Fact]
    public void A_top_level_bill_counts_each_file_and_configuration_at_the_top()
    {
        var rows = BomRows.Expected(Assembly, BomKind.TopLevel);
        Assert.Equal(new[] { "block.SLDPRT (Default) x1", "pin.SLDPRT (Default) x2", "sub.SLDASM (Default) x1" }, rows.Select(r => r.ToString()));
        Assert.All(rows, r => Assert.Equal(0, r.Level));
    }

    [Fact]
    public void A_parts_only_bill_counts_parts_at_every_level_and_no_subassembly()
    {
        var rows = BomRows.Expected(Assembly, BomKind.PartsOnly);
        Assert.Equal(new[] { "block.SLDPRT (Default) x1", "pin.SLDPRT (Default) x3", "block.SLDPRT (Long) x1" }, rows.Select(r => r.ToString()));
    }

    [Fact]
    public void An_indented_bill_puts_a_subassemblys_rows_after_it_one_level_down()
    {
        var rows = BomRows.Expected(Assembly, BomKind.Indented);
        Assert.Equal(new[] { "block.SLDPRT (Default) x1", "pin.SLDPRT (Default) x2", "sub.SLDASM (Default) x1", "pin.SLDPRT (Default) x1", "block.SLDPRT (Long) x1" }, rows.Select(r => r.ToString()));
        Assert.Equal(new[] { 0, 0, 0, 1, 1 }, rows.Select(r => r.Level));
    }

    [Fact]
    public void Suppressed_components_are_not_counted()
    {
        var list = new List<ComponentInfo> { C("block-1", Block), C("pin-1", Pin), C("pin-2", Pin, state: ComponentState.Suppressed) };
        Assert.Equal(new[] { "block.SLDPRT (Default) x1", "pin.SLDPRT (Default) x1" }, BomRows.Expected(list, BomKind.TopLevel).Select(r => r.ToString()));
    }

    static TableRow Row(string path, int? quantity, params string[] components) =>
        new TableRow { Path = path, Configuration = "default", Quantity = quantity, Components = components.ToList() };

    [Fact]
    public void Rows_with_the_same_files_and_quantities_in_any_order_and_case_match()
    {
        var expected = BomRows.Expected(Assembly.Take(3).ToList(), BomKind.TopLevel);
        Assert.Null(BomRows.Mismatch(expected, new[] { Row(Pin.ToLowerInvariant(), 2, "pin-1", "pin-2"), Row(Block, 1, "block-1") }));
    }

    [Fact]
    public void A_table_with_no_rows_is_said_to_have_none()
    {
        Assert.Equal("the table has no rows", BomRows.Mismatch(BomRows.Expected(Assembly, BomKind.TopLevel), new TableRow[0]));
    }

    [Fact]
    public void A_wrong_quantity_says_what_is_missing_and_what_was_not_expected()
    {
        var expected = BomRows.Expected(Assembly.Take(3).ToList(), BomKind.TopLevel);
        Assert.Equal("missing pin.SLDPRT (Default) x2; not expected pin.SLDPRT (default) x1",
            BomRows.Mismatch(expected, new[] { Row(Block, 1, "block-1"), Row(Pin, 1, "pin-1") }));
    }

    [Fact]
    public void A_row_without_a_quantity_cell_counts_its_components()
    {
        var expected = BomRows.Expected(Assembly.Take(3).ToList(), BomKind.TopLevel);
        Assert.Null(BomRows.Mismatch(expected, new[] { Row(Block, null, "block-1"), Row(Pin, null, "pin-1", "pin-2") }));
    }

    [Fact]
    public void A_row_naming_no_file_is_refused_by_its_number()
    {
        var expected = BomRows.Expected(Assembly.Take(1).ToList(), BomKind.TopLevel);
        Assert.Equal("row 1 names no single component file", BomRows.Mismatch(expected, new[] { new TableRow { Quantity = 1 } }));
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData(" 12 ", 12)]
    [InlineData("", null)]
    [InlineData("2 pcs", null)]
    public void Quantity_cells_read_as_whole_numbers(string text, int? quantity)
    {
        Assert.Equal(quantity, BomRows.ParseQuantity(text));
    }

    [Fact]
    public void A_bills_configuration_is_its_own_or_else_the_one_it_shows()
    {
        // SP4 (5.D live run, case 33): IBomFeature.Configuration read "" for a top-level bill made for Default.
        Assert.Equal("Default", BomRows.ConfigurationOf("Default", null, null));
        Assert.Equal("Default", BomRows.ConfigurationOf("", new object[] { "Default" }, new object[] { true }));
        Assert.Equal("Default", BomRows.ConfigurationOf(null, new object[] { "Other", "Default" }, new object[] { false, true }));
        Assert.Equal("Default", BomRows.ConfigurationOf("", new[] { "Default" }, null));
    }

    [Fact]
    public void A_bill_that_shows_no_one_configuration_has_none()
    {
        Assert.Null(BomRows.ConfigurationOf("", null, null));
        Assert.Null(BomRows.ConfigurationOf("", new object[] { "A", "B" }, new object[] { true, true }));
        Assert.Null(BomRows.ConfigurationOf("", new object[] { "A" }, new object[] { false }));
    }
}
