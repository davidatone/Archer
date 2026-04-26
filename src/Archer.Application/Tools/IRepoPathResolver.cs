namespace Archer.Application.Tools;

public interface IRepoPathResolver
{
    string Resolve(string repoRoot, string relativePath);
    bool TryResolve(string repoRoot, string relativePath, out string fullPath, out string? error);
}
