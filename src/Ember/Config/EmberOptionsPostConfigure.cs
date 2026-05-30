using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Ember.Config;

/// <summary>
/// Replaces the default-bound <see cref="EmberOptions.Repos"/> dictionary with one that
/// accepts both the legacy string shape and the v1.5 object shape. The standard configuration
/// binder turns <c>"ember": "D:\\work\\ember"</c> into a half-filled <see cref="RepoEntry"/>
/// (path empty), and turns <c>"tempo": { "path": "...", "constellation": "..." }</c> into a
/// fully-filled one. This post-configurator looks at the raw <c>IConfiguration</c> section,
/// detects which shape each child uses, and rebuilds the dictionary accordingly — so legacy
/// setups keep working while operators can opt into the manifest seam per repo.
/// </summary>
public sealed class EmberOptionsPostConfigure : IPostConfigureOptions<EmberOptions>
{
    private readonly IConfiguration _configuration;

    public EmberOptionsPostConfigure(IConfiguration configuration) => _configuration = configuration;

    public void PostConfigure(string? name, EmberOptions options)
    {
        var section = _configuration.GetSection($"{EmberOptions.Section}:Repos");
        if (!section.Exists())
            return;

        options.Repos = BindRepos(section);
    }

    /// <summary>
    /// Walks the <c>Ember:Repos</c> section once and parses each child as either a string
    /// (legacy) or an object (v1.5+). Keeps both shapes valid simultaneously so an operator
    /// can convert one entry at a time without breaking the others.
    /// </summary>
    public static Dictionary<string, RepoEntry> BindRepos(IConfigurationSection section)
    {
        var repos = new Dictionary<string, RepoEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            // Leaf with a Value but no nested children: legacy "name": "path" form.
            if (child.Value is { Length: > 0 } legacyPath && !child.GetChildren().Any())
            {
                repos[child.Key] = new RepoEntry { Path = legacyPath };
                continue;
            }

            // Otherwise expect the object shape — the binder fills the record's properties.
            var entry = new RepoEntry();
            child.Bind(entry);
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue; // skip entries with no resolvable path rather than failing config bind
            repos[child.Key] = entry;
        }
        return repos;
    }
}
