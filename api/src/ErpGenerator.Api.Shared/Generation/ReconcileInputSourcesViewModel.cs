namespace ErpGenerator.Api.Shared.Generation;

public sealed record ReconcileInputSourcesViewModel(
    IReadOnlyList<GenerationInputSourceViewModel> Sources,
    int Reconciled,
    int Removed);
