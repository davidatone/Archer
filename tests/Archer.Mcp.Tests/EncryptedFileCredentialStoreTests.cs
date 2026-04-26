using System.Text;
using Archer.Domain.Mcp;
using Archer.Mcp.Credentials;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Archer.Mcp.Tests;

public sealed class EncryptedFileCredentialStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _credentialsPath;
    private readonly IDataProtectionProvider _protection;

    public EncryptedFileCredentialStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "archer-creds-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _credentialsPath = Path.Combine(_root, "credentials.dat");

        var sp = new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("archer-tests")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_root, "dp-keys")))
            .Services
            .BuildServiceProvider();
        _protection = sp.GetRequiredService<IDataProtectionProvider>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Round_trip_save_and_load_bearer_token()
    {
        var store = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        var creds = new ServerCredentials { BearerToken = "super-secret-token" };

        await store.SaveAsync("simplemem", creds);
        var loaded = await store.GetAsync("simplemem");

        loaded.Should().NotBeNull();
        loaded!.BearerToken.Should().Be("super-secret-token");
    }

    [Fact]
    public async Task Round_trip_api_key_pair_for_trello()
    {
        var store = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        var creds = new ServerCredentials
        {
            ApiKey = new ApiKeyPair { Key = "abc-key", Token = "xyz-token" },
        };

        await store.SaveAsync("trello", creds);
        var loaded = await store.GetAsync("trello");

        loaded!.ApiKey!.Key.Should().Be("abc-key");
        loaded.ApiKey.Token.Should().Be("xyz-token");
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_server()
    {
        var store = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        await store.SaveAsync("a", new ServerCredentials { BearerToken = "x" });

        var loaded = await store.GetAsync("b");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_removes_only_the_named_server()
    {
        var store = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        await store.SaveAsync("a", new ServerCredentials { BearerToken = "1" });
        await store.SaveAsync("b", new ServerCredentials { BearerToken = "2" });

        var removed = await store.DeleteAsync("a");

        removed.Should().BeTrue();
        (await store.GetAsync("a")).Should().BeNull();
        (await store.GetAsync("b")).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_unknown_server()
    {
        var store = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        var removed = await store.DeleteAsync("never-saved");
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task On_disk_blob_does_not_contain_token_as_plaintext()
    {
        var store = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        await store.SaveAsync("trello", new ServerCredentials
        {
            ApiKey = new ApiKeyPair
            {
                Key = "VERY-DISTINCTIVE-KEY-12345",
                Token = "VERY-DISTINCTIVE-TOKEN-67890",
            },
        });

        var bytes = await File.ReadAllBytesAsync(_credentialsPath);
        var asUtf8 = Encoding.UTF8.GetString(bytes);

        asUtf8.Should().NotContain("VERY-DISTINCTIVE-KEY-12345");
        asUtf8.Should().NotContain("VERY-DISTINCTIVE-TOKEN-67890");
    }

    [Fact]
    public async Task GetAsync_throws_when_existing_blob_cannot_be_decrypted()
    {
        // Regression: an earlier version returned an empty blob on read failure, which would
        // let the next SaveAsync silently overwrite the file and lose every credential. The
        // store must surface decryption failures so the caller can decide what to do.
        var store = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        await store.SaveAsync("simplemem", new ServerCredentials { BearerToken = "real" });

        // Corrupt the on-disk blob so DataProtection can't decrypt it.
        await File.WriteAllBytesAsync(_credentialsPath, [0x01, 0x02, 0x03, 0x04, 0x05]);

        var act = async () => await store.GetAsync("simplemem");

        await act.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("*Failed to decrypt*");
    }

    [Fact]
    public async Task Reads_back_after_recreating_store_with_same_keyring()
    {
        // First store writes the blob.
        var store1 = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        await store1.SaveAsync("simplemem", new ServerCredentials { BearerToken = "persisted" });

        // A fresh instance against the same keyring + path must decrypt it.
        var store2 = new EncryptedFileCredentialStore(_credentialsPath, _protection);
        var loaded = await store2.GetAsync("simplemem");

        loaded!.BearerToken.Should().Be("persisted");
    }
}
