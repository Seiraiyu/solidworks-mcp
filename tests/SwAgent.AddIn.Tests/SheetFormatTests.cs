using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// A sheet's format as SOLIDWORKS names it (ISheet.GetTemplateName). The default drawing template's sheet reads "*.drt":
/// its format is held in the template, not in a file, so it is no format file (5.A live run).
/// </summary>
public class SheetFormatTests
{
    [Theory]
    [InlineData("*.drt")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("a3 - iso.slddrt")]
    [InlineData(@"C:\formats\a?.slddrt")]
    [InlineData("C:\\formats\\a3|.slddrt")]
    public void A_name_that_is_not_a_full_path_is_no_format_file(string? name)
    {
        Assert.Null(SheetSetups.FormatFile(name));
    }

    [Fact]
    public void A_full_path_is_the_format_file()
    {
        Assert.Equal(@"C:\formats\a3 - iso.slddrt", SheetSetups.FormatFile(@"C:\formats\a3 - iso.slddrt"));
    }

    [Fact]
    public void A_sheet_whose_format_name_is_not_a_path_compares_without_throwing()
    {
        var wanted = new SheetFacts { Name = "Sheet1", WidthMm = 420, HeightMm = 297, Scale = new ScaleRatio(1, 1), Format = "*.drt" };
        var actual = wanted.Copy();
        Assert.Null(SheetSetups.Mismatch(wanted, actual));
        actual.Format = @"C:\formats\a3.slddrt";
        Assert.Equal("format", SheetSetups.Mismatch(wanted, actual)?.Check);
    }
}
