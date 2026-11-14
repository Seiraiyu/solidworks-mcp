using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SwAgent.AddIn.Core;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class PdfFileTests
{
    /// <summary>A3 landscape and A4 portrait, in points.</summary>
    const string A3 = "[0 0 1190.55 841.89]", A4 = "[0 0 595.276 841.89]";

    /// <summary>A PDF of these objects (each "n 0 obj" body), a cross-reference table that is not read, and a trailer naming the catalog.</summary>
    internal static byte[] Pdf(string version, int root, params string[] objects)
    {
        var text = new StringBuilder($"%PDF-{version}\n%âãÏÓ\n");
        for (int i = 0; i < objects.Length; i++) text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \ntrailer\n<< /Size {objects.Length + 1} /Root {root} 0 R >>\nstartxref\n0\n%%EOF\n");
        return Encoding.GetEncoding(28591).GetBytes(text.ToString());
    }

    /// <summary>The usual shape: 1 the catalog, 2 the page tree, then one page per media box.</summary>
    internal static byte[] Simple(params string[] mediaBoxes)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {mediaBoxes.Length} /Kids [{string.Join(" ", mediaBoxes.Select((_, i) => $"{i + 3} 0 R"))}] >>",
        };
        objects.AddRange(mediaBoxes.Select(b => $"<< /Type /Page /Parent 2 0 R /MediaBox {b} /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>"));
        return Pdf("1.4", 1, objects.ToArray());
    }

    static PdfFile Parse(byte[] bytes) => PdfFile.Parse(bytes);

    [Fact]
    public void Reads_the_version_and_each_pages_size_in_order()
    {
        var pdf = Parse(Simple(A3, A4));
        Assert.Equal("1.4", pdf.Version);
        Assert.Equal(2, pdf.Pages.Count);
        Assert.Equal(420, pdf.Pages[0].WidthMm, 2);
        Assert.Equal(297, pdf.Pages[0].HeightMm, 2);
        Assert.Equal(210, pdf.Pages[1].WidthMm, 2);
        Assert.Equal(297, pdf.Pages[1].HeightMm, 2);
        Assert.Equal(2, pdf.DeclaredCount);
        Assert.False(pdf.ObjectStreams);
    }

    [Fact]
    public void A_page_takes_its_media_box_from_its_parents_and_is_turned_by_its_rotation()
    {
        var bytes = Pdf("1.7", 1,
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count 2 /MediaBox {A4} /Kids [3 0 R 4 0 R] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Rotate 90 >>");
        var pdf = Parse(bytes);
        Assert.Equal(210, pdf.Pages[0].WidthMm, 2);
        Assert.Equal(297, pdf.Pages[0].HeightMm, 2);
        Assert.Equal(297, pdf.Pages[1].WidthMm, 2);
        Assert.Equal(210, pdf.Pages[1].HeightMm, 2);
        Assert.Equal(90, pdf.Pages[1].Rotate);
    }

    [Fact]
    public void Nested_page_trees_are_read_in_order_and_an_indirect_media_box_is_followed()
    {
        var bytes = Pdf("1.5", 1,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 3 /Kids [3 0 R 6 0 R] >>",
            "<< /Type /Pages /Parent 2 0 R /Count 2 /Kids [4 0 R 5 0 R] >>",
            $"<< /Type /Page /Parent 3 0 R /MediaBox {A3} >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox 7 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {A4} >>",
            "[0 0 2383.94 1683.78]");
        var pdf = Parse(bytes);
        Assert.Equal(new[] { 420.0, 841.0, 210.0 }, pdf.Pages.Select(p => Math.Round(p.WidthMm, 1)));
        Assert.Equal(594, pdf.Pages[1].HeightMm, 1);
    }

    [Fact]
    public void A_stream_is_skipped_by_its_length_so_text_inside_it_is_not_read_as_objects()
    {
        string content = "BT /F1 12 Tf (9 0 obj << /Type /Page >> endobj) Tj ET";
        var bytes = Pdf("1.4", 1,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {A3} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        Assert.Single(Parse(bytes).Pages);
    }

    [Fact]
    public void Pages_held_in_a_compressed_object_stream_are_read()
    {
        // PDF 1.5: the catalog, the tree and the pages inside a FlateDecode object stream, the root named by an XRef stream.
        string[] inner =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {A3} >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {A4} /Rotate 270 >>",
        };
        var body = new StringBuilder();
        var offsets = new List<int>();
        foreach (var o in inner)
        {
            offsets.Add(body.Length);
            body.Append(o).Append('\n');
        }
        string header = string.Join(" ", offsets.Select((off, i) => $"{i + 1} {off}")) + "\n";
        byte[] compressed = Zlib(Encoding.ASCII.GetBytes(header + body));
        var latin1 = Encoding.GetEncoding(28591);
        var file = new MemoryStream();
        void Write(string s) => file.Write(latin1.GetBytes(s), 0, latin1.GetByteCount(s));
        Write("%PDF-1.5\n");
        Write($"5 0 obj\n<< /Type /ObjStm /N 4 /First {header.Length} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
        file.Write(compressed, 0, compressed.Length);
        Write("\nendstream\nendobj\n");
        Write("6 0 obj\n<< /Type /XRef /Size 7 /Root 1 0 R /W [1 2 1] /Length 0 >>\nstream\n\nendstream\nendobj\nstartxref\n0\n%%EOF\n");

        var pdf = Parse(file.ToArray());
        Assert.True(pdf.ObjectStreams);
        Assert.Equal(2, pdf.Pages.Count);
        Assert.Equal(420, pdf.Pages[0].WidthMm, 2);
        Assert.Equal(297, pdf.Pages[1].WidthMm, 2);
        Assert.Equal(210, pdf.Pages[1].HeightMm, 2);
    }

    [Fact]
    public void A_later_update_of_an_object_wins()
    {
        var first = Simple(A4);
        string update = $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox {A3} >>\nendobj\ntrailer\n<< /Root 1 0 R /Prev 0 >>\n%%EOF\n";
        var bytes = first.Concat(Encoding.ASCII.GetBytes(update)).ToArray();
        Assert.Equal(420, Parse(bytes).Pages.Single().WidthMm, 2);
    }

    [Fact]
    public void Strings_with_brackets_and_escapes_do_not_break_the_dictionary()
    {
        var bytes = Pdf("1.4", 1,
            "<< /Type /Catalog /Pages 2 0 R /Lang (en\\)>> [x) /Name <FEFF0041> >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {A3} /Title (a (nested) title) >>");
        Assert.Single(Parse(bytes).Pages);
    }

    [Theory]
    [InlineData("not a pdf", "%PDF-")]
    [InlineData("%PDF-1.4\n1 0 obj\n<< /Type /Pages /Count 0 /Kids [] >>\nendobj\n%%EOF\n", "catalog")]
    [InlineData("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n", "MediaBox")]
    [InlineData("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 9 0 R >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n", "page tree")]
    public void A_file_that_cannot_be_read_says_why(string text, string says)
    {
        var e = Assert.Throws<FormatException>(() => Parse(Encoding.ASCII.GetBytes(text)));
        Assert.Contains(says, e.Message);
    }

    [Fact]
    public void A_page_tree_that_points_back_at_itself_is_refused_not_followed_for_ever()
    {
        var bytes = Pdf("1.4", 1,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [2 0 R] >>");
        Assert.Contains("loop", Assert.Throws<FormatException>(() => Parse(bytes)).Message);
    }

    /// <summary>zlib: the two-byte header, raw deflate, and the Adler-32 of the input.</summary>
    static byte[] Zlib(byte[] data)
    {
        var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data, 0, data.Length);
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        uint adler = (b << 16) | a;
        output.Write(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler }, 0, 4);
        return output.ToArray();
    }
}
