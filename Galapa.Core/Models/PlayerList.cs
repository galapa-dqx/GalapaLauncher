using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using Galapa.Core.Configuration;
using Galapa.Core.Game;
using Galapa.Core.Game.Authentication;

namespace Galapa.Core.Models;

/// <summary>
///     A player registered in Dragon Quest X. Backed by records in three different places: our own PlayerList.json, the
///     official dqxPlayerList.xml, and an implementation of IPlayerCredential. Modifies the records in-place.
/// </summary>
/// <param name="json">The PlayerList.json record</param>
/// <param name="xml">The dqxPlayerList.xml record</param>
/// <param name="credential">The IPlayerCredential record</param>
public class SavedPlayer(
    PlayerListJson.SavedPlayer json,
    PlayerListXml.SavedPlayer xml,
    IPlayerCredential credential)
{
    private readonly IPlayerCredential _credential = credential;
    private readonly PlayerListJson.SavedPlayer _json = json;
    private readonly PlayerListXml.SavedPlayer _xml = xml;
    private LoginStrategy? _loginStrategy;

    public LoginStrategy LoginStrategy
    {
        get
        {
            if (this._loginStrategy is null)
            {
                var savedStrategy = new SavedPlayerLoginStrategy(this.Token);
                this._loginStrategy = this.Password is not null || this.TotpKey is not null
                    ? new AutoLoginStrategy(savedStrategy, this._credential)
                    : savedStrategy;
            }

            return this._loginStrategy;
        }
    }

    /// <summary>
    ///     The number assigned to this player.
    /// </summary>
    /// <remarks>
    ///     This is the number used in the official launcher to identify the player, and it is stored in the XML file. It
    ///     seems to be one-indexed, and does not align with the folder used for the save file, which is zero-indexed.
    ///     Honestly, it might be entirely for displaying "Player 1"?
    /// </remarks>
    public int Number
    {
        get => this._json.Number;
        set
        {
            this._xml.Number = value;
            this._json.Number = value;
        }
    }

    /// <summary>
    ///     The name (sqexid) of the Player.
    /// </summary>
    /// <remarks>
    ///     This is not stored by the official launcher, but we store it in <see cref="PlayerListJson">PlayerList.json</see>
    ///     to display in our UI. It is not used for anything else.
    /// </remarks>
    public string? Name
    {
        get => this._json.Name;
        set => this._json.Name = value;
    }

    /// <summary>
    ///     The token identifying the player.
    /// </summary>
    /// <remarks>
    ///     This is the same token used in the official launcher, and is stored in both the XML and JSON files. It is a
    ///     base64 string which seem to uniquely identify the user by sqexid.
    /// </remarks>
    public string Token => this._json.Token;

    /// <summary>
    ///     The player's password, if saved.
    /// </summary>
    /// <remarks>
    ///     This is stored in the <see cref="IPlayerCredential" /> implementation, and is not stored in the XML or
    ///     JSON files.
    /// </remarks>
    public string? Password
    {
        get => this._credential.Password;
        set => this._credential.Password = value;
    }

    /// <summary>
    ///     The TOTP key for the player, if saved.
    /// </summary>
    /// <remarks>
    ///     This is a base32 string which is used to generate a TOTP code for the player. It is stored in the
    ///     <see cref="IPlayerCredential" /> implementation, and is not stored in the XML or JSON files.
    /// </remarks>
    public string? TotpKey
    {
        get => this._credential.TotpKey;
        set => this._credential.TotpKey = value;
    }

    /// <summary>
    ///     Save the credential to the credential store. This is called by <see cref="PlayerList.SaveAsync" />.
    /// </summary>
    internal void SaveCredential()
    {
        this._credential.Save();
    }
}

/// <summary>
///     A player using "Easy Play"
/// </summary>
/// <remarks>
///     There's only ever one of these, and it's stored in the XML file with the TrialInfo tag. We don't really know how
///     all of these work, and we haven't implemented it yet.
/// </remarks>
public class TrialPlayer
{
    /// <summary>
    ///     This appears to be a Device ID, since the request to /create/token has this as the x-cis-device-id header.
    ///     Unfortunately it doesn't appear to be the computer ID used in the User-Agent.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     This is the `device_token` we get from /api/device/create/token
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    ///     This is the `supportid` we get from submitting the login form.
    /// </summary>
    public required string Code { get; init; }
}

/// <summary>
///     This class represents our list of registered players in Dragon Quest X.
/// </summary>
/// <remarks>
///     This provides a superset of the data stored in <see cref="PlayerListXml">dqxPlayerList.xml</see>, with additional
///     properties such as the username, password, OTP key, etc. As such, we store this data in our own
///     <see cref="PlayerListJson">PlayerList.json</see> and an OS-specific implementation of
///     <see cref="IPlayerCredential" /> which is used to store the password and TOTP key securely. This class
///     coordinates the three; when you save it, you save all three. When loading, the XML file is used as the source of
///     truth, and the JSON and PlayerCredential are updated to match. This means removing a player from the XML (using the
///     official launcher) will also remove it from the JSON on next startup.
/// </remarks>
public class PlayerList
{
    private readonly IPlayerCredentialFactory _credentialFactory;
    private ImmutableList<string>? _credentialTokens;
    private PlayerListJson? _json;
    private PlayerListXml? _xml;

    public PlayerList(IPlayerCredentialFactory credentialFactory)
    {
        this._credentialFactory = credentialFactory;
    }

    public List<SavedPlayer> Players { get; private set; } = new();

    /// <summary>
    ///     Whether <see cref="LoadAsync" /> deletes stored credentials for players that are no longer in
    ///     dqxPlayerList.xml. This is how removing a player in the official launcher cleans up our credentials.
    /// </summary>
    /// <remarks>
    ///     Credentials are keyed only by token and shared by every app on the machine, not scoped to
    ///     <see cref="ConfigFile.RootDirectory" />. Anything loading a player list from somewhere other than the user's
    ///     real save folder (Toolbox, dev setups) must disable this, or it will delete the launcher's saved passwords and
    ///     TOTP keys, which cannot be recovered.
    /// </remarks>
    public bool PruneOrphanedCredentials { get; init; } = true;

    /// <summary>
    ///     Add a new player to the list by their token. This will add the player to the XML, JSON, and credential in memory
    ///     but
    ///     NOT save them to disk/credential store until you call <see cref="SaveAsync" />.
    /// </summary>
    /// <param name="token">The sqex token for a saved player</param>
    /// <returns>The SavedPlayer that was created</returns>
    public SavedPlayer Add(string token)
    {
        var xml = new PlayerListXml.SavedPlayer { Token = token, Number = this.GetAvailableNumber() };
        var json = new PlayerListJson.SavedPlayer { Token = token, Number = this.GetAvailableNumber() };
        var credential = this._credentialFactory.Create(token);

        var player = new SavedPlayer(json, xml, credential);
        this.Add(player);
        return player;
    }

    /// <summary>
    ///     Add a SavedPlayer to the list. This will add the player to the XML, JSON, and credential store but NOT save them
    ///     until you call <see cref="SaveAsync" />.
    /// </summary>
    /// <param name="player">The SavedPlayer to add</param>
    public void Add(SavedPlayer player)
    {
        this.Players.Add(player);
    }

    /// <summary>
    ///     Remove a player from the list by their token. This will remove the player from the XML, JSON, and credential
    ///     store but NOT save them to disk until you call <see cref="SaveAsync" />.
    /// </summary>
    /// <param name="token">The token identifying the player to remove</param>
    public void Remove(string token)
    {
        var player = this.Players.ToList().Find(p => p.Token == token);
        if (player is null) return;

        this.Remove(player);
    }

    /// <summary>
    ///     Remove a SavedPlayer from the list. This will remove the player from the XML, JSON, and credential store but NOT
    ///     save them to disk until you call <see cref="SaveAsync" />.
    /// </summary>
    /// <param name="player">The player to remove</param>
    public void Remove(SavedPlayer player)
    {
        this.Players.Remove(player);
    }

    /// <summary>
    ///     Save the PlayerList to disk. This will save the XML, JSON, and credential store.
    /// </summary>
    /// <remarks>
    ///     When saving the JSON, if any players don't have a Name yet, their name will be loaded by making an HTTP request
    ///     to the login form with their token. It is recommended to set the name if you have it, to avoid hammering their
    ///     servers.
    /// </remarks>
    public async Task SaveAsync()
    {
        Contract.Assert(this._xml is not null);
        Contract.Assert(this._json is not null);

        // Save all credentials for all players
        foreach (var player in this.Players) player.SaveCredential();

        await Task.WhenAll(this._xml.SaveAsync(), this._json.SaveAsync());
    }

    /// <summary>
    ///     Load the PlayerList from disk. This will load the XML, JSON, and credential store, then sync it all to match the
    ///     XML.
    /// </summary>
    public async Task LoadAsync()
    {
        this._xml = await PlayerListXml.LoadAsync();
        this._json = await PlayerListJson.LoadAsync();
        this._credentialTokens = await this._credentialFactory.GetAllTokensAsync();

        // Sync the XML to the JSON and credential store
        await this.SyncFromXml();

        var players = new List<SavedPlayer>();
        foreach (var jsonPlayer in this._json!.Players.Values)
        {
            var xmlPlayer = this._xml!.Players[jsonPlayer.Token];
            var credential = await this._credentialFactory.LoadAsync(jsonPlayer.Token);
            players.Add(new SavedPlayer(jsonPlayer, xmlPlayer, credential));
        }

        this.Players = players;

        await this.SaveAsync();
    }

    private int GetAvailableNumber()
    {
        if (this.Players.Count >= 4) throw new PlayerLimitReached();

        var numberSet = new SortedSet<int> { 1, 2, 3, 4 };
        foreach (var player in this.Players)
            numberSet.Remove(player.Number);

        return numberSet.First();
    }

    private async Task SyncFromXml()
    {
        Contract.Assert(this._xml is not null);
        Contract.Assert(this._json is not null);
        Contract.Assert(this._credentialTokens is not null);

        // Delete credentials for any players removed from the XML file
        if (this.PruneOrphanedCredentials)
        {
            var xmlTokens = this._xml.Players.Keys.ToHashSet();
            foreach (var token in this._credentialTokens.Except(xmlTokens))
            {
                var cred = await this._credentialFactory.LoadAsync(token);
                cred.Remove();
            }
        }

        // Recreate JSON player list from XML file
        this._json.Players = this._xml.Players.Values.Select(xmlPlayer =>
        {
            this._json.Players.TryGetValue(xmlPlayer.Token, out var match);

            if (match is not null) return match;

            return new PlayerListJson.SavedPlayer
            {
                Number = xmlPlayer.Number,
                Token = xmlPlayer.Token
            };
        }).ToDictionary(p => p.Token);
        this._json.Trial = this._xml.Trial is null
            ? null
            : new PlayerListJson.TrialPlayer
            {
                Token = this._xml.Trial.Token,
                Id = this._xml.Trial.Id,
                Code = this._xml.Trial.Code
            };
        await this._json.SaveAsync();
    }

    /// <summary>
    ///     The official launcher only supports 4 players. This exception is thrown when you try to add a 5th player.
    /// </summary>
    /// <remarks>
    ///     Ideally we should catch it in the UI and prevent the user from adding a 5th player, so this is a last-ditch
    ///     protection.
    /// </remarks>
    public class PlayerLimitReached() : Exception("Player limit reached");
}