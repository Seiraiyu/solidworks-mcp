using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SolidWorksAnnotationTests
{
    [Theory]
    [InlineData((int)swAnnotationType_e.swDisplayDimension, AnnotationKind.Dimension)]
    [InlineData((int)swAnnotationType_e.swNote, AnnotationKind.Note)]
    [InlineData((int)swAnnotationType_e.swCenterMarkSym, AnnotationKind.CentreMark)]
    [InlineData((int)swAnnotationType_e.swCenterLine, AnnotationKind.CentreLine)]
    [InlineData((int)swAnnotationType_e.swDatumTag, AnnotationKind.Datum)]
    [InlineData((int)swAnnotationType_e.swGTol, AnnotationKind.Gtol)]
    [InlineData((int)swAnnotationType_e.swWeldSymbol, AnnotationKind.Other)]
    public void Annotation_types_are_named_as_the_design_names_them(int swType, AnnotationKind kind)
    {
        Assert.Equal(kind, SolidWorksDrawing.KindOf(swType));
    }

    [Theory]
    [InlineData((int)swDimensionType_e.swLinearDimension, DimensionKind.Linear)]
    [InlineData((int)swDimensionType_e.swHorLinearDimension, DimensionKind.Horizontal)]
    [InlineData((int)swDimensionType_e.swVertLinearDimension, DimensionKind.Vertical)]
    [InlineData((int)swDimensionType_e.swRadialDimension, DimensionKind.Radius)]
    [InlineData((int)swDimensionType_e.swDiameterDimension, DimensionKind.Diameter)]
    [InlineData((int)swDimensionType_e.swAngularDimension, DimensionKind.Angle)]
    public void Dimension_types_are_named_as_the_design_names_them(int swType, DimensionKind kind)
    {
        Assert.Equal(kind, SolidWorksDrawing.DimensionKindOf(swType));
        Assert.Null(SolidWorksDrawing.DimensionKindOf((int)swDimensionType_e.swOrdinateDimension));
    }

    [Fact]
    public void Each_characteristic_has_its_SOLIDWORKS_symbol_both_ways()
    {
        foreach (GtolCharacteristic c in System.Enum.GetValues(typeof(GtolCharacteristic)))
            Assert.Equal(c, SolidWorksDrawing.CharacteristicOf(SolidWorksDrawing.Gcs(c)));
        Assert.Equal((int)swGtolGeomCharSymbol_e.swGcsPOSITION, SolidWorksDrawing.Gcs(GtolCharacteristic.Position));
        Assert.Null(SolidWorksDrawing.CharacteristicOf((int)swGtolGeomCharSymbol_e.swGcsNONE));
    }

    [Fact]
    public void Material_conditions_go_both_ways_and_rfs_reads_as_none()
    {
        foreach (MaterialCondition c in System.Enum.GetValues(typeof(MaterialCondition)))
            Assert.Equal(c, SolidWorksDrawing.ConditionOf(SolidWorksDrawing.Mc(c)));
        Assert.Equal(MaterialCondition.None, SolidWorksDrawing.ConditionOf((int)swGtolMatCondition_e.swMcRFS));
        Assert.Null(SolidWorksDrawing.ConditionOf((int)swGtolMatCondition_e.swMsPROJTOLZONE));
    }

    [Fact]
    public void Import_types_are_SOLIDWORKS_insert_flags_with_dimensions_marked_or_not()
    {
        int flags = SolidWorksDrawing.ImportTypes(new[] { AnnotationImportType.Dimensions, AnnotationImportType.Datums });
        Assert.Equal((int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing | (int)swInsertAnnotation_e.swInsertDimensionsNotMarkedForDrawing | (int)swInsertAnnotation_e.swInsertDatums, flags);
        Assert.Equal((int)swInsertAnnotation_e.swInsertholeCallout, SolidWorksDrawing.ImportTypes(new[] { AnnotationImportType.HoleCallouts }));
    }

    [Fact]
    public void A_frame_reads_from_SOLIDWORKS_values_and_symbols()
    {
        var frame = SolidWorksDrawing.FrameOf(new[] { "0.1", "", "A", "B", "" }, new short[] { (short)swGtolGeomCharSymbol_e.swGcsPOSITION, 1, (short)swGtolMatCondition_e.swMcMMC, 0, 0, 0, 0, 0 });
        Assert.Equal(GtolCharacteristic.Position, frame.Characteristic);
        Assert.Equal(0.1, frame.ToleranceMm);
        Assert.True(frame.Diameter);
        Assert.Equal(MaterialCondition.Mmc, frame.MaterialCondition);
        Assert.Equal(new[] { "A", "B" }, frame.Datums);
    }

    [Fact]
    public void A_frame_SOLIDWORKS_gives_nothing_for_reads_empty()
    {
        var frame = SolidWorksDrawing.FrameOf(null, new object[] { "x" });
        Assert.Null(frame.Characteristic);
        Assert.Null(frame.ToleranceMm);
        Assert.Empty(frame.Datums);
    }
}

public class FakeAnnotationTests
{
    readonly FakeDocument _drawing = FakeDocument.NewDrawing("block.SLDDRW", @"C:\d\block.SLDDRW");
    readonly FakeDocument _block = FakeDocument.Part("block.SLDPRT", @"C:\d\block.SLDPRT");
    readonly FakeView _front;
    readonly FakeEntity _left;
    readonly FakeEntity _right;
    readonly FakeEntity _hole;

    public FakeAnnotationTests()
    {
        _block.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 20) };
        _front = _drawing.Drw!.AddView("Sheet1", _block, "front", new SheetPoint(100, 150));
        _left = _block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        _right = _block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(60, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        _hole = _block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 20), Axis = new Vector3(0, 0, 1), RadiusMm = 5, LengthMm = 10 * System.Math.PI });
        foreach (var e in new[] { _left, _right, _hole }) _front.Visible.Add(new VisibleEntity(e, false, false));
    }

    FakeDrawing D => _drawing.Drw!;

    [Fact]
    public void A_reference_dimension_reads_what_the_model_measures_and_is_driven()
    {
        string name = D.AddDimension(_front.Facts.Name, DimensionKind.Horizontal, new object[] { _left, _right }, new SheetPoint(100, 190))!;
        var dim = Assert.Single(D.Annotations("Sheet1", _front.Facts.Name));
        Assert.Equal(name, dim.Name);
        Assert.Equal(60, dim.Value!.Value, 9);
        Assert.True(dim.Driven);
        Assert.Equal(new object[] { _left, _right }, dim.Attached);
        Assert.Equal(1, D.ReadView(_front.Facts.Name)!.Annotations);
    }

    [Fact]
    public void Nothing_is_made_of_an_entity_the_view_does_not_show()
    {
        var hidden = _block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 0), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        Assert.Null(D.AddCentreMark(_front.Facts.Name, hidden));
        Assert.False(D.Shows(_front.Facts.Name, hidden, null));
        Assert.True(D.Shows(_front.Facts.Name, _hole, null));
    }

    [Fact]
    public void A_centre_mark_sits_on_its_circles_drawn_centre()
    {
        D.AddCentreMark(_front.Facts.Name, _hole);
        var mark = Assert.Single(D.Annotations("Sheet1", _front.Facts.Name));
        Assert.Equal(AnnotationKind.CentreMark, mark.Kind);
        Assert.True(ViewGeometry.Near(new SheetPoint(90, 150), mark.Position!));
    }

    [Fact]
    public void A_note_shows_its_linked_property_and_goes_where_it_was_asked()
    {
        D.Props["Title"] = "Block";
        D.AddNote("Sheet1", null, "TITLE: $PRP:\"Title\"", new SheetPoint(300, 30), null);
        var note = Assert.Single(D.Annotations("Sheet1", null));
        Assert.Equal("TITLE: Block", note.Text);
        Assert.Equal("TITLE: $PRP:\"Title\"", note.LinkedText);
        D.AddNote("Sheet1", _front.Facts.Name, "THRU", new SheetPoint(120, 190), _hole);
        Assert.Equal(new object[] { _hole }, Assert.Single(D.Annotations("Sheet1", _front.Facts.Name)).Attached);
    }

    [Fact]
    public void Imports_bring_the_models_dimensions_into_the_views_planned_once()
    {
        var boss = _block.AddFeature("Boss-Extrude1", "Extrusion");
        _block.AddFeatureDimension(boss, "D1@Boss-Extrude1", 20, angle: false);
        D.ImportPlan.Add((_front.Facts.Name, AnnotationKind.Dimension, "D1@Boss-Extrude1"));
        D.ImportAnnotations(new[] { AnnotationImportType.Dimensions });
        D.ImportAnnotations(new[] { AnnotationImportType.Dimensions });
        var dim = Assert.Single(D.Annotations("Sheet1", _front.Facts.Name));
        Assert.Equal("D1@Boss-Extrude1@block.Part", dim.DimensionName);
        Assert.Equal(20, dim.Value);
        Assert.False(dim.Driven);
    }

    [Fact]
    public void Deleting_a_view_or_its_annotations_takes_them_away()
    {
        string mark = D.AddCentreMark(_front.Facts.Name, _hole)!;
        Assert.True(D.DeleteAnnotations("Sheet1", _front.Facts.Name, new[] { mark }));
        Assert.Empty(D.Annotations("Sheet1", _front.Facts.Name));
        D.AddCentreMark(_front.Facts.Name, _hole);
        D.DeleteViews(new[] { _front.Facts.Name });
        Assert.Empty(D.Annotated);
    }

    [Fact]
    public void Properties_are_written_read_resolved_and_deleted()
    {
        D.SetProperty("PartNo", "BLK-1");
        D.SetProperty("Description", "$PRP:\"PartNo\" block");
        Assert.Equal("BLK-1 block", D.Properties().Single(p => p.Name == "Description").Resolved);
        Assert.True(D.DeleteProperty("PartNo"));
        Assert.Equal(new[] { "Description" }, D.Properties().Select(p => p.Name));
    }
}
