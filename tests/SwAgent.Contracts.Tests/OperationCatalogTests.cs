using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public sealed class EmptyResult { }

[Operation("test.one", typeof(EmptyResult))] public sealed class OneRequest { }
[Operation("test.one", typeof(EmptyResult))] public sealed class OneAgainRequest { }
[Operation("Bad Name", typeof(EmptyResult))] public sealed class BadNameRequest { }
[Operation("test.peek", typeof(EmptyResult), Thread = OperationThread.Pipe, Internal = true)] public sealed class PeekRequest { }

public class OperationCatalogTests
{
    [Fact]
    public void Catalog_holds_the_phase_3D_operations()
    {
        Assert.Equal(new[] { "addin.events", "addin.state", "diag.dialog", "diag.edit_sketch", "diag.editing", "diag.scratch_block", "diag.scratch_part", "diag.select", "diag.sleep", "dialog.respond", "dimension.set", "document.close", "document.new", "document.open", "document.rebuild", "document.save", "document.save_as", "feature.chamfer", "feature.circular_pattern", "feature.delete", "feature.draft", "feature.extrude", "feature.fillet", "feature.hole", "feature.linear_pattern", "feature.loft", "feature.mirror", "feature.revolve", "feature.rib", "feature.shell", "feature.suppress", "feature.sweep", "model.bounding_box", "model.dimensions", "model.find", "model.mass", "model.measure", "model.rebuild_status", "model.tree", "refgeom.axis", "refgeom.plane", "refgeom.point", "selection.get", "session.hello", "session.status", "sketch.create", "sketch.modify", "sketch.read" },
            OperationCatalog.All.Select(o => o.Name).ToArray());
        var status = OperationCatalog.Find("session.status")!;
        Assert.Equal("session", status.Family);
        Assert.Equal(typeof(SessionStatusRequest), status.RequestType);
        Assert.Equal(typeof(SessionStatusResult), status.ResultType);
        Assert.False(string.IsNullOrEmpty(status.Description));
        Assert.Null(OperationCatalog.Find("session.nope"));
    }

    [Fact]
    public void Threads_and_internal_flags_are_as_designed()
    {
        string Shape(string name)
        {
            var o = OperationCatalog.Find(name)!;
            return $"{o.Thread}/{(o.Internal ? "internal" : "tool")}";
        }
        Assert.Equal("Pipe/internal", Shape("addin.state"));
        Assert.Equal("Pipe/internal", Shape("addin.events"));
        Assert.Equal("Ui/internal", Shape("diag.sleep"));
        Assert.Equal("Pipe/tool", Shape("dialog.respond"));
        Assert.Equal("Ui/internal", Shape("diag.dialog"));
        Assert.Equal("Ui/internal", Shape("diag.scratch_part"));
        Assert.Equal("Ui/tool", Shape("sketch.create"));
        Assert.Equal("Ui/internal", Shape("diag.editing"));
        Assert.True(OperationCatalog.Find("sketch.read")!.ReadOnly);
        Assert.True(OperationCatalog.Find("model.dimensions")!.ReadOnly);
        Assert.False(OperationCatalog.Find("dimension.set")!.ReadOnly);
        Assert.Equal("Ui/internal", Shape("session.hello"));
        Assert.Equal("Ui/internal", Shape("session.status"));
        Assert.Equal("Ui/tool", Shape("document.new"));
        Assert.Equal("Ui/tool", Shape("document.open"));
        Assert.True(OperationCatalog.Find("model.rebuild_status")!.ReadOnly);
        Assert.True(OperationCatalog.Find("model.tree")!.ReadOnly);
        Assert.True(OperationCatalog.Find("model.mass")!.ReadOnly);
        Assert.True(OperationCatalog.Find("model.bounding_box")!.ReadOnly);
        Assert.True(OperationCatalog.Find("model.find")!.ReadOnly);
        Assert.True(OperationCatalog.Find("selection.get")!.ReadOnly);
        Assert.Equal("Ui/internal", Shape("diag.select"));
        Assert.Equal("Ui/internal", Shape("diag.scratch_block"));
        Assert.Equal("Ui/tool", Shape("feature.extrude"));
        Assert.False(OperationCatalog.Find("feature.extrude")!.ReadOnly);
        Assert.Equal("Ui/tool", Shape("feature.revolve"));
        Assert.Equal("Ui/tool", Shape("feature.fillet"));
        Assert.Equal("Ui/tool", Shape("feature.chamfer"));
        Assert.Equal("Ui/tool", Shape("feature.suppress"));
        Assert.Equal("Ui/tool", Shape("feature.delete"));
        Assert.True(OperationCatalog.Find("model.measure")!.ReadOnly);
        Assert.Equal("Ui/internal", Shape("diag.edit_sketch"));
        Assert.Equal("Ui/tool", Shape("refgeom.plane"));
        Assert.Equal("Ui/tool", Shape("refgeom.axis"));
        Assert.Equal("Ui/tool", Shape("refgeom.point"));
        Assert.Equal("Ui/tool", Shape("feature.sweep"));
        Assert.Equal("Ui/tool", Shape("feature.loft"));
        Assert.Equal("Ui/tool", Shape("feature.rib"));
        Assert.Equal("Ui/tool", Shape("feature.shell"));
        Assert.Equal("Ui/tool", Shape("feature.draft"));
        Assert.Equal("Ui/tool", Shape("feature.hole"));
        Assert.Equal("Ui/tool", Shape("feature.linear_pattern"));
        Assert.Equal("Ui/tool", Shape("feature.circular_pattern"));
        Assert.Equal("Ui/tool", Shape("feature.mirror"));
    }

    [Fact]
    public void Read_only_operations_are_marked_and_the_rest_are_not()
    {
        var readOnly = OperationCatalog.All.Where(o => o.ReadOnly).Select(o => o.Name).ToArray();
        Assert.Contains("session.status", readOnly);
        Assert.Contains("addin.state", readOnly);
        Assert.DoesNotContain("dialog.respond", readOnly);
        Assert.False(OperationCatalog.FromTypes(new[] { typeof(OneRequest) }).Single().ReadOnly);
    }

    [Theory]
    [InlineData("dialog.respond", "sw_dialog_respond")]
    [InlineData("document.save_as", "sw_document_save_as")]
    public void A_tool_name_is_sw_and_the_operation_with_underscores(string operation, string tool)
    {
        var descriptor = new OperationDescriptor(operation, operation.Split('.')[0], typeof(OneRequest), typeof(EmptyResult), "");
        Assert.Equal(tool, descriptor.ToolName);
    }

    [Fact]
    public void Thread_and_internal_default_to_ui_and_public()
    {
        var one = OperationCatalog.FromTypes(new[] { typeof(OneRequest) }).Single();
        Assert.Equal(OperationThread.Ui, one.Thread);
        Assert.False(one.Internal);
    }

    [Fact]
    public void Thread_and_internal_are_read_from_the_attribute()
    {
        var peek = OperationCatalog.FromTypes(new[] { typeof(PeekRequest) }).Single();
        Assert.Equal(OperationThread.Pipe, peek.Thread);
        Assert.True(peek.Internal);
    }

    [Fact]
    public void Two_request_types_cannot_claim_one_name()
    {
        var e = Assert.Throws<ArgumentException>(() => OperationCatalog.FromTypes(new[] { typeof(OneRequest), typeof(OneAgainRequest) }));
        Assert.Contains("test.one", e.Message);
    }

    [Fact]
    public void Names_must_be_family_dot_verb()
    {
        Assert.Throws<ArgumentException>(() => OperationCatalog.FromTypes(new[] { typeof(BadNameRequest) }));
    }

    [Fact]
    public void Pipe_name_carries_the_process_id()
    {
        Assert.Equal("solidworks-mcp-1234", Protocol.PipeName(1234));
        Assert.Equal(3, Protocol.Version);
    }

    [Fact]
    public void Status_result_round_trips()
    {
        var status = new SessionStatusResult
        {
            SwRevision = "34.4.0", ProcessId = 4321, AddinVersion = "0.1.0", ProtocolVersion = 1,
            ActiveDocument = new DocumentInfo { Title = "Part1", Type = DocumentType.Part, Dirty = true },
            OpenDocumentCount = 1, QueuedRequests = 0,
        };
        string json = JsonCodec.Serialize(status);
        Assert.Contains("\"active_document\":{\"title\":\"Part1\",\"type\":\"part\",\"dirty\":true}", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<SessionStatusResult>(json)));
    }

    public static IEnumerable<object[]> ContractTypes() =>
        OperationCatalog.All.SelectMany(o => new[] { new object[] { o.Name, "request" }, new object[] { o.Name, "result" } });

    [Theory]
    [MemberData(nameof(ContractTypes))]
    public void Every_contract_type_round_trips_with_every_property_set(string operation, string side)
    {
        var o = OperationCatalog.Find(operation)!;
        var type = side == "request" ? o.RequestType : o.ResultType;
        object filled = Samples.Fill(type);
        string once = JsonCodec.Serialize(filled);
        object back = JsonCodec.Deserialize(once, type)!;
        Assert.IsType(type, back);
        Assert.Equal(once, JsonCodec.Serialize(back));
        Assert.True(Samples.AllPropertiesSet(back), $"{type.Name} lost a property in the round trip: {once}");
    }
}

/// <summary>Builds a contract object with every property set to a non-default value, recursively.</summary>
static class Samples
{
    public static object Fill(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return "text é\n";
        if (t == typeof(bool)) return true;
        if (t == typeof(int)) return 7;
        if (t == typeof(long)) return 1L << 40;
        if (t == typeof(double)) return 1.5;
        if (t.IsEnum) return Enum.GetValues(t).Cast<object>().Last();
        if (t == typeof(JsonObject)) return new JsonObject { ["k"] = "v" };
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(t)!;
            list.Add(Fill(t.GetGenericArguments()[0]));
            return list;
        }
        object instance = Activator.CreateInstance(t)!;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) p.SetValue(instance, Fill(p.PropertyType));
        return instance;
    }

    public static bool AllPropertiesSet(object value)
    {
        foreach (var p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? v = p.GetValue(value);
            if (v == null) return false;
            var t = v.GetType();
            if (t.IsClass && t != typeof(string) && t != typeof(JsonObject) && !(v is IList) && !AllPropertiesSet(v)) return false;
        }
        return true;
    }
}
