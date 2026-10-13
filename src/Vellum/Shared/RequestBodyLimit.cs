namespace Vellum.Shared;

/// <summary>
/// Endpoint metadata: the most bytes the request body may hold, and the error a larger body
/// gets. An endpoint without it gets <see cref="Default"/>.
/// </summary>
public sealed record RequestBodyLimit(long MaxBytes, string ErrorType = "payload_too_large", string? ErrorTitle = null)
{
    /// <summary>A command: names, descriptions, IDs and flags.</summary>
    public const long Default = 64 * 1024;

    /// <summary>A body that carries a document: a schema, a scenario, a doc page, a layout.</summary>
    public const long Document = 1024 * 1024;
}

/// <summary>
/// Bounds the body of every request that can carry one. The server's own limit is one number
/// for the whole app and answers without the error envelope, and the test server has none.
/// Here each endpoint states its limit, and a larger body gets 413 with
/// <see cref="ErrorResponse"/> whether it declares its length or streams.
/// </summary>
public static class RequestBodyLimits
{
    public static TBuilder WithBodyLimit<TBuilder>(this TBuilder builder, long maxBytes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequestBodyLimit(maxBytes));

    /// <summary>Runs after routing, so the endpoint and its limit are known.</summary>
    public static IApplicationBuilder UseRequestBodyLimits(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var method = http.Request.Method;
        if (http.GetEndpoint() is not { } endpoint
            || !(HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method)))
        {
            await next(http);
            return;
        }

        // An endpoint that is read before its role check has its own limit (RequestLimits);
        // that middleware has already bounded the body.
        if (endpoint.Metadata.GetMetadata<BodyLimitMetadata>() is not null)
        {
            await next(http);
            return;
        }

        var limit = endpoint.Metadata.GetMetadata<RequestBodyLimit>() ?? new RequestBodyLimit(RequestBodyLimit.Default);
        if (http.Request.ContentLength > limit.MaxBytes)
        {
            await RefuseAsync(http, limit);
            return;
        }

        // A body without a declared length, or with a false one, is cut off as it is read.
        var body = new LimitedStream(http.Request.Body, limit.MaxBytes);
        http.Request.Body = body;
        try
        {
            await next(http);
        }
        catch (BadHttpRequestException) when (body.Exceeded && !http.Response.HasStarted)
        {
            // A handler that reads the body itself lets the exception through.
        }

        // Parameter binding catches the exception and sets the status with no body.
        if (body.Exceeded && !http.Response.HasStarted)
            await RefuseAsync(http, limit);
    });

    private static Task RefuseAsync(HttpContext http, RequestBodyLimit limit)
    {
        http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return http.Response.WriteAsJsonAsync(new ErrorResponse(
            limit.ErrorType, limit.ErrorTitle ?? $"The request body holds at most {limit.MaxBytes} bytes."));
    }

    private sealed class LimitedStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public bool Exceeded { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        private int Count(int read)
        {
            _read += read;
            if (_read <= maxBytes)
                return read;

            Exceeded = true;
            throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
