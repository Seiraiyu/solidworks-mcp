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

    [Theory]
    [InlineData("*.drt", "*.drt")]
    [InlineData(@"C:\formats\a3 - iso.slddrt", @"C:\formats\a3 - iso.slddrt")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_template_s_own_format_reads_as_that_not_as_none(string? name, string? format)
    {
        Assert.Equal(format, SheetSetups.FormatOf(name));
    }

    [Theory]
    [InlineData("*.drt", true)]
    [InlineData(@"C:\formats\a3.slddrt", false)]
    [InlineData(null, false)]
    public void A_format_with_no_file_is_the_template_s_own(string? format, bool inTemplate)
    {
        Assert.Equal(inTemplate, SheetSetups.InTemplate(format));
    }

    [Theory]
    [InlineData(null, @"C:\formats\a3.slddrt")]
    [InlineData(null, "*.drt")]
    [InlineData("*.drt", null)]
    [InlineData(@"C:\formats\a3.slddrt", "*.drt")]
    [InlineData(@"C:\formats\a3.slddrt", null)]
    public void The_format_is_compared_both_ways(string? wantedFormat, string? actualFormat)
    {
        var wanted = new SheetFacts { Name = "Sheet1", WidthMm = 420, HeightMm = 297, Scale = new ScaleRatio(1, 1), Format = wantedFormat };
        var actual = wanted.Copy();
        actual.Format = actualFormat;
        var mismatch = SheetSetups.Mismatch(wanted, actual);
        Assert.Equal("format", mismatch?.Check);
        Assert.Equal(wantedFormat ?? "none", mismatch?.Expected);
        Assert.Equal(actualFormat ?? "none", mismatch?.Measured);
    }

    [Theory]
    [InlineData(null, SheetFormatStep.Remove)]
    [InlineData(@"C:\formats\a3.slddrt", SheetFormatStep.UseFile)]
    [InlineData("*.drt", SheetFormatStep.Keep)]
    public void A_sheet_is_given_no_format_a_file_or_keeps_the_template_s_own(string? format, SheetFormatStep step)
    {
        Assert.Equal(step, SheetSetups.Step(new SheetFacts { Format = format }));
    }
}
