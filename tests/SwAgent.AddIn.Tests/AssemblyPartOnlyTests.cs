using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Plan 4.B: the operations that stay part-only say what to use on an assembly instead (design 455, Error handling).</summary>
public class AssemblyPartOnlyTests : AssemblyTestBase
{
    [Fact]
    public void Model_tree_on_an_assembly_points_at_assembly_components()
    {
        var e = Fails(() => Run(new ModelTreeOperation(Services), new ModelTreeRequest()));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Contains("assembly.components", (string)e.Error.Details!["instead"]!);
        Assert.Contains("assembly.components", e.Message);
    }

    [Fact]
    public void A_feature_on_an_assembly_points_at_the_components_part()
    {
        var e = Fails(() => Run(new FeatureSuppressOperation(Services), new FeatureSuppressRequest { Features = { "front" }, Suppressed = true }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Contains("document.open", (string)e.Error.Details!["instead"]!);
    }

    [Fact]
    public void Rebuild_status_reads_an_assemblys_problems()
    {
        Doc.Problems.Add(new RebuildItem { Feature = "Coincident1", Code = "swFeatureErrorMateEntityNotLinear" });
        var status = Run(new ModelRebuildStatusOperation(Services), new ModelRebuildStatusRequest());
        Assert.Equal(1, status.Errors);
        Assert.Equal("Coincident1", status.Items[0].Feature);
    }
}
