using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SwAgent.Live.Tests;

/// <summary>
/// Whether this run may touch a real SOLIDWORKS. Every live case is skipped unless SWMCP_LIVE=1, so CI and a plain
/// dotnet test never reach SOLIDWORKS. The canary also needs SWMCP_CANARY=1: it is never part of the default run.
/// </summary>
public static class LiveSettings
{
    public const string LiveVariable = "SWMCP_LIVE";
    public const string CanaryVariable = "SWMCP_CANARY";

    public static bool LiveEnabled(Func<string, string?> environment) => environment(LiveVariable) == "1";

    public static bool CanaryEnabled(Func<string, string?> environment) => LiveEnabled(environment) && environment(CanaryVariable) == "1";

    public static bool Live => LiveEnabled(Environment.GetEnvironmentVariable);

    public static bool Canary => CanaryEnabled(Environment.GetEnvironmentVariable);

    /// <summary>Skips the calling case unless the live suite is on.</summary>
    public static void RequireLive() =>
        Skip.IfNot(Live, $"live fault suite: set {LiveVariable}=1 on seiraiyu2 to run it against SOLIDWORKS");

    /// <summary>Skips the calling case unless the canary was asked for.</summary>
    public static void RequireCanary() =>
        Skip.IfNot(Canary, $"canary: crashes SOLIDWORKS on purpose; run only on demand with {LiveVariable}=1 and {CanaryVariable}=1");
}

/// <summary>The order a live case runs in within its class: cases share one SOLIDWORKS, so each starts where the last left it.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StepAttribute : Attribute
{
    public StepAttribute(int order)
    {
        Order = order;
    }

    public int Order { get; }
}

/// <summary>Runs a class's cases by their [Step], lowest first.</summary>
public sealed class StepOrderer : ITestCaseOrderer
{
    public const string TypeName = "SwAgent.Live.Tests.StepOrderer";
    public const string AssemblyName = "SwAgent.Live.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(t => Order(t)).ThenBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal).ToList();

    static int Order(ITestCase testCase)
    {
        var step = testCase.TestMethod.Method.GetCustomAttributes(typeof(StepAttribute).AssemblyQualifiedName).FirstOrDefault();
        return step == null ? int.MaxValue : step.GetConstructorArguments().Cast<int>().First();
    }
}
