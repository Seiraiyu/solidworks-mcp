using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>An empty assembly the agent made, and part files on disk to insert.</summary>
public abstract class AssemblyTestBase : DocumentTestBase
{
    internal readonly FakeDocument Doc;
    internal readonly FakeAssembly Asm;

    protected AssemblyTestBase()
    {
        Doc = Sw.Track(FakeDocument.NewAssembly("stand.SLDASM", PathOf("stand.SLDASM")));
        Asm = Doc.Asm!;
        foreach (var name in new[] { "base.SLDPRT", "pin.SLDPRT", "sub.SLDASM", "stand.SLDASM" }) File.WriteAllText(PathOf(name), "file");
    }
}

public class AssemblyComponentsTests : AssemblyTestBase
{
    AssemblyComponentsResult List(bool? recursive = null) => Run(new AssemblyComponentsOperation(Services), new AssemblyComponentsRequest { Recursive = recursive });

    [Fact]
    public void Lists_the_top_level_in_order_with_references_and_positions()
    {
        Asm.AddComponent("base-1", PathOf("base.SLDPRT"), fix: true);
        Asm.AddComponent("sub-1", PathOf("sub.SLDASM"), new Point3(0, 0, 50));
        Asm.AddComponent("sub-1/pin-1", PathOf("pin.SLDPRT"), parent: "sub-1");
        Asm.Named("base-1").Mates.Add("Coincident1");
        var result = List();
        Assert.Equal(new[] { "base-1", "sub-1" }, result.Components.Select(c => c.Name));
        Assert.All(result.Components, c => Assert.StartsWith("r1.component.", c.Reference));
        Assert.Equal(Constraint.Fixed, result.Components[0].Constraint);
        Assert.Equal(1, result.Components[0].Mates);
        Assert.Equal(50, result.Components[1].Transform.Origin.Z);
        Assert.Equal(SolveAs.Rigid, result.Components[1].SolveAs);
        Assert.Equal("stand.SLDASM", result.Document.Title);
    }

    [Fact]
    public void Recursive_lists_what_is_inside_subassemblies_after_them()
    {
        Asm.AddComponent("sub-1", PathOf("sub.SLDASM"));
        Asm.AddComponent("sub-1/pin-1", PathOf("pin.SLDPRT"), parent: "sub-1");
        var result = List(true);
        Assert.Equal(new[] { "sub-1", "sub-1/pin-1" }, result.Components.Select(c => c.Name));
        Assert.Equal("sub-1", result.Components[1].Parent);
    }

    [Fact]
    public void An_empty_assembly_lists_nothing()
    {
        Assert.Empty(List().Components);
    }

    [Fact]
    public void A_part_is_the_wrong_document_type()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        var e = Fails(() => Run(new AssemblyComponentsOperation(Services), new AssemblyComponentsRequest { Document = "plate.SLDPRT" }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
    }
}

public class AssemblyInsertTests : AssemblyTestBase
{
    AssemblyInsertResult Insert(AssemblyInsertRequest request) => Run(new AssemblyInsertOperation(Services), request);

    [Fact]
    public void Inserts_a_part_where_it_was_asked_to_go_and_returns_its_reference()
    {
        var result = Insert(new AssemblyInsertRequest
        {
            Path = PathOf("pin.SLDPRT"),
            At = new Point3(10, 20, 30),
            XAxis = new Vector3(0, 1, 0),
            ZAxis = new Vector3(0, 0, 1),
        });
        Assert.Equal("pin-1", result.Component.Name);
        Assert.StartsWith("r1.component.", result.Component.Reference);
        Assert.Equal(30, result.Component.Transform.Origin.Z);
        Assert.Equal(1, result.Component.Transform.XAxis.Y, 12);
        Assert.Equal("pin-1", result.Change.Created.Single().Name);
        Assert.Equal(ReferenceKind.Component, result.Change.Created.Single().Kind);
        Assert.Equal(0, result.Change.ComponentsBefore);
        Assert.Equal(1, result.Change.ComponentsAfter);
    }

    [Fact]
    public void The_first_component_is_fixed_and_the_next_floats()
    {
        Assert.True(Insert(new AssemblyInsertRequest { Path = PathOf("base.SLDPRT") }).Component.Fixed);
        var second = Insert(new AssemblyInsertRequest { Path = PathOf("pin.SLDPRT") });
        Assert.False(second.Component.Fixed);
        Assert.Equal(Constraint.UnderDefined, second.Change.Constraints.Single(c => c.Name == "pin-2").Constraint);
    }

    [Fact]
    public void Fixed_says_otherwise_either_way()
    {
        Assert.False(Insert(new AssemblyInsertRequest { Path = PathOf("base.SLDPRT"), Fixed = false }).Component.Fixed);
        Assert.True(Insert(new AssemblyInsertRequest { Path = PathOf("pin.SLDPRT"), Fixed = true }).Component.Fixed);
    }

    [Fact]
    public void A_subassembly_goes_in_rigid()
    {
        var result = Insert(new AssemblyInsertRequest { Path = PathOf("sub.SLDASM") });
        Assert.True(result.Component.Subassembly);
        Assert.Equal(SolveAs.Rigid, result.Component.SolveAs);
    }

    [Fact]
    public void A_configuration_is_used_and_one_the_file_lacks_is_rolled_back()
    {
        Asm.FileConfigurations.Add("Long");
        Assert.Equal("Long", Insert(new AssemblyInsertRequest { Path = PathOf("pin.SLDPRT"), Configuration = "Long" }).Component.Configuration);
        var e = Fails(() => Insert(new AssemblyInsertRequest { Path = PathOf("pin.SLDPRT"), Configuration = "Short" }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("configuration", e.Error.Details!["check"]);
        Assert.Equal(new object?[] { "pin-2" }, (List<object?>)e.Error.Details["rolled_back"]!);
        Assert.Single(Asm.Items);
    }

    [Fact]
    public void A_component_that_did_not_land_where_asked_is_removed_again()
    {
        Asm.InsertLandsAt = new Transform { Origin = new Point3(0, 0, 5) };
        var e = Fails(() => Insert(new AssemblyInsertRequest { Path = PathOf("pin.SLDPRT") }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("transform", e.Error.Details!["check"]);
        Assert.Empty(Asm.Items);
    }

    [Fact]
    public void SOLIDWORKS_making_nothing_is_operation_failed()
    {
        Asm.InsertFails = true;
        var e = Fails(() => Insert(new AssemblyInsertRequest { Path = PathOf("pin.SLDPRT") }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Empty(Asm.Items);
    }

    [Theory]
    [InlineData("nope.SLDPRT", "path")]
    [InlineData("pin.STEP", "path")]
    [InlineData("stand.SLDASM", "path")]
    public void A_bad_path_is_refused_before_anything_is_inserted(string name, string field)
    {
        File.WriteAllText(PathOf("pin.STEP"), "step");
        var e = Fails(() => Insert(new AssemblyInsertRequest { Path = PathOf(name) }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void Half_an_orientation_is_refused_before_anything_is_inserted()
    {
        var e = Fails(() => Insert(new AssemblyInsertRequest { Path = PathOf("pin.SLDPRT"), XAxis = new Vector3(1, 0, 0) }));
        Assert.Equal("z_axis", e.Error.Details!["field"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void A_part_document_is_the_wrong_type()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        var e = Fails(() => Insert(new AssemblyInsertRequest { Document = "plate.SLDPRT", Path = PathOf("pin.SLDPRT") }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
    }
}
