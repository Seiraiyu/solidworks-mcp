using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class FeatureTreeTests
{
    static FeatureRecord F(string name, string type, string? parent = null) => new FeatureRecord(new object(), name, type, parent, false, 0, false);

    static (FeatureRecord, IReadOnlyList<FeatureRecord>) Top(string name, string type, params FeatureRecord[] subs) => (F(name, type), subs);

    /// <summary>The block's tree as SOLIDWORKS 2026 walks it (3.A live run, case 5).</summary>
    static List<(FeatureRecord, IReadOnlyList<FeatureRecord>)> Block() => new List<(FeatureRecord, IReadOnlyList<FeatureRecord>)>
    {
        Top("Comments", "CommentsFolder"),
        Top("Favorites", "FavoriteFolder"),
        Top("History", "HistoryFolder"),
        Top("Sensors", "SensorFolder"),
        Top("Annotations", "DetailCabinet"),
        Top("Notes1___EndTag___", "NotesEndTag"),
        Top("Solid Bodies", "SolidBodyFolder"),
        Top("Lights and Cameras", "EnvFolder"),
        Top("Equations", "EqnFolder"),
        Top("Material <not specified>", "MaterialFolder"),
        Top("Front Plane", "RefPlane"),
        Top("Top Plane", "RefPlane"),
        Top("Right Plane", "RefPlane"),
        Top("Origin", "OriginProfileFeature"),
        Top("Sketch1", "ProfileFeature"),
        Top("Boss-Extrude1", "Extrusion", F("Sketch1", "ProfileFeature", "Boss-Extrude1")),
    };

    [Fact]
    public void Starts_at_the_first_plane_and_lists_an_absorbed_sketch_once_under_its_feature()
    {
        var tree = FeatureTree.Arrange(Block());
        Assert.Equal(new[] { "Front Plane", "Top Plane", "Right Plane", "Origin", "Boss-Extrude1", "Sketch1" }, tree.Select(f => f.Name));
        Assert.Equal("Boss-Extrude1", tree.Last().Parent);
    }

    [Fact]
    public void A_sketch_no_feature_uses_stays_at_the_top_level()
    {
        var tops = Block();
        tops.Add(Top("Sketch2", "ProfileFeature"));
        var tree = FeatureTree.Arrange(tops);
        Assert.Equal("Sketch2", tree.Last().Name);
        Assert.Null(tree.Last().Parent);
    }

    [Fact]
    public void A_tree_without_planes_is_kept_whole()
    {
        var tree = FeatureTree.Arrange(new List<(FeatureRecord, IReadOnlyList<FeatureRecord>)> { Top("Origin", "OriginProfileFeature"), Top("Sketch1", "ProfileFeature") });
        Assert.Equal(new[] { "Origin", "Sketch1" }, tree.Select(f => f.Name));
    }
}
