using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Live.Tests;

public class TranscriptTests
{
    /// <summary>A fake MCP server: answers each tool from <paramref name="answers"/> and records what it was sent.</summary>
    sealed class Tools
    {
        readonly Func<string, JsonObject, (bool, JsonObject)> _answers;

        public Tools(Func<string, JsonObject, (bool, JsonObject)> answers)
        {
            _answers = answers;
        }

        public List<(string Tool, JsonObject Arguments)> Sent { get; } = new List<(string, JsonObject)>();

        public Task<(bool IsError, JsonObject Body)> CallAsync(string tool, JsonObject arguments)
        {
            Sent.Add((tool, arguments));
            return Task.FromResult(_answers(tool, arguments));
        }
    }

    static JsonObject Obj(string json) => (JsonObject)Json.Parse(json)!;

    [Fact]
    public async Task Variables_fill_whole_values_and_text_and_results_save_by_pointer()
    {
        var transcript = Transcript.Parse(@"{
          ""name"": ""t"",
          ""steps"": [
            { ""tool"": ""sw_model_find"", ""arguments"": { ""document"": ""${part}"", ""limit"": 1 }, ""save"": { ""edge"": ""/entities/0/reference"", ""n"": ""/total"" } },
            { ""say"": ""round it"", ""tool"": ""sw_feature_fillet"", ""arguments"": { ""edges"": [""${edge}""], ""note"": ""from ${part} (${n})"", ""count"": ""${n}"" } }
          ]
        }");
        var tools = new Tools((tool, _) => tool == "sw_model_find"
            ? (false, Obj(@"{""entities"":[{""reference"":""r1.edge.AAA""}],""total"":1}"))
            : (false, Obj(@"{""ok"":true}")));
        var said = new List<string>();
        var variables = new Dictionary<string, object?> { ["part"] = @"C:\scratch\p.SLDPRT" };

        var calls = await transcript.RunAsync(tools.CallAsync, variables, said.Add);

        Assert.Equal(2, calls.Count);
        Assert.Equal(@"C:\scratch\p.SLDPRT", tools.Sent[0].Arguments["document"]);
        var fillet = tools.Sent[1].Arguments;
        Assert.Equal("r1.edge.AAA", Assert.Single((List<object?>)fillet["edges"]!));
        Assert.Equal(@"from C:\scratch\p.SLDPRT (1)", fillet["note"]);
        Assert.Equal(1L, fillet["count"]);
        Assert.Equal("r1.edge.AAA", variables["edge"]);
        Assert.Equal(new[] { "step 2: round it" }, said);
        Assert.Equal(new[] { 1, 2 }, calls.Select(c => c.Step));
    }

    [Fact]
    public async Task An_error_result_stops_the_transcript_and_keeps_the_calls_made()
    {
        var transcript = Transcript.Parse(@"{ ""name"": ""t"", ""steps"": [
            { ""tool"": ""sw_document_new"" }, { ""tool"": ""sw_feature_fillet"" }, { ""tool"": ""sw_model_mass"" } ] }");
        var tools = new Tools((tool, _) => tool == "sw_feature_fillet" ? (true, Obj(@"{""code"":""operation_failed""}")) : (false, new JsonObject()));

        var e = await Assert.ThrowsAsync<TranscriptFailedException>(() => transcript.RunAsync(tools.CallAsync, new Dictionary<string, object?>()));

        Assert.Equal(2, e.Calls.Count);
        Assert.True(e.Calls[1].IsError);
        Assert.Contains("step 2 (sw_feature_fillet)", e.Message);
        Assert.Contains("operation_failed", e.Message);
        Assert.Equal(2, tools.Sent.Count);
    }

    [Fact]
    public async Task A_variable_no_step_saved_is_refused_before_its_call()
    {
        var transcript = Transcript.Parse(@"{ ""name"": ""t"", ""steps"": [ { ""tool"": ""sw_feature_fillet"", ""arguments"": { ""edges"": [""${edge}""] } } ] }");
        var tools = new Tools((_, _) => (false, new JsonObject()));

        var e = await Assert.ThrowsAsync<TranscriptFailedException>(() => transcript.RunAsync(tools.CallAsync, new Dictionary<string, object?>()));

        Assert.Contains("${edge}", e.Message);
        Assert.Empty(tools.Sent);
    }

    [Fact]
    public async Task A_pointer_with_nothing_there_names_the_step()
    {
        var transcript = Transcript.Parse(@"{ ""name"": ""t"", ""steps"": [ { ""tool"": ""sw_model_find"", ""save"": { ""edge"": ""/entities/0/reference"" } } ] }");
        var tools = new Tools((_, _) => (false, Obj(@"{""entities"":[]}")));

        var e = await Assert.ThrowsAsync<TranscriptFailedException>(() => transcript.RunAsync(tools.CallAsync, new Dictionary<string, object?>()));

        Assert.Contains("step 1 (sw_model_find)", e.Message);
        Assert.Contains("/entities/0/reference", e.Message);
        Assert.Single(e.Calls);
    }

    [Theory]
    [InlineData(@"{ ""steps"": [ { ""tool"": ""sw_status"" } ] }", "name")]
    [InlineData(@"{ ""name"": ""t"", ""steps"": [] }", "steps")]
    [InlineData(@"{ ""name"": ""t"", ""steps"": [ { ""tool"": ""status"" } ] }", "sw_")]
    [InlineData(@"{ ""name"": ""t"", ""steps"": [ { ""tool"": ""sw_status"", ""arguments"": [] } ] }", "arguments")]
    public void A_malformed_transcript_is_refused(string json, string says) =>
        Assert.Contains(says, Assert.Throws<FormatException>(() => Transcript.Parse(json)).Message);

    [Fact]
    public void The_bracket_transcript_calls_only_tools_the_server_offers()
    {
        var transcript = Transcript.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "Scenarios", "bracket.transcript.json"));
        var offered = Mcp.Server.ToolCatalog.All.Select(t => t.Name).ToHashSet();
        Assert.Equal("bracket", transcript.Name);
        Assert.All(transcript.Steps, s => Assert.Contains((string)s["tool"]!, offered));
    }
}
