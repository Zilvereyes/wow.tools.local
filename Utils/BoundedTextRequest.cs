using System.Text;

namespace wow.tools.local.Utils
{
    internal enum BoundedTextRequestStatus
    {
        Success,
        TooLarge,
        InvalidEncoding,
        TooManyLines,
        LineTooLong
    }

    internal readonly record struct BoundedTextRequestResult(BoundedTextRequestStatus Status, IReadOnlyList<string>? Lines);

    internal static class BoundedTextRequest
    {
        public const long MaxBodyBytes = 1024 * 1024;
        public const int MaxLines = 50000;
        public const int MaxLineCharacters = 4096;
        public const string RequestHeaderName = "X-Wow-Tools-Request";
        public const string RequestHeaderValue = "1";

        public static bool HasRequiredHeader(HttpRequest request)
        {
            return request.Headers.TryGetValue(RequestHeaderName, out var values)
                && values.Count == 1
                && string.Equals(values[0], RequestHeaderValue, StringComparison.Ordinal);
        }

        public static async Task<BoundedTextRequestResult> ReadLinesAsync(
            Stream body,
            long? contentLength,
            CancellationToken cancellationToken)
        {
            if (contentLength > MaxBodyBytes)
                return new(BoundedTextRequestStatus.TooLarge, null);

            using var content = new MemoryStream();
            var buffer = new byte[8192];
            long bytesRead = 0;

            while (bytesRead <= MaxBodyBytes)
            {
                var remaining = MaxBodyBytes + 1 - bytesRead;
                var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                    break;

                bytesRead += read;
                if (bytesRead > MaxBodyBytes)
                    return new(BoundedTextRequestStatus.TooLarge, null);

                await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            content.Position = 0;
            var lines = new List<string>();

            try
            {
                using var reader = new StreamReader(
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true);

                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (line.Length > MaxLineCharacters)
                        return new(BoundedTextRequestStatus.LineTooLong, null);

                    if (lines.Count >= MaxLines)
                        return new(BoundedTextRequestStatus.TooManyLines, null);

                    lines.Add(line);
                }
            }
            catch (DecoderFallbackException)
            {
                return new(BoundedTextRequestStatus.InvalidEncoding, null);
            }

            return new(BoundedTextRequestStatus.Success, lines);
        }
    }
}
