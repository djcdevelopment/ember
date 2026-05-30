using System.IO;
using System.Linq;
using System.Text;
using Ember.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// Slice B keeps the legacy <c>"name": "absolute_path"</c> shape parsing as before AND adds
/// the <c>"name": { "path", "constellation" }</c> shape — these tests pin both. The mixed
/// case is the important one: many appsettings.json files will have one new-style entry next
/// to several legacy ones, and the configurator must handle that without operator effort.
/// </summary>
public class OptionsBindingTests
{
    [Fact]
    public void Legacy_string_shape_parses_with_null_constellation()
    {
        var repos = LoadRepos("""
        {
          "Ember": {
            "Repos": {
              "ember": "D:\\work\\ember"
            }
          }
        }
        """);

        Assert.Single(repos);
        Assert.True(repos.ContainsKey("ember"));
        Assert.Equal("D:\\work\\ember", repos["ember"].Path);
        Assert.Null(repos["ember"].Constellation);
    }

    [Fact]
    public void Object_shape_parses_both_path_and_constellation()
    {
        var repos = LoadRepos("""
        {
          "Ember": {
            "Repos": {
              "tempo": {
                "path": "D:\\World of Warcraft\\Tempo",
                "constellation": "D:\\work\\gad\\constellation.yaml"
              }
            }
          }
        }
        """);

        Assert.Single(repos);
        var tempo = repos["tempo"];
        Assert.Equal("D:\\World of Warcraft\\Tempo", tempo.Path);
        Assert.Equal("D:\\work\\gad\\constellation.yaml", tempo.Constellation);
    }

    [Fact]
    public void Object_shape_without_constellation_parses_with_null()
    {
        var repos = LoadRepos("""
        {
          "Ember": {
            "Repos": {
              "ember": { "path": "D:\\work\\ember" }
            }
          }
        }
        """);

        Assert.Equal("D:\\work\\ember", repos["ember"].Path);
        Assert.Null(repos["ember"].Constellation);
    }

    [Fact]
    public void Mixed_shapes_in_the_same_config_both_parse()
    {
        var repos = LoadRepos("""
        {
          "Ember": {
            "Repos": {
              "ember": "D:\\work\\ember",
              "tempo": {
                "path": "D:\\World of Warcraft\\Tempo",
                "constellation": "D:\\work\\gad\\constellation.yaml"
              }
            }
          }
        }
        """);

        Assert.Equal(2, repos.Count);
        Assert.Equal("D:\\work\\ember", repos["ember"].Path);
        Assert.Null(repos["ember"].Constellation);
        Assert.Equal("D:\\World of Warcraft\\Tempo", repos["tempo"].Path);
        Assert.Equal("D:\\work\\gad\\constellation.yaml", repos["tempo"].Constellation);
    }

    [Fact]
    public void Repos_keys_are_case_insensitive()
    {
        var repos = LoadRepos("""
        {
          "Ember": {
            "Repos": {
              "Tempo": "D:\\World of Warcraft\\Tempo"
            }
          }
        }
        """);

        Assert.True(repos.ContainsKey("tempo"));
        Assert.True(repos.ContainsKey("TEMPO"));
    }

    [Fact]
    public void Object_entry_with_no_path_is_dropped()
    {
        // No path means the entry can never be the target of /plan — better to drop it than
        // to leave a half-bound record around. The legacy shape can't hit this branch.
        var repos = LoadRepos("""
        {
          "Ember": {
            "Repos": {
              "broken": { "constellation": "D:\\somewhere\\constellation.yaml" }
            }
          }
        }
        """);

        Assert.Empty(repos);
    }

    [Fact]
    public void Bound_via_IOptions_yields_same_result_as_direct_call()
    {
        // End-to-end through Options binding so we know Program.cs's wiring (Configure +
        // IPostConfigureOptions) actually produces the same result the static helper does.
        var json = """
        {
          "Ember": {
            "Repos": {
              "ember": "D:\\work\\ember",
              "tempo": {
                "path": "D:\\World of Warcraft\\Tempo",
                "constellation": "D:\\work\\gad\\constellation.yaml"
              }
            }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = new EmberOptions();
        config.GetSection(EmberOptions.Section).Bind(options);
        new EmberOptionsPostConfigure(config).PostConfigure(Options.DefaultName, options);

        Assert.Equal(2, options.Repos.Count);
        Assert.Equal("D:\\work\\ember", options.Repos["ember"].Path);
        Assert.Equal("D:\\work\\gad\\constellation.yaml", options.Repos["tempo"].Constellation);
    }

    private static Dictionary<string, RepoEntry> LoadRepos(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return EmberOptionsPostConfigure.BindRepos(config.GetSection($"{EmberOptions.Section}:Repos"));
    }
}
