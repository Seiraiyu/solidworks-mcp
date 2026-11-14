using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class DocumentNewDrawingTests : DocumentTestBase
{
    readonly string _drawingTemplate;

    public DocumentNewDrawingTests()
    {
        _drawingTemplate = PathOf("Drawing.DRWDOT");
        File.WriteAllText(_drawingTemplate, "template");
        Sw.DefaultDrwTemplate = _drawingTemplate;
    }

    DocumentNewResult New(DocumentNewRequest request) => Run(new DocumentNewOperation(Services), request);

    FakeDrawing Made => Sw.Docs.Last().Drw!;

    [Fact]
    public void Makes_a_drawing_from_the_default_drawing_template_with_its_sheet_and_no_view()
    {
        string path = PathOf("block.SLDDRW");
        var result = New(new DocumentNewRequest { Type = DocumentType.Drawing, Path = path });
        Assert.Equal(DocumentType.Drawing, result.Document.Type);
        Assert.Equal(_drawingTemplate, result.Template);
        Assert.Equal(path, result.Document.Path);
        Assert.True(Services.Runtime.Owned.Contains(path));
        var sheet = result.Sheet!;
        Assert.Equal("Sheet1", sheet.Name);
        Assert.Equal(SheetSize.A3, sheet.Size);
        Assert.Equal(SheetOrientation.Landscape, sheet.Orientation);
        Assert.True(sheet.Active);
        Assert.Empty(sheet.Views);
        Assert.Equal(path, References.Decode(sheet.Reference!, "sheet").DocumentKey);
    }

    [Fact]
    public void The_agents_drawing_hides_the_models_scaffolding_and_inserts_nothing_with_a_view()
    {
        New(new DocumentNewRequest { Type = DocumentType.Drawing });
        Assert.All(DrawingSettings.All, name => Assert.False(Made.Toggles[name], name));
    }

    [Fact]
    public void The_first_sheet_takes_the_size_scale_projection_and_format_asked()
    {
        string format = PathOf("a4 portrait.slddrt");
        File.WriteAllText(format, "format");
        var result = New(new DocumentNewRequest
        {
            Type = DocumentType.Drawing,
            Sheet = new SheetSetup { Size = SheetSize.A4, Orientation = SheetOrientation.Portrait, Scale = new ScaleRatio(1, 2), Projection = Projection.FirstAngle, Format = format },
        });
        var sheet = result.Sheet!;
        Assert.Equal(210, sheet.WidthMm);
        Assert.Equal(297, sheet.HeightMm);
        Assert.Equal(SheetSize.A4, sheet.Size);
        Assert.Equal(SheetOrientation.Portrait, sheet.Orientation);
        Assert.Equal(2, sheet.Scale.Denominator);
        Assert.Equal(Projection.FirstAngle, sheet.Projection);
        Assert.Equal(format, sheet.Format);
        Assert.Contains("set sheet Sheet1", Made.Calls);
    }

    [Fact]
    public void A_custom_size_is_taken_in_sheet_mm()
    {
        var sheet = New(new DocumentNewRequest { Type = DocumentType.Drawing, Sheet = new SheetSetup { WidthMm = 500, HeightMm = 300 } }).Sheet!;
        Assert.Null(sheet.Size);
        Assert.Equal(500, sheet.WidthMm);
    }

    [Theory]
    [InlineData("missing format", "sheet/format")]
    [InlineData("not a format", "sheet/format")]
    [InlineData("size and width", "sheet/width_mm")]
    [InlineData("a part with a sheet", "sheet")]
    [InlineData("a part path", "path")]
    [InlineData("a part template", "template")]
    public void Bad_drawing_requests_are_refused_before_anything_is_made(string what, string field)
    {
        File.WriteAllText(PathOf("format.txt"), "x");
        var request = what switch
        {
            "missing format" => new DocumentNewRequest { Type = DocumentType.Drawing, Sheet = new SheetSetup { Format = PathOf("none.slddrt") } },
            "not a format" => new DocumentNewRequest { Type = DocumentType.Drawing, Sheet = new SheetSetup { Format = PathOf("format.txt") } },
            "size and width" => new DocumentNewRequest { Type = DocumentType.Drawing, Sheet = new SheetSetup { Size = SheetSize.A3, WidthMm = 400 } },
            "a part with a sheet" => new DocumentNewRequest { Sheet = new SheetSetup { Size = SheetSize.A3 } },
            "a part path" => new DocumentNewRequest { Type = DocumentType.Drawing, Path = PathOf("block.SLDPRT") },
            _ => new DocumentNewRequest { Type = DocumentType.Drawing, Template = Template },
        };
        var e = Fails(() => New(request));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Sw.NewFrom);
    }

    [Fact]
    public void No_default_drawing_template_says_where_to_set_one()
    {
        Sw.DefaultDrwTemplate = null;
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Drawing }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("default drawing template", e.Message);
    }

    [Fact]
    public void A_model_view_command_left_open_fails_verification_naming_the_preference()
    {
        Sw.OpenCommandTitle = "Model View";
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Drawing }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("Model View", e.Error.Details!["command"]);
        Assert.Contains("insert_view_for_new_drawing", e.Message);
    }

    [Fact]
    public void A_sheet_that_does_not_take_its_scale_fails_verification_with_what_was_read()
    {
        Sw.NewDocumentMade = d => d.Drw!.SetSheetIgnored = true;
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Drawing, Sheet = new SheetSetup { Scale = new ScaleRatio(1, 5) } }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("scale", e.Error.Details!["check"]);
        Assert.Equal("1:5", e.Error.Details["expected"]);
        Assert.Equal("1:1", e.Error.Details["measured"]);
    }

    [Fact]
    public void A_setting_that_does_not_take_fails_verification_naming_it()
    {
        Sw.NewDocumentMade = d => d.Drw!.SettingsIgnored.Add("display_planes");
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Drawing }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(new object?[] { "display_planes" }, (List<object?>)e.Error.Details!["settings"]!);
    }

    [Fact]
    public void A_template_with_a_view_on_it_fails_verification()
    {
        var block = FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT"));
        block.ExactBox = new Box3 { Max = new Point3(60, 40, 20) };
        Sw.NewDocumentMade = d => d.Drw!.AddView("Sheet1", block, "front", new SheetPoint(100, 100));
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Drawing }));
        Assert.Equal("views", e.Error.Details!["check"]);
    }

    [Fact]
    public void A_drawing_SOLIDWORKS_fails_to_set_up_is_closed_again()
    {
        Sw.NewDocumentMade = d => d.Drw!.Sheets.Clear();
        var e = Fails(() => New(new DocumentNewRequest { Type = DocumentType.Drawing }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Contains("closed", e.Message);
        Assert.Empty(Sw.Docs);
    }
}
