using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ApiCallLogTests
{
    static readonly DateTime Noon = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Each_call_is_one_line_with_target_member_arguments_result_and_duration()
    {
        string path = Path.Combine(TempDir.Create(), "logs", "api-calls.log");
        var log = new ApiCallLog(path, () => Noon);
        log.Write(new ApiCallRecord
        {
            Target = "document",
            Document = @"C:\scratch\block.SLDPRT",
            Member = "Extension.GetMassProperties2",
            Args = new List<object?> { 1L, 0L, false },
            Result = new JsonObject { ["result"] = new List<object?> { 0.02, 0.01 } },
            DurationMs = 12,
        });
        log.Write(new ApiCallRecord
        {
            Target = "app",
            Member = "CloseDoc",
            Args = new List<object?> { "block" },
            Error = new SwAgentError(ErrorCode.InvalidArgument, "api.call refuses CloseDoc", new JsonObject { ["refused"] = "CloseDoc" }),
            DurationMs = 0,
        });
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        var first = (JsonObject)Json.Parse(lines[0])!;
        Assert.Equal("document", first["target"]);
        Assert.Equal(@"C:\scratch\block.SLDPRT", first["document"]);
        Assert.Equal("Extension.GetMassProperties2", first["member"]);
        Assert.Equal(3, ((List<object?>)first["args"]!).Count);
        Assert.Equal("ok", first["outcome"]);
        Assert.Equal(12L, first["duration_ms"]);
        Assert.StartsWith("2026-11-06T12:00:00.000", (string)first["time"]!);
        var second = (JsonObject)Json.Parse(lines[1])!;
        Assert.Equal("invalid_argument", second["outcome"]);
        Assert.Equal("CloseDoc", ((JsonObject)((JsonObject)second["error"]!)["details"]!)["refused"]);
        Assert.False(second.ContainsKey("result"));
    }

    [Fact]
    public void Past_ten_megabytes_the_log_starts_again_and_keeps_one_old_file()
    {
        string folder = TempDir.Create();
        string path = Path.Combine(folder, "api-calls.log");
        File.WriteAllText(path, new string('x', (int)ApiCallLog.MaxBytes));
        new ApiCallLog(path).Write(new ApiCallRecord { Target = "app", Member = "RevisionNumber" });
        Assert.Single(File.ReadAllLines(path));
        Assert.Equal(ApiCallLog.MaxBytes, new FileInfo(Path.Combine(folder, "api-calls.1.log")).Length);
    }

    [Fact]
    public void A_log_that_cannot_be_written_is_dropped()
    {
        string folder = TempDir.Create();
        string blocked = Path.Combine(folder, "a-file");
        File.WriteAllText(blocked, "not a folder");
        new ApiCallLog(Path.Combine(blocked, "api-calls.log")).Write(new ApiCallRecord { Target = "app", Member = "RevisionNumber" });
        Assert.Equal(Path.Combine(FileLog.DefaultDirectory, "api-calls.log"), ApiCallLog.DefaultPath);
    }
}
