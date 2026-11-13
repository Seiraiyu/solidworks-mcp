using System;
using System.Collections.Generic;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class ExplodeContractTests
{
    [Fact]
    public void An_explode_request_names_its_steps_in_mm_and_degrees()
    {
        var request = new ExplodeCreateRequest { Name = "Pin out", Configuration = "Default" };
        request.Steps.Add(new ExplodeStep { Components = { "pin-1" }, Direction = "z", DistanceMm = 40 });
        request.Steps.Add(new ExplodeStep
        {
            Components = { "bracket-1" }, Direction = "r1.face.abc", DistanceMm = 20, Reverse = true,
            RotateAbout = "Axis1@plate-1", AngleDeg = 90,
        });
        string json = JsonCodec.Serialize(request);
        Assert.Contains("\"distance_mm\":40", json);
        Assert.Contains("\"rotate_about\":\"Axis1@plate-1\"", json);
        Assert.Contains("\"angle_deg\":90", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<ExplodeCreateRequest>(json)));
    }

    [Fact]
    public void An_explode_result_says_where_each_component_went_and_which_way()
    {
        var result = new ExplodeCreateResult
        {
            View = new ExplodedViewInfo { Name = "ExplView1", Configuration = "Default", Steps = new List<ExplodeStepInfo> { new ExplodeStepInfo { Name = "Explode Step1", Components = { "pin-1" }, DistanceMm = 40 } } },
        };
        result.Steps.Add(new ExplodeStepOutcome
        {
            Index = 0, Name = "Explode Step1", Sense = "along",
            Components = { new ExplodedComponent { Name = "pin-1", Before = new Point3(20, 20, 0), Expected = new Point3(20, 20, 40), Actual = new Point3(20, 20, 40) } },
        });
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"sense\":\"along\"", json);
        Assert.Contains("\"offset_mm\":0", json);
        Assert.Contains("\"exploded\":false", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<ExplodeCreateResult>(json)));
    }

    [Fact]
    public void A_view_of_another_configuration_has_no_steps()
    {
        string json = JsonCodec.Serialize(new ExplodedViewInfo { Name = "ExplView1", Configuration = "Other" });
        Assert.DoesNotContain("\"steps\"", json);
    }

    [Fact]
    public void An_assembly_export_says_what_it_covered()
    {
        var result = new ExportAssemblyResult { Path = @"C:\x\stand.step", Format = ExportFormat.Step, Components = 3, StepOccurrences = 3, Header = "ISO-10303-21;" };
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"components\":3", json);
        Assert.Contains("\"step_occurrences\":3", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<ExportAssemblyResult>(json)));
    }

    [Theory]
    [InlineData("explode.create", "explode", false)]
    [InlineData("explode.list", "explode", true)]
    [InlineData("explode.show", "explode", false)]
    [InlineData("explode.delete", "explode", false)]
    [InlineData("export.assembly", "export", false)]
    public void The_operations_are_catalogued_in_their_families(string method, string family, bool readOnly)
    {
        var op = OperationCatalog.Find(method);
        Assert.NotNull(op);
        Assert.Equal(family, op!.Family);
        Assert.Equal(readOnly, op.ReadOnly);
        Assert.False(op.Internal);
    }

    [Fact]
    public void Explode_has_a_minute_and_export_two()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), OperationFamilies.DefaultFor("explode.create", TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(120), OperationFamilies.DefaultFor("export.assembly", TimeSpan.Zero));
    }

    [Fact]
    public void Protocol_speaks_exploded_views()
    {
        Assert.Equal(12, Protocol.Version);
    }
}
