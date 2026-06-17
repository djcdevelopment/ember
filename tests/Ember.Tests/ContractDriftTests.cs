using System.Text.RegularExpressions;
using Ember.Reflect;
using Xunit;

namespace Ember.Tests;

/// <summary>
/// The drift guard for the experiment-corpus practice: the live prompt constants must match
/// their active contract files (whitespace-normalized). Edit a prompt in code without versioning
/// the contract — or vice-versa — and the build fails. Skips when the contracts checkout is
/// absent so CI elsewhere still passes.
/// </summary>
public class ContractDriftTests
{
    private const string ContractsRoot = @"D:\work\ember\contracts";

    [SkippableFact]
    public void Recap_prompt_matches_its_active_contract()
        => AssertMatches("recap-prompt/v2-xml-cite.md", RecapJudge.SystemPrompt);

    [SkippableFact]
    public void Comparer_prompt_matches_its_active_contract()
        => AssertMatches("comparer-prompt/v2-contradiction-or-omission.md", DivergenceComparer.SystemPrompt);

    private static void AssertMatches(string relPath, string codeConstant)
    {
        var path = Path.Combine(ContractsRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Skip.IfNot(File.Exists(path), $"contract not present at {path}");

        var body = StripFrontmatter(File.ReadAllText(path));
        Assert.Equal(Normalize(codeConstant), Normalize(body));
    }

    /// <summary>Drops a leading <c>--- ... ---</c> YAML frontmatter block if present.</summary>
    private static string StripFrontmatter(string text)
    {
        var m = Regex.Match(text, @"\A---.*?---\s*", RegexOptions.Singleline);
        return m.Success ? text[m.Length..] : text;
    }

    private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
