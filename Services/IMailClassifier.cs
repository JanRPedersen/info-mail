using InfoMail.Models;

namespace InfoMail.Services;

public interface IMailClassifier
{
    Task<EmailDecision> ClassifyAsync(EmailMessage message, CancellationToken cancellationToken);
}
