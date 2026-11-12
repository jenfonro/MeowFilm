using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static partial class FilmSourcesTests
{
    internal static async Task SearchWire()
    {
        var runtime = new Runtime();
        runtime.Add("a"); runtime.Add("b"); runtime.Add("disabled"); runtime.Nodes["disabled"].Enabled = false;
        await using var service = new FilmSourceService(runtime, new Store());
        await service.RefreshAsync();
        await service.SaveVideoOptionsAsync(Key("b"), new(true, true, false, false));
        await service.SaveVideoOptionsAsync(Key("a"), new(true, false, true, true), order: 17);
        var sources = await service.ReadSearchSourcesAsync(default);
        Check(sources.Count == 1 && sources[0].Cat.InstallationId == "a", "only enabled ready searchable sources are selected; home visibility is independent");
        var wire = sources[0].Wire;
        Fields(wire, "key", "engine", "installationId", "siteId", "generation", "name", "enabled", "searchable", "aggregateImages", "order");
        Check(wire.GetProperty("installationId").GetString() == "a" &&
            wire.GetProperty("generation").GetInt64() == runtime.Nodes["a"].Generation &&
            wire.GetProperty("siteId").GetString() == sources[0].Site.Key.SiteKey &&
            wire.GetProperty("key").GetString() == sources[0].Key &&
            wire.GetProperty("aggregateImages").GetBoolean() && wire.GetProperty("order").GetInt32() == 17,
            "Node receives exact source identity plus the existing cover choice and numeric ordering at the top level");
        int changes = 0; service.Changed += (_, _) => changes++;
        await service.SaveVideoOptionsAsync(Key("b"), new(true, false, true, true), order: 2);
        sources = service.CurrentSearchSources;
        Check(changes > 0 && sources.Select(source => source.Cat.InstallationId).SequenceEqual(["b", "a"]) &&
            sources[0].Wire.GetProperty("aggregateImages").GetBoolean() && !sources[1].Wire.GetProperty("aggregateImages").GetBoolean() &&
            sources[0].Site.Options == new FilmVideoOptions(true, false, true, true) &&
            sources[1].Site.Options == new FilmVideoOptions(true, false, true, false),
            "existing single cover-source selection preserves all other options and configured source order");
        await service.EnableCatAsync("a", false);
        Check(service.CurrentSearchSources.All(source => source.Cat.InstallationId != "a") &&
            runtime.Invocations.All(call => call.Operation != "search"), "configuration reads and changes never fetch upstream search content");
    }
}
