using System;
using System.Linq;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class ReferenceTests
{
    static ReferenceData Sample(ReferenceKind kind = ReferenceKind.Face) =>
        new ReferenceData(kind, @"C:\parts\bracket.SLDPRT", new byte[] { 0, 1, 2, 250, 251, 252, 253, 254, 255 }, "planar face of Boss-Extrude1, 800 mm²");

    [Fact]
    public void A_reference_round_trips_and_shows_its_kind()
    {
        string text = References.Encode(Sample());
        Assert.StartsWith("r1.face.", text);
        Assert.DoesNotContain("=", text);
        Assert.DoesNotContain("+", text);
        Assert.DoesNotContain("/", text);
        var back = References.Decode(text, "edges/0");
        Assert.Equal(ReferenceKind.Face, back.Kind);
        Assert.Equal(@"C:\parts\bracket.SLDPRT", back.DocumentKey);
        Assert.Equal(Sample().PersistId, back.PersistId);
        Assert.Equal("planar face of Boss-Extrude1, 800 mm²", back.Description);
    }

    [Theory]
    [InlineData(ReferenceKind.SketchEntity, "r1.sketch_entity.")]
    [InlineData(ReferenceKind.SketchPoint, "r1.sketch_point.")]
    [InlineData(ReferenceKind.Dimension, "r1.dimension.")]
    [InlineData(ReferenceKind.Plane, "r1.plane.")]
    [InlineData(ReferenceKind.Component, "r1.component.")]
    [InlineData(ReferenceKind.Mate, "r1.mate.")]
    public void Every_kind_has_a_snake_case_name(ReferenceKind kind, string prefix)
    {
        string text = References.Encode(Sample(kind));
        Assert.StartsWith(prefix, text);
        Assert.Equal(kind, References.KindOf(text));
        Assert.Equal(kind, References.Decode(text, "f").Kind);
    }

    [Theory]
    [InlineData("Boss-Extrude1")]
    [InlineData("front")]
    [InlineData("D1@Sketch1")]
    [InlineData("")]
    public void A_name_is_not_a_reference(string text)
    {
        Assert.False(References.IsReference(text));
        Assert.Null(References.KindOf(text));
    }

    [Theory]
    [InlineData("r1.face")]
    [InlineData("r1.wheel.AQID")]
    [InlineData("r1.face.not*base64")]
    [InlineData("r1.face.AQ")]
    [InlineData("r1.face.AgAAAAA")]
    [InlineData("r1.face.")]
    [InlineData("Boss-Extrude1")]
    public void A_malformed_reference_is_invalid_argument_naming_the_field(string text)
    {
        var e = Assert.Throws<SwAgentException>(() => References.Decode(text, "faces/2"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("faces/2", e.Error.Details!["field"]);
        Assert.Equal(text, e.Error.Details["reference"]);
    }

    [Fact]
    public void A_truncated_or_padded_payload_is_refused()
    {
        string text = References.Encode(Sample());
        Assert.Throws<SwAgentException>(() => References.Decode(text.Substring(0, text.Length - 6), "f"));
        Assert.Throws<SwAgentException>(() => References.Decode(text + "AAAA", "f"));
    }

    [Fact]
    public void A_reference_without_a_persistent_id_cannot_be_made()
    {
        Assert.Throws<ArgumentException>(() => References.Encode(new ReferenceData(ReferenceKind.Edge, "Part1", Array.Empty<byte>(), "")));
    }

    [Fact]
    public void Unicode_document_titles_survive()
    {
        var data = new ReferenceData(ReferenceKind.Feature, "Teil1 – Ø", Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), "Ø 5 mm");
        var back = References.Decode(References.Encode(data), "f");
        Assert.Equal("Teil1 – Ø", back.DocumentKey);
        Assert.Equal(300, back.PersistId.Length);
    }

    /// <summary>A persistent id shaped like SOLIDWORKS' own: long, and mostly zeros and repeated words (3.A live run: 1047 characters).</summary>
    static byte[] LikeSolidWorks() => Enumerable.Range(0, 620).Select(i => (byte)(i % 16 == 0 ? i / 16 : i % 4 == 0 ? 0xFF : 0)).ToArray();

    [Fact]
    public void A_long_reference_is_compressed_and_still_round_trips()
    {
        var data = new ReferenceData(ReferenceKind.Face, @"C:\Users\someone\AppData\Local\solidworks-mcp\live-scratch\run\references.SLDPRT", LikeSolidWorks(), "plane face of Boss-Extrude1, 800 mm²");
        string text = References.Encode(data);
        Assert.StartsWith("r1.face.", text);
        Assert.True(text.Length < 400, $"{text.Length} characters");
        var back = References.Decode(text, "f");
        Assert.Equal(data.PersistId, back.PersistId);
        Assert.Equal(data.DocumentKey, back.DocumentKey);
        Assert.Equal(data.Description, back.Description);
    }

    [Fact]
    public void A_reference_made_before_compression_still_decodes()
    {
        // Payload version 1, as 3.A issued it: the version byte and three length-prefixed blocks, uncompressed.
        var raw = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)1);
            foreach (var block in new[] { System.Text.Encoding.UTF8.GetBytes("Part1"), new byte[] { 9, 8, 7 }, System.Text.Encoding.UTF8.GetBytes("a face") })
            {
                w.Write(block.Length);
                w.Write(block);
            }
        }
        string text = "r1.face." + Convert.ToBase64String(raw.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var back = References.Decode(text, "f");
        Assert.Equal("Part1", back.DocumentKey);
        Assert.Equal(new byte[] { 9, 8, 7 }, back.PersistId);
        Assert.Equal("a face", back.Description);
    }

    [Fact]
    public void A_compressed_reference_cut_short_or_padded_is_refused()
    {
        string text = References.Encode(new ReferenceData(ReferenceKind.Face, "Part1", LikeSolidWorks(), "a face"));
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() => References.Decode(text.Substring(0, text.Length - 4), "f")).Code);
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() => References.Decode(text + "AAAA", "f")).Code);
    }
}
