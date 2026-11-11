using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class SheetMetalBendContractTests
{
    /// <summary>The names in a request's schema that it requires.</summary>
    internal static List<string> Required(System.Type request) =>
        JsonSchema.For(request).TryGetValue("required", out var list) ? ((List<object?>)list!).Select(v => (string)v!).ToList() : new List<string>();

    /// <summary>An operation of the sheetmetal family that changes the part and is an MCP tool named after it.</summary>
    internal static void IsSheetMetalChange(string name, System.Type request, System.Type result)
    {
        var op = OperationCatalog.Find(name)!;
        Assert.Equal("sheetmetal", op.Family);
        Assert.Equal(request, op.RequestType);
        Assert.Equal(result, op.ResultType);
        Assert.False(op.ReadOnly);
        Assert.False(op.Internal);
        Assert.Equal("sw_" + name.Replace('.', '_'), op.ToolName);
    }

    [Fact]
    public void A_sketched_bend_needs_its_sketch_its_fixed_face_and_its_angle()
    {
        IsSheetMetalChange("sheetmetal.sketched_bend", typeof(SheetMetalSketchedBendRequest), typeof(SheetMetalFeatureResult));
        Assert.Equal(new[] { "sketch", "fixed_face", "angle_deg" }, Required(typeof(SheetMetalSketchedBendRequest)).ToArray());
    }

    [Fact]
    public void Flatten_takes_its_state_as_a_required_flag_and_says_when_nothing_changed()
    {
        IsSheetMetalChange("sheetmetal.flatten", typeof(SheetMetalFlattenRequest), typeof(SheetMetalFlattenResult));
        Assert.Equal(new[] { "flat" }, Required(typeof(SheetMetalFlattenRequest)).ToArray());
        var result = new SheetMetalFlattenResult { State = SheetState.Flattened, Unchanged = true, Flat = new FlatSize { LengthMm = 136.5664, WidthMm = 96.5664, Bends = 4 } };
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"state\":\"flattened\"", json);
        Assert.Contains("\"unchanged\":true", json);
        Assert.Equal(136.5664, JsonCodec.Deserialize<SheetMetalFlattenResult>(json).Flat!.LengthMm);
    }

    [Fact]
    public void Unfold_needs_a_fixed_face_and_fold_needs_nothing()
    {
        IsSheetMetalChange("sheetmetal.unfold", typeof(SheetMetalUnfoldRequest), typeof(SheetMetalFeatureResult));
        IsSheetMetalChange("sheetmetal.fold", typeof(SheetMetalFoldRequest), typeof(SheetMetalFeatureResult));
        Assert.Equal(new[] { "fixed_face" }, Required(typeof(SheetMetalUnfoldRequest)).ToArray());
        Assert.Empty(Required(typeof(SheetMetalFoldRequest)));
    }

    [Fact]
    public void Convert_needs_its_fixed_face_bend_edges_thickness_and_radius()
    {
        IsSheetMetalChange("sheetmetal.convert", typeof(SheetMetalConvertRequest), typeof(SheetMetalFeatureResult));
        Assert.Equal(new[] { "fixed_face", "bend_edges", "thickness_mm", "bend_radius_mm" }, Required(typeof(SheetMetalConvertRequest)).ToArray());
    }

    [Fact]
    public void Protocol_8_speaks_bends_and_flattening()
    {
        Assert.True(Protocol.Version >= 8, $"protocol {Protocol.Version} speaks sheet-metal bends from 8 on");
    }
}
