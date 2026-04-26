using Microsoft.Extensions.AI;

namespace Archer.Application.Model;

/// <summary>
/// Builds an <see cref="IChatClient"/> for a given model deployment. The runner depends on
/// this seam so tests can supply a fake (returning a stub IChatClient) without standing up
/// an Azure resource or DefaultAzureCredential.
/// </summary>
public interface IChatClientFactory
{
    IChatClient Create(string deployment);
}
