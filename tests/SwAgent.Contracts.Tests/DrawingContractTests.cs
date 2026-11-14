using System;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class DrawingContractTests
{
    [Fact]
    public void Protocol_13_speaks_drawings()
    {
        Assert.Equal(13, Protocol.Version);
    }

    [Theory]
    [InlineData(ReferenceKind.Sheet, "r1.sheet.", "Sheet1")]
    [InlineData(ReferenceKind.View, "r1.view.", "Drawing View1")]
    [InlineData(ReferenceKind.Annotation, "r1.annotation.", "Note1")]
    [InlineData(ReferenceKind.Table, "r1.table.", "Bill of Materials1")]
    public void Drawing_kinds_carry_a_name_as_their_persistent_id(ReferenceKind kind, string prefix, string name)
    {
        string text = References.Encode(new ReferenceData(kind, @"C:\d\block.SLDDRW", Encoding.UTF8.GetBytes(name), $"{WireNames.ForEnum(kind)} {name}"));
        Assert.StartsWith(prefix, text);
        var back = References.Decode(text, "view");
        Assert.Equal(kind, back.Kind);
        Assert.Equal(name, Encoding.UTF8.GetString(back.PersistId));
    }

    [Fact]
    public void A_sheet_point_is_x_and_y_in_sheet_mm()
    {
        string json = JsonCodec.Serialize(new SheetPoint(210, 148.5));
        Assert.Equal("{\"x\":210,\"y\":148.5}", json);
        Assert.Equal(148.5, JsonCodec.Deserialize<SheetPoint>(json)!.Y);
        Assert.Contains("lower-left corner", typeof(SheetPoint).GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    [Fact]
    public void Wire_names_are_the_designs()
    {
        Assert.Equal("first_angle", WireNames.ForEnum(Projection.FirstAngle));
        Assert.Equal("third_angle", WireNames.ForEnum(Projection.ThirdAngle));
        Assert.Equal(new[] { "a4", "a3", "a2", "a1", "a0", "a", "b", "c", "d", "e" }, WireNames.EnumNames(typeof(SheetSize)));
        Assert.Equal(new[] { "model", "projected", "section", "detail", "flat_pattern", "other" }, WireNames.EnumNames(typeof(ViewType)));
        Assert.Equal(new[] { "hidden_lines_visible", "hidden_lines_removed", "wireframe", "shaded", "shaded_with_edges" }, WireNames.EnumNames(typeof(ViewDisplay)));
        Assert.Equal(new[] { "left", "right", "above", "below" }, WireNames.EnumNames(typeof(ProjectDirection)));
    }

    [Fact]
    public void A_view_entity_names_its_view_and_one_entity()
    {
        var entity = new ViewEntity { View = "Drawing View1", Component = "pin-1", Entity = "Axis1" };
        string json = JsonCodec.Serialize(entity);
        Assert.Equal("{\"view\":\"Drawing View1\",\"component\":\"pin-1\",\"entity\":\"Axis1\"}", json);
        Assert.Throws<SwAgentException>(() => JsonCodec.Deserialize<ViewEntity>("{\"entity\":\"Axis1\"}"));
    }

    [Fact]
    public void A_sheet_with_its_views_round_trips()
    {
        var sheet = new SheetInfo
        {
            Name = "Sheet1", Size = SheetSize.A3, WidthMm = 420, HeightMm = 297, Scale = new ScaleRatio(1, 2), Projection = Projection.ThirdAngle, Active = true,
        };
        sheet.Views.Add(new ViewInfo
        {
            Name = "Drawing View1", Sheet = "Sheet1", Type = ViewType.Model, Model = @"C:\d\block.SLDPRT", Orientation = "front",
            Position = new SheetPoint(100, 150), Scale = new ScaleRatio(1, 2), SheetScale = true, Display = ViewDisplay.HiddenLinesVisible,
            Extents = new SheetBox { Min = new SheetPoint(85, 140), Max = new SheetPoint(115, 160) },
        });
        string json = JsonCodec.Serialize(sheet);
        Assert.Contains("\"size\":\"a3\"", json);
        Assert.Contains("\"scale\":{\"numerator\":1,\"denominator\":2}", json);
        Assert.Contains("\"display\":\"hidden_lines_visible\"", json);
        Assert.Contains("\"sheet_scale\":true", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<SheetInfo>(json)));
    }

    [Fact]
    public void Drawing_change_evidence_lists_the_models_before_and_after()
    {
        var change = new DrawingChange { SheetsBefore = 1, SheetsAfter = 1, ViewsBefore = 0, ViewsAfter = 1 };
        change.Created.Add(new CreatedItem { Name = "Drawing View1", Kind = ReferenceKind.View });
        change.Models.Add(new DrawingModelState { Path = @"C:\d\block.SLDPRT" });
        string json = JsonCodec.Serialize(change);
        Assert.Contains("\"kind\":\"view\"", json);
        Assert.Contains("\"dirty_before\":false", json);
        Assert.Contains("\"rebuild_errors_after\":0", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<DrawingChange>(json)));
    }

    [Theory]
    [InlineData("drawing", 60)]
    [InlineData("sheet", 30)]
    [InlineData("view", 60)]
    [InlineData("annotation", 60)]
    [InlineData("table", 60)]
    public void Drawing_families_have_their_designed_deadlines(string family, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), OperationFamilies.DefaultDeadline(family));
    }
}
