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

public class ComponentPlaceTests : AssemblyTestBase
{
    ComponentPlaceResult Place(ComponentPlaceRequest request) => Run(new ComponentPlaceOperation(Services), request);

    public ComponentPlaceTests()
    {
        Asm.AddComponent("base-1", PathOf("base.SLDPRT"), fix: true);
        Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"));
    }

    [Fact]
    public void Moves_a_floating_component_keeping_its_axes_and_records_the_move()
    {
        Asm.Named("pin-1").Info.Transform.XAxis = new Vector3(0, 1, 0);
        var result = Place(new ComponentPlaceRequest { Component = "pin-1", Origin = new Point3(5, 6, 7) });
        Assert.Equal(7, result.Component.Transform.Origin.Z);
        Assert.Equal(1, result.Component.Transform.XAxis.Y);
        Assert.False(result.Solved);
        var changed = result.Change.Changed.Single();
        Assert.Equal(("pin-1", "position", "(0, 0, 0) mm", "(5, 6, 7) mm"), (changed.Name, changed.Property, changed.Before, changed.After));
        Assert.Equal("pin-1", result.Change.Moved.Single().Name);
    }

    [Fact]
    public void New_axes_turn_it()
    {
        var result = Place(new ComponentPlaceRequest { Component = "pin-1", Origin = new Point3(), XAxis = new Vector3(0, 0, 1), ZAxis = new Vector3(1, 0, 0) });
        Assert.Equal(1, result.Component.Transform.XAxis.Z, 12);
        Assert.True(result.Change.Moved.Single().Rotated);
    }

    [Fact]
    public void A_mated_component_is_solved_and_says_where_the_solve_left_it()
    {
        Asm.Named("pin-1").Mates.Add("Concentric1");
        Asm.OnSolve = a => a.Named("pin-1").Info.Transform.Origin.X = 0;
        var result = Place(new ComponentPlaceRequest { Component = "pin-1", Origin = new Point3(5, 0, 7) });
        Assert.True(result.Solved);
        Assert.Equal(0, result.Component.Transform.Origin.X);
        Assert.Equal(7, result.Component.Transform.Origin.Z);
        Assert.Equal(1, Asm.Rebuilds);
    }

    [Fact]
    public void An_unmated_component_that_did_not_move_fails_and_stays_put()
    {
        Asm.PlacementIgnored = true;
        var e = Fails(() => Place(new ComponentPlaceRequest { Component = "pin-1", Origin = new Point3(5, 0, 0) }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("origin", e.Error.Details!["check"]);
    }

    [Fact]
    public void A_fixed_component_is_refused()
    {
        var e = Fails(() => Place(new ComponentPlaceRequest { Component = "base-1", Origin = new Point3(1, 0, 0) }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("component.set", e.Message);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void A_fully_defined_component_is_refused_naming_its_mates()
    {
        var pin = Asm.Named("pin-1");
        pin.Mates.AddRange(new[] { "Concentric1", "Coincident1" });
        pin.Info.Constraint = Constraint.FullyDefined;
        var e = Fails(() => Place(new ComponentPlaceRequest { Component = "pin-1", Origin = new Point3(1, 0, 0) }));
        Assert.Equal(new object?[] { "Concentric1", "Coincident1" }, (List<object?>)e.Error.Details!["mates"]!);
    }

    [Fact]
    public void A_component_carried_by_a_rigid_subassembly_is_refused_but_a_flexible_one_moves()
    {
        Asm.AddComponent("sub-1", PathOf("sub.SLDASM"));
        Asm.AddComponent("sub-1/bolt-1", PathOf("pin.SLDPRT"), parent: "sub-1");
        var e = Fails(() => Place(new ComponentPlaceRequest { Component = "sub-1/bolt-1", Origin = new Point3(1, 0, 0) }));
        Assert.Equal("sub-1", e.Error.Details!["subassembly"]);
        Asm.Named("sub-1").Info.SolveAs = SolveAs.Flexible;
        Assert.Equal(1, Place(new ComponentPlaceRequest { Component = "sub-1/bolt-1", Origin = new Point3(1, 0, 0) }).Component.Transform.Origin.X);
    }
}

public class ComponentSetTests : AssemblyTestBase
{
    ComponentSetResult Set(ComponentSetRequest request) => Run(new ComponentSetOperation(Services), request);

    public ComponentSetTests()
    {
        Asm.AddComponent("base-1", PathOf("base.SLDPRT"), fix: true);
        Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"));
        Asm.AddComponent("sub-1", PathOf("sub.SLDASM"));
    }

    [Fact]
    public void Sets_each_property_asked_on_each_component_and_records_what_was_different()
    {
        var result = Set(new ComponentSetRequest { Components = { "pin-1", "base-1" }, Fixed = true, Hidden = true });
        Assert.All(result.Components, c => Assert.True(c.Fixed && c.Hidden));
        Assert.Equal(new[] { "pin-1 fixed", "pin-1 hidden", "base-1 hidden" }, result.Change.Changed.Select(c => $"{c.Name} {c.Property}"));
        Assert.Equal(new[] { "fix pin-1", "hide pin-1", "hide base-1" }, Asm.Calls);
    }

    [Fact]
    public void A_subassembly_solves_flexible()
    {
        var result = Set(new ComponentSetRequest { Components = { "sub-1" }, SolveAs = SolveAs.Flexible });
        Assert.Equal(SolveAs.Flexible, result.Components.Single().SolveAs);
        var changed = result.Change.Changed.Single();
        Assert.Equal(("solve_as", "rigid", "flexible"), (changed.Property, changed.Before, changed.After));
    }

    [Fact]
    public void Solve_as_on_a_part_is_refused_before_anything_changes()
    {
        var e = Fails(() => Set(new ComponentSetRequest { Components = { "sub-1", "pin-1" }, SolveAs = SolveAs.Flexible }));
        Assert.Equal("/components/1", e.Error.Details!["field"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void Suppress_and_resolve_round_trip()
    {
        Assert.Equal(ComponentState.Suppressed, Set(new ComponentSetRequest { Components = { "pin-1" }, Suppression = Suppression.Suppressed }).Components[0].State);
        Assert.Equal(ComponentState.Resolved, Set(new ComponentSetRequest { Components = { "pin-1" }, Suppression = Suppression.Resolved }).Components[0].State);
    }

    [Fact]
    public void A_refused_suppression_is_operation_failed_and_earlier_edits_go_back()
    {
        Asm.SuppressionRefused = true;
        var e = Fails(() => Set(new ComponentSetRequest { Components = { "pin-1" }, Hidden = true, Suppression = Suppression.Suppressed }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.False(Asm.Named("pin-1").Info.Hidden);
        Assert.Equal(new object?[] { "pin-1" }, (List<object?>)e.Error.Details!["rolled_back"]!);
    }

    [Fact]
    public void A_configuration_the_file_lacks_fails_the_check_and_other_edits_go_back()
    {
        Asm.Named("pin-1").Configurations.Add("Long");
        Assert.Equal("Long", Set(new ComponentSetRequest { Components = { "pin-1" }, Configuration = "Long" }).Components[0].Configuration);
        var e = Fails(() => Set(new ComponentSetRequest { Components = { "pin-1" }, Configuration = "Short", Fixed = true }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("configuration", e.Error.Details!["check"]);
        Assert.False(Asm.Named("pin-1").Info.Fixed);
        Assert.Equal("Long", Asm.Named("pin-1").Info.Configuration);
    }

    [Fact]
    public void Nothing_to_set_and_no_components_are_refused()
    {
        Assert.Equal("components", Fails(() => Set(new ComponentSetRequest { Components = { "pin-1" } })).Error.Details!["field"]);
        Assert.Equal("components", Fails(() => Set(new ComponentSetRequest { Fixed = true })).Error.Details!["field"]);
    }

    [Fact]
    public void A_component_named_twice_is_set_once()
    {
        var result = Set(new ComponentSetRequest { Components = { "pin-1", "PIN-1" }, Hidden = true });
        Assert.Single(result.Components);
        Assert.Single(Asm.Calls);
    }

    [Fact]
    public void A_property_already_as_asked_changes_nothing()
    {
        var result = Set(new ComponentSetRequest { Components = { "base-1" }, Fixed = true });
        Assert.Empty(result.Change.Changed);
        Assert.Empty(Asm.Calls);
    }
}

public class ComponentDeleteTests : AssemblyTestBase
{
    ComponentDeleteResult Delete(params string[] components) => Run(new ComponentDeleteOperation(Services), new ComponentDeleteRequest { Components = components.ToList() });

    public ComponentDeleteTests()
    {
        Asm.AddComponent("base-1", PathOf("base.SLDPRT"), fix: true);
        Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"));
        Asm.AddComponent("sub-1", PathOf("sub.SLDASM"));
        Asm.AddComponent("sub-1/bolt-1", PathOf("pin.SLDPRT"), parent: "sub-1");
        Asm.MateNames.AddRange(new[] { "Concentric1", "Coincident1" });
        Asm.Named("pin-1").Mates.AddRange(new[] { "Concentric1", "Coincident1" });
        Asm.Named("base-1").Mates.AddRange(new[] { "Concentric1", "Coincident1" });
    }

    [Fact]
    public void Deletes_components_and_the_mates_that_used_them()
    {
        var result = Delete("pin-1");
        Assert.Equal(new[] { "pin-1" }, result.Deleted);
        Assert.Equal(new[] { "Concentric1", "Coincident1" }, result.MatesDeleted);
        Assert.Equal(2, result.Change.MatesBefore);
        Assert.Equal(0, result.Change.MatesAfter);
        Assert.Equal(4, result.Change.ComponentsBefore);
        Assert.Equal(3, result.Change.ComponentsAfter);
        Assert.Null(Asm.ComponentByName("pin-1"));
    }

    [Fact]
    public void A_subassembly_goes_with_what_is_inside_it()
    {
        Assert.Equal(new[] { "sub-1" }, Delete("sub-1").Deleted);
        Assert.Equal(new[] { "base-1", "pin-1" }, Asm.Items.Select(i => i.Info.Name));
    }

    [Fact]
    public void A_component_inside_a_subassembly_is_refused()
    {
        var e = Fails(() => Delete("sub-1/bolt-1"));
        Assert.Equal("/components/0", e.Error.Details!["field"]);
        Assert.Equal("sub-1", e.Error.Details["subassembly"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void Nothing_deleted_fails_the_check_and_says_what_is_left()
    {
        Asm.DeleteIgnored = true;
        var e = Fails(() => Delete("pin-1"));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(new object?[] { "pin-1" }, (List<object?>)e.Error.Details!["left"]!);
    }

    [Fact]
    public void An_unknown_component_is_refused_before_anything_is_deleted()
    {
        Assert.Equal("/components/1", Fails(() => Delete("pin-1", "nut-1")).Error.Details!["field"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void An_empty_list_is_refused()
    {
        Assert.Equal("components", Fails(() => Delete()).Error.Details!["field"]);
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
