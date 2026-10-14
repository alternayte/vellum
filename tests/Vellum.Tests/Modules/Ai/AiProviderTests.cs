using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Ai;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Ai;

/// <summary>
/// Every provider kind through <see cref="AiClient"/>, its real Refit client and the real
/// handler pipeline. The transport serves the fixtures under <c>Fixtures/Ai</c>, which are
/// written from each vendor's API reference.
/// </summary>
public class AiProviderTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private readonly OutboundHost _host = shared.Host;

    public static TheoryData<string> Kinds => new(AiScenario.Kinds);

    private static IReadOnlyList<string> Outcomes(IEnumerable<Vellum.Modules.Ai.UsageRecordEntity> rows) =>
        rows.Select(r => r.Outcome).ToList();

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_structured_answer_is_validated_and_metered(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind, p => p["temperature"] = 0.2);
        ai.Stub.Json(ai.Completion(AiScenario.ValidAnswer));

        var result = Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

        Assert.Equal(new Summary("Orders", 2), result.Value);
        Assert.Equal((1, 21, 9), (result.Usage.Calls, result.Usage.InputTokens, result.Usage.OutputTokens));
        Assert.Equal(ai.Model, result.Usage.Model);
        Assert.Contains("fixture", result.Usage.ReportedModel);
        Assert.Null(result.Usage.CostUsd);

        var sent = Assert.Single(ai.Stub.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(ai.ExpectedPath(), sent.Uri.AbsolutePath);
        Assert.Equal(ai.ExpectedCredential.Value, sent.Header(ai.ExpectedCredential.Name));
        Assert.Contains(ai.NativeSchemaField, sent.Body);
        Assert.Contains("\"additionalProperties\":false", sent.Body);
        Assert.Contains("0.2", sent.Body);
        // The Anthropic adapter adds its thinking headroom to the output limit.
        Assert.Contains(kind == "anthropic" ? "\"max_tokens\":8448" : "256", sent.Body);
        // The model name comes from settings: in the body, or in the path for Gemini.
        Assert.Contains(ai.Model, sent.Body + sent.Uri.AbsolutePath);
        if (kind == "anthropic")
            Assert.Equal("2023-06-01", sent.Header("anthropic-version"));
        // The key is in a header only.
        Assert.DoesNotContain(AiScenario.ApiKey, sent.Uri.ToString() + sent.Body);

        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal(("succeeded", kind, ai.Model, "proposeFlow", "summarise", "summarise/1"),
            (row.Outcome, row.ProviderKind, row.Model, row.Task, row.Purpose, row.PromptTemplateVersion));
        Assert.Equal((21, 9, "user:test"), (row.InputTokens, row.OutputTokens, row.ActorId));
        Assert.Null(row.CostUsd);
        Assert.NotNull(row.DurationMs);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task With_streaming_off_the_text_arrives_as_one_item(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind, p => p["supportsStreaming"] = false);
        ai.Stub.Json(ai.Completion("Hello world"));

        var (chunks, failure) = await ai.StreamAsync();

        Assert.Null(failure);
        Assert.Equal(["Hello world"], chunks);
        Assert.Equal(ai.ExpectedPath(), Assert.Single(ai.Stub.Requests).Uri.AbsolutePath);
        Assert.DoesNotContain("\"stream\":true", ai.Stub.Requests[0].Body);
        Assert.Equal(["succeeded"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_stream_gives_its_deltas_and_its_usage(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Sse(ai.Fixture("stream.sse"));

        var (chunks, failure) = await ai.StreamAsync();

        Assert.Null(failure);
        Assert.Equal(["Hello", " world"], chunks);
        var sent = Assert.Single(ai.Stub.Requests);
        Assert.Equal(ai.ExpectedPath(stream: true), sent.Uri.AbsolutePath);
        Assert.Contains(kind == "gemini" ? "alt=sse" : "\"stream\":true", sent.Uri.Query + sent.Body);
        // A text stream asks for no schema.
        Assert.DoesNotContain(ai.NativeSchemaField, sent.Body);

        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal(("succeeded", 21, 9), (row.Outcome, row.InputTokens, row.OutputTokens));
        Assert.Contains("fixture", row.ReportedModel);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_stream_split_into_single_bytes_reads_the_same(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        var bytes = Encoding.UTF8.GetBytes(ai.Fixture("stream.sse"));
        ai.Stub.Body(() => new DripStream(bytes, failAt: null), "text/event-stream");

        var (chunks, failure) = await ai.StreamAsync();

        Assert.Null(failure);
        Assert.Equal(["Hello", " world"], chunks);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task An_answer_that_is_not_json_is_invalid_after_two_repairs(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(ai.Completion("Sure! Here is the summary: Orders, 2"));

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        var invalid = Assert.IsType<AiFailure.InvalidOutput>(failed.Failure);
        Assert.Contains("not one JSON value", Assert.Single(invalid.Issues));
        Assert.Equal(1 + AiClient.MaxRepairs, ai.Stub.Requests.Count);
        Assert.Equal(["invalid_output", "invalid_output", "invalid_output"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_schema_invalid_answer_is_repaired_with_the_issues_only(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        const string wrong = """{"title":"WRONG-ANSWER-MARKER","count":"two"}""";
        ai.Stub.Json(ai.Completion(wrong)).Json(ai.Completion(AiScenario.ValidAnswer));

        var result = Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

        Assert.Equal(new Summary("Orders", 2), result.Value);
        Assert.Equal((2, 42, 18), (result.Usage.Calls, result.Usage.InputTokens, result.Usage.OutputTokens));
        Assert.Equal(2, ai.Stub.Requests.Count);
        var repair = ai.Stub.Requests[1].Body;
        Assert.Contains("/count", repair);
        Assert.Contains("Summarise the repository.", repair);
        // The invalid answer is not sent back.
        Assert.DoesNotContain("WRONG-ANSWER-MARKER", repair);
        Assert.Equal(["invalid_output", "succeeded"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_schema_invalid_answer_after_all_repairs_is_invalid_output(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(ai.Completion("""{"title":"Orders","count":2,"extra":true}"""));

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        Assert.NotEmpty(Assert.IsType<AiFailure.InvalidOutput>(failed.Failure).Issues);
        Assert.Equal(3, ai.Stub.Requests.Count);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_refusal_is_a_typed_result(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(ai.Fixture("refusal.json"));

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        var refused = Assert.IsType<AiFailure.Refused>(failed.Failure);
        Assert.Equal(kind switch { "anthropic" => "cyber", "gemini" => "safety", _ => "refusal" }, refused.Reason);
        Assert.Single(ai.Stub.Requests);
        Assert.Equal(["refused"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task An_answer_cut_at_the_output_limit_is_invalid_and_never_parsed(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(ai.Fixture("truncated.json"));

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        Assert.Contains("cut at the output limit", Assert.Single(Assert.IsType<AiFailure.InvalidOutput>(failed.Failure).Issues));
        // A cut answer is not sent for repair.
        Assert.Single(ai.Stub.Requests);
        Assert.Equal(["truncated"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_refused_key_and_an_unknown_model_are_named(string kind)
    {
        var wrongKey = await AiScenario.CreateAsync(_host, kind);
        wrongKey.Stub.Json(HttpStatusCode.Unauthorized, wrongKey.Fixture("error-401.json"));
        var noModel = await AiScenario.CreateAsync(_host, kind);
        noModel.Stub.Json(HttpStatusCode.NotFound, noModel.Fixture("error-404.json"));

        var unauthorized = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await wrongKey.StructuredAsync()).Failure);
        var notFound = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await noModel.StructuredAsync()).Failure);

        Assert.Equal("invalid_api_key", unauthorized.Code);
        Assert.Contains("Save a valid key", unauthorized.Message);
        Assert.Equal("model_not_found", notFound.Code);
        Assert.Contains("Check the model name", notFound.Message);
        // Neither is retried, and neither message repeats the provider's text.
        Assert.Single(wrongKey.Stub.Requests);
        Assert.Single(noModel.Stub.Requests);
        Assert.DoesNotContain("sk-te", unauthorized.Message);

        var row = Assert.Single(await wrongKey.RowsAsync());
        Assert.Equal(("provider_error", "invalid_api_key", 0m), (row.Outcome, row.ErrorCode, row.CostUsd));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_429_is_retried_within_bounds_and_then_rate_limited(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(HttpStatusCode.TooManyRequests, ai.Fixture("error-429.json"), ("Retry-After", "2"));
        _host.Waits.Clear();

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<AiFailure.RateLimited>(failed.Failure).RetryAfter);
        Assert.Equal(3, ai.Stub.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], _host.Waits.ToArray());
        // Every attempt carries the credential.
        Assert.All(ai.Stub.Requests, sent => Assert.Equal(ai.ExpectedCredential.Value, sent.Header(ai.ExpectedCredential.Name)));
        // One call to the provider is one row, however many attempts it took.
        Assert.Equal(["rate_limited"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_429_followed_by_an_answer_succeeds(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(HttpStatusCode.TooManyRequests, ai.Fixture("error-429.json"), ("Retry-After", "1"))
            .Json(ai.Completion(AiScenario.ValidAnswer));

        Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());
        Assert.Equal(2, ai.Stub.Requests.Count);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_retry_after_longer_than_the_wait_cap_is_not_waited(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(HttpStatusCode.TooManyRequests, ai.Fixture("error-429.json"), ("Retry-After", "3600"));
        _host.Waits.Clear();

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        Assert.Equal(TimeSpan.FromHours(1), Assert.IsType<AiFailure.RateLimited>(failed.Failure).RetryAfter);
        Assert.Single(ai.Stub.Requests);
        Assert.Empty(_host.Waits);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_503_is_retried_within_bounds_and_a_500_is_not_retried(string kind)
    {
        var overloaded = await AiScenario.CreateAsync(_host, kind);
        overloaded.Stub.Json(HttpStatusCode.ServiceUnavailable, overloaded.Fixture("error-503.json"));
        var broken = await AiScenario.CreateAsync(_host, kind);
        broken.Stub.Json(HttpStatusCode.InternalServerError, broken.Fixture("error-503.json"));

        var first = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await overloaded.StructuredAsync()).Failure);
        var second = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await broken.StructuredAsync()).Failure);

        Assert.Equal(("provider_unavailable", "provider_unavailable"), (first.Code, second.Code));
        Assert.Equal(3, overloaded.Stub.Requests.Count);
        // A 500 does not say that the request was not processed, so a POST is not sent again.
        Assert.Single(broken.Stub.Requests);
        // No success status arrived, so the provider generated nothing that it bills.
        Assert.Equal((0m, null), (Assert.Single(await broken.RowsAsync()) is var row ? (row.CostUsd, row.ReservedUsd) : default));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_provider_that_does_not_answer_times_out(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind, p => p["timeoutSeconds"] = 1);
        ai.Stub.Hang();

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        Assert.IsType<AiFailure.TimedOut>(failed.Failure);
        Assert.Single(ai.Stub.Requests);
        Assert.True(ai.Stub.Requests[0].Cancellation.IsCancellationRequested);
        Assert.Equal(["timed_out"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_connection_reset_is_a_provider_error_and_is_not_retried(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Throw(() => new HttpRequestException(HttpRequestError.ResponseEnded, "reset", new IOException("Connection reset by peer")));

        var error = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync()).Failure);

        Assert.Equal("connection_lost", error.Code);
        Assert.DoesNotContain("reset by peer", error.Message);
        Assert.Single(ai.Stub.Requests);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_connection_that_cannot_be_made_is_retried_and_then_named(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Throw(() => new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var error = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync()).Failure);

        Assert.Equal("connection_failed", error.Code);
        Assert.Equal(3, ai.Stub.Requests.Count);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_stream_cut_in_the_middle_of_a_frame_is_a_provider_error(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        var whole = ai.Fixture("stream.sse");
        ai.Stub.Sse(whole[..(whole.IndexOf(" world", StringComparison.Ordinal) + 4)]);

        var (chunks, failure) = await ai.StreamAsync();

        Assert.Equal(["Hello"], chunks);
        Assert.Equal("stream_interrupted", Assert.IsType<AiFailure.ProviderError>(failure).Code);
        Assert.Equal(("provider_error", "stream_interrupted"), (Assert.Single(await ai.RowsAsync()) is var row ? (row.Outcome, row.ErrorCode) : default));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_stream_whose_connection_breaks_is_a_provider_error(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        var whole = ai.Fixture("stream.sse");
        var bytes = Encoding.UTF8.GetBytes(whole);
        ai.Stub.Body(() => new DripStream(bytes, failAt: Encoding.UTF8.GetByteCount(whole[..(whole.IndexOf(" world", StringComparison.Ordinal) + 4)])), "text/event-stream");

        var (chunks, failure) = await ai.StreamAsync();

        Assert.Equal(["Hello"], chunks);
        Assert.Equal("stream_interrupted", Assert.IsType<AiFailure.ProviderError>(failure).Code);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task An_error_event_in_the_middle_of_a_stream_is_a_provider_error(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Sse(ai.Fixture("stream-error.sse"));

        var (chunks, failure) = await ai.StreamAsync();

        Assert.Equal(["Hel"], chunks);
        Assert.Equal("provider_unavailable", Assert.IsType<AiFailure.ProviderError>(failure).Code);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_cancelled_call_is_cancelled_and_recorded(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Hang();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync(ct: cancel.Token));

        Assert.IsType<AiFailure.Cancelled>(failed.Failure);
        Assert.True(Assert.Single(ai.Stub.Requests).Cancellation.IsCancellationRequested);
        Assert.Equal(["cancelled"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_cancelled_stream_aborts_the_request(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        var whole = ai.Fixture("stream.sse");
        var first = Encoding.UTF8.GetBytes(whole[..whole.IndexOf(" world", StringComparison.Ordinal)]);
        var body = new DripStream(first, failAt: null, hangAtEnd: true);
        ai.Stub.Body(() => body, "text/event-stream");
        using var cancel = new CancellationTokenSource();

        var chunks = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var scope = _host.Factory.Services.CreateScope();
            var client = (AiClient)scope.ServiceProvider.GetService(typeof(AiClient))!;
            await foreach (var chunk in client.StreamTextAsync(ai.Request(), cancel.Token))
            {
                chunks.Add(chunk);
                cancel.Cancel();
            }
        });

        Assert.Equal(["Hello"], chunks);
        Assert.True(body.Disposed);
        Assert.Equal(["cancelled"], Outcomes(await ai.RowsAsync()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task With_structured_output_off_the_schema_goes_in_the_prompt(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind, p => p["supportsStructuredOutput"] = false);
        ai.Stub.Json(ai.Completion($"```json\n{AiScenario.ValidAnswer}\n```"));

        var result = Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

        Assert.Equal(new Summary("Orders", 2), result.Value);
        var body = Assert.Single(ai.Stub.Requests).Body;
        Assert.DoesNotContain(ai.NativeSchemaField, body);
        Assert.Contains("valid against this JSON Schema", body);
        Assert.Contains("additionalProperties", body);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task With_structured_output_on_a_code_fence_is_not_accepted(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(ai.Completion($"```json\n{AiScenario.ValidAnswer}\n```"));

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        Assert.IsType<AiFailure.InvalidOutput>(failed.Failure);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_redirect_is_not_followed(string kind)
    {
        var ai = await AiScenario.CreateAsync(_host, kind);
        ai.Stub.Json(HttpStatusCode.Found, "{}", ("Location", "https://elsewhere.test/v1"));

        var error = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync()).Failure);

        Assert.Equal("unexpected_redirect", error.Code);
        Assert.Single(ai.Stub.Requests);
    }

    [Theory]
    [InlineData("openai", "api.openai.com", "/v1/responses")]
    [InlineData("anthropic", "api.anthropic.com", "/v1/messages")]
    [InlineData("gemini", "generativelanguage.googleapis.com", "/v1beta/models/")]
    public async Task A_provider_without_a_base_url_uses_the_vendor_address(string kind, string host, string path)
    {
        var ai = await AiScenario.CreateAsync(_host, kind, defaultBaseUrl: true);
        var stub = _host.Transport.For(host);
        try
        {
            stub.Json(Fixtures.WithText($"Ai/{kind}/completion.json", AiScenario.ValidAnswer));

            Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

            var sent = Assert.Single(stub.Requests);
            Assert.Equal(("https", host), (sent.Uri.Scheme, sent.Uri.Host));
            Assert.StartsWith(path, sent.Uri.AbsolutePath);
        }
        finally
        {
            _host.Transport.Forget(host);
        }
    }

    [Fact]
    public async Task An_openai_compatible_server_needs_no_key_and_can_leave_out_usage()
    {
        var ai = await AiScenario.CreateAsync(_host, "openai-compatible", withKey: false);
        ai.Stub.Json(Fixtures.WithText("Ai/openai-compatible/completion-no-usage.json", AiScenario.ValidAnswer));

        var result = Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

        // Counts that the server did not report stay unknown. They are not guessed.
        Assert.Equal((null, null, null), (result.Usage.InputTokens, result.Usage.OutputTokens, result.Usage.ReportedModel));
        Assert.Null(Assert.Single(ai.Stub.Requests).Header("Authorization"));
        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal((null, null, null), (row.InputTokens, row.OutputTokens, row.CostUsd));
    }

    [Fact]
    public async Task A_gemini_400_that_names_an_invalid_key_is_an_invalid_key()
    {
        var ai = await AiScenario.CreateAsync(_host, "gemini");
        ai.Stub.Json(HttpStatusCode.BadRequest, ai.Fixture("error-400-key.json"));

        var error = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync()).Failure);

        Assert.Equal("invalid_api_key", error.Code);
    }

    [Fact]
    public async Task A_rejected_request_keeps_the_provider_sentence_for_the_caller()
    {
        var ai = await AiScenario.CreateAsync(_host, "openai-compatible");
        ai.Stub.Json(HttpStatusCode.BadRequest, ai.Fixture("error-400.json"));

        var error = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync()).Failure);

        Assert.Equal("invalid_request", error.Code);
        Assert.Contains("response_format of type json_schema is not supported", error.Message);
    }

    [Fact]
    public async Task An_answer_larger_than_the_cap_is_refused()
    {
        var ai = await AiScenario.CreateAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(new string('x', 9 * 1024 * 1024)));

        var error = Assert.IsType<AiFailure.ProviderError>(Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync()).Failure);

        Assert.Equal("response_too_large", error.Code);
    }

    [Fact]
    public async Task An_answer_nested_deeper_than_the_cap_is_invalid_output()
    {
        var ai = await AiScenario.CreateAsync(_host, "anthropic");
        ai.Stub.Json(ai.Completion(new string('[', 200) + new string(']', 200)));

        var failed = Assert.IsType<AiResult<Summary>.Failed>(await ai.StructuredAsync());

        Assert.IsType<AiFailure.InvalidOutput>(failed.Failure);
    }

    /// <summary>Gives one byte per read. It can break at a byte or wait at the end until it is disposed.</summary>
    private sealed class DripStream(byte[] bytes, int? failAt, bool hangAtEnd = false) : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public bool Disposed => _closed.Task.IsCompleted;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position == failAt)
                throw new IOException("Connection reset by peer");
            if (_position == bytes.Length)
            {
                if (!hangAtEnd)
                    return 0;
                await _closed.Task.WaitAsync(ct);
                return 0;
            }

            buffer.Span[0] = bytes[_position++];
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
