using System.Text;

namespace AnswerCode.Services;

public class LLMRequestLoggingHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    public LLMRequestLoggingHandler(ILogger logger)
    {
        _logger = logger;
        InnerHandler = new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Read, log, and rewrite request payload if needed
        if (request.Content != null)
        {
            try
            {
                var requestContent = await request.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogInformation("LLM HTTP Request Payload: {Payload}", requestContent);

                // GPT-5 series models require max_completion_tokens instead of max_tokens
                if (requestContent.Contains("\"max_tokens\"") && RequiresMaxCompletionTokens(requestContent))
                {
                    requestContent = requestContent.Replace("\"max_tokens\"", "\"max_completion_tokens\"");
                    request.Content = new StringContent(requestContent, Encoding.UTF8, "application/json");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process request payload");
            }
        }

        // Send Request
        var response = await base.SendAsync(request, cancellationToken);

        // Log Response
        if (response.Content != null)
        {
            try
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogInformation("LLM HTTP Response Payload: {Payload}", responseContent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log response payload");
            }
        }

        return response;
    }

    private static bool RequiresMaxCompletionTokens(string requestBody)
    {
        // GPT-5.4-mini rejects max_tokens, requires max_completion_tokens
        return requestBody.Contains("\"model\":\"gpt-5.4-mini\"")
            || requestBody.Contains("\"model\": \"gpt-5.4-mini\"");
    }
}
