namespace InfoMail.Services;

public interface IDocumentReferenceService
{
    Task<IReadOnlyList<string>> GetReferenceTextsAsync(CancellationToken cancellationToken);
}
