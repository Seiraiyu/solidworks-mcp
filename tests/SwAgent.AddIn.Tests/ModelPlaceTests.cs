using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ModelPlaceTests
{
    readonly ReferenceService _refs = new ReferenceService(new RenameMap());
    readonly FakeDocument _block = FakeDocument.Part("block.SLDPRT", @"C:\d\block.SLDPRT");
    readonly FakeEntity _hole;
    readonly FakeEntity _face;

    public ModelPlaceTests()
    {
        _hole = _block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 20), RadiusMm = 5, Feature = "Cut-Extrude1" });
        _face = _block.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Origin = new Point3(30, 20, 20), Normal = new Vector3(0, 0, 1) });
        _block.Add(ReferenceKind.Axis, new EntityInfo { Name = "Axis1", Geometry = GeometryType.Line, Origin = new Point3(20, 20, 0), Axis = new Vector3(0, 0, 1) });
    }

    ResolvedPlace Resolve(ModelPlace given) => ModelPlaces.Resolve(_refs, _block, given, "/cut/through");

    [Fact]
    public void A_point_is_taken_as_it_is_in_model_mm()
    {
        var place = Resolve(new ModelPlace { Point = new Point3(20, 20, 10) });
        Assert.Equal(10, place.Point.Z);
        Assert.Equal("the point (20, 20, 10)", place.Described);
    }

    [Fact]
    public void A_circular_edge_gives_its_centre_and_an_axis_by_name_its_point()
    {
        var hole = Resolve(new ModelPlace { Reference = _refs.Describe(_block, _hole).Reference });
        Assert.Equal(20, hole.Point.Z);
        Assert.Contains("circle edge at (20, 20, 20)", hole.Described);
        var axis = Resolve(new ModelPlace { Entity = "Axis1" });
        Assert.Equal(0, axis.Point.Z);
        Assert.StartsWith("Axis1 at", axis.Described);
    }

    [Theory]
    [InlineData("none", "/cut/through/point")]
    [InlineData("two", "/cut/through")]
    [InlineData("component without entity", "/cut/through/component")]
    [InlineData("a planar face", "/cut/through/reference")]
    [InlineData("a component in a part", "/cut/through/component")]
    [InlineData("not a number", "/cut/through/point")]
    public void Each_mistake_is_invalid_argument_naming_its_field(string what, string field)
    {
        var given = what switch
        {
            "none" => new ModelPlace(),
            "two" => new ModelPlace { Point = new Point3(1, 2, 3), Entity = "Axis1" },
            "component without entity" => new ModelPlace { Point = new Point3(1, 2, 3), Component = "pin-1" },
            "a planar face" => new ModelPlace { Reference = _refs.Describe(_block, _face).Reference },
            "a component in a part" => new ModelPlace { Component = "pin-1", Entity = "Axis1" },
            _ => new ModelPlace { Point = new Point3(double.NaN, 0, 0) },
        };
        var e = Assert.Throws<SwAgentException>(() => Resolve(given));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
    }
}
