using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class JsonSchemaTests
{
    public static IEnumerable<object[]> Sides() =>
        OperationCatalog.All.SelectMany(o => new[] { new object[] { o.Name, "request" }, new object[] { o.Name, "result" } });

    [Fact]
    public void Describes_kinds_requiredness_and_closed_objects()
    {
        string schema = Json.Write(JsonSchema.For(typeof(Inner)));
        Assert.Equal(
            "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"size\":{\"type\":\"number\"}},\"required\":[\"name\"],\"additionalProperties\":false}",
            schema);
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void Schema_matches_its_pinned_snapshot(string operation, string side)
    {
        var o = OperationCatalog.Find(operation)!;
        string actual = Json.Write(JsonSchema.For(side == "request" ? o.RequestType : o.ResultType), indented: true) + "\n";
        string name = $"{operation}.{side}.json";
        if (Environment.GetEnvironmentVariable("SWMCP_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(Path.Combine(SourceSchemaDirectory(), name), actual);
            return;
        }
        string file = Path.Combine(AppContext.BaseDirectory, "Schemas", name);
        Assert.True(File.Exists(file), $"no snapshot {name}; run the tests with SWMCP_UPDATE_SNAPSHOTS=1 and review the new file");
        Assert.Equal(File.ReadAllText(file).Replace("\r\n", "\n"), actual);
    }

    public static IEnumerable<object[]> EventKinds() => EventCatalog.All.Select(e => new object[] { e.Kind });

    [Theory]
    [MemberData(nameof(EventKinds))]
    public void Event_schema_matches_its_pinned_snapshot(string kind)
    {
        string actual = Json.Write(JsonSchema.For(EventCatalog.Find(kind)!.DataType), indented: true) + "\n";
        string name = $"event.{kind}.json";
        if (Environment.GetEnvironmentVariable("SWMCP_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(Path.Combine(SourceSchemaDirectory(), name), actual);
            return;
        }
        string file = Path.Combine(AppContext.BaseDirectory, "Schemas", name);
        Assert.True(File.Exists(file), $"no snapshot {name}; run the tests with SWMCP_UPDATE_SNAPSHOTS=1 and review the new file");
        Assert.Equal(File.ReadAllText(file).Replace("\r\n", "\n"), actual);
    }

    static string SourceSchemaDirectory([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source) ?? ".", "Schemas");
}
