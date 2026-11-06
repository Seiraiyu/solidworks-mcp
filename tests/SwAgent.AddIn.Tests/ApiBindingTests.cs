using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ApiBindingTests
{
    readonly FakeSolidWorks _sw = new FakeSolidWorks();
    readonly FakeBinder _binder = new FakeBinder();
    readonly FakeDocument _doc;
    static readonly IReadOnlyDictionary<string, object> NoRefs = new Dictionary<string, object>();

    public ApiBindingTests()
    {
        _doc = _sw.Track(FakeDocument.Part("block.SLDPRT", @"C:\scratch\block.SLDPRT"));
        _doc.MassProperties = new ModelMassResult { VolumeMm3 = 8000 };
    }

    FakeApiApp App() => new FakeApiApp(_sw);

    ApiCallOutcome Call(object target, string member, params object?[] args) =>
        ApiCaller.Call(_binder, target, member, args.ToList(), NoRefs);

    static SwAgentException Fails(Action action) => Assert.Throws<SwAgentException>(action);

    [Fact]
    public void A_dotted_path_walks_to_the_member_and_returns_its_value_and_out_arguments()
    {
        var outcome = Call(_doc.Api, "extension.getmassproperties2", 1L, 0L, false);
        Assert.Equal("Extension.GetMassProperties2", outcome.Member);
        Assert.Equal(new[] { "IModelDoc2", "IModelDocExtension" }, outcome.Interfaces);
        var values = Assert.IsType<double[]>(outcome.Value);
        Assert.Equal(8e-6, values[3], 15);
        var status = Assert.Single(outcome.Out);
        Assert.Equal("Status", status.Key);
        Assert.Equal(0, status.Value);
    }

    [Fact]
    public void A_property_or_a_parameterless_method_is_called_the_same_way()
    {
        Assert.Equal("34.4.0", Call(App(), "RevisionNumber").Value);
        Assert.Equal("block.SLDPRT", Call(App(), "ActiveDoc.GetTitle").Value);
        Assert.Equal(new[] { "ISldWorks", "IModelDoc2" }, Call(App(), "ActiveDoc.GetTitle").Interfaces);
    }

    [Theory]
    [InlineData("CloseDoc")]
    [InlineData("closedoc")]
    [InlineData("ExitApp")]
    [InlineData("RunMacro2")]
    [InlineData("CloseUserNotification")]
    [InlineData("ExitApp.RevisionNumber")]
    [InlineData("ActiveDoc.Extension.CloseDoc")]
    public void A_refused_member_on_SOLIDWORKS_is_never_called(string member)
    {
        var app = App();
        var e = Fails(() => Call(app, member, "x"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.NotNull(e.Error.Details!["refused"]);
        Assert.Empty(app.Called);
    }

    [Theory]
    [InlineData("ReloadOrReplace")]
    [InlineData("reloadorreplace")]
    [InlineData("Extension.ReloadOrReplace")]
    public void ReloadOrReplace_closes_the_document_and_is_never_called(string member)
    {
        var e = Fails(() => Call(_doc.Api, member, false, "", true));
        Assert.Equal("closes_document", e.Error.Details!["reason"]);
        Assert.Contains("only with document.close", e.Message);
        Assert.Empty(_doc.Api.Called);
        Assert.Equal(0, _doc.Api.Extension.Reloads);
        Fails(() => Call(App(), "ActiveDoc." + member, false, "", true));
        Assert.Empty(_doc.Api.Called);
    }

    [Fact]
    public void Null_is_a_placeholder_for_a_ref_or_out_value()
    {
        var outcome = Call(_doc.Api, "Extension.Counted", new object?[] { null });
        Assert.Equal(true, outcome.Value);
        Assert.Equal(3, Assert.Single(outcome.Out).Value);
        var mass = Call(_doc.Api, "Extension.GetMassProperties2", 1L, null, false);
        Assert.Equal(0, Assert.Single(mass.Out).Value);
    }

    [Theory]
    [InlineData("Close")]
    [InlineData("QUIT")]
    public void A_documents_Close_and_Quit_are_refused(string member)
    {
        var e = Fails(() => Call(_doc.Api, member));
        Assert.Equal("closes_document", e.Error.Details!["reason"]);
        Assert.Contains("document.close", e.Message);
        Assert.Empty(_doc.Api.Called);
        Fails(() => Call(App(), "ActiveDoc." + member));
        Assert.Empty(_doc.Api.Called);
    }

    [Fact]
    public void ClosestDistance_is_a_measurement_and_is_called()
    {
        var outcome = Call(_doc.Api, "closestdistance", null, null, null, null);
        Assert.Equal("ClosestDistance", outcome.Member);
        Assert.Equal(0.005, outcome.Value);
        Assert.Equal(new[] { "Point1", "Point2" }, outcome.Out.Select(o => o.Key));
        Assert.Equal(new[] { 0.0, 0.0, 0.015 }, (double[])outcome.Out[1].Value!);
    }

    [Fact]
    public void Arguments_are_converted_to_the_parameter_types()
    {
        Call(_doc.Api, "Extension.SelectByID2", "Front Plane", "PLANE", 0L, 0L, 0.001, false, 1L, null, 0L);
        var given = _doc.Api.Extension.Selections.Single();
        Assert.Equal(new object?[] { "Front Plane", "PLANE", 0.0, 0.0, 0.001, false, 1, null, 0 }, given);
        Assert.IsType<double>(given[2]);
        Assert.IsType<int>(given[6]);
    }

    [Fact]
    public void Arguments_that_do_not_fit_name_their_place_and_the_signature()
    {
        var count = Fails(() => Call(_doc.Api, "Extension.GetMassProperties2", 1L));
        Assert.Equal("args", count.Error.Details!["field"]);
        Assert.Contains("GetMassProperties2(int Accuracy, out int Status, bool UseSelected) takes 3 arguments", count.Message);
        var type = Fails(() => Call(_doc.Api, "Extension.GetMassProperties2", "one", 0L, false));
        Assert.Equal("/args/0", type.Error.Details!["field"]);
        Assert.Equal("/args/0", Fails(() => Call(_doc.Api, "Extension.GetMassProperties2", 1.5, 0L, false)).Error.Details!["field"]);
        Assert.Equal("/args/2", Fails(() => Call(_doc.Api, "Extension.GetMassProperties2", 1L, 0L, null)).Error.Details!["field"]);
        Assert.Equal("/args/0", Fails(() => Call(_doc.Api, "Extension.GetMassProperties2", 3_000_000_000L, 0L, false)).Error.Details!["field"]);
    }

    [Fact]
    public void Only_the_last_member_may_take_arguments_or_be_a_value()
    {
        Assert.Contains("can only be the last member", Fails(() => Call(_doc.Api, "FeatureByName.GetTitle")).Message);
        Assert.Contains("is a value", Fails(() => Call(_doc.Api, "Count.ToString")).Message);
        Assert.Contains("returned nothing", Fails(() => Call(_doc.Api, "Nothing.GetTitle")).Message);
    }

    [Fact]
    public void An_unknown_member_names_the_interface()
    {
        var e = Fails(() => Call(_doc.Api, "Extension.NoSuchThing"));
        Assert.Equal("member", e.Error.Details!["field"]);
        Assert.Equal("IModelDocExtension", e.Error.Details["interface"]);
    }

    [Fact]
    public void What_SOLIDWORKS_throws_is_api_error_with_its_hresult()
    {
        var e = Fails(() => Call(_doc.Api, "Extension.Fails"));
        Assert.Equal(ErrorCode.ApiError, e.Code);
        Assert.Equal("0x80004005", e.Error.Details!["hresult"]);
        Assert.Contains("SOLIDWORKS said no", e.Message);
    }

    [Fact]
    public void References_are_listed_before_the_call_and_passed_as_the_objects_they_resolved_to()
    {
        var face = new FakeEntity(ReferenceKind.Face, new EntityInfo());
        var args = new List<object?> { new JsonObject { ["ref"] = "r1.face.A" }, new List<object?> { new JsonObject { ["ref"] = "r1.face.A" }, new JsonObject { ["ref"] = "r1.edge.B" } } };
        Assert.Equal(new[] { ("r1.face.A", "/args/0"), ("r1.face.A", "/args/1/0"), ("r1.edge.B", "/args/1/1") }, ApiValues.References(args).ToArray());
        var resolved = new Dictionary<string, object> { ["r1.face.A"] = face, ["r1.edge.B"] = new object() };
        var outcome = ApiCaller.Call(_binder, _doc.Api, "Extension.TakesObjects", new List<object?> { args[1] }, resolved);
        Assert.Equal(2, outcome.Value);
        Assert.Equal(new[] { 2 }, _binder.WrappedArrays);
        Assert.Same(face, ((object[])_doc.Api.Extension.ObjectArrays.Single())[0]);
        var e = Fails(() => ApiValues.References(new List<object?> { new JsonObject { ["name"] = "Boss-Extrude1" } }));
        Assert.Equal("/args/0", e.Error.Details!["field"]);
    }

    [Fact]
    public void Arrays_of_numbers_and_strings_become_typed_arrays()
    {
        Assert.Equal(3.5, Call(_doc.Api, "Extension.Sum", new List<object?> { 1L, 2.5 }).Value);
        Assert.Equal(2, Call(_doc.Api, "Extension.Words", new List<object?> { "a", "b" }).Value);
    }

    [Fact]
    public void Results_become_JSON_with_objects_described_and_NaN_as_null()
    {
        object? Describe(object o) => new JsonObject { ["interface"] = _binder.InterfaceName(o) };
        Assert.Null(ApiValues.ToJson(Call(_doc.Api, "Extension.NotANumber").Value, Describe));
        var mixed = (List<object?>)ApiValues.ToJson(Call(_doc.Api, "Extension.Mixed").Value, Describe)!;
        Assert.Equal(new object?[] { 1L, "a", true, null, 2.5, 5L }, mixed.ToArray());
        var described = (JsonObject)ApiValues.ToJson(_doc.Api.Extension, Describe)!;
        Assert.Equal("IModelDocExtension", described["interface"]);
    }
}
