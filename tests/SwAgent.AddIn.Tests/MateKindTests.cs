using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class MateKindTests
{
    [Theory]
    [InlineData("MateCoincident", ReferenceKind.Mate)]
    [InlineData("MateDistanceDim", ReferenceKind.Mate)]
    [InlineData("MatePlanarAngleDim", ReferenceKind.Mate)]
    [InlineData("MateLock", ReferenceKind.Mate)]
    [InlineData("MateGroup", ReferenceKind.Feature)]
    [InlineData("RefPlane", ReferenceKind.Plane)]
    public void Mate_features_are_named_as_mates(string typeName, ReferenceKind kind) =>
        Assert.Equal(kind, SolidWorksDocument.FeatureKind(typeName));

    [Theory]
    [InlineData(MateType.Coincident, 0)]
    [InlineData(MateType.Concentric, 1)]
    [InlineData(MateType.Distance, 5)]
    [InlineData(MateType.Angle, 6)]
    [InlineData(MateType.Lock, 16)]
    public void Mate_types_map_both_ways(MateType type, int sw)
    {
        Assert.Equal(sw, SolidWorksCodes.MateType(type));
        Assert.Equal(type, SolidWorksCodes.MateTypeOf(sw));
    }

    [Fact]
    public void Other_mates_have_no_standard_type_but_keep_their_name()
    {
        Assert.Null(SolidWorksCodes.MateTypeOf(10));
        Assert.Equal("swMateGEAR", SolidWorksCodes.MateTypeName(10));
    }

    [Fact]
    public void Add_mate_errors_and_alignments_are_named()
    {
        Assert.Equal("swAddMateError_OverDefinedAssembly", SolidWorksCodes.AddMateError(5));
        Assert.Equal(1, SolidWorksCodes.Align(MateAlignment.AntiAligned));
        Assert.Equal(MateAlignment.Closest, SolidWorksCodes.AlignmentOf(2));
    }
}
