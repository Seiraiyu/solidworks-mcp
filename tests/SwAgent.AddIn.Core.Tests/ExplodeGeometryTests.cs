using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ExplodeGeometryTests
{
    static Transform At(double x, double y, double z) => new Transform { Origin = new Point3(x, y, z) };

    static Dictionary<string, Transform> Places(params (string Name, Transform At)[] items) =>
        items.ToDictionary(i => i.Name, i => i.At, StringComparer.OrdinalIgnoreCase);

    static readonly Vector3 Z = new Vector3(0, 0, 1);

    [Fact]
    public void A_step_moves_its_components_either_way_along_the_direction()
    {
        var before = Places(("pin-1", At(20, 20, 0)), ("plate-1", At(0, 0, 0)));
        var readings = ExplodeGeometry.Step(before, new[] { "pin-1" }, new Vector3(0, 0, 2), 40, null, 0);
        Assert.Equal(new[] { "along", "against" }, readings.Select(r => r.Sense));
        Assert.Equal(40, readings[0].Places["pin-1"].Origin.Z, 9);
        Assert.Equal(-40, readings[1].Places["pin-1"].Origin.Z, 9);
        Assert.All(readings, r => Assert.Null(r.TurnSense));
        Assert.False(readings[0].Places.ContainsKey("plate-1"));
    }

    [Theory]
    [InlineData(40, "along")]
    [InlineData(-40, "against")]
    public void The_reading_the_components_fit_is_the_sense(double z, string sense)
    {
        var before = Places(("pin-1", At(20, 20, 0)), ("plate-1", At(0, 0, 0)));
        var after = Places(("pin-1", At(20, 20, z)), ("plate-1", At(0, 0, 0)));
        var (reading, moved) = ExplodeGeometry.Match(0, ExplodeGeometry.Step(before, new[] { "pin-1" }, Z, 40, null, 0), before, after);
        Assert.Equal(sense, reading.Sense);
        var pin = Assert.Single(moved);
        Assert.Equal("pin-1", pin.Name);
        Assert.Equal(0, pin.OffsetMm, 9);
        Assert.Equal(z, pin.Actual.Z, 9);
        Assert.Equal(0, pin.Before.Z, 9);
    }

    [Fact]
    public void A_turning_step_moves_first_then_turns_about_the_axis_where_it_is()
    {
        var before = Places(("arm-1", At(10, 0, 0)));
        var axis = (new Point3(0, 0, 0), Z);
        var readings = ExplodeGeometry.Step(before, new[] { "arm-1" }, Z, 20, axis, 90);
        Assert.Equal(4, readings.Count);
        var alongAlong = readings.Single(r => r.Sense == "along" && r.TurnSense == "along").Places["arm-1"];
        Assert.Equal(0, alongAlong.Origin.X, 9);
        Assert.Equal(10, alongAlong.Origin.Y, 9);
        Assert.Equal(20, alongAlong.Origin.Z, 9);
        Assert.Equal(1, alongAlong.XAxis.Y, 9);
        var after = Places(("arm-1", alongAlong));
        var (reading, moved) = ExplodeGeometry.Match(0, readings, before, after);
        Assert.Equal("along", reading.TurnSense);
        Assert.Equal(0, Assert.Single(moved).TurnDeg, 6);
    }

    [Fact]
    public void A_component_off_its_place_fails_naming_the_step()
    {
        var before = Places(("pin-1", At(20, 20, 0)));
        var after = Places(("pin-1", At(20, 20, 40.01)));
        var e = Assert.Throws<SwAgentException>(() => ExplodeGeometry.Match(1, ExplodeGeometry.Step(before, new[] { "pin-1" }, Z, 40, null, 0), before, after));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("explode_offset_mm", e.Error.Details!["check"]);
        Assert.Equal("/steps/1", e.Error.Details["field"]);
        Assert.Equal("pin-1", e.Error.Details["component"]);
        Assert.Contains("step 2", e.Message);
    }

    [Fact]
    public void A_component_the_step_does_not_name_must_not_move()
    {
        var before = Places(("pin-1", At(20, 20, 0)), ("bracket-1", At(10, 10, 10)));
        var after = Places(("pin-1", At(20, 20, 40)), ("bracket-1", At(10, 10, 12)));
        var e = Assert.Throws<SwAgentException>(() => ExplodeGeometry.Match(0, ExplodeGeometry.Step(before, new[] { "pin-1" }, Z, 40, null, 0), before, after));
        Assert.Equal("explode_unmoved_mm", e.Error.Details!["check"]);
        Assert.Equal("bracket-1", e.Error.Details["component"]);
    }

    [Fact]
    public void Collapsed_means_every_component_back_where_it_was()
    {
        var collapsed = Places(("pin-1", At(20, 20, 0)), ("bracket-1", At(10, 10, 10)));
        ExplodeGeometry.Back(collapsed, Places(("pin-1", At(20, 20, 0)), ("bracket-1", At(10, 10, 10.0005))));
        var e = Assert.Throws<SwAgentException>(() => ExplodeGeometry.Back(collapsed, Places(("pin-1", At(20, 20, 40)), ("bracket-1", At(10, 10, 10)))));
        Assert.Equal("explode_collapsed", e.Error.Details!["check"]);
        Assert.Equal("pin-1", e.Error.Details["component"]);
    }
}
