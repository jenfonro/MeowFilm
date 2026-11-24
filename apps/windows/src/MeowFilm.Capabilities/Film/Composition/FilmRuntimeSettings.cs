using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>The six declared native settings capabilities on the private runtime channel.</summary>
public sealed class FilmRuntimeSettings : IRuntimeSettingsStore
{
    private readonly IStorageDatabase database;
    private readonly PanCredentialRepository pan;

    public FilmRuntimeSettings(IStorageDatabase database)
    {
        this.database = database;
        pan = new(database);
    }

    public Task<RuntimeSettingsSnapshot> ReadAsync(string block, CancellationToken token = default) => block switch
    {
        "pan.accounts" => ReadPanSnapshotAsync(token),
        "cat.sources" => ReadCatSnapshotAsync(token),
        "tmdb" => ReadTmdbSnapshotAsync(token),
        "douban" => ReadDoubanSnapshotAsync(token),
        "film.rules" => ReadFilmRulesSnapshotAsync(token),
        "film.preferences" => ReadFilmPreferencesSnapshotAsync(token),
        _ => throw new BridgeException("INVALID_REQUEST")
    };

    public async Task<RuntimeSettingsSnapshot> UpdatePanAsync(JsonElement command, CancellationToken token = default)
    {
        if (command.ValueKind != JsonValueKind.Object ||
            !TryGetString(command, "provider", out var provider) ||
            !TryGetString(command, "mode", out var mode) ||
            !TryGetNonNegativeInt64(command, "accountRevision", out var accountRevision) ||
            !TryGetNonNegativeInt64(command, "rowRevision", out var rowRevision) ||
            !TryGetUniqueProperty(command, "credentials", out var credentials) ||
            credentials.ValueKind != JsonValueKind.Object ||
            mode is not ("replace" or "refresh") ||
            mode == "refresh" && accountRevision == 0)
            throw new BridgeException("INVALID_REQUEST");
        var parsed = PanSettingsStore.ParseCredentials(provider, credentials, "INVALID_REQUEST");
        var (rows, revision) = await pan.SaveAsync(
            provider, parsed, accountRevision, rowRevision, mode == "refresh", token).ConfigureAwait(false);
        return SerializePanSnapshot(rows, revision);
    }

    private static bool TryGetUniqueProperty(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        bool found = false;
        foreach (var property in obj.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) return false;
            found = true;
            value = property.Value;
        }
        return found;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!TryGetUniqueProperty(obj, name, out var prop) || prop.ValueKind != JsonValueKind.String ||
            prop.GetString() is not { } text)
            return false;
        value = text;
        return true;
    }

    private static bool TryGetNonNegativeInt64(JsonElement obj, string name, out long value)
    {
        value = 0;
        if (!TryGetUniqueProperty(obj, name, out var prop) || prop.ValueKind != JsonValueKind.Number ||
            !prop.TryGetInt64(out var number) || number < 0)
            return false;
        value = number;
        return true;
    }

    private async Task<RuntimeSettingsSnapshot> ReadPanSnapshotAsync(CancellationToken token)
    {
        var (rows, revision) = await pan.ReadAllAsync(token).ConfigureAwait(false);
        return SerializePanSnapshot(rows, revision);
    }

    private static RuntimeSettingsSnapshot SerializePanSnapshot(IReadOnlyList<PanAccountRecord> rows, long revision)
    {
        if (revision == 0) return new(null, 0);
        var accounts = new JsonObject();
        foreach (var account in rows)
        {
            var credentials = new JsonObject();
            if (account.Cookie is not null) credentials["cookie"] = account.Cookie;
            if (account.Username is not null) credentials["username"] = account.Username;
            if (account.Password is not null) credentials["password"] = account.Password;
            if (account.Authorization is not null) credentials["authorization"] = account.Authorization;
            if (account.RefreshedAtMs is { } refreshed) credentials["refreshedAt"] = refreshed;
            accounts.Add(account.Provider, new JsonObject
            {
                ["revision"] = account.AccountRevision,
                ["rowRevision"] = account.RowRevision,
                ["credentials"] = credentials
            });
        }
        return new(JsonSerializer.SerializeToElement(new JsonObject { ["version"] = 1, ["accounts"] = accounts }), revision);
    }

    private async Task<RuntimeSettingsSnapshot> ReadCatSnapshotAsync(CancellationToken token)
    {
        var (state, rev) = await CatSourceSettingsStore.ReadSnapshotAsync(database, token).ConfigureAwait(false);
        if (state is null) return new RuntimeSettingsSnapshot(null, rev);

        var providersObj = new JsonObject();
        foreach (var (provider, enabled) in state.Providers)
            providersObj[provider] = enabled;

        var mappingsArr = new JsonArray();
        foreach (var m in state.Mappings)
        {
            var aliasesArr = new JsonArray();
            foreach (var a in m.Aliases) aliasesArr.Add(a);
            mappingsArr.Add(new JsonObject { ["provider"] = m.Provider, ["aliases"] = aliasesArr });
        }

        var scriptsArr = new JsonArray();
        foreach (var s in state.Scripts)
        {
            scriptsArr.Add(new JsonObject
            {
                ["installationId"] = s.InstallationId,
                ["name"] = s.Name,
                ["sourceType"] = s.SourceType,
                ["enabled"] = s.Enabled,
                ["inputRevision"] = s.InputRevision,
                ["sourceUrl"] = s.SourceUrl,
                ["localPath"] = s.LocalPath,
                ["localHash"] = s.LocalHash
            });
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["port"] = state.Port,
            ["providers"] = providersObj,
            ["mappings"] = mappingsArr,
            ["scripts"] = scriptsArr
        };
        return new RuntimeSettingsSnapshot(JsonSerializer.SerializeToElement(root), rev);
    }

    private async Task<RuntimeSettingsSnapshot> ReadTmdbSnapshotAsync(CancellationToken token)
    {
        var (settings, rev) = await FilmMetadataSettingsStore.ReadTmdbSnapshotAsync(database, token).ConfigureAwait(false);
        if (settings is null) return new RuntimeSettingsSnapshot(null, rev);
        var doc = JsonSerializer.SerializeToElement(new
        {
            version = 1,
            settings = new
            {
                apiBase = settings.ApiBase,
                imageBase = settings.ImageBase,
                apiKey = settings.ApiKey,
                accessToken = settings.AccessToken,
                language = settings.Language,
                region = settings.Region,
                includeAdult = settings.IncludeAdult
            }
        });
        return new RuntimeSettingsSnapshot(doc, rev);
    }

    private async Task<RuntimeSettingsSnapshot> ReadDoubanSnapshotAsync(CancellationToken token)
    {
        var (settings, rev) = await FilmMetadataSettingsStore.ReadDoubanSnapshotAsync(database, token).ConfigureAwait(false);
        if (settings is null) return new RuntimeSettingsSnapshot(null, rev);
        var doc = JsonSerializer.SerializeToElement(new
        {
            version = 1,
            settings = new
            {
                apiBase = settings.ApiBase,
                searchBase = settings.SearchBase,
                imageBase = settings.ImageBase,
                apiProxy = settings.ApiProxy,
                imageProxy = settings.ImageProxy,
                cookie = settings.Cookie
            }
        });
        return new RuntimeSettingsSnapshot(doc, rev);
    }

    private async Task<RuntimeSettingsSnapshot> ReadFilmRulesSnapshotAsync(CancellationToken token)
    {
        var (rules, revision) = await FilmRulesStore.ReadAsync(database, token).ConfigureAwait(false);
        if (rules is null) return new RuntimeSettingsSnapshot(null, revision.Revision);
        var doc = JsonSerializer.SerializeToElement(new { version = 1, rules }, FilmRuleService.Json);
        return new RuntimeSettingsSnapshot(doc, revision.Revision);
    }

    private async Task<RuntimeSettingsSnapshot> ReadFilmPreferencesSnapshotAsync(CancellationToken token)
    {
        var (prefs, rev) = await FilmPreferencesStore.ReadSnapshotAsync(database, token).ConfigureAwait(false);
        if (prefs is null) return new RuntimeSettingsSnapshot(null, rev);
        var doc = JsonSerializer.SerializeToElement(new
        {
            version = 1,
            preferences = new
            {
                ruleOrder = prefs.RuleOrder,
                panOrder = prefs.PanOrder,
                keywords = prefs.Keywords,
                siteCleanKeywords = prefs.SiteCleanKeywords,
                iosAutoPrepare = prefs.IosAutoPrepare,
                metadataViewProvider = prefs.MetadataViewProvider
            }
        });
        return new RuntimeSettingsSnapshot(doc, rev);
    }
}
