using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>A drawing the agent opened, showing a block and a pin loaded only for it.</summary>
public abstract class DrawingSceneBase : DocumentTestBase
{
    internal readonly FakeDocument Drawing;
    internal readonly FakeDocument Block;
    internal readonly FakeDocument Pin;

    protected DrawingSceneBase()
    {
        Drawing = Sw.Track(FakeDocument.NewDrawing("block.SLDDRW", PathOf("block.SLDDRW")));
        Block = Sw.Track(FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT")));
        Pin = Sw.Track(FakeDocument.Part("pin.SLDPRT", PathOf("pin.SLDPRT")));
        Block.Document.Visible = Pin.Document.Visible = false;
        Drawing.Uses.AddRange(new[] { Block.Document.Path!, Pin.Document.Path! });
        Drawing.Document.Dirty = true;
        Services.Runtime.DocumentOpened(Drawing.Document.Path!, owned: true);
    }
}

public class DrawingSaveTests : DrawingSceneBase
{
    DocumentSaveResult Save(string document) => Run(new DocumentSaveOperation(Services), new DocumentSaveRequest { Document = document });

    [Fact]
    public void Saves_the_dirty_models_it_shows_first_then_the_drawing_and_lists_each()
    {
        Block.Document.Dirty = true;
        var result = Save("block.SLDDRW");
        Assert.Equal(new[] { Block.Document.Path, Drawing.Document.Path }, Block.Saves.Concat(Drawing.Saves).ToArray());
        Assert.Empty(Pin.Saves);
        Assert.Equal(new[] { Block.Document.Path }, result.Referenced.Select(r => r.Path));
        Assert.False(Block.Document.Dirty);
    }

    [Fact]
    public void A_model_that_will_not_save_stops_before_the_drawing_and_names_it()
    {
        Block.Document.Dirty = Pin.Document.Dirty = true;
        Pin.FailNextSave = new SaveOutcome(false, 1, 0);
        var e = Fails(() => Save("block.SLDDRW"));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Empty(Drawing.Saves);
        Assert.Equal(Drawing.Document.Path, e.Error.Details!["drawing"]);
        Assert.Equal(new object?[] { Block.Document.Path }, (List<object?>)e.Error.Details["saved"]!);
    }

    [Fact]
    public void Save_as_writes_the_drawing_only()
    {
        Block.Document.Dirty = true;
        string copy = PathOf("copy.SLDDRW");
        var result = Run(new DocumentSaveAsOperation(Services), new DocumentSaveAsRequest { Document = "block.SLDDRW", Path = copy });
        Assert.Equal(copy, result.Path);
        Assert.Empty(result.Referenced);
        Assert.Empty(Block.Saves);
        Assert.True(Block.Document.Dirty);
    }
}

public class DrawingCloseTests : DrawingSceneBase
{
    DocumentCloseResult Close(string document, bool discard = false) =>
        Run(new DocumentCloseOperation(Services), new DocumentCloseRequest { Document = document, Discard = discard });

    [Fact]
    public void Closing_a_drawing_unloads_the_models_only_it_shows_and_lists_them()
    {
        Drawing.Document.Dirty = false;
        var result = Close("block.SLDDRW");
        Assert.Equal(new[] { "block.SLDPRT", "pin.SLDPRT" }, result.Unloaded.Select(u => u.Title));
        Assert.Empty(Sw.Docs);
        Assert.False(Services.Runtime.Owned.Contains(Drawing.Document.Path!));
    }

    [Fact]
    public void A_dirty_model_it_would_unload_refuses_the_close_and_says_to_save_the_drawing()
    {
        Drawing.Document.Dirty = false;
        Pin.Document.Dirty = true;
        var e = Fails(() => Close("block.SLDDRW"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("discard", e.Error.Details!["field"]);
        Assert.Equal(new object?[] { Pin.Document.Path }, (List<object?>)e.Error.Details["unsaved"]!);
        Assert.Contains("the drawing (document.save saves the models it shows too)", e.Message);
        Assert.Contains(Drawing, Sw.Docs);
    }

    [Fact]
    public void A_model_with_its_own_window_stays_open()
    {
        Drawing.Document.Dirty = false;
        Block.Document.Visible = true;
        var result = Close("block.SLDDRW");
        Assert.Equal(new[] { "pin.SLDPRT" }, result.Unloaded.Select(u => u.Title));
        Assert.Contains(Block, Sw.Docs);
    }

    [Fact]
    public void A_model_loaded_only_for_the_drawing_is_closed_through_the_drawing()
    {
        var e = Fails(() => Close("pin.SLDPRT"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains($"Close {Drawing.Document.Path} instead", e.Message);
        Assert.Equal(new object?[] { Drawing.Document.Path }, (List<object?>)e.Error.Details!["used_by"]!);
    }

    [Fact]
    public void A_model_of_the_agents_drawing_is_the_agents()
    {
        Pin.Document.Visible = true;
        var result = Close("pin.SLDPRT");
        Assert.Equal(new[] { Drawing.Document.Path }, result.StillUsedBy);
        Assert.False(Pin.Document.Visible);
    }
}

public class DocumentOpenDrawingTests : DocumentTestBase
{
    DocumentOpenResult Open(string path) => Run(new DocumentOpenOperation(Services), new DocumentOpenRequest { Path = path });

    /// <summary>block.SLDDRW on disk, showing block.SLDPRT (on disk) and gone.SLDPRT (not).</summary>
    (string Path, string Block, string Gone) Files()
    {
        string path = PathOf("block.SLDDRW"), block = PathOf("block.SLDPRT"), gone = PathOf("gone.SLDPRT");
        File.WriteAllText(path, "drawing");
        File.WriteAllText(block, "part");
        Sw.OpenWith = p =>
        {
            var drawing = FakeDocument.NewDrawing("block.SLDDRW", p);
            drawing.Uses.AddRange(new[] { block, gone });
            return new OpenOutcome(Sw.Track(drawing), 0, 0);
        };
        return (path, block, gone);
    }

    [Fact]
    public void A_drawing_lists_the_model_files_that_are_not_there()
    {
        var (path, _, gone) = Files();
        var result = Open(path);
        Assert.Equal(DocumentType.Drawing, result.Document.Type);
        Assert.Equal(new[] { gone }, result.Missing);
        Assert.Equal((path, DocumentType.Drawing, false), Sw.Opened[0]);
    }

    [Fact]
    public void A_drawings_model_found_in_another_folder_is_listed_as_substituted()
    {
        var (path, block, _) = Files();
        Sw.Stored[path] = new List<string> { PathOf(Path.Combine("moved", "block.SLDPRT")) };
        var s = Assert.Single(Open(path).Substituted);
        Assert.Equal(block, s.Loaded);
    }

    [Fact]
    public void A_model_SOLIDWORKS_leaves_active_is_replaced_by_the_drawing()
    {
        var (path, block, _) = Files();
        var model = Sw.Track(FakeDocument.Part("block.SLDPRT", block));
        model.Document.Visible = false;
        Sw.OpenLeavesActive = model;
        var result = Open(path);
        Assert.Equal("block.SLDDRW", Sw.ActiveDoc()!.Info().Title);
        Assert.Equal(path, result.Document.Path);
    }

    [Fact]
    public void A_drawing_that_will_not_become_active_fails_verification()
    {
        var (path, block, _) = Files();
        Sw.OpenLeavesActive = Sw.Track(FakeDocument.Part("block.SLDPRT", block));
        var open = Sw.OpenWith!;
        Sw.OpenWith = p =>
        {
            var outcome = open(p);
            ((FakeDocument)outcome.Document!).ActivateIgnored = true;
            return outcome;
        };
        var e = Fails(() => Open(path));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("active", e.Error.Details!["check"]);
    }
}

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
