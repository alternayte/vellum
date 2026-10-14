using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Ai;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Ai;

/// <summary>Vendor answers that the first set of fixtures did not hold, and what a failure may say about them.</summary>
public class AiVendorShapeTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private readonly OutboundHost _host = shared.Host;

    private static AiFailure FailureOf(AiResult<Summary> result) => Assert.IsType<AiResult<Summary>.Failed>(result).Failure;

    private static string Sse(params string[] data) => string.Concat(data.Select(d => $"data: {d}\n\n"));

    [Fact]
    public async Task An_invalid_answer_is_described_by_schema_position_and_keyword_and_never_by_its_own_text()
    {
        const string marker = "MODEL-CONTROLLED-TEXT";
        var ai = await AiScenario.CreateAsync(_host, "openai");
        ai.Stub
            .Json(ai.Completion($$"""{"title":"Orders","count":2,"{{marker}}-name":true}"""))
            .Json(ai.Completion($$"""{"title":"Orders","count":"{{marker}}-value"}"""))
            .Json(ai.Completion("""{"title":["MARK"],"count":2,"MARK":{"MARK":1}}""".Replace("MARK", marker)));

        var invalid = Assert.IsType<AiFailure.InvalidOutput>(FailureOf(await ai.StructuredAsync()));

        Assert.NotEmpty(invalid.Issues);
        Assert.All(invalid.Issues, issue =>
        {
            Assert.DoesNotContain(marker, issue);
            Assert.InRange(issue.Length, 1, 200);
        });
        Assert.Contains(invalid.Issues, issue => issue.Contains("/properties/title", StringComparison.Ordinal) && issue.Contains("type", StringComparison.Ordinal));
        // The repair requests carried the same bounded issues and none of the answer.
        Assert.Equal(3, ai.Stub.Requests.Count);
        Assert.All(ai.Stub.Requests, sent => Assert.DoesNotContain(marker, sent.Body));
        Assert.Contains("/properties/count", ai.Stub.Requests[2].Body);
    }

    [Fact]
    public async Task An_openai_compatible_stream_that_gave_a_finish_reason_and_no_done_line_is_complete()
    {
        var ai = await AiScenario.CreateAsync(_host, "openai-compatible");
        var whole = ai.Fixture("stream.sse");
        ai.Stub.Sse(whole[..whole.IndexOf("data: [DONE]", StringComparison.Ordinal)]);

        var (chunks, failure) = await ai.StreamAsync();

        Assert.Null(failure);
        Assert.Equal(["Hello", " world"], chunks);
        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal(("succeeded", 21, 9), (row.Outcome, row.InputTokens, row.OutputTokens));
    }

    [Theory]
    [InlineData("api.openai.com", "max_completion_tokens", "max_tokens")]
    [InlineData("llm.internal.example", "max_tokens", "max_completion_tokens")]
    public async Task The_openai_compatible_token_limit_has_the_name_the_server_takes(string host, string sent, string notSent)
    {
        var ai = await AiScenario.CreateAsync(_host, "openai-compatible", p => p["baseUrl"] = $"https://{host}/v1");
        var stub = _host.Transport.For(host);
        try
        {
            stub.Json(ai.Completion(AiScenario.ValidAnswer));

            Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

            var body = JsonNode.Parse(stub.Requests[^1].Body)!.AsObject();
            Assert.Equal(256, body[sent]!.GetValue<int>());
            Assert.False(body.ContainsKey(notSent));
        }
        finally
        {
            _host.Transport.Forget(host);
        }
    }

    [Fact]
    public async Task A_gemini_stream_is_read_to_its_end_so_usage_after_the_finish_reason_counts()
    {
        var ai = await AiScenario.CreateAsync(_host, "gemini");
        ai.Stub.Sse(Sse(
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"Hello"}]}}],"modelVersion":"gemini-fixture-001"}""",
            """{"candidates":[{"content":{"role":"model","parts":[{"text":" world"}]},"finishReason":"STOP"}],"modelVersion":"gemini-fixture-001"}""",
            """{"usageMetadata":{"promptTokenCount":21,"candidatesTokenCount":7,"thoughtsTokenCount":2,"totalTokenCount":30}}"""));

        var (chunks, failure) = await ai.StreamAsync();

        Assert.Null(failure);
        Assert.Equal(["Hello", " world"], chunks);
        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal(("succeeded", 21, 9), (row.Outcome, row.InputTokens, row.OutputTokens));
    }

    [Theory]
    [InlineData("ESCALATION", "refused", "escalation")]
    [InlineData("PUP_LIMITED_DISABLED", "refused", "pup_limited_disabled")]
    [InlineData("IMAGE_SAFETY", "refused", "image_safety")]
    [InlineData("SPII", "refused", "spii")]
    [InlineData("CONTINUATION", "truncated", null)]
    [InlineData("MAX_TOKENS", "truncated", null)]
    [InlineData("MALFORMED_FUNCTION_CALL", "provider_error", "unexpected_stop.malformed_function_call")]
    [InlineData("A-Reason\r\nThat Is <New> And Very Long Indeed Indeed Indeed", "provider_error", "unexpected_stop.a_reason_that_is_new_and_very_")]
    public async Task A_gemini_finish_reason_maps_to_refusal_truncation_or_a_named_stop(string finishReason, string outcome, string? word)
    {
        var ai = await AiScenario.CreateAsync(_host, "gemini");
        var body = JsonNode.Parse(ai.Completion("{}"))!.AsObject();
        body["candidates"]![0]!["finishReason"] = finishReason;
        ai.Stub.Json(body.ToJsonString());

        var failure = FailureOf(await ai.StructuredAsync());

        var row = Assert.Single(await ai.RowsAsync());
        Assert.Equal(outcome, row.Outcome);
        switch (failure)
        {
            case AiFailure.Refused refused:
                Assert.Equal(word, refused.Reason);
                break;
            case AiFailure.ProviderError error:
                Assert.Equal(word, error.Code);
                Assert.Equal(word, row.ErrorCode);
                break;
            default:
                Assert.IsType<AiFailure.InvalidOutput>(failure);
                break;
        }
    }

    [Theory]
    [InlineData("tunedModels/orders-tuned-1", "/v1beta/tunedModels/orders-tuned-1:generateContent")]
    [InlineData("models/gemini-fixture", "/v1beta/models/gemini-fixture:generateContent")]
    [InlineData("gemini-fixture", "/v1beta/models/gemini-fixture:generateContent")]
    public async Task A_gemini_model_name_keeps_its_collection_in_the_path(string model, string path)
    {
        var ai = await AiScenario.CreateAsync(_host, "gemini", p => p["model"] = model);
        ai.Stub.Json(ai.Completion(AiScenario.ValidAnswer));

        Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync());

        Assert.Equal(path, Assert.Single(ai.Stub.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_gemini_403_fails_the_connection_test_and_does_not_call_the_key_valid()
    {
        var ai = await AiScenario.CreateAsync(_host, "gemini");
        ai.Stub.Json(HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"Your API key was reported as leaked. Please use another API key.","status":"PERMISSION_DENIED"}}""");

        var test = (await (await ai.Owner.PostAsync($"/api/workspaces/{ai.WorkspaceId}/ai/providers/{ai.ProviderId}/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.False(test["ok"]!.GetValue<bool>());
        Assert.Equal("permission_denied", test["error"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("key is valid", test["error"]!["message"]!.GetValue<string>());
        Assert.Contains("API key", test["error"]!["message"]!.GetValue<string>());
        Assert.Single(ai.Stub.Requests);
    }

    [Theory]
    [InlineData("credit_balance_exhausted")]
    [InlineData("insufficient_quota")]
    [InlineData("organization_spend_limit_exceeded")]
    public async Task An_openai_billing_code_is_a_quota_error_in_a_status_a_failed_response_and_a_stream(string code)
    {
        var error = $$"""{"message":"Add credits.","type":"insufficient_quota","param":null,"code":"{{code}}"}""";
        var byStatus = await AiScenario.CreateAsync(_host, "openai");
        byStatus.Stub.Json(HttpStatusCode.TooManyRequests, $$"""{"error":{{error}}}""");
        var byBody = await AiScenario.CreateAsync(_host, "openai");
        byBody.Stub.Json($$"""{"id":"resp_1","object":"response","status":"failed","error":{{error}},"model":"gpt-fixture","output":[],"usage":null}""");
        var byStream = await AiScenario.CreateAsync(_host, "openai");
        byStream.Stub.Sse($$"""
            event: error
            data: {"type":"error","code":"{{code}}","message":"Add credits.","param":null,"sequence_number":1}


            """);

        Assert.Equal("quota_exceeded", Assert.IsType<AiFailure.ProviderError>(FailureOf(await byStatus.StructuredAsync())).Code);
        Assert.Equal("quota_exceeded", Assert.IsType<AiFailure.ProviderError>(FailureOf(await byBody.StructuredAsync())).Code);
        Assert.Equal("quota_exceeded", Assert.IsType<AiFailure.ProviderError>((await byStream.StreamAsync()).Failure).Code);
    }

    [Theory]
    [InlineData("max_output_tokens", null)]
    [InlineData("content_filter", null)]
    [InlineData("max_messages", "unexpected_stop.max_messages")]
    [InlineData("steered", "unexpected_stop.steered")]
    [InlineData("Some New\nReason!", "unexpected_stop.some_new_reason_")]
    public async Task An_openai_incomplete_reason_is_truncation_or_refusal_only_where_the_reference_says_so(string reason, string? code)
    {
        var ai = await AiScenario.CreateAsync(_host, "openai");
        var body = JsonNode.Parse(ai.Fixture("truncated.json"))!.AsObject();
        body["incomplete_details"]!["reason"] = reason;
        ai.Stub.Json(body.ToJsonString());

        var failure = FailureOf(await ai.StructuredAsync());

        if (code is null)
            Assert.True(failure is AiFailure.InvalidOutput or AiFailure.Refused);
        else
            Assert.Equal(code, Assert.IsType<AiFailure.ProviderError>(failure).Code);
    }
}
