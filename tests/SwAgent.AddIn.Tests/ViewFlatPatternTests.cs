using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Flat-pattern views of phase 6's tab: 100 mm wide with a 25 mm flange, one 90° bend at R 3, 2 mm, K 0.5, so flat 100 × 83.2832.</summary>
public class ViewFlatPatternTests : DrawingTestBase
{
    /// <summary>The tab, saved at tab.SLDPRT; open unless told otherwise, when SOLIDWORKS opens it on request.</summary>
    internal FakeDocument Tab(bool open = true)
    {
        var tab = FakeDocument.Part("tab.SLDPRT", PathOf("tab.SLDPRT"));
        File.WriteAllText(tab.Document.Path!, "part");
        tab.AddFeature("Sheet-Metal1", SheetMetalParts.SheetMetalType);
        tab.AddFeature("Flat-Pattern1", SheetMetalParts.FlatPatternType, suppressed: true);
        tab.Sheet = new SheetDefaults { ThicknessMm = 2, BendRadiusMm = 3, Allowance = new BendAllowance { KFactor = 0.5 } };
        tab.FlatStraightMm = 77;
        tab.FlatWidthMm = 100;
        tab.FakeBends.Add(new FakeBend { Feature = tab.AddFeature("EdgeBend1", "OneBend", parent: "Edge-Flange1"), Owner = "Edge-Flange1", AngleDeg = 90, RadiusMm = 3 });
        if (open) Sw.Track(tab);
        else Sw.OpenWith = p => new OpenOutcome(string.Equals(p, tab.Document.Path, StringComparison.OrdinalIgnoreCase) ? tab : null, 0, 0);
        return tab;
    }

    ViewFlatPatternResult Flat(string model, ScaleRatio? scale = null, bool? bendNotes = null, string? configuration = null) =>
        Run(new ViewFlatPatternOperation(Services), new ViewFlatPatternRequest { Model = model, At = new SheetPoint(200, 150), Scale = scale, BendNotes = bendNotes, Configuration = configuration });

    [Fact]
    public void The_tabs_flat_pattern_draws_its_flat_size_with_one_bend_line_and_bend_notes()
    {
        var tab = Tab();
        var r = Flat(tab.Document.Path!);
        Assert.Equal(ViewType.FlatPattern, r.View.Type);
        Assert.Equal(100, r.Flat.LengthMm, 4);
        Assert.Equal(83.2832, r.Flat.WidthMm, 4);
        Assert.Equal(100, r.View.Extents!.Max.X - r.View.Extents.Min.X, 4);
        Assert.Equal(1, r.BendLines);
        Assert.True(r.BendNotes);
        Assert.Equal(SheetState.Folded, r.State);
        Assert.False(r.Opened);
        Assert.False(r.MarkedDirty);
        Assert.Empty(r.ConfigurationsAdded);
    }

    [Fact]
    public void Its_own_scale_and_no_bend_notes_are_taken()
    {
        var tab = Tab();
        var r = Flat(tab.Document.Title, new ScaleRatio(1, 2), bendNotes: false);
        Assert.Equal(50, r.View.Extents!.Max.X - r.View.Extents.Min.X, 4);
        Assert.Equal(41.6416, r.View.Extents.Max.Y - r.View.Extents.Min.Y, 4);
        Assert.False(r.BendNotes);
        Assert.False(r.View.SheetScale);
    }

    [Fact]
    public void A_part_that_is_not_open_is_opened_and_the_drawing_made_active_again()
    {
        var tab = Tab(open: false);
        var r = Flat(tab.Document.Path!);
        Assert.True(r.Opened);
        Assert.Contains(r.Change.Changed, c => c.Property == "open" && c.Name == "tab.SLDPRT");
        Assert.Same(Doc, Sw.ActiveDoc());
    }

    [Fact]
    public void A_derived_configuration_that_marks_the_part_changed_is_reported_and_measured_unchanged()
    {
        var tab = Tab();
        Drw.FlatPatternChangesPart = true;
        var r = Flat(tab.Document.Path!);
        Assert.Equal(new[] { "DefaultSM-FLAT-PATTERN" }, r.ConfigurationsAdded);
        Assert.True(r.MarkedDirty);
        Assert.Contains(r.Change.Changed, c => c.Property == "configurations");
        Assert.True(r.Change.Models.Single(m => m.Path == tab.Document.Path).DirtyAfter);
    }

    [Fact]
    public void A_part_marked_changed_and_changed_is_refused_and_its_added_configuration_deleted()
    {
        var tab = Tab();
        Drw.FlatPatternChangesPart = true;
        Drw.AfterCreate = _ => tab.FakeBends.Add(new FakeBend { Feature = tab.AddFeature("EdgeBend2", "OneBend", parent: "Edge-Flange2"), Owner = "Edge-Flange2", AngleDeg = 90, RadiusMm = 3 });
        var e = Fails(() => Flat(tab.Document.Path!));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("document_unchanged", e.Error.Details!["check"]);
        Assert.DoesNotContain("DefaultSM-FLAT-PATTERN", tab.Configurations);
        Assert.Empty(Drw.ViewNames("Sheet1"));
    }

    [Fact]
    public void A_part_that_is_not_sheet_metal_is_the_wrong_document_type_and_a_part_opened_for_it_is_closed()
    {
        var plate = FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT"));
        File.WriteAllText(plate.Document.Path!, "part");
        Sw.OpenWith = _ => new OpenOutcome(plate, 0, 0);
        var e = Fails(() => Flat(plate.Document.Path!));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Equal("sheet_metal", e.Error.Details!["needs"]);
        Assert.Contains("plate.SLDPRT", Sw.ClosedTitles);
    }

    [Fact]
    public void An_assembly_or_a_never_saved_part_or_an_unknown_configuration_is_refused_naming_its_field()
    {
        var tab = Tab();
        string asm = PathOf("pair.SLDASM");
        File.WriteAllText(asm, "assembly");
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Flat(asm)).Code);
        Sw.Track(FakeDocument.Part("Part9"));
        Assert.Equal("/model", Fails(() => Flat("Part9")).Error.Details!["field"]);
        Assert.Equal("/configuration", Fails(() => Flat(tab.Document.Path!, configuration: "Long")).Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void A_view_with_a_bend_line_missing_is_rolled_back()
    {
        var tab = Tab();
        Drw.BendLinesDrawn = 0;
        var e = Fails(() => Flat(tab.Document.Path!));
        Assert.Equal("bend_lines", e.Error.Details!["check"]);
        Assert.Empty(Drw.ViewNames("Sheet1"));
    }

    [Fact]
    public void A_view_drawn_off_the_flat_size_is_rolled_back()
    {
        var tab = Tab();
        Drw.AfterCreate = v => v.Drawn = new List<double> { 1, 0, 0, 1, 2, 3, 4, 5, 2, -0.05, -0.04, 0, 0.05, 0.04, 0 };
        var e = Fails(() => Flat(tab.Document.Path!));
        Assert.Equal("extents", e.Error.Details!["check"]);
        Assert.Contains("100 × 80 mm", (string)e.Error.Details["measured"]!);
    }

    [Fact]
    public void A_part_left_flattened_is_rolled_back()
    {
        var tab = Tab();
        // SOLIDWORKS leaving Flat-Pattern1 unsuppressed: the part reads as flattened.
        Drw.AfterCreate = _ =>
        {
            int i = tab.Tree.FindIndex(f => f.Type == SheetMetalParts.FlatPatternType);
            var old = tab.Tree[i];
            tab.Tree[i] = new FeatureRecord(old.Feature, old.Name, old.Type, old.Parent, false, old.ErrorCode, old.Warning);
        };
        Assert.Equal("state", Fails(() => Flat(tab.Document.Path!)).Error.Details!["check"]);
    }

    [Fact]
    public void SOLIDWORKS_making_no_view_is_operation_failed_and_a_part_opened_for_it_is_closed()
    {
        var tab = Tab(open: false);
        Drw.CreateFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Flat(tab.Document.Path!)).Code);
        Assert.Contains("tab.SLDPRT", Sw.ClosedTitles);
    }

    [Fact]
    public void A_cut_list_with_no_flat_size_is_operation_failed_before_anything_is_drawn()
    {
        var tab = Tab();
        tab.CutListOverride = new List<KeyValuePair<string, string>>();
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Flat(tab.Document.Path!)).Code);
        Assert.Empty(Drw.Calls);
    }
}
