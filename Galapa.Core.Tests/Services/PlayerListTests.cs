using System.Collections.Immutable;
using Galapa.Core.Configuration;
using Galapa.Core.Game;
using Galapa.Core.Models;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Services;

[Collection("Sequential")]
public class PlayerListTests : IDisposable
{
    private readonly TempDirectory _appData = new();
    private readonly TempDirectory _saveFolder = new();

    public PlayerListTests()
    {
        Paths.AppData = this._appData.Path;
        ConfigFile.RootDirectory = this._saveFolder.Path;
    }

    public void Dispose()
    {
        Paths.AppData = null;
        ConfigFile.RootDirectory = null;
        this._appData.Dispose();
        this._saveFolder.Dispose();
    }

    [Fact]
    public async Task LoadAsync_PrunesCredentialsMissingFromExistingXml()
    {
        await CreateEmptyPlayerListXml();
        var store = new FakeCredentialStore("orphan");

        await new PlayerList(store).LoadAsync();

        Assert.Empty(store.Tokens);
    }

    [Fact]
    public async Task LoadAsync_WithPruningDisabled_KeepsOrphanedCredentials()
    {
        await CreateEmptyPlayerListXml();
        var store = new FakeCredentialStore("orphan");

        await new PlayerList(store) { PruneOrphanedCredentials = false }.LoadAsync();

        Assert.Equal(["orphan"], store.Tokens);
    }

    [Fact]
    public async Task LoadAsync_WhenXmlIsCreated_KeepsOrphanedCredentials()
    {
        var store = new FakeCredentialStore("orphan");

        await new PlayerList(store).LoadAsync();

        Assert.Equal(["orphan"], store.Tokens);
    }

    [Fact]
    public async Task LoadAsync_WithoutRootDirectory_ThrowsWithoutTouchingCredentials()
    {
        ConfigFile.RootDirectory = null;
        var store = new FakeCredentialStore("orphan");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PlayerList(store).LoadAsync());

        Assert.Equal(["orphan"], store.Tokens);
    }

    private static async Task CreateEmptyPlayerListXml()
    {
        var xml = await PlayerListXml.LoadAsync();
        Assert.True(xml.WasCreated);
        Assert.False((await PlayerListXml.LoadAsync()).WasCreated);
    }

    private sealed class FakeCredentialStore(params string[] tokens) : IPlayerCredentialFactory
    {
        public List<string> Tokens { get; } = [..tokens];

        public Task<IPlayerCredential> LoadAsync(string token) =>
            Task.FromResult<IPlayerCredential>(new FakeCredential(this) { Token = token });

        public Task<ImmutableList<string>> GetAllTokensAsync() => Task.FromResult(this.Tokens.ToImmutableList());

        public IPlayerCredential Create(string token) => new FakeCredential(this) { Token = token };
    }

    private sealed class FakeCredential(FakeCredentialStore store) : IPlayerCredential
    {
        public required string Token { get; init; }
        public string? Password { get; set; }
        public string? TotpKey { get; set; }

        public void Save()
        {
            if (!store.Tokens.Contains(this.Token)) store.Tokens.Add(this.Token);
        }

        public void Remove() => store.Tokens.Remove(this.Token);
    }
}
