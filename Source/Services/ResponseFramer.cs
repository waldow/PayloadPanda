using System.Globalization;
using System.Text;
using PayloadPanda.Models;

namespace PayloadPanda.Services;

/// <summary>
/// Tells the raw-socket read loop when an HTTP/1.1 response has fully arrived, so it
/// doesn't depend on the server closing the connection: after the head for responses
/// that never carry a body, at <c>Content-Length</c> bytes, or at the final chunk and its
/// trailers. Anything else (no length, an unparsable chunk) runs until the connection closes.
/// Fed the whole buffer after every read; it remembers how far it has looked.
/// </summary>
internal sealed class ResponseFramer(bool headRequest)
{
    private enum State { Head, Length, Chunked, UntilClose, Complete }

    private State _state = State.Head;
    private int _headStart;
    private int _scanFrom;
    private long _bodyEnd;
    private int _chunkPos;

    public bool IsComplete(byte[] data, int length)
    {
        if (_state == State.Head)
            ReadHead(data, length);

        switch (_state)
        {
            case State.Length:
                if (length >= _bodyEnd)
                    _state = State.Complete;
                break;
            case State.Chunked:
                ReadChunks(data, length);
                break;
        }

        return _state == State.Complete;
    }

    private void ReadHead(byte[] data, int length)
    {
        while (true)
        {
            var sep = RawSocketService.IndexOfHeaderSeparator(data, Math.Max(_headStart, _scanFrom), length);
            if (sep < 0)
            {
                // The separator may straddle two reads: look again from 3 bytes back.
                _scanFrom = Math.Max(_headStart, length - 3);
                return;
            }

            var probe = new ResponseModel();
            var headers = RawSocketService.ParseHead(Encoding.ASCII.GetString(data, _headStart, sep - _headStart), probe);
            var bodyStart = sep + 4;
            var status = probe.StatusCode;

            // Interim responses (100 Continue, 103 Early Hints): the real one follows.
            if (status is >= 100 and < 200 && status != 101)
            {
                _headStart = _scanFrom = bodyStart;
                continue;
            }

            if (headRequest || status is 101 or 204 or 304)
            {
                _state = State.Complete;
            }
            else if (ResponseModel.FindHeader(headers, "Transfer-Encoding")
                         ?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            {
                _state = State.Chunked;
                _chunkPos = bodyStart;
            }
            else if (long.TryParse(ResponseModel.FindHeader(headers, "Content-Length"),
                         NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength))
            {
                _state = State.Length;
                _bodyEnd = bodyStart + contentLength;
            }
            else
            {
                _state = State.UntilClose;
            }
            return;
        }
    }

    private void ReadChunks(byte[] data, int length)
    {
        while (true)
        {
            var lineEnd = RawSocketService.FindCrlf(data, _chunkPos, length);
            if (lineEnd < 0)
                return;

            var sizeToken = Encoding.ASCII.GetString(data, _chunkPos, lineEnd - _chunkPos).Trim();
            var semi = sizeToken.IndexOf(';'); // chunk extensions
            if (semi >= 0) sizeToken = sizeToken[..semi];

            if (!int.TryParse(sizeToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                _state = State.UntilClose; // not valid chunking: take whatever the server sends
                return;
            }

            if (size == 0)
            {
                // Trailer lines, then an empty line, end the message.
                var pos = lineEnd + 2;
                while (true)
                {
                    var trailerEnd = RawSocketService.FindCrlf(data, pos, length);
                    if (trailerEnd < 0)
                        return;
                    if (trailerEnd == pos)
                    {
                        _state = State.Complete;
                        return;
                    }
                    pos = trailerEnd + 2;
                }
            }

            var next = (long)lineEnd + 2 + size + 2; // the chunk data and its trailing CRLF
            if (next > length)
                return;
            _chunkPos = (int)next;
        }
    }
}
