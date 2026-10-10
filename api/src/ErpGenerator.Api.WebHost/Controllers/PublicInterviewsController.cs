using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("public-interviews")]
[Route("api/interviews")]
public sealed class PublicInterviewsController(
    IGenerationWorkflowService workflow,
    IInterviewVoiceInputService voiceInput,
    ILogger<PublicInterviewsController> logger) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<ActionResult<PublicInterviewEnvelope>> Get(string token, CancellationToken cancellationToken)
    {
        var interview = await workflow.GetInterviewAsync(token, cancellationToken);
        return interview is null ? NotFound(new { message = "Interview link is invalid or expired." }) : Ok(interview);
    }

    [HttpPost("{token}/start")]
    public async Task<ActionResult<PublicInterviewEnvelope>> Start(string token, CancellationToken cancellationToken) =>
        Ok(await workflow.StartInterviewAsync(token, cancellationToken));

    [HttpPost("{token}/messages")]
    public async Task<ActionResult<SubmitInterviewMessageViewModel>> Message(
        string token,
        SubmitInterviewMessageInputModel input,
        CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await workflow.AddMessageAsync(token, input, cancellationToken));

    [HttpPost("{token}/assistant-response")]
    public async Task<ActionResult<InterviewAssistantResponseViewModel>> AssistantResponse(
        string token,
        RequestInterviewAssistantInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await workflow.CompleteTurnAsync(token, input, cancellationToken));

    [HttpPost("{token}/voice-token")]
    public async Task<ActionResult<InterviewVoiceTokenViewModel>> VoiceToken(
        string token,
        CancellationToken cancellationToken)
    {
        if (await workflow.GetInterviewAsync(token, cancellationToken) is null)
        {
            return NotFound(new { message = "Interview link is invalid or expired." });
        }
        if (!voiceInput.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Voice input is not available." });
        }
        try
        {
            return Ok(await voiceInput.MintTokenAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Voice token provider request failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Voice input is not available right now." });
        }
    }

    [HttpPost("{token}/answers")]
    public async Task<ActionResult<PublicInterviewEnvelope>> Answer(
        string token,
        SubmitInterviewAnswerInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await workflow.AddAnswerAsync(token, input, cancellationToken));

    [HttpPost("{token}/complete")]
    public async Task<ActionResult<PublicInterviewEnvelope>> Complete(
        string token,
        CompleteInterviewInputModel input,
        CancellationToken cancellationToken) =>
        Ok(await workflow.CompleteInterviewAsync(token, input, cancellationToken));
}
