using System.Collections.Generic;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>The orderer runs a class's cases by [Step], whatever their names: these sort the other way by name.</summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class StepOrdererTests
{
    static readonly List<int> Ran = new List<int>();

    [Fact, Step(1)]
    public void C_runs_first() => Ran.Add(1);

    [Fact, Step(2)]
    public void B_runs_second() => Ran.Add(2);

    [Fact, Step(3)]
    public void A_runs_last_and_sees_the_order() => Assert.Equal(new[] { 1, 2 }, Ran);
}
