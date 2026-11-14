using System.Collections.Generic;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AnnotationSymbolTests : AnnotationTestBase
{
    AnnotationResult Datum(string label, ViewEntity on, SheetPoint? at = null) =>
        Run(new AnnotationDatumOperation(Services), new AnnotationDatumRequest { Label = label, AttachTo = on, At = at });

    AnnotationResult Gtol(GtolCharacteristic characteristic, double tolerance, ViewEntity on, List<string>? datums = null, bool? diameter = null, MaterialCondition? condition = null) =>
        Run(new AnnotationGtolOperation(Services), new AnnotationGtolRequest { Characteristic = characteristic, ToleranceMm = tolerance, AttachTo = on, Datums = datums, Diameter = diameter, MaterialCondition = condition, At = new SheetPoint(150, 200) });

    [Fact]
    public void Datum_A_on_the_bottom_edge_reads_back_its_label_attached_to_the_edge()
    {
        var r = Datum("a", On(Bottom), new SheetPoint(100, 110));
        Assert.Equal("A", r.Annotation.Label);
        Assert.Equal(AnnotationKind.Datum, r.Annotation.Kind);
        Assert.Equal(On(Bottom).Reference, Assert.Single(r.Annotation.Attached).Reference);
        Assert.Equal(110, r.Annotation.Position!.Y, 9);
        Assert.Contains($"datum A {Front.Facts.Name}", Drw.Calls);
    }

    [Fact]
    public void Labels_that_are_not_datum_letters_or_already_taken_are_refused()
    {
        Assert.Equal("/label", Fails(() => Datum("I", On(Bottom))).Error.Details!["field"]);
        Datum("A", On(Bottom));
        Assert.Equal("/label", Fails(() => Datum("A", On(Left))).Error.Details!["field"]);
        Assert.Equal("/attach_to", Fails(() => Datum("B", On(Back))).Error.Details!["field"]);
    }

    [Fact]
    public void A_tag_that_keeps_SOLIDWORKS_label_is_refused_and_deleted()
    {
        Drw.DatumLabelIgnored = true;
        Assert.Equal("label", Fails(() => Datum("A", On(Bottom))).Error.Details!["check"]);
        Assert.Empty(Drw.Annotated);
    }

    [Fact]
    public void A_position_tolerance_of_the_hole_reads_back_its_frame_to_datum_A()
    {
        Datum("A", On(Bottom));
        var r = Gtol(GtolCharacteristic.Position, 0.1, On(Hole), new List<string> { "a" }, diameter: true, condition: MaterialCondition.Mmc);
        var f = r.Annotation.Gtol!;
        Assert.Equal(GtolCharacteristic.Position, f.Characteristic);
        Assert.Equal(0.1, f.ToleranceMm);
        Assert.True(f.Diameter);
        Assert.Equal(MaterialCondition.Mmc, f.MaterialCondition);
        Assert.Equal(new[] { "A" }, f.Datums);
        Assert.Equal(On(Hole).Reference, Assert.Single(r.Annotation.Attached).Reference);
    }

    [Fact]
    public void Datums_not_on_the_drawing_a_form_tolerance_with_a_datum_and_no_tolerance_are_refused()
    {
        Assert.Equal("/datums/0", Fails(() => Gtol(GtolCharacteristic.Position, 0.1, On(Hole), new List<string> { "B" })).Error.Details!["field"]);
        Assert.Equal("/datums", Fails(() => Gtol(GtolCharacteristic.Flatness, 0.05, On(Bottom), new List<string> { "A" })).Error.Details!["field"]);
        Assert.Equal("/datums", Fails(() => Gtol(GtolCharacteristic.Position, 0.1, On(Hole), new List<string> { "A", "B", "C", "D" })).Error.Details!["field"]);
        Assert.Equal("/tolerance_mm", Fails(() => Gtol(GtolCharacteristic.Position, 0, On(Hole))).Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void A_flatness_frame_takes_no_datum_and_a_frame_that_lost_its_datums_is_refused()
    {
        Assert.Empty(Gtol(GtolCharacteristic.Flatness, 0.05, On(Bottom)).Annotation.Gtol!.Datums);
        Datum("A", On(Bottom));
        Drw.GtolDatumsIgnored = true;
        Assert.Equal("datums", Fails(() => Gtol(GtolCharacteristic.Perpendicularity, 0.05, On(Left), new List<string> { "A" })).Error.Details!["check"]);
        Assert.Equal(2, Drw.Annotated.Count);
    }
}
