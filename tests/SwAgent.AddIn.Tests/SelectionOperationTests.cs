using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SelectionOperationTests : DocumentTestBase
{
    readonly FakeDocument _part;
    readonly FakeEntity _face;

    public SelectionOperationTests()
    {
        _part = Sw.Track(FakeDocument.Part("Part1"));
        _face = _part.Add(ReferenceKind.Face, new EntityInfo { Feature = "Boss-Extrude1", Geometry = GeometryType.Plane, AreaMm2 = 800 });
    }

    AddInServices Diagnostic(bool on = true)
    {
        var runtime = new AgentRuntime("t", 1, NullLog.Instance, diagnosticsEnabled: on);
        runtime.Selection = new SelectionReporter(runtime.Events, () => runtime.Ledger.Running != null, NullLog.Instance, a => a(), TimeSpan.FromHours(1));
        return new AddInServices(Sw, "t", runtime);
    }

    [Fact]
    public void Get_names_what_is_selected_with_references_and_what_it_cannot_name_by_type()
    {
        _part.Selected.Add(new SelectedObject(2, _face));
        _part.Selected.Add(new SelectedObject(4, _part.StandardPlane(0)));
        _part.Selected.Add(new SelectedObject(91, new object()));
        var result = Run(new SelectionGetOperation(Services), new SelectionGetRequest());
        Assert.Equal(3, result.Total);
        Assert.Equal(new[] { "face", "plane", "other" }, result.Items.Select(i => i.Type));
        Assert.StartsWith("r1.face.", result.Items[0].Entity!.Reference);
        Assert.Equal("Ebene vorne", result.Items[1].Entity!.Name);
        Assert.Null(result.Items[2].Entity);
    }

    [Fact]
    public void A_huge_selection_is_counted_but_only_the_first_thousand_described()
    {
        for (int i = 0; i < 1500; i++) _part.Selected.Add(new SelectedObject(2, _face));
        var result = Run(new SelectionGetOperation(Services), new SelectionGetRequest());
        Assert.Equal(1500, result.Total);
        Assert.Equal(SelectionReading.MaxRead, result.Items.Count);
        Assert.Equal(new[] { SelectionReading.MaxRead }, _part.SelectionReads);
    }

    [Fact]
    public void The_event_describes_at_most_a_hundred()
    {
        for (int i = 0; i < 150; i++) _part.Selected.Add(new SelectedObject(2, _face));
        var e = SelectionReading.Event(_part, Services.References);
        Assert.Equal(150, e.Total);
        Assert.Equal(SelectionReading.MaxEvent, e.Items.Count);
    }

    [Fact]
    public void Diag_select_selects_by_reference_and_name_and_raises_the_event()
    {
        var services = Diagnostic();
        string face = services.References.Describe(_part, _face).Reference!;
        var result = Run(new DiagSelectOperation(services), new DiagSelectRequest { Items = { face, "top" } });
        Assert.Equal(new[] { "face", "plane" }, result.Items.Select(i => i.Type));
        services.Runtime.Selection!.Flush();
        var raised = services.Runtime.Events.Since(null).Events.Single(e => e.Kind == "selection.changed");
        Assert.Equal("Part1", raised.Data!["title"]);
    }

    [Fact]
    public void Diag_select_replaces_the_selection_unless_append()
    {
        var services = Diagnostic();
        _part.Selected.Add(new SelectedObject(4, _part.StandardPlane(2)));
        Assert.Equal(1, Run(new DiagSelectOperation(services), new DiagSelectRequest { Items = { "front" } }).Total);
        Assert.Equal(2, Run(new DiagSelectOperation(services), new DiagSelectRequest { Items = { "top" }, Append = true }).Total);
    }

    [Fact]
    public void Diag_select_is_refused_while_diagnostics_are_off()
    {
        var e = Fails(() => Run(new DiagSelectOperation(Diagnostic(on: false)), new DiagSelectRequest { Items = { "front" } }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("Diagnostics", e.Message);
        Assert.Empty(_part.Selected);
    }

    [Fact]
    public void Diag_select_resolves_everything_before_selecting_anything()
    {
        var e = Fails(() => Run(new DiagSelectOperation(Diagnostic()), new DiagSelectRequest { Items = { "front", "Boss-Extrude9" } }));
        Assert.Equal("items/1", e.Error.Details!["field"]);
        Assert.Empty(_part.Selected);
    }

    [Theory]
    [InlineData(1, "edge")]
    [InlineData(22, "feature")]
    [InlineData(76, "solid_body")]
    [InlineData(999, "other")]
    public void Select_types_are_named(int type, string name)
    {
        Assert.Equal(name, SolidWorksCodes.SelectType(type));
    }
}
