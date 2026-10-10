using System.Security.Cryptography;
using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ErpGenerator.Api.Shared.Generation;

namespace ErpGenerator.Api.Services.Implementations.Generation;

public sealed class GenerationWorkflowService(
    ErpGeneratorDbContext dbContext,
    IOptions<PortalOptions> portalOptions,
    IInterviewInviteDeliveryService inviteDelivery,
    IGenerationResumeService resumeService,
    ILogger<GenerationWorkflowService> logger) : IGenerationWorkflowService
{
    public async Task<PendingWorkResolutionViewModel> AddEvidenceAsync(
        Guid customerId,
        Guid itemId,
        AddEvidenceDocumentInputModel input,
        CancellationToken cancellationToken)
    {
        var item = await OwnedPendingItem(customerId, itemId, cancellationToken);
        var title = Required(input.Title, "Evidence title", 240);
        var text = Required(input.Text, "Evidence text", 50_000);
        dbContext.GenerationProjectInputDocuments.Add(new GenerationProjectInputDocument
        {
            GenerationProjectId = item.GenerationRun.GenerationProjectId,
            Title = title,
            Kind = "evidence",
            Content = text,
            ContentSha256 = Sha256(text)
        });
        item.Status = "resolved";
        item.ResolutionNote = "Evidence document submitted by the customer.";
        item.ResolvedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        var resumed = await TryResumeSafelyAsync(
            item.GenerationRunId,
            "customer_evidence",
            cancellationToken);
        return new PendingWorkResolutionViewModel(true, new AutoResumeViewModel(resumed));
    }

    public async Task<PendingWorkResolutionViewModel> ResolveAsync(
        Guid customerId,
        Guid itemId,
        ResolvePendingWorkInputModel input,
        CancellationToken cancellationToken)
    {
        var item = await OwnedPendingItem(customerId, itemId, cancellationToken);
        item.Status = "resolved";
        item.ResolutionNote = Required(input.ResolutionNote, "Resolution note", 4000);
        item.ResolvedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        var resumed = await TryResumeSafelyAsync(
            item.GenerationRunId,
            "customer_resolution",
            cancellationToken);
        return new PendingWorkResolutionViewModel(true, new AutoResumeViewModel(resumed));
    }

    public async Task<InterviewInviteViewModel> CreateInterviewAsync(
        Guid customerId,
        Guid itemId,
        CreateInterviewRequestInputModel input,
        CancellationToken cancellationToken)
    {
        var item = await OwnedPendingItem(customerId, itemId, cancellationToken);
        var request = item.InterviewRequest;
        if (request is null)
        {
            request = new InterviewRequest
            {
                GenerationProjectId = item.GenerationRun.GenerationProjectId,
                PendingWorkItemId = item.Id,
                QuestionKey = item.Key,
                Question = item.Prompt,
                ParticipantId = MatchParticipant(item.GenerationRun.GenerationProject, item.TargetRole)?.Id,
                RecipientEmail = Optional(input.RecipientEmail, 320),
                Status = "pending"
            };
            dbContext.InterviewRequests.Add(request);
            item.InterviewRequest = request;
        }
        return await Rotate(request, cancellationToken);
    }

    public async Task<InterviewInviteViewModel> RotateInviteAsync(Guid customerId, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await dbContext.InterviewRequests
            .Include(value => value.GenerationProject)
            .Include(value => value.Participant)
            .SingleOrDefaultAsync(value => value.Id == requestId && value.GenerationProject.CustomerId == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Interview request was not found.");
        return await Rotate(request, cancellationToken);
    }

    public async Task<InterviewInviteViewModel> SendInviteAsync(
        Guid customerId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await dbContext.InterviewRequests
            .Include(value => value.GenerationProject)
            .Include(value => value.Participant)
            .SingleOrDefaultAsync(value => value.Id == requestId && value.GenerationProject.CustomerId == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Interview request was not found.");
        var invite = await Rotate(request, cancellationToken);
        request.InviteDeliveryAttemptCount += 1;
        try
        {
            request.InviteDeliveryStatus = await inviteDelivery.DeliverAsync(
                new InterviewInviteDeliveryRequest(
                    request.RecipientEmail,
                    request.GenerationProject.CompanyName,
                    invite.InviteLink),
                cancellationToken);
            request.InviteDeliveredAt = request.InviteDeliveryStatus == "sent" ? DateTimeOffset.UtcNow : null;
            request.InviteDeliveryError = null;
        }
        catch (Exception exception) when (exception is SmtpException or InvalidOperationException or FormatException)
        {
            request.InviteDeliveryStatus = "failed";
            request.InviteDeliveryError = "The interview invitation could not be delivered.";
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return invite with { InviteDelivery = request.InviteDeliveryStatus };
    }

    public async Task<PublicInterviewEnvelope?> GetInterviewAsync(string token, CancellationToken cancellationToken)
    {
        var request = await FindByToken(token, cancellationToken);
        return request is null ? null : Envelope(request);
    }

    public async Task<PublicInterviewEnvelope> StartInterviewAsync(string token, CancellationToken cancellationToken)
    {
        var request = await RequireByToken(token, cancellationToken);
        if (request.Status == "completed")
        {
            return Envelope(request);
        }
        var session = EnsureSession(request);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Envelope(request, session.Id);
    }

    public async Task<SubmitInterviewMessageViewModel> AddMessageAsync(
        string token,
        SubmitInterviewMessageInputModel input,
        CancellationToken cancellationToken)
    {
        var request = await RequireByToken(token, cancellationToken);
        if (request.Status == "completed") throw new InvalidOperationException("Interview is already completed.");

        var content = Required(input.Content, "Answer", 8000);
        var clientTurnKey = Required(input.TurnKey, "Answer submission key", 160);
        if (clientTurnKey.Length < 16 || clientTurnKey.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not ':' and not '-'))
        {
            throw new ArgumentException("A valid answer submission key is required.");
        }

        var turnKey = $"interview-turn-{Sha256($"{request.Id:D}|{clientTurnKey}")}";
        var session = EnsureSession(request);
        var messages = request.Sessions.SelectMany(value => value.Messages).ToArray();
        var existing = messages.SingleOrDefault(value => value.Role == "user" && value.TurnKey == turnKey);
        if (existing is not null)
        {
            if (!string.Equals(existing.Content, content, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This answer submission key is already in use.");
            }
            return new SubmitInterviewMessageViewModel(true, existing.InterviewSessionId, MapMessage(existing),
                new PublicInterviewTurnViewModel(turnKey, TurnStatus(messages, turnKey), DateTimeOffset.UtcNow));
        }

        var active = messages.LastOrDefault(value => value.Role == "user" && value.TurnKey is not null &&
            !messages.Any(candidate => candidate.Role == "assistant" && candidate.TurnKey == value.TurnKey));
        if (active is not null)
        {
            throw new InvalidOperationException("The assistant is still processing the previous answer.");
        }

        var message = new InterviewMessage
        {
            InterviewSession = session,
            Role = "user",
            MessageKind = "answer",
            QuestionKey = request.QuestionKey,
            TurnKey = turnKey,
            Content = content
        };
        session.Messages.Add(message);
        dbContext.InterviewMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SubmitInterviewMessageViewModel(true, session.Id, MapMessage(message),
            new PublicInterviewTurnViewModel(turnKey, "queued", DateTimeOffset.UtcNow));
    }

    public async Task<InterviewAssistantResponseViewModel> CompleteTurnAsync(
        string token,
        RequestInterviewAssistantInputModel input,
        CancellationToken cancellationToken)
    {
        var request = await RequireByToken(token, cancellationToken);
        var turnKey = Required(input.TurnKey, "Turn key", 160);
        if (!turnKey.StartsWith("interview-turn-", StringComparison.Ordinal) || turnKey.Length != 79)
        {
            throw new ArgumentException("The saved answer turn could not be verified.");
        }

        var messages = request.Sessions.SelectMany(value => value.Messages).OrderBy(value => value.CreatedAt).ToArray();
        var answerMessage = messages.LastOrDefault(value => value.Role == "user" && value.TurnKey == turnKey)
            ?? throw new KeyNotFoundException("The saved answer turn could not be found.");
        var assistantMessage = messages.LastOrDefault(value => value.Role == "assistant" && value.TurnKey == turnKey);
        if (assistantMessage is null)
        {
            var session = request.Sessions.Single(value => value.Id == answerMessage.InterviewSessionId);
            if (!request.Answers.Any(value => value.QuestionKey == request.QuestionKey))
            {
                var answer = new InterviewAnswer
                {
                    InterviewRequest = request,
                    QuestionKey = request.QuestionKey,
                    AnswerText = answerMessage.Content,
                    StructuredFactsJson = "[]"
                };
                request.Answers.Add(answer);
                dbContext.InterviewAnswers.Add(answer);
            }

            assistantMessage = new InterviewMessage
            {
                InterviewSession = session,
                Role = "assistant",
                MessageKind = "completion",
                QuestionKey = request.QuestionKey,
                TurnKey = turnKey,
                Content = "Thank you. Your answer has been saved and the ERP discovery process can continue."
            };
            session.Messages.Add(assistantMessage);
            dbContext.InterviewMessages.Add(assistantMessage);
            request.Status = "completed";
            session.Status = "completed";
            session.CompletedAt = DateTimeOffset.UtcNow;
            if (request.PendingWorkItem is { } pending)
            {
                pending.Status = "resolved";
                pending.ResolutionNote = "Linked interview completed.";
                pending.ResolvedAt = DateTimeOffset.UtcNow;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (request.PendingWorkItem is
            {
                Status: "resolved"
            } completedPending)
        {
            await TryResumeSafelyAsync(
                completedPending.GenerationRunId,
                "interview_completion",
                cancellationToken);
        }

        return new InterviewAssistantResponseViewModel(
            "complete",
            true,
            MapMessage(assistantMessage),
            new PublicInterviewTurnViewModel(turnKey, "completed"));
    }

    public async Task<PublicInterviewEnvelope> AddAnswerAsync(
        string token,
        SubmitInterviewAnswerInputModel input,
        CancellationToken cancellationToken)
    {
        var clientTurnKey = $"legacy-{Guid.NewGuid():N}";
        await AddMessageAsync(token, new SubmitInterviewMessageInputModel(input.Answer, clientTurnKey), cancellationToken);
        var request = await RequireByToken(token, cancellationToken);
        return Envelope(request);
    }

    public async Task<PublicInterviewEnvelope> CompleteInterviewAsync(
        string token,
        CompleteInterviewInputModel input,
        CancellationToken cancellationToken)
    {
        var request = await RequireByToken(token, cancellationToken);
        var latest = request.Sessions.SelectMany(value => value.Messages)
            .OrderBy(value => value.CreatedAt)
            .LastOrDefault(value => value.Role == "user" && value.TurnKey is not null);
        if (latest is null && !string.IsNullOrWhiteSpace(input.Answer))
        {
            var saved = await AddMessageAsync(token,
                new SubmitInterviewMessageInputModel(input.Answer, $"legacy-{Guid.NewGuid():N}"), cancellationToken);
            latest = request.Sessions.SelectMany(value => value.Messages)
                .Single(value => value.Id == saved.Message.Id);
        }
        if (latest?.TurnKey is null) throw new ArgumentException("An answer is required before completing the interview.");
        await CompleteTurnAsync(token, new RequestInterviewAssistantInputModel(latest.TurnKey), cancellationToken);
        return Envelope(request);
    }

    private async Task<PendingWorkItem> OwnedPendingItem(Guid customerId, Guid itemId, CancellationToken cancellationToken) =>
        await dbContext.PendingWorkItems
            .Include(value => value.GenerationRun).ThenInclude(value => value.GenerationProject).ThenInclude(value => value.Participants)
            .Include(value => value.InterviewRequest)
            .SingleOrDefaultAsync(value => value.Id == itemId && value.GenerationRun.GenerationProject.CustomerId == customerId, cancellationToken)
        ?? throw new KeyNotFoundException("Pending work item was not found.");

    private async Task<bool> TryResumeSafelyAsync(
        Guid generationRunId,
        string trigger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await resumeService.TryResumeReadyAsync(
                generationRunId,
                trigger,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Generation run {GenerationRunId} could not resume immediately after {Trigger}; worker reconciliation will retry.",
                generationRunId,
                trigger);
            return false;
        }
    }

    private async Task<InterviewInviteViewModel> Rotate(InterviewRequest request, CancellationToken cancellationToken)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        request.TokenHash = Sha256(token);
        request.ExpiresAt = DateTimeOffset.UtcNow.AddDays(14);
        await dbContext.SaveChangesAsync(cancellationToken);
        var baseUrl = portalOptions.Value.PublicBaseUrl.TrimEnd('/');
        return new InterviewInviteViewModel(request.Id, $"{baseUrl}/interviews/{token}", request.InviteDeliveryStatus);
    }

    private async Task<InterviewRequest?> FindByToken(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Sha256(token.Trim());
        var request = await dbContext.InterviewRequests
            .AsSplitQuery()
            .Include(value => value.GenerationProject)
            .Include(value => value.PendingWorkItem).ThenInclude(value => value!.GenerationRun).ThenInclude(value => value.GenerationProject)
            .Include(value => value.Participant)
            .Include(value => value.Answers)
            .Include(value => value.Sessions).ThenInclude(value => value.Messages)
            .SingleOrDefaultAsync(value => value.TokenHash == hash, cancellationToken);
        return request is null || request.ExpiresAt < DateTimeOffset.UtcNow ? null : request;
    }

    private async Task<InterviewRequest> RequireByToken(string token, CancellationToken cancellationToken) =>
        await FindByToken(token, cancellationToken) ?? throw new KeyNotFoundException("Interview link is invalid or expired.");

    private InterviewSession EnsureSession(InterviewRequest request)
    {
        var session = request.Sessions.OrderByDescending(value => value.CreatedAt).FirstOrDefault();
        if (session is not null) return session;
        session = new InterviewSession { InterviewRequest = request, Status = "active" };
        session.Messages.Add(new InterviewMessage
        {
            InterviewSession = session,
            Role = "assistant",
            MessageKind = "question",
            QuestionKey = request.QuestionKey,
            Content = request.Question
        });
        request.Sessions.Add(session);
        dbContext.InterviewSessions.Add(session);
        request.Status = "in_progress";
        return session;
    }

    private static PublicInterviewEnvelope Envelope(InterviewRequest request, Guid? sessionId = null) =>
        new(MapPublic(request), sessionId);

    private static PublicInterviewViewModel MapPublic(InterviewRequest request)
    {
        var messages = request.Sessions.SelectMany(value => value.Messages).OrderBy(value => value.CreatedAt).ToArray();
        var activeAnswer = messages.LastOrDefault(value => value.Role == "user" && value.TurnKey is not null &&
            !messages.Any(candidate => candidate.Role == "assistant" && candidate.TurnKey == value.TurnKey));
        var completedAt = request.Sessions.Where(value => value.CompletedAt.HasValue)
            .MaxBy(value => value.CompletedAt)?.CompletedAt;
        var role = request.Participant?.RoleTitle ?? request.PendingWorkItem?.TargetRole;
        var name = request.Participant?.Name ?? request.RecipientEmail?.Split('@', 2)[0] ?? string.Empty;
        return new PublicInterviewViewModel(
            new PublicInterviewProjectViewModel(
                request.GenerationProject.CompanyName,
                request.GenerationProject.Industry,
                request.GenerationProject.Goal),
            new PublicInterviewParticipantViewModel(name, role, request.Participant?.Department),
            new PublicInterviewRequestViewModel(request.Status, request.QuestionKey, request.ExpiresAt),
            request.PendingWorkItem is null ? null : new PublicInterviewPendingWorkViewModel(
                request.PendingWorkItem.Id,
                request.PendingWorkItem.Prompt,
                request.PendingWorkItem.TargetRole,
                request.PendingWorkItem.SourceStage,
                request.PendingWorkItem.Status),
            new PublicInterviewCompletionViewModel(request.Status == "completed", completedAt),
            activeAnswer?.TurnKey is null ? null : new PublicInterviewTurnViewModel(
                activeAnswer.TurnKey,
                TurnStatus(messages, activeAnswer.TurnKey),
                activeAnswer.CreatedAt),
            messages.Select(MapMessage).ToArray());
    }

    private static string TurnStatus(IReadOnlyCollection<InterviewMessage> messages, string turnKey) =>
        messages.Any(value => value.Role == "assistant" && value.TurnKey == turnKey) ? "completed" : "queued";

    private static InterviewMessageViewModel MapMessage(InterviewMessage value) => new(
        value.Id,
        value.Role,
        value.Content,
        value.MessageKind,
        value.QuestionKey,
        value.CreatedAt);

    private static GenerationProjectParticipant? MatchParticipant(GenerationProject project, string? targetRole)
    {
        if (project.Participants.Count == 0) return null;
        var normalizedRole = targetRole?.Trim();
        return project.Participants.FirstOrDefault(value =>
                   !string.IsNullOrWhiteSpace(normalizedRole) &&
                   string.Equals(value.RoleTitle?.Trim(), normalizedRole, StringComparison.OrdinalIgnoreCase))
               ?? project.Participants.FirstOrDefault();
    }

    private static string Required(string? value, string label, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length is < 1 || text.Length > max) throw new ArgumentException($"{label} is required.");
        return text;
    }

    private static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > max) throw new ArgumentException("One or more fields exceed allowed length.");
        return text;
    }

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
