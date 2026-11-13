using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ExportFileCheckTests
{
    readonly string _folder = TempDir.Create();
    readonly DateTime _started = DateTime.UtcNow;

    string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    string WriteText(string name, string text) => Write(name, Encoding.ASCII.GetBytes(text));

    static string StepText(string schema) =>
        "ISO-10303-21;\r\nHEADER;\r\nFILE_DESCRIPTION (( 'STEP AP214' ), '1' );\r\nFILE_NAME ('block.STEP', '2026-11-06', ( '' ), ( '' ), 'SwSTEP 2.0', 'SolidWorks 2026', '' );\r\n" +
        $"FILE_SCHEMA (( '{schema}' ));\r\nENDSEC;\r\nDATA;\r\n";

    static byte[] BinaryStl(int triangles, int extraBytes = 0)
    {
        var bytes = new byte[84 + 50 * triangles + extraBytes];
        Encoding.ASCII.GetBytes("solid block").CopyTo(bytes, 0);
        BitConverter.GetBytes((uint)triangles).CopyTo(bytes, 80);
        return bytes;
    }

    byte[] Zip(params string[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(e).Open());
                w.Write("<model unit=\"millimeter\"/>");
            }
        }
        return memory.ToArray();
    }

    static SwAgentException Fails(Action check) => Assert.Throws<SwAgentException>(check);

    static string CheckOf(SwAgentException e)
    {
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        return (string)e.Error.Details!["check"]!;
    }

    [Fact]
    public void A_STEP_file_of_the_schema_asked_passes()
    {
        string ap214 = WriteText("block.step", StepText("AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }"));
        var file = ExportFileCheck.Check(ap214, ExportFormat.Step, _started, StepSchema.Ap214, 1);
        Assert.Equal("ISO-10303-21;", file.Header);
        Assert.StartsWith("AUTOMOTIVE_DESIGN", file.Schema);
        Assert.Contains(file.Checks, c => c.Contains("(ap214)"));
        string ap242 = WriteText("block242.step", StepText("AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }"));
        Assert.StartsWith("AP242", ExportFileCheck.Check(ap242, ExportFormat.Step, _started, StepSchema.Ap242, 1).Schema);
    }

    [Fact]
    public void A_STEP_file_of_another_schema_or_no_header_fails()
    {
        string ap214 = WriteText("block.step", StepText("AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }"));
        var e = Fails(() => ExportFileCheck.Check(ap214, ExportFormat.Step, _started, StepSchema.Ap242, 1));
        Assert.Equal("step_schema", CheckOf(e));
        Assert.Equal("AP242", e.Error.Details!["expected"]);
        Assert.Equal("STEP AP214", e.Error.Details["description"]);
        Assert.Contains("its FILE_DESCRIPTION is STEP AP214", e.Message);
        string text = WriteText("not.step", "solid block\nendsolid");
        Assert.Equal("file_header", CheckOf(Fails(() => ExportFileCheck.Check(text, ExportFormat.Step, _started, StepSchema.Ap214, 1))));
    }

    [Fact]
    public void A_Parasolid_file_needs_the_transmit_header()
    {
        string good = WriteText("block.x_t", ExportFileCheck.ParasolidHeader + "**************************\n**PARASOLID");
        Assert.Equal(ExportFileCheck.ParasolidHeader, ExportFileCheck.Check(good, ExportFormat.Parasolid, _started, StepSchema.Ap214, 1).Header);
        string bad = WriteText("bad.x_t", "ISO-10303-21;");
        Assert.Equal("file_header", CheckOf(Fails(() => ExportFileCheck.Check(bad, ExportFormat.Parasolid, _started, StepSchema.Ap214, 1))));
    }

    [Fact]
    public void A_binary_STL_is_its_triangle_count_and_at_least_four_per_body()
    {
        string block = Write("block.stl", BinaryStl(12));
        var file = ExportFileCheck.Check(block, ExportFormat.Stl, _started, StepSchema.Ap214, 1);
        Assert.Equal(12, file.Triangles);
        Assert.Contains(file.Checks, c => c.Contains("684 = 84 + 50 × 12"));
        Assert.Equal("stl_triangles", CheckOf(Fails(() => ExportFileCheck.Check(block, ExportFormat.Stl, _started, StepSchema.Ap214, 4))));
        string truncated = Write("short.stl", BinaryStl(12, extraBytes: -10));
        Assert.Equal("file_header", CheckOf(Fails(() => ExportFileCheck.Check(truncated, ExportFormat.Stl, _started, StepSchema.Ap214, 1))));
        string ascii = WriteText("ascii.stl", "solid block\n facet normal 0 0 1\n" + new string(' ', 100));
        var e = Fails(() => ExportFileCheck.Check(ascii, ExportFormat.Stl, _started, StepSchema.Ap214, 1));
        Assert.Equal("an ASCII STL", e.Error.Details!["measured"]);
    }

    [Fact]
    public void A_3MF_file_is_a_zip_holding_the_model()
    {
        string good = Write("block.3mf", Zip("[Content_Types].xml", "_rels/.rels", "3D/3dmodel.model"));
        var file = ExportFileCheck.Check(good, ExportFormat.ThreeMf, _started, StepSchema.Ap214, 1);
        Assert.Contains(file.Checks, c => c.Contains("3 entries holding 3D/3dmodel.model"));
        string other = Write("other.3mf", Zip("[Content_Types].xml"));
        Assert.Equal("threemf_model", CheckOf(Fails(() => ExportFileCheck.Check(other, ExportFormat.ThreeMf, _started, StepSchema.Ap214, 1))));
        string notZip = WriteText("text.3mf", "<model/>");
        Assert.Equal("file_header", CheckOf(Fails(() => ExportFileCheck.Check(notZip, ExportFormat.ThreeMf, _started, StepSchema.Ap214, 1))));
    }

    [Fact]
    public void A_missing_empty_or_old_file_was_not_written_by_this_call()
    {
        string missing = Path.Combine(_folder, "none.step");
        Assert.Equal("file_written", CheckOf(Fails(() => ExportFileCheck.Check(missing, ExportFormat.Step, _started, StepSchema.Ap214, 1))));
        string empty = Write("empty.step", new byte[0]);
        Assert.Equal("file_written", CheckOf(Fails(() => ExportFileCheck.Check(empty, ExportFormat.Step, _started, StepSchema.Ap214, 1))));
        string old = WriteText("old.step", StepText("AUTOMOTIVE_DESIGN"));
        File.SetLastWriteTimeUtc(old, _started - TimeSpan.FromMinutes(5));
        var e = Fails(() => ExportFileCheck.Check(old, ExportFormat.Step, _started, StepSchema.Ap214, 1));
        Assert.Equal("file_written", CheckOf(e));
        Assert.Contains("before this call", e.Message);
    }

    [Fact]
    public void A_STEP_files_assembly_occurrences_are_counted()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".step");
        try
        {
            System.IO.File.WriteAllText(path, "ISO-10303-21;\nDATA;\n#1=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','a','',#2,#3,$);\n" +
                "#4 = NEXT_ASSEMBLY_USAGE_OCCURRENCE ('2','b','',#2,#5,$);\n#6=PRODUCT('NEXT_ASSEMBLY_USAGE_OCCURRENCE','x','',(#7));\nENDSEC;\n");
            Assert.Equal(2, ExportFileCheck.StepOccurrences(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
