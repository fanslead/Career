namespace Career.Core;

public interface ISecretStore
{
    ValueTask<string?> ReadAsync(string reference, CancellationToken cancellationToken = default);
    ValueTask SaveAsync(string reference, string secret, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default);
}
