namespace NFour.AutoUpdater.Repository;

/// <summary>
/// Reads newline-delimited rows with an explicit per-row limit.
///
/// <c>StreamReader.ReadLineAsync</c> grows its result until it finds a newline, so a shard
/// that contains no newline at all is materialised in full before any size or digest check
/// can reject it — and those checks only run once the whole shard has been read, which is far
/// too late to serve as the bound. Rows are read through a block buffer rather than a
/// character at a time, because file tables routinely carry hundreds of thousands of them.
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maximumLineLength)
{
    private readonly char[] _buffer = new char[16 * 1024];
    private int _length;
    private int _position;

    /// <returns>The next row, or null at end of input.</returns>
    /// <exception cref="InvalidDataException">A row exceeded the configured limit.</exception>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        StringBuilder? overflow = null;
        var start = _position;

        while (true)
        {
            if (_position == _length)
            {
                if (_position > start)
                {
                    (overflow ??= new StringBuilder()).Append(_buffer, start, _position - start);
                    EnsureWithinLimit(overflow.Length);
                }
                _length = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                start = 0;
                if (_length == 0)
                    return overflow is { Length: > 0 } ? overflow.ToString() : null;
            }

            var character = _buffer[_position];
            if (character == '\n')
            {
                var line = Combine(overflow, start, _position);
                _position++;
                return TrimCarriageReturn(line);
            }

            _position++;
            EnsureWithinLimit((overflow?.Length ?? 0) + (_position - start));
        }
    }

    private string Combine(StringBuilder? overflow, int start, int end)
    {
        if (overflow is null) return new string(_buffer, start, end - start);
        overflow.Append(_buffer, start, end - start);
        return overflow.ToString();
    }

    private static string TrimCarriageReturn(string line)
        => line.Length > 0 && line[^1] == '\r' ? line[..^1] : line;

    private void EnsureWithinLimit(int length)
    {
        if (length > maximumLineLength)
            throw new InvalidDataException($"A delimited row exceeded the {maximumLineLength} character limit.");
    }
}
