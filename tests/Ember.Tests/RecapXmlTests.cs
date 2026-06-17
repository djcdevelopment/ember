using Ember.Reflect;
using Xunit;

namespace Ember.Tests;

/// <summary>The recap XML render + the citation-grounding score (the near-free trust signal).</summary>
public class RecapXmlTests
{
    private const string Sample =
        """
        <recap>
          <repo name="leopard">
            <claim><statement>Shipped the trend lens.</statement><from>3d4999f</from></claim>
            <claim><statement>Added the Night Lens.</statement><from>58823f9</from></claim>
          </repo>
          <threads><thread>ember and leopard may integrate.</thread></threads>
          <open-questions><question>Is it tested?</question></open-questions>
        </recap>
        """;

    [Fact]
    public void Render_turns_recap_xml_into_markdown()
    {
        var md = RecapXml.Render(Sample);

        Assert.Contains("**leopard**", md);
        Assert.Contains("- Shipped the trend lens. (`3d4999f`)", md);
        Assert.Contains("**Threads & risks**", md);
        Assert.Contains("**Open questions**", md);
        Assert.DoesNotContain("<recap>", md);
    }

    [Fact]
    public void Render_falls_back_to_raw_when_not_xml()
    {
        Assert.Equal("just plain markdown", RecapXml.Render("just plain markdown"));
    }

    [Fact]
    public void CountCitations_counts_all_valid_when_every_cite_is_in_evidence()
    {
        var evidence = "leopard: 3d4999f the trend lens; 58823f9 night lens.";
        var (valid, total) = RecapXml.CountCitations(Sample, evidence);
        Assert.Equal(2, total);
        Assert.Equal(2, valid);
    }

    [Fact]
    public void CountCitations_flags_a_fabricated_citation()
    {
        var evidence = "leopard: 3d4999f only this one is real.";
        var (valid, total) = RecapXml.CountCitations(Sample, evidence);
        Assert.Equal(2, total);
        Assert.Equal(1, valid); // 58823f9 is absent from the evidence -> fabricated
    }
}
