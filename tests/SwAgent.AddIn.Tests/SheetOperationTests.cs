using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SheetSetTests : DrawingTestBase
{
    SheetResult Set(SheetSetRequest request) => Run(new SheetSetOperation(Services), request);

    [Fact]
    public void Changes_size_scale_and_projection_in_place_and_records_the_old_values()
    {
        var result = Set(new SheetSetRequest { Sheet = "Sheet1", Size = SheetSize.A2, Scale = new ScaleRatio(1, 5), Projection = Projection.FirstAngle });
        Assert.Equal(SheetSize.A2, result.Sheet.Size);
        Assert.Equal(5, result.Sheet.Scale.Denominator);
        Assert.Equal(Projection.FirstAngle, result.Sheet.Projection);
        var changed = result.Change.Changed.Single();
        Assert.Equal("size, scale, projection", changed.Property);
        Assert.StartsWith("Sheet1: 420 x 297 mm, 1:1, third_angle", changed.Before);
        Assert.StartsWith("Sheet1: 594 x 420 mm, 1:5, first_angle", changed.After);
    }

    [Fact]
    public void A_sheet_is_renamed_and_named_by_its_reference_too()
    {
        string reference = Services.References.IssueSheet(Doc, "Sheet1");
        var result = Set(new SheetSetRequest { Sheet = reference, Name = "Front views" });
        Assert.Equal("Front views", result.Sheet.Name);
        Assert.Equal(new[] { "Front views" }, Drw.SheetNames());
        var stale = Fails(() => Set(new SheetSetRequest { Sheet = reference, Scale = new ScaleRatio(1, 2) }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, stale.Code);
    }

    [Fact]
    public void Views_that_follow_the_sheet_are_checked_at_the_new_scale()
    {
        var follows = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        var own = Drw.AddView("Sheet1", Block, "top", new SheetPoint(100, 60), new ScaleRatio(2, 1));
        var result = Set(new SheetSetRequest { Sheet = "Sheet1", Scale = new ScaleRatio(1, 2) });
        var views = result.Sheet.Views.ToDictionary(v => v.Name);
        Assert.Equal(30, views[follows.Facts.Name].Extents!.Max.X - views[follows.Facts.Name].Extents!.Min.X, 9);
        Assert.Equal(2, views[own.Facts.Name].Scale.Numerator);
    }

    [Fact]
    public void A_view_not_redrawn_at_the_new_scale_rolls_the_sheet_back()
    {
        var view = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        view.KeepsScale = true;
        var e = Fails(() => Set(new SheetSetRequest { Sheet = "Sheet1", Scale = new ScaleRatio(1, 2) }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("scale", e.Error.Details!["check"]);
        Assert.Equal(view.Facts.Name, e.Error.Details["view"]);
        Assert.Equal(1, Drw.ReadSheet("Sheet1")!.Scale.Denominator);
    }

    [Fact]
    public void A_sheet_that_ignores_the_change_fails_verification()
    {
        Drw.SetSheetIgnored = true;
        var e = Fails(() => Set(new SheetSetRequest { Sheet = "Sheet1", WidthMm = 500, HeightMm = 300 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("size", e.Error.Details!["check"]);
    }

    [Theory]
    [InlineData("nothing", "/sheet")]
    [InlineData("taken name", "/name")]
    [InlineData("unknown sheet", "/sheet")]
    [InlineData("orientation alone", "/orientation")]
    public void Bad_requests_are_refused_before_anything_is_changed(string what, string field)
    {
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        Drw.Calls.Clear();
        var request = what switch
        {
            "nothing" => new SheetSetRequest { Sheet = "Sheet1" },
            "taken name" => new SheetSetRequest { Sheet = "Sheet1", Name = "sheet2" },
            "unknown sheet" => new SheetSetRequest { Sheet = "Sheet9", Scale = new ScaleRatio(1, 2) },
            _ => new SheetSetRequest { Sheet = "Sheet1", Orientation = SheetOrientation.Portrait },
        };
        var e = Fails(() => Set(request));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }
}

public class SheetActivateTests : DrawingTestBase
{
    SheetActivateResult Activate(string sheet) => Run(new SheetActivateOperation(Services), new SheetActivateRequest { Sheet = sheet });

    [Fact]
    public void Shows_the_sheet_and_says_which_was_shown_before()
    {
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        var result = Activate("Sheet1");
        Assert.Equal("Sheet1", result.Sheet);
        Assert.Equal("Sheet2", result.Before);
        Assert.Equal("Sheet1", Drw.Active);
    }

    [Fact]
    public void A_sheet_SOLIDWORKS_does_not_show_fails_verification()
    {
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        var op = new SheetActivateOperation(Services);
        IOperation o = op;
        var request = new SheetActivateRequest { Sheet = "Sheet1" };
        var result = (SheetActivateResult)o.Run(request, new TestContext());
        Drw.Active = "Sheet2";
        var e = Fails(() => o.Verify(request, result, new TestContext()));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("active", e.Error.Details!["check"]);
    }

    [Fact]
    public void It_waits_while_the_user_edits_the_sheet_format()
    {
        Drw.EditingFormat = true;
        var e = Fails(() => Activate("Sheet1"));
        Assert.Equal(ErrorCode.Busy, e.Code);
    }
}

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
