using System.Text;
using System.Text.Json;
using Archer.Application.Mcp;
using Archer.Domain.Mcp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Archer.Mcp.Credentials;

/// <summary>
/// Per-user credential store backed by a single encrypted file. Keyed by server name.
/// Uses ASP.NET Core DataProtection with a file-system-persisted key ring; on macOS/Linux
/// the keys themselves are protected only by filesystem permissions on the keyring directory
/// (0700), so set <c>KeyRingDirectory</c> to a path under <c>~/.config/archer/</c> and not
/// a shared location.
/// </summary>
public sealed class EncryptedFileCredentialStore : ICredentialStore
{
    private const string Purpose = "archer.mcp.credentials.v1";

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedFileCredentialStore(
        string credentialsPath,
        IDataProtectionProvider dataProtection,
        ILogger<EncryptedFileCredentialStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credentialsPath);
        ArgumentNullException.ThrowIfNull(dataProtection);
        _path = credentialsPath;
        _protector = dataProtection.CreateProtector(Purpose);
        _logger = logger;

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public async Task<ServerCredentials?> GetAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        var blob = await ReadBlobAsync(cancellationToken).ConfigureAwait(false);
        return blob.Entries.TryGetValue(serverName, out var creds) ? creds : null;
    }

    public async Task SaveAsync(string serverName, ServerCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        ArgumentNullException.ThrowIfNull(credentials);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var blob = await ReadBlobAsync(cancellationToken).ConfigureAwait(false);
            blob.Entries[serverName] = credentials with { SavedAtUtc = DateTimeOffset.UtcNow };
            await WriteBlobAsync(blob, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var blob = await ReadBlobAsync(cancellationToken).ConfigureAwait(false);
            if (!blob.Entries.Remove(serverName))
            {
                return false;
            }
            await WriteBlobAsync(blob, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- internals -----------------------------------------------------------------------

    private async Task<CredentialsBlob> ReadBlobAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new CredentialsBlob();
        }

        byte[] cipher;
        try
        {
            cipher = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't silently return an empty blob — that would let the next SaveAsync overwrite
            // the file and lose every stored credential. Surface the failure so the caller can
            // decide whether to recover or fail.
            _logger?.LogError(ex, "Failed to read credential file at {Path}", _path);
            throw new InvalidDataException(
                $"Failed to read credential file at {_path}. Refusing to overwrite — fix the underlying " +
                "issue (permissions, disk error) or delete the file to start fresh.", ex);
        }

        if (cipher.Length == 0)
        {
            return new CredentialsBlob();
        }

        try
        {
            var plain = _protector.Unprotect(cipher);
            return JsonSerializer.Deserialize<CredentialsBlob>(plain) ?? new CredentialsBlob();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to decrypt credential file at {Path}. The file may have been written with a different " +
                "DataProtection key. Manual intervention required.", _path);
            throw new InvalidDataException(
                $"Failed to decrypt {_path}. Either the key ring has changed or the file is corrupt.", ex);
        }
    }

    private async Task WriteBlobAsync(CredentialsBlob blob, CancellationToken cancellationToken)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(blob);
        var cipher = _protector.Protect(plain);

        var tmp = _path + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(tmp, cipher, cancellationToken).ConfigureAwait(false);

        // 0600 on POSIX so the cipher blob is at least not world-readable. DataProtection's
        // crypto is the actual line of defense — file mode is defense-in-depth.
        TrySetFileModeOwnerOnly(tmp);
        File.Move(tmp, _path, overwrite: true);
    }

    private static void TrySetFileModeOwnerOnly(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Best-effort; not all filesystems support chmod.
        }
    }

    /// <summary>Internal blob shape — never serialized as plaintext to disk.</summary>
    private sealed class CredentialsBlob
    {
        public Dictionary<string, ServerCredentials> Entries { get; set; } =
            new(StringComparer.Ordinal);
    }

    public override string ToString() =>
        // Crucially, never include the path's contents or any token-like value.
        $"EncryptedFileCredentialStore(path={_path})";
}
