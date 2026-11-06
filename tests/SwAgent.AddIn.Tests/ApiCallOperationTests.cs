using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>The 40 x 20 x 10 block, the fake binder and a log of api.call's calls in the test's folder.</summary>
public abstract class ApiCallTestBase : FeatureTestBase
{
    internal readonly FakeBinder Binder = new FakeBinder();

    protected ApiCallTestBase()
    {
        Services.ApiBinder = Binder;
        Services.ApiCalls = new ApiCallLog(PathOf("api-calls.log"));
    }

    internal FakeDocument ApiBlock()
    {
        var doc = Part();
        Block();
        return doc;
    }

    internal ApiCallResult Api(ApiCallRequest request) => Run(new ApiCallOperation(Services), request);

    internal static List<object?> Args(params object?[] args) => args.ToList();

    internal List<JsonObject> Logged() =>
        File.Exists(PathOf("api-calls.log"))
            ? File.ReadAllLines(PathOf("api-calls.log")).Select(l => (JsonObject)Json.Parse(l)!).ToList()
            : new List<JsonObject>();
}

public class ApiCallTests : ApiCallTestBase
{
    [Fact]
    public void A_read_on_the_document_returns_SI_values_out_arguments_and_the_rebuild()
    {
        var doc = ApiBlock();
        var result = Api(new ApiCallRequest { Target = "document", Member = "Extension.GetMassProperties2", Args = Args(1L, 0L, false) });
        var values = (List<object?>)result.Result!;
        Assert.Equal(8e-6, (double)values[3]!, 15);
        Assert.Equal(0L, result.Out["Status"]);
        Assert.Equal(new[] { "IModelDoc2", "IModelDocExtension" }, result.Interfaces);
        Assert.Equal("Part1", result.Document!.Title);
        Assert.NotNull(result.Rebuild);
        Assert.Equal(0, result.Rebuild!.Errors);
        Assert.Equal(new[] { false }, doc.Rebuilds);
    }

    [Fact]
    public void An_app_call_rebuilds_nothing_unless_asked()
    {
        var doc = ApiBlock();
        var result = Api(new ApiCallRequest { Target = "app", Member = "RevisionNumber" });
        Assert.Equal("34.4.0", result.Result);
        Assert.Null(result.Rebuild);
        Assert.Null(result.Document);
        Assert.Empty(doc.Rebuilds);
        var rebuilt = Api(new ApiCallRequest { Target = "app", Member = "RevisionNumber", Rebuild = true });
        Assert.NotNull(rebuilt.Rebuild);
        Assert.Single(doc.Rebuilds);
    }

    [Fact]
    public void A_document_call_can_skip_the_rebuild()
    {
        var doc = ApiBlock();
        Assert.Null(Api(new ApiCallRequest { Target = "document", Member = "GetTitle", Rebuild = false }).Rebuild);
        Assert.Empty(doc.Rebuilds);
    }

    [Fact]
    public void A_reference_target_and_reference_arguments_resolve_to_their_entities()
    {
        var doc = ApiBlock();
        string face = Face(doc);
        var result = Api(new ApiCallRequest
        {
            Target = "document",
            Member = "Extension.SelectEntity",
            Args = Args(new JsonObject { ["ref"] = face }, false),
        });
        Assert.Equal(true, result.Result);
        Assert.Equal(2, Assert.Single(doc.Selected).Type);
        var feature = (string)Services.References.Describe(doc, doc.FeatureByName("Boss-Extrude1")!).Reference!;
        var on = Api(new ApiCallRequest { Target = feature, Member = "Info" });
        Assert.Equal(new[] { "IFeature" }, on.Interfaces);
    }

    [Fact]
    public void A_returned_entity_comes_back_as_its_reference()
    {
        var doc = ApiBlock();
        var result = Api(new ApiCallRequest { Target = "document", Member = "FeatureByName", Args = Args("Boss-Extrude1") });
        var described = (JsonObject)result.Result!;
        Assert.StartsWith("r1.feature.", (string)described["ref"]!);
        Assert.Equal("IFeature", described["interface"]);
        var extension = (JsonObject)Api(new ApiCallRequest { Target = "document", Member = "Extension", Rebuild = false }).Result!;
        Assert.False(extension.ContainsKey("ref"));
        Assert.Equal("IModelDocExtension", extension["interface"]);
    }

    [Theory]
    [InlineData("app", "CloseDoc")]
    [InlineData("app", "closealldocuments")]
    [InlineData("app", "ExitApp")]
    [InlineData("app", "RunMacro2")]
    [InlineData("app", "ActiveDoc.Close")]
    [InlineData("document", "Close")]
    [InlineData("document", "Quit")]
    [InlineData("document", "Extension.CloseDoc")]
    [InlineData("document", "ReloadOrReplace")]
    [InlineData("document", "Extension.ReloadOrReplace")]
    [InlineData("app", "ActiveDoc.FileReload")]
    public void A_refused_member_is_refused_logged_and_never_called(string target, string member)
    {
        var doc = ApiBlock();
        var e = Fails(() => Api(new ApiCallRequest { Target = target, Member = member, Args = Args("part1"), Rebuild = false }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.NotNull(e.Error.Details!["refused"]);
        Assert.Empty(Sw.Api.Called);
        Assert.Empty(doc.Api.Called);
        var logged = Assert.Single(Logged());
        Assert.Equal(member, logged["member"]);
        Assert.Equal("invalid_argument", logged["outcome"]);
    }

    [Fact]
    public void A_refused_member_answers_as_refused_before_documents_sketches_or_references_are_looked_at()
    {
        // No document open, the user in a command, a reference that resolves nowhere: still refused, and logged.
        Sw.OpenCommandTitle = "Boss-Extrude";
        var e = Fails(() => Api(new ApiCallRequest { Target = "r1.face.AAAA", Document = "nothing.SLDPRT", Member = "ReloadOrReplace", Args = Args(false, "", true) }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("ReloadOrReplace", e.Error.Details!["refused"]);
        Assert.Equal("invalid_argument", Assert.Single(Logged())["outcome"]);
    }

    [Fact]
    public void A_closing_refusal_says_documents_close_through_document_close()
    {
        ApiBlock();
        Assert.Contains("only with document.close", Fails(() => Api(new ApiCallRequest { Target = "app", Member = "CloseDoc", Args = Args("part1") })).Message);
    }

    [Fact]
    public void Every_call_is_logged_with_its_arguments_result_and_duration()
    {
        ApiBlock();
        Api(new ApiCallRequest { Target = "document", Member = "Extension.GetMassProperties2", Args = Args(1L, 0L, false) });
        var logged = Assert.Single(Logged());
        Assert.Equal("document", logged["target"]);
        Assert.Equal(PathOf("part1.SLDPRT"), logged["document"]);
        Assert.Equal(new List<object?> { 1L, 0L, false }, (List<object?>)logged["args"]!);
        Assert.Equal("ok", logged["outcome"]);
        Assert.Equal("Extension.GetMassProperties2", ((JsonObject)logged["result"]!)["member"]);
        Assert.True(logged.ContainsKey("duration_ms"));
    }

    [Fact]
    public void A_bad_reference_calls_nothing()
    {
        var doc = ApiBlock();
        var entity = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, AreaMm2 = 800 });
        string face = Services.References.Describe(doc, entity).Reference!;
        entity.State = PersistState.Deleted;
        var e = Fails(() => Api(new ApiCallRequest { Target = "document", Member = "Extension.SelectEntity", Args = Args(new JsonObject { ["ref"] = face }, false) }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("/args/0", e.Error.Details!["field"]);
        Assert.Empty(doc.Selected);
        Assert.Empty(doc.Rebuilds);
    }

    [Fact]
    public void A_name_is_taken_wherever_a_reference_is()
    {
        var doc = ApiBlock();
        Assert.Equal(new[] { "IFeature" }, Api(new ApiCallRequest { Target = "Boss-Extrude1", Member = "Info" }).Interfaces);
        var result = Api(new ApiCallRequest { Target = "document", Member = "Extension.SelectEntity", Args = Args(new JsonObject { ["ref"] = "front" }, false) });
        Assert.Equal(4, Assert.Single(doc.Selected).Type);
        Assert.Equal(true, result.Result);
    }

    [Fact]
    public void A_target_or_member_that_names_nothing_is_refused()
    {
        ApiBlock();
        Assert.Equal("target", Fails(() => Api(new ApiCallRequest { Target = "Boss-Extrude9", Member = "GetTitle" })).Error.Details!["field"]);
        Assert.Equal("target", Fails(() => Api(new ApiCallRequest { Target = " ", Member = "GetTitle" })).Error.Details!["field"]);
        Assert.Equal("member", Fails(() => Api(new ApiCallRequest { Target = "document", Member = "[DISPID=3]" })).Error.Details!["field"]);
    }

    [Fact]
    public void A_rebuilding_call_waits_while_the_user_edits_a_sketch()
    {
        var doc = ApiBlock();
        doc.EditingSketchName = "Sketch1";
        var e = Fails(() => Api(new ApiCallRequest { Target = "document", Member = "ForceRebuild3", Args = Args(false) }));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        Assert.Empty(doc.Api.Called);
        Assert.Equal("Part1", Api(new ApiCallRequest { Target = "document", Member = "GetTitle", Rebuild = false }).Result);
    }

    [Fact]
    public void A_document_that_closes_behind_the_call_fails_verification()
    {
        var doc = ApiBlock();
        doc.Vanished = () => Sw.Docs.Remove(doc);
        var e = Fails(() => Api(new ApiCallRequest { Target = "document", Member = "Extension.Vanish", Rebuild = false }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("document_open", e.Error.Details!["check"]);
    }
}

/// <summary>Every error code api.call lists in the plan's code table is reachable (design 371, Error handling), one case per code.</summary>
public class ApiCallErrorCodeTests : ApiCallTestBase
{
    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Api_call_reaches_every_listed_code(ErrorCode code)
    {
        var doc = ApiBlock();
        var request = new ApiCallRequest { Target = "document", Member = "GetTitle" };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Target = "app";
                request.Member = "CloseDoc";
                request.Args = Args("part1");
                break;
            case ErrorCode.ReferenceUnresolved:
                var edge = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, LengthMm = 10 });
                request.Target = Services.References.Describe(doc, edge).Reference!;
                edge.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Boss-Extrude";
                break;
            case ErrorCode.VerificationFailed:
                doc.Vanished = () => Sw.Docs.Remove(doc);
                request.Member = "Extension.Vanish";
                break;
            case ErrorCode.ApiError:
                request.Member = "Extension.Fails";
                break;
        }
        Assert.Equal(code, Fails(() => Api(request)).Code);
    }
}
