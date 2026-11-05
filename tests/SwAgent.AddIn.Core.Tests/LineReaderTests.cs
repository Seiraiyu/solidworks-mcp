using System.IO;
using System.Text;
using System.Threading.Tasks;
using SwAgent.AddIn.Core.Pipe;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class LineReaderTests
{
    /// <summary>Returns one byte per read, so every line crosses read boundaries.</summary>
    sealed class TrickleStream : MemoryStream
    {
        public TrickleStream(byte[] data) : base(data) { }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, count == 0 ? 0 : 1);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));
    }

    [Fact]
    public async Task Splits_lines_across_reads_and_decodes_utf8()
    {
        var reader = new LineReader(new TrickleStream(Encoding.UTF8.GetBytes("a\nbé\r\n\nlast")), 1024);
        Assert.Equal("a", await reader.ReadLineAsync());
        Assert.Equal("bé", await reader.ReadLineAsync());
        Assert.Equal("", await reader.ReadLineAsync());
        Assert.Equal("last", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Refuses_a_line_longer_than_the_limit()
    {
        var reader = new LineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 2000) + "\n")), 1024);
        await Assert.ThrowsAsync<MessageTooLargeException>(() => reader.ReadLineAsync());
    }
}
