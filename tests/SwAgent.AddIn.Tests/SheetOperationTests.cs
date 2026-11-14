using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SheetAddTests : DrawingTestBase
{
    SheetResult Add(SheetAddRequest request) => Run(new SheetAddOperation(Services), request);

    [Fact]
    public void Adds_a_standard_sheet_and_reads_it_back()
    {
        var result = Add(new SheetAddRequest { Size = SheetSize.A3, Scale = new ScaleRatio(1, 2), Projection = Projection.FirstAngle });
        var sheet = result.Sheet;
        Assert.Equal("Sheet2", sheet.Name);
        Assert.Equal(SheetSize.A3, sheet.Size);
        Assert.Equal(420, sheet.WidthMm);
        Assert.Equal(2, sheet.Scale.Denominator);
        Assert.Equal(Projection.FirstAngle, sheet.Projection);
        Assert.True(sheet.Active);
        Assert.Equal(Drw.ReadSheet("Sheet1")!.Format, sheet.Format);
        Assert.StartsWith("r1.sheet.", sheet.Reference);
        Assert.Equal("Sheet2", result.Change.Created.Single().Name);
        Assert.Equal((1, 2), (result.Change.SheetsBefore, result.Change.SheetsAfter));
    }

    [Fact]
    public void A_custom_portrait_sheet_with_its_own_name_and_format()
    {
        string format = PathOf("custom.slddrt");
        File.WriteAllText(format, "format");
        var sheet = Add(new SheetAddRequest { Name = "Details", WidthMm = 300, HeightMm = 500, Format = format }).Sheet;
        Assert.Equal("Details", sheet.Name);
        Assert.Null(sheet.Size);
        Assert.Equal(SheetOrientation.Portrait, sheet.Orientation);
        Assert.Equal(format, sheet.Format);
    }

    [Theory]
    [InlineData("Sheet1", "/name")]
    [InlineData(" ", "/name")]
    public void A_name_a_sheet_has_or_an_empty_one_is_refused(string name, string field)
    {
        var e = Fails(() => Add(new SheetAddRequest { Name = name }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Single(Drw.Sheets);
    }

    [Theory]
    [InlineData("/width_mm")]
    [InlineData("/format")]
    [InlineData("/scale/denominator")]
    public void Bad_values_are_refused_naming_the_field(string field)
    {
        var request = field switch
        {
            "/width_mm" => new SheetAddRequest { HeightMm = 300 },
            "/format" => new SheetAddRequest { Format = PathOf("none.slddrt") },
            _ => new SheetAddRequest { Scale = new ScaleRatio(1, 0) },
        };
        var e = Fails(() => Add(request));
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void SOLIDWORKS_making_no_sheet_is_operation_failed()
    {
        Drw.AddSheetFails = true;
        var e = Fails(() => Add(new SheetAddRequest { Size = SheetSize.A4 }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("SOLIDWORKS made no sheet", e.Message);
    }

    [Fact]
    public void A_sheet_that_does_not_take_its_scale_is_deleted_again()
    {
        Drw.SheetScaleIgnored = new ScaleRatio(1, 1);
        var e = Fails(() => Add(new SheetAddRequest { Scale = new ScaleRatio(2, 1) }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("scale", e.Error.Details!["check"]);
        Assert.Equal(new object?[] { "Sheet2" }, (List<object?>)e.Error.Details["rolled_back"]!);
        Assert.Equal(new[] { "Sheet1" }, Drw.SheetNames());
    }
}
