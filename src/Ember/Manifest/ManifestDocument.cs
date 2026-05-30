using System.Text.Json.Serialization;

namespace Ember.Manifest;

/// <summary>
/// The subset of <c>constellation_manifest.cli load --json</c> output that Slice B reads.
/// The framework's consumer contract guarantees field presence (optionals serialize as
/// <c>null</c>, not absent) and additive-only changes within a schema version — see
/// <c>D:\\work\\manifest\\docs\\MANIFEST-CONSUMER-PATTERN.md</c>. Adding a field here that
/// isn't in the contract is a contract bug — surface it as v1.5.x feedback instead.
/// </summary>
public sealed record ManifestDocument(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("archetype")] string Archetype,
    [property: JsonPropertyName("topology")] string Topology,
    [property: JsonPropertyName("intent")] string? Intent,
    [property: JsonPropertyName("constellation")] ManifestConstellation Constellation,
    [property: JsonPropertyName("saga")] ManifestSaga? Saga,
    [property: JsonPropertyName("repos")] List<ManifestRepo> Repos);

public sealed record ManifestConstellation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description);

public sealed record ManifestSaga(
    [property: JsonPropertyName("active_epic_ids")] List<int>? ActiveEpicIds);

public sealed record ManifestRepo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("producer_type")] string? ProducerType,
    [property: JsonPropertyName("lifecycle")] string? Lifecycle,
    [property: JsonPropertyName("aliases")] List<string>? Aliases,
    [property: JsonPropertyName("surfaces")] List<ManifestSurface>? Surfaces);

public sealed record ManifestSurface(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string? Kind);
