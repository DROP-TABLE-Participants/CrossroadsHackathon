using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ErpGenerator.Api.Services.Implementations.Generation;

internal static class GenerationFailureClassifier
{
    public static GenerationFailure Classify(Exception exception)
    {
        if (exception is GeneratedBundleValidationException validation)
        {
            return new GenerationFailure(
                GenerationFailureDispositions.Retryable,
                "generated_bundle_invalid",
                "The generated ERP definition did not pass validation. Recovery is automatic.",
                Bounded(validation.Message));
        }

        if (exception is HttpRequestException request &&
            (request.StatusCode is HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests ||
             (int?)request.StatusCode >= 500))
        {
            return new GenerationFailure(
                GenerationFailureDispositions.Retryable,
                "generation_provider_unavailable",
                "The generation provider is temporarily unavailable. Recovery is automatic.",
                "The generation provider returned a transient HTTP failure.");
        }

        if (exception is TimeoutException or TaskCanceledException or DbUpdateException)
        {
            return new GenerationFailure(
                GenerationFailureDispositions.Retryable,
                "generation_dependency_unavailable",
                "A generation dependency is temporarily unavailable. Recovery is automatic.",
                "A transient timeout or database update failure interrupted generation.");
        }

        if (exception is InvalidOperationException &&
            (exception.Message.Contains("Claude__", StringComparison.Ordinal) ||
             exception.Message.Contains("generation is disabled", StringComparison.OrdinalIgnoreCase)))
        {
            return new GenerationFailure(
                GenerationFailureDispositions.Configuration,
                "generation_configuration_required",
                "Generation is temporarily unavailable until its server configuration is updated.",
                "The Claude generation configuration is missing or invalid.");
        }

        if (exception is InvalidOperationException or ArgumentException or JsonException)
        {
            return new GenerationFailure(
                GenerationFailureDispositions.Terminal,
                "generation_state_invalid",
                "Generation stopped because its saved state could not be processed safely.",
                "Saved generation state or structured output violated its contract.");
        }

        return new GenerationFailure(
            GenerationFailureDispositions.Retryable,
            "internal_generation_incident",
            "Generation encountered an internal incident. Recovery is automatic; no customer retry is required.",
            "An unexpected internal generation incident occurred.");
    }

    private static string Bounded(string value) =>
        value.Length <= 1_000 ? value : value[..1_000];
}
