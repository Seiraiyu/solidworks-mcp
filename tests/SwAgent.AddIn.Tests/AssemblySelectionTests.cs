using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Plan 4.B: selection.get, diag.select and selection.changed in an assembly.</summary>
public class AssemblySelectionTests : AssemblyInspectionBase
{
    AddInServices Diagnostic()
    {
        var runtime = new AgentRuntime("t", 1, NullLog.Instance, diagnosticsEnabled: true);
        runtime.Selection = new SelectionReporter(runtime.Events, () => runtime.Ledger.Running != null, NullLog.Instance, a => a(), TimeSpan.FromHours(1));
        return new AddInServices(Sw, "t", runtime);
    }

    [Fact]
    public void A_selected_face_names_its_component_and_a_selected_component_is_one()
    {
        Doc.Selected.Add(new SelectedObject(2, PostLeft));
        Doc.Selected.Add(new SelectedObject(20, Asm.Named("base-1").Entity));
        var result = Run(new SelectionGetOperation(Services), new SelectionGetRequest());
        Assert.Equal(new[] { "face", "component" }, result.Items.Select(i => i.Type));
        Assert.Equal("post-1", result.Items[0].Entity!.Component);
        Assert.Equal("post-1", References.Decode(result.Items[0].Entity!.Reference!, "e").Component);
        Assert.Equal(ReferenceKind.Component, result.Items[1].Entity!.Kind);
        Assert.Equal("base-1", result.Items[1].Entity!.Name);
        Assert.StartsWith("r1.component.", result.Items[1].Entity!.Reference);
    }

    [Fact]
    public void Diag_select_takes_assembly_references_component_names_and_name_at_component()
    {
        var services = Diagnostic();
        string face = services.References.Describe(Doc, PostLeft).Reference!;
        var result = Run(new DiagSelectOperation(services), new DiagSelectRequest { Items = { face, "front@post-1", "base-1", "top" } });
        Assert.Equal(new[] { "face", "plane", "component", "plane" }, result.Items.Select(i => i.Type));
        Assert.Equal("post-1", result.Items[1].Entity!.Component);
        Assert.Null(result.Items[3].Entity!.Component);
        services.Runtime.Selection!.Flush();
        var raised = services.Runtime.Events.Since(null).Events.Single(e => e.Kind == "selection.changed");
        Assert.Equal("stand.SLDASM", raised.Data!["title"]);
    }

    [Fact]
    public void Diag_select_of_an_unknown_name_is_still_refused()
    {
        var e = Fails(() => Run(new DiagSelectOperation(Diagnostic()), new DiagSelectRequest { Items = { "nothing-9" } }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("items/0", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_drawings_selection_is_the_wrong_document_type()
    {
        Sw.Track(new FakeDocument("Sheet1", null, DocumentType.Drawing));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new SelectionGetOperation(Services), new SelectionGetRequest { Document = "Sheet1" })).Code);
    }
}
