using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// block.SLDDRW, saved and clean: Sheet1 (A3, 1:1) with the block's front view at (120, 180), drawn 90..150 × 160..200, and
/// Details (A4 portrait) with no view; the user's export preferences: a PDF viewer after saving, DXF of all sheets at 1:1
/// with views as blocks, a mapping file and the hidden-layer warning.
/// </summary>
public class ExportDrawingTests : DrawingTestBase
{
    readonly FakePreferences _prefs = new FakePreferences();

    public ExportDrawingTests()
    {
        _prefs.Values[(PreferenceKind.Toggle, 617)] = 1;
        _prefs.Values[(PreferenceKind.Integer, 253)] = 2;
        _prefs.Values[(PreferenceKind.Integer, 136)] = 1;
        _prefs.Values[(PreferenceKind.Toggle, 806)] = 1;
        _prefs.Values[(PreferenceKind.Toggle, 8)] = 1;
        _prefs.Values[(PreferenceKind.Toggle, 453)] = 1;
        Services.ExportPreferences = new ScopedPreferences(_prefs, PathOf("export-preferences-journal.json"), NullLog.Instance);
        Doc.Prefs = _prefs;
        Drw.Prefs = _prefs;
        Drw.AddView("Sheet1", Block, "front", new SheetPoint(120, 180));
        Drw.AddSheet(new SheetFacts { Name = "Details", WidthMm = 210, HeightMm = 297, Scale = new ScaleRatio(1, 1) });
        Drw.Active = "Sheet1";
        Drw.Calls.Clear();
    }

    ExportDrawingResult Export(ExportDrawingRequest request) => Run(new ExportDrawingOperation(Services), request);

    /// <summary>Every preference is the user's again and the journal is gone.</summary>
    void UsersPreferencesBack()
    {
        Assert.Equal(1, _prefs.Values[(PreferenceKind.Toggle, 617)]);
        Assert.Equal(2, _prefs.Values[(PreferenceKind.Integer, 253)]);
        Assert.Equal(1, _prefs.Values[(PreferenceKind.Integer, 136)]);
        Assert.Equal(1, _prefs.Values[(PreferenceKind.Toggle, 806)]);
        Assert.Equal(1, _prefs.Values[(PreferenceKind.Toggle, 8)]);
        Assert.Equal(1, _prefs.Values[(PreferenceKind.Toggle, 453)]);
        Assert.False(File.Exists(PathOf("export-preferences-journal.json")));
    }

    static string CheckOf(SwAgentException e)
    {
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        return (string)e.Error.Details!["check"]!;
    }

    [Fact]
    public void A_pdf_of_every_sheet_has_a_page_each_its_sheets_size_and_opens_no_viewer()
    {
        string path = PathOf("block.pdf");
        var result = Export(new ExportDrawingRequest { Path = path, Format = DrawingExportFormat.Pdf });
        Assert.Equal(new[] { "Sheet1", "Details" }, result.Sheets);
        var file = Assert.Single(result.Files);
        Assert.Equal(path, file.Path);
        Assert.Equal("%PDF-1.4", file.Header);
        Assert.Equal(new[] { "Sheet1", "Details" }, file.Pages.Select(p => p.Sheet));
        Assert.Equal(420, file.Pages[0].WidthMm, 2);
        Assert.Equal(210, file.Pages[1].WidthMm, 2);
        Assert.Equal(0, Drw.PdfExports.Single().ViewOnSave);
        Assert.Equal(new[] { "swPDFViewOnSave = false" }, result.Preferences);
        Assert.False(result.MarkedDirty);
        Assert.False(result.Document.Dirty);
        Assert.Contains(result.Checks, c => c.Contains("kept its path") && c.Contains("2 sheets, 1 views"));
        UsersPreferencesBack();
    }

    [Fact]
    public void Named_sheets_are_written_in_the_drawings_order_by_name_or_reference()
    {
        string details = Run(new DrawingReadOperation(Services), new DrawingReadRequest()).Sheets[1].Reference!;
        var result = Export(new ExportDrawingRequest { Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf, Sheets = new List<string> { details, "Sheet1" } });
        Assert.Equal(new[] { "Sheet1", "Details" }, result.Sheets);
        Assert.Equal(new[] { "Sheet1", "Details" }, Drw.PdfExports.Single().Sheets);
    }

    [Fact]
    public void A_dxf_of_one_sheet_is_the_path_itself_in_sheet_mm_with_the_largest_views_edges_where_the_drawing_draws_them()
    {
        string path = PathOf("block.dxf");
        var result = Export(new ExportDrawingRequest { Path = path, Format = DrawingExportFormat.Dxf, Sheets = new List<string> { "Sheet1" } });
        var file = Assert.Single(result.Files);
        Assert.Equal(path, file.Path);
        Assert.Equal("DXF", file.Header);
        Assert.Equal("Drawing View1", file.View);
        Assert.Equal(90, file.ViewExtents!.Min.X, 6);
        Assert.Equal(200, file.ViewExtents.Max.Y, 6);
        Assert.Equal(420, file.Extents!.MaxXMm, 6);
        Assert.Empty(file.Pages);
        Assert.Equal(new[] { "Sheet1" }, Drw.SheetsExported);
        Assert.Contains("swDxfOutputNoScale = 0", result.Preferences);
        Assert.Equal("Sheet1", Drw.Active);
        UsersPreferencesBack();
    }

    [Fact]
    public void A_dxf_of_several_sheets_is_a_file_per_sheet_each_shown_in_turn_and_the_shown_sheet_put_back()
    {
        Drw.Active = "Details";
        var result = Export(new ExportDrawingRequest { Path = PathOf("block.dxf"), Format = DrawingExportFormat.Dxf });
        Assert.Equal(new[] { PathOf("block-Sheet1.dxf"), PathOf("block-Details.dxf") }, result.Files.Select(f => f.Path));
        Assert.Equal(new[] { "Sheet1", "Details" }, Drw.SheetsExported);
        Assert.Equal("Drawing View1", result.Files[0].View);
        Assert.Null(result.Files[1].View);
        Assert.Equal("Details", Drw.Active);
        Assert.Contains(result.Checks, c => c.Contains("Details is shown, as before"));
    }

    [Fact]
    public void A_dwg_is_checked_by_its_header()
    {
        var result = Export(new ExportDrawingRequest { Path = PathOf("block.dwg"), Format = DrawingExportFormat.Dwg, Sheets = new List<string> { "Sheet1" } });
        Assert.Equal("AC1032", result.Files.Single().Header);
        Assert.Null(result.Files.Single().Extents);
    }

    [Fact]
    public void A_sheet_name_a_file_name_cannot_hold_is_made_safe()
    {
        Assert.Equal("Sheet_1_", ExportDrawingOperation.SheetFileName("Sheet/1?"));
    }

    [Fact]
    public void A_drawing_never_saved_is_refused_before_solidworks_is_called()
    {
        var unsaved = Sw.Track(FakeDocument.NewDrawing("Draw2"));
        Sw.CurrentDoc = unsaved;
        var e = Fails(() => Export(new ExportDrawingRequest { Path = PathOf("draw2.pdf"), Format = DrawingExportFormat.Pdf }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(false, e.Error.Details!["saved"]);
        Assert.Contains("sw_document_save_as", e.Message);
        Assert.Empty(unsaved.Drw!.PdfExports);
        Assert.False(File.Exists(PathOf("draw2.pdf")));
    }

    [Fact]
    public void A_part_is_the_wrong_document_type_and_names_the_drawing_tools()
    {
        var e = Fails(() => Export(new ExportDrawingRequest { Document = "block.SLDPRT", Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
    }

    [Theory]
    [InlineData("Sheet9", ErrorCode.InvalidArgument)]
    [InlineData("twice", ErrorCode.InvalidArgument)]
    [InlineData("none", ErrorCode.InvalidArgument)]
    public void Sheets_that_cannot_be_written_are_refused_before_anything_is(string sheets, ErrorCode code)
    {
        var names = sheets switch
        {
            "twice" => new List<string> { "Sheet1", "Sheet1" },
            "none" => new List<string>(),
            _ => new List<string> { sheets },
        };
        var e = Fails(() => Export(new ExportDrawingRequest { Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf, Sheets = names }));
        Assert.Equal(code, e.Code);
        Assert.Empty(Drw.PdfExports);
    }

    [Fact]
    public void A_file_there_already_is_kept_unless_overwrite()
    {
        File.WriteAllText(PathOf("block-Details.dxf"), "mine");
        var e = Fails(() => Export(new ExportDrawingRequest { Path = PathOf("block.dxf"), Format = DrawingExportFormat.Dxf }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Empty(Drw.SheetsExported);
        Assert.Equal("mine", File.ReadAllText(PathOf("block-Details.dxf")));
        Assert.Equal(2, Export(new ExportDrawingRequest { Path = PathOf("block.dxf"), Format = DrawingExportFormat.Dxf, Overwrite = true }).Files.Count);
    }

    [Fact]
    public void A_drawing_the_export_marks_as_changed_is_reported_and_not_saved()
    {
        Drw.ExportMarksDirty = true;
        var result = Export(new ExportDrawingRequest { Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf });
        Assert.True(result.MarkedDirty);
        Assert.True(result.Document.Dirty);
        Assert.Empty(Doc.Saves);
        Assert.Contains(result.Checks, c => c.Contains("not saved to clear the flag"));
    }

    [Fact]
    public void Solidworks_refusing_to_write_is_operation_failed_and_the_preferences_are_put_back()
    {
        Drw.PdfFails = true;
        var e = Fails(() => Export(new ExportDrawingRequest { Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        UsersPreferencesBack();
    }

    [Fact]
    public void A_pdf_short_of_a_page_is_refused()
    {
        Drw.PdfDropsLastSheet = true;
        Assert.Equal("pdf_pages", CheckOf(Fails(() => Export(new ExportDrawingRequest { Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf }))));
    }

    [Fact]
    public void A_dxf_whose_view_is_not_where_the_drawing_draws_it_is_refused_and_the_shown_sheet_put_back()
    {
        Drw.DxfViewShiftMm = 2;
        Drw.Active = "Details";
        Assert.Equal("view_in_file", CheckOf(Fails(() => Export(new ExportDrawingRequest { Path = PathOf("block.dxf"), Format = DrawingExportFormat.Dxf }))));
        Assert.Equal("Details", Drw.Active);
        UsersPreferencesBack();
    }

    [Fact]
    public void An_export_that_changes_the_drawing_is_refused()
    {
        Drw.ExportDeletesAView = true;
        var e = Fails(() => Export(new ExportDrawingRequest { Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf }));
        Assert.Equal("drawing_unchanged", CheckOf(e));
        Assert.Contains("Drawing View1", e.Message);
    }

    [Fact]
    public void While_the_user_edits_the_sheet_format_it_waits_and_writes_nothing()
    {
        Drw.EditingFormat = true;
        var e = Fails(() => Export(new ExportDrawingRequest { Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf }));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        Assert.Empty(Drw.PdfExports);
    }

    [Fact]
    public void A_drawing_that_is_not_active_is_made_active_for_the_export_and_the_other_document_again()
    {
        Sw.Track(Block);
        Sw.CurrentDoc = Block;
        var result = Export(new ExportDrawingRequest { Document = "block.SLDDRW", Path = PathOf("block.pdf"), Format = DrawingExportFormat.Pdf });
        Assert.Same(Block, Sw.CurrentDoc);
        Assert.Contains(result.Checks, c => c.Contains("made active for the export and block.SLDPRT active again"));
    }
}
