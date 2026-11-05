using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SwAgent.AddIn.Core.Pipe;

internal sealed class MessageTooLargeException : Exception
{
    public MessageTooLargeException(int maxBytes) : base($"a message longer than {maxBytes} bytes")
    {
    }
}

/// <summary>Reads '\n'-terminated UTF-8 lines (a trailing '\r' is dropped), refusing any longer than the limit.</summary>
internal sealed class LineReader
{
    readonly Stream _stream;
    readonly int _maxBytes;
    readonly byte[] _buffer = new byte[64 * 1024];
    readonly MemoryStream _line = new MemoryStream();
    int _start;
    int _end;

    public LineReader(Stream stream, int maxBytes)
    {
        _stream = stream;
        _maxBytes = maxBytes;
    }

    /// <summary>The next line, or null at the end of the stream.</summary>
    public async Task<string?> ReadLineAsync()
    {
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                Append(_start, newline - _start);
                _start = newline + 1;
                return TakeLine();
            }
            Append(_start, _end - _start);
            _start = _end = 0;
            int read = await _stream.ReadAsync(_buffer, 0, _buffer.Length).ConfigureAwait(false);
            if (read == 0) return _line.Length > 0 ? TakeLine() : null;
            _end = read;
        }
    }

    void Append(int offset, int count)
    {
        if (count == 0) return;
        if (_line.Length + count > _maxBytes) throw new MessageTooLargeException(_maxBytes);
        _line.Write(_buffer, offset, count);
    }

    string TakeLine()
    {
        int length = (int)_line.Length;
        byte[] bytes = _line.GetBuffer();
        if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
        string line = Encoding.UTF8.GetString(bytes, 0, length);
        _line.SetLength(0);
        return line;
    }
}
