using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class SheetSizesTests
{
    [Theory]
    [InlineData(SheetSize.A4, 297, 210)]
    [InlineData(SheetSize.A3, 420, 297)]
    [InlineData(SheetSize.A2, 594, 420)]
    [InlineData(SheetSize.A1, 841, 594)]
    [InlineData(SheetSize.A0, 1189, 841)]
    [InlineData(SheetSize.A, 279.4, 215.9)]
    [InlineData(SheetSize.B, 431.8, 279.4)]
    [InlineData(SheetSize.C, 558.8, 431.8)]
    [InlineData(SheetSize.D, 863.6, 558.8)]
    [InlineData(SheetSize.E, 1117.6, 863.6)]
    public void Standard_sizes_are_exact_mm_and_turn_for_portrait(SheetSize size, double width, double height)
    {
        Assert.Equal((width, height), SheetSizes.Of(size, SheetOrientation.Landscape));
        Assert.Equal((height, width), SheetSizes.Of(size, SheetOrientation.Portrait));
        Assert.Equal(size, SheetSizes.Match(width, height));
        Assert.Equal(size, SheetSizes.Match(height + 0.009, width - 0.009));
    }

    [Fact]
    public void A_size_off_by_more_than_a_hundredth_is_custom()
    {
        Assert.Null(SheetSizes.Match(420.02, 297));
        Assert.Null(SheetSizes.Match(500, 300));
        Assert.Equal(SheetOrientation.Portrait, SheetSizes.OrientationOf(297, 420));
        Assert.Equal(SheetOrientation.Landscape, SheetSizes.OrientationOf(300, 300));
    }

    [Fact]
    public void Asked_gives_a_standard_or_custom_size_or_nothing()
    {
        Assert.Equal((297.0, 420.0), SheetSizes.Asked(new SheetSetup { Size = SheetSize.A3, Orientation = SheetOrientation.Portrait }, ""));
        Assert.Equal((500.0, 300.0), SheetSizes.Asked(new SheetSetup { WidthMm = 500, HeightMm = 300 }, ""));
        Assert.Null(SheetSizes.Asked(new SheetSetup { Scale = new ScaleRatio(1, 2) }, ""));
    }

    [Theory]
    [InlineData("size and width", "sheet/width_mm")]
    [InlineData("width alone", "sheet/height_mm")]
    [InlineData("height alone", "sheet/width_mm")]
    [InlineData("orientation alone", "sheet/orientation")]
    [InlineData("too small", "sheet/width_mm")]
    [InlineData("too large", "sheet/height_mm")]
    [InlineData("zero numerator", "sheet/scale/numerator")]
    [InlineData("negative denominator", "sheet/scale/denominator")]
    public void Bad_values_are_refused_naming_the_field(string what, string field)
    {
        var setup = what switch
        {
            "size and width" => new SheetSetup { Size = SheetSize.A4, WidthMm = 300 },
            "width alone" => new SheetSetup { WidthMm = 300 },
            "height alone" => new SheetSetup { HeightMm = 300 },
            "orientation alone" => new SheetSetup { Orientation = SheetOrientation.Portrait },
            "too small" => new SheetSetup { WidthMm = 5, HeightMm = 300 },
            "too large" => new SheetSetup { WidthMm = 300, HeightMm = 20000 },
            "zero numerator" => new SheetSetup { Scale = new ScaleRatio(0, 1) },
            _ => new SheetSetup { Scale = new ScaleRatio(1, -2) },
        };
        var e = Assert.Throws<SwAgentException>(() => SheetSizes.Asked(setup, "sheet"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        var own = Assert.Throws<SwAgentException>(() => SheetSizes.Asked(setup, ""));
        Assert.Equal(field.Substring("sheet".Length), own.Error.Details!["field"]);
    }
}
