using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>export.flat_pattern on the 2 mm channel of <see cref="SheetMetalTestBase"/>: two 90° bends at R 3, its part saved at part1.SLDPRT.</summary>
public class ExportFlatPatternOperationTests : SheetMetalTestBase
{
    internal ExportFlatPatternResult ExportFlat(ExportFlatPatternRequest request) => Run(new ExportFlatPatternOperation(Services), request);

    /// <summary>The channel, saved: its unsaved-changes flag cleared.</summary>
    internal FakeDocument SavedChannel()
    {
        var doc = Part();
        U();
        Channel();
        doc.Document.Dirty = false;
        return doc;
    }

    static FlatSize FlatOf(FakeDocument doc) => SheetMetalParts.Flat(doc.CutList(), doc.MmPerUnit(), doc.FakeBends.Count)!;

    [Fact]
    public void A_DXF_with_bend_lines_is_written_by_ExportToDWG2_and_matches_the_flat_size_with_a_line_per_bend()
    {
        var doc = SavedChannel();
        string path = PathOf("channel.dxf");
        var result = ExportFlat(new ExportFlatPatternRequest { Path = path, Format = FlatPatternFormat.Dxf });
        Assert.Equal(path, result.Path);
        Assert.Equal("DXF", result.Header);
        Assert.Equal(FlatPatternExports.ToDwg2, result.Method);
        Assert.Equal((FlatPatternExports.ToDwg2, path, true, false), doc.FlatExports.Single());
        Assert.Single(result.Attempts);
        var flat = FlatOf(doc);
        Assert.Equal(flat.LengthMm, result.Flat!.LengthMm, 9);
        Assert.Equal(flat.LengthMm, result.Extents!.LengthMm, 6);
        Assert.Equal(flat.WidthMm, result.Extents.WidthMm, 6);
        Assert.Equal("its entities", result.Extents.From);
        Assert.Equal(2, result.BendLines);
        Assert.Contains("0 = 6", result.Layers);
        Assert.Equal(SheetState.Folded, result.State);
        Assert.Contains(result.Checks, c => c.Contains("match the flat size"));
        Assert.Contains(result.Checks, c => c.Contains("2 bend lines across the outline for 2 bends"));
        Assert.Contains(result.Checks, c => c.Contains("kept its path") && c.Contains("folded"));
        Assert.False(result.Document.Dirty);
        Assert.Equal(new FileInfo(path).Length, result.Bytes);
    }

    [Fact]
    public void Without_bend_lines_CADapters_call_writes_it_and_the_sheet_in_its_header_is_only_reported()
    {
        var doc = SavedChannel();
        var result = ExportFlat(new ExportFlatPatternRequest { Path = PathOf("plain.dxf"), Format = FlatPatternFormat.Dxf, BendLines = false });
        Assert.Equal((FlatPatternExports.FlatPatternView, PathOf("plain.dxf"), false, false), doc.FlatExports.Single());
        Assert.Equal(0, result.BendLines);
        Assert.Equal(FlatOf(doc).LengthMm, result.Extents!.LengthMm, 6);
        Assert.Contains(result.Checks, c => c == "no bend lines, as asked");
        Assert.Contains(result.Checks, c => c.Contains("$EXTMIN/$EXTMAX give 279.4 × 215.9 mm") && c.Contains("not compared"));
    }

    [Fact]
    public void Bend_lines_are_never_passed_without_them_when_only_CADapters_call_writes()
    {
        // ExportFlatPatternView draws no bend lines (6.D live run); when ExportToDWG2 writes nothing, its file fails.
        var doc = SavedChannel();
        doc.FlatExportWritesNothing.Add(FlatPatternExports.ToDwg2);
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("view-only.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("bend_lines", e.Error.Details!["check"]);
        Assert.Equal(new[] { FlatPatternExports.ToDwg2, FlatPatternExports.FlatPatternView }, doc.FlatExports.Select(x => x.Method));
    }

    [Fact]
    public void A_DWG_is_checked_by_its_header_and_size()
    {
        SavedChannel();
        var result = ExportFlat(new ExportFlatPatternRequest { Path = PathOf("channel.dwg"), Format = FlatPatternFormat.Dwg });
        Assert.Equal("AC1032", result.Header);
        Assert.Null(result.Extents);
        Assert.Null(result.BendLines);
        Assert.Empty(result.Layers);
        Assert.Contains(result.Checks, c => c.Contains("its extents are not read"));
    }

    [Fact]
    public void Sketches_go_through_ExportToDWG2_with_the_part_named()
    {
        var doc = SavedChannel();
        var result = ExportFlat(new ExportFlatPatternRequest { Path = PathOf("sketches.dxf"), Format = FlatPatternFormat.Dxf, Sketches = true });
        Assert.Equal(FlatPatternExports.ToDwg2, result.Method);
        Assert.Equal((FlatPatternExports.ToDwg2, PathOf("sketches.dxf"), true, true), doc.FlatExports.Single());
    }

    [Fact]
    public void When_ExportFlatPatternView_writes_nothing_ExportToDWG2_is_tried()
    {
        var doc = SavedChannel();
        doc.FlatExportWritesNothing.Add(FlatPatternExports.FlatPatternView);
        var result = ExportFlat(new ExportFlatPatternRequest { Path = PathOf("second.dxf"), Format = FlatPatternFormat.Dxf, BendLines = false });
        Assert.Equal(FlatPatternExports.ToDwg2, result.Method);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Contains("ExportFlatPatternView answered true and wrote nothing", result.Attempts[0]);
        Assert.StartsWith("ExportToDWG2 answered true and wrote", result.Attempts[1]);
    }

    [Fact]
    public void When_no_call_writes_it_fails_listing_every_attempt()
    {
        var doc = SavedChannel();
        doc.FlatExportFails.Add(FlatPatternExports.FlatPatternView);
        doc.FlatExportFails.Add(FlatPatternExports.ToDwg2);
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("none.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(2, ((List<object?>)e.Error.Details!["attempts"]!).Count);
        Assert.Contains("ExportFlatPatternView answered false", e.Message);
        Assert.False(File.Exists(PathOf("none.dxf")));
    }

    [Fact]
    public void A_part_never_saved_is_refused_before_SOLIDWORKS_is_called()
    {
        var doc = SavedChannel();
        doc.Document.Path = null;
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("unsaved.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("document", e.Error.Details!["field"]);
        Assert.Contains("sw_document_save_as", e.Message);
        Assert.Empty(doc.FlatExports);
    }

    [Fact]
    public void A_part_that_is_not_sheet_metal_is_the_wrong_type()
    {
        var doc = Part();
        Block();
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("block.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Equal("sheet_metal", e.Error.Details!["needs"]);
        Assert.Empty(doc.FlatExports);
    }

    [Fact]
    public void The_path_must_end_as_its_format_says()
    {
        var doc = SavedChannel();
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("channel.dxf"), Format = FlatPatternFormat.Dwg }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("path", e.Error.Details!["field"]);
        Assert.Empty(doc.FlatExports);
    }

    [Fact]
    public void An_existing_file_is_replaced_only_when_asked()
    {
        var doc = SavedChannel();
        string path = PathOf("again.dxf");
        File.WriteAllText(path, "old");
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = path, Format = FlatPatternFormat.Dxf })).Code);
        Assert.Empty(doc.FlatExports);
        Assert.Equal("DXF", ExportFlat(new ExportFlatPatternRequest { Path = path, Format = FlatPatternFormat.Dxf, Overwrite = true }).Header);
    }

    [Fact]
    public void A_DXF_needs_the_flat_size_from_the_cut_list_before_anything_is_written()
    {
        var doc = SavedChannel();
        doc.CutListOverride = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Bends", "2") };
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("no-size.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("no flat size", e.Message);
        Assert.Empty(doc.FlatExports);
    }

    [Fact]
    public void A_drawing_off_the_flat_size_fails_its_check()
    {
        var doc = SavedChannel();
        doc.FlatExportOffMm = 0.02;
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("off.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("flat_size", e.Error.Details!["check"]);
    }

    [Fact]
    public void Bend_lines_asked_for_and_missing_fail_their_check()
    {
        var doc = SavedChannel();
        doc.FlatExportDropsBendLines = true;
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("dropped.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal("bend_lines", e.Error.Details!["check"]);
    }

    [Fact]
    public void An_export_that_leaves_the_part_flattened_fails_and_says_how_to_fold_it()
    {
        var doc = SavedChannel();
        doc.FlatExportFlattens = true;
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("flattened.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("state_unchanged", e.Error.Details!["check"]);
        Assert.Contains("sw_sheetmetal_flatten", e.Message);
    }

    [Fact]
    public void An_export_that_saves_the_part_fails()
    {
        var doc = SavedChannel();
        doc.Document.Dirty = true;
        doc.FlatExportTogglesDirty = true;
        var e = Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("saved.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal("document_unchanged", e.Error.Details!["check"]);
    }

    [Fact]
    public void A_clean_part_the_call_marks_as_changed_is_reported_not_failed()
    {
        // The 6.D live run: on SOLIDWORKS 2026 SP4, ExportToDWG2 marks a saved part as having unsaved changes. The
        // export answers, says so, and leaves the flag as SOLIDWORKS set it: the user's file is not saved behind them.
        var doc = SavedChannel();
        doc.FlatExportTogglesDirty = true;
        var result = ExportFlat(new ExportFlatPatternRequest { Path = PathOf("marked.dxf"), Format = FlatPatternFormat.Dxf });
        Assert.True(result.MarkedDirty);
        Assert.True(result.Document.Dirty);
        Assert.Contains(result.Checks, c => c.Contains("ExportToDWG2 marked") && c.Contains("SOLIDWORKS 2026 SP4") && c.Contains("not saved"));
        Assert.True(doc.Info().Dirty, "nothing saved the part to clear the flag");
    }

    [Fact]
    public void A_part_whose_flag_stays_is_not_marked()
    {
        SavedChannel();
        Assert.False(ExportFlat(new ExportFlatPatternRequest { Path = PathOf("kept.dxf"), Format = FlatPatternFormat.Dxf }).MarkedDirty);
    }

    [Fact]
    public void A_part_with_unsaved_changes_is_exported_as_it_is_and_stays_unsaved()
    {
        var doc = SavedChannel();
        doc.Document.Dirty = true;
        var result = ExportFlat(new ExportFlatPatternRequest { Path = PathOf("dirty.dxf"), Format = FlatPatternFormat.Dxf });
        Assert.True(result.Document.Dirty);
        Assert.True(doc.Info().Dirty);
    }

    [Fact]
    public void A_flattened_part_is_exported_and_left_flattened()
    {
        var doc = SavedChannel();
        doc.Suppress(doc.Tree.Single(f => f.Type == SheetMetalParts.FlatPatternType).Feature, false);
        var result = ExportFlat(new ExportFlatPatternRequest { Path = PathOf("flat.dxf"), Format = FlatPatternFormat.Dxf });
        Assert.Equal(SheetState.Flattened, result.State);
        Assert.Equal(SheetState.Flattened, SheetMetalParts.State(doc));
    }

    [Fact]
    public void A_part_that_is_not_active_is_activated_for_the_export_and_the_active_one_put_back()
    {
        var doc = SavedChannel();
        var other = Sw.Track(FakeDocument.Part("Part2"));
        Sw.CurrentDoc = other;
        var result = ExportFlat(new ExportFlatPatternRequest { Document = doc.Info().Path, Path = PathOf("inactive.dxf"), Format = FlatPatternFormat.Dxf });
        Assert.Equal(1, doc.Activations);
        Assert.Same(other, Sw.CurrentDoc);
        Assert.Contains(result.Checks, c => c.Contains("Part2 active again"));
    }

    [Fact]
    public void A_file_gone_after_the_export_fails_verification()
    {
        SavedChannel();
        var op = new ExportFlatPatternOperation(Services);
        IOperation o = op;
        var request = new ExportFlatPatternRequest { Path = PathOf("gone.dxf"), Format = FlatPatternFormat.Dxf };
        var result = (ExportFlatPatternResult)o.Run(request, new TestContext());
        File.Delete(result.Path);
        var e = Assert.Throws<SwAgentException>(() => o.Verify(request, result, new TestContext()));
        Assert.Equal("file_written", e.Error.Details!["check"]);
    }

    /// <summary>Every code export.flat_pattern lists in the plan's code table (design 454, Error handling), one case each.</summary>
    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    public void Export_flat_pattern_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", PathOf("a.SLDASM"), DocumentType.Assembly));
            Assert.Equal(code, Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("a.dxf"), Format = FlatPatternFormat.Dxf })).Code);
            return;
        }
        var doc = SavedChannel();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                doc.Document.Path = null;
                break;
            case ErrorCode.OperationFailed:
                doc.FlatExportFails.Add(FlatPatternExports.FlatPatternView);
                doc.FlatExportFails.Add(FlatPatternExports.ToDwg2);
                break;
            case ErrorCode.VerificationFailed:
                doc.FlatExportOffMm = 1;
                break;
        }
        Assert.Equal(code, Fails(() => ExportFlat(new ExportFlatPatternRequest { Path = PathOf("codes.dxf"), Format = FlatPatternFormat.Dxf })).Code);
    }
}
