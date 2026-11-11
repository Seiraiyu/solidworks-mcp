using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Plan 4.B: the configuration family on an assembly.</summary>
public class AssemblyConfigurationTests : AssemblyInspectionBase
{
    [Fact]
    public void An_assemblys_configurations_are_listed()
    {
        Doc.Configurations.Add("Exploded");
        var list = Run(new ConfigurationListOperation(Services), new ConfigurationListRequest());
        Assert.Equal(new[] { "Default", "Exploded" }, list.Configurations);
        Assert.Equal(DocumentType.Assembly, list.Document.Type);
    }

    [Fact]
    public void A_configuration_is_added_to_an_assembly_and_activated_back()
    {
        var added = Run(new ConfigurationAddOperation(Services), new ConfigurationAddRequest { Name = "Long" });
        Assert.Equal("Long", added.Active);
        Assert.Equal(DocumentType.Assembly, added.Change.Document.Type);
        var back = Run(new ConfigurationActivateOperation(Services), new ConfigurationActivateRequest { Name = "Default" });
        Assert.Equal("Default", back.Active);
        Assert.Equal("Default", Doc.Active);
    }

    [Fact]
    public void An_in_context_edit_makes_a_configuration_change_wait()
    {
        Asm.EditingTitle = "post";
        var e = Fails(() => Run(new ConfigurationAddOperation(Services), new ConfigurationAddRequest { Name = "Long" }));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        Assert.Equal("post", e.Error.Details["component"]);
        Assert.Equal(new[] { "Default" }, Doc.Configurations);
    }

    [Fact]
    public void A_drawings_configurations_are_the_wrong_document_type()
    {
        Sw.Track(new FakeDocument("Sheet1", null, DocumentType.Drawing));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ConfigurationListOperation(Services), new ConfigurationListRequest { Document = "Sheet1" })).Code);
    }

    [Fact]
    public void Other_model_changes_still_refuse_an_assembly()
    {
        var e = Fails(() => Run(new FeatureSuppressOperation(Services), new FeatureSuppressRequest { Features = { "front" }, Suppressed = true }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
    }
}
