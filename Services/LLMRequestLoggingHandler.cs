using Microsoft.Extensions.Logging;

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
        // Log Request
        if (request.Content != null)
        {
            try
            {
                var requestContent = await request.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogInformation("LLM HTTP Request Payload: {Payload}", requestContent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log request payload");
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
}
