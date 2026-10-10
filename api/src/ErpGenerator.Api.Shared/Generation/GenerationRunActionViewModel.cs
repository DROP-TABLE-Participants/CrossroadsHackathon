namespace ErpGenerator.Api.Shared.Generation;

public sealed record GenerationRunActionViewModel(
    GenerationRunViewModel Run,
    OneTimeActivationLinkViewModel? Activation = null);
