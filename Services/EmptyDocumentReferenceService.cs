namespace InfoMail.Services;

public sealed class EmptyDocumentReferenceService : IDocumentReferenceService
{
    public Task<IReadOnlyList<string>> GetReferenceTextsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
