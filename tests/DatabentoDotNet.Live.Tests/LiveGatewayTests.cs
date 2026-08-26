using DatabentoDotNet.Dbn.Publishers;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="LiveGateway"/>, which turns a dataset code into the host to connect to.
/// </summary>
/// <remarks>
/// <para>
/// The transformation is three characters of code and it is never exercised until a real connect
/// against a real gateway, where getting it wrong looks like a DNS failure rather than like a
/// bug. So every <see cref="Dataset"/> the codec knows about is asserted, not a handful —
/// <see cref="EveryDatasetInTheEnum_IsInTheHostTable"/> is what stops the table drifting behind
/// the enum when the publisher tables are regenerated.
/// </para>
/// <para>
/// <b>What the table is and is not.</b> It was generated once from the enum's own wire strings by
/// a separate script and pasted here as literal data. It is therefore a frozen snapshot rather
/// than an independently reasoned expectation: its job is to make any future change to the
/// transformation visible across all fifty-two datasets at once. The independent check is
/// <see cref="For_TheExampleFromDatabentosDocumentation_Matches"/>, whose expected value comes
/// from Databento's own documentation and from upstream's doc comment.
/// </para>
/// </remarks>
public class LiveGatewayTests
{
    private static readonly Dictionary<Dataset, string> Hosts = new()
    {
        { Dataset.GlbxMdp3, "glbx-mdp3.lsg.databento.com" },
        { Dataset.XnasItch, "xnas-itch.lsg.databento.com" },
        { Dataset.XbosItch, "xbos-itch.lsg.databento.com" },
        { Dataset.XpsxItch, "xpsx-itch.lsg.databento.com" },
        { Dataset.BatsPitch, "bats-pitch.lsg.databento.com" },
        { Dataset.BatyPitch, "baty-pitch.lsg.databento.com" },
        { Dataset.EdgaPitch, "edga-pitch.lsg.databento.com" },
        { Dataset.EdgxPitch, "edgx-pitch.lsg.databento.com" },
        { Dataset.XnysPillar, "xnys-pillar.lsg.databento.com" },
        { Dataset.XcisPillar, "xcis-pillar.lsg.databento.com" },
        { Dataset.XasePillar, "xase-pillar.lsg.databento.com" },
        { Dataset.XchiPillar, "xchi-pillar.lsg.databento.com" },
        { Dataset.XcisBbo, "xcis-bbo.lsg.databento.com" },
        { Dataset.XcisTrades, "xcis-trades.lsg.databento.com" },
        { Dataset.MemxMemoir, "memx-memoir.lsg.databento.com" },
        { Dataset.EprlDom, "eprl-dom.lsg.databento.com" },
        { Dataset.FinnNls, "finn-nls.lsg.databento.com" },
        { Dataset.FinyTrades, "finy-trades.lsg.databento.com" },
        { Dataset.OpraPillar, "opra-pillar.lsg.databento.com" },
        { Dataset.DbeqBasic, "dbeq-basic.lsg.databento.com" },
        { Dataset.ArcxPillar, "arcx-pillar.lsg.databento.com" },
        { Dataset.IexgTops, "iexg-tops.lsg.databento.com" },
        { Dataset.EqusPlus, "equs-plus.lsg.databento.com" },
        { Dataset.XnysBbo, "xnys-bbo.lsg.databento.com" },
        { Dataset.XnysTrades, "xnys-trades.lsg.databento.com" },
        { Dataset.XnasQbbo, "xnas-qbbo.lsg.databento.com" },
        { Dataset.XnasNls, "xnas-nls.lsg.databento.com" },
        { Dataset.IfeuImpact, "ifeu-impact.lsg.databento.com" },
        { Dataset.NdexImpact, "ndex-impact.lsg.databento.com" },
        { Dataset.EqusAll, "equs-all.lsg.databento.com" },
        { Dataset.XnasBasic, "xnas-basic.lsg.databento.com" },
        { Dataset.EqusSummary, "equs-summary.lsg.databento.com" },
        { Dataset.XcisTradesbbo, "xcis-tradesbbo.lsg.databento.com" },
        { Dataset.XnysTradesbbo, "xnys-tradesbbo.lsg.databento.com" },
        { Dataset.EqusMini, "equs-mini.lsg.databento.com" },
        { Dataset.IfusImpact, "ifus-impact.lsg.databento.com" },
        { Dataset.IfllImpact, "ifll-impact.lsg.databento.com" },
        { Dataset.XeurEobi, "xeur-eobi.lsg.databento.com" },
        { Dataset.XeeeEobi, "xeee-eobi.lsg.databento.com" },
        { Dataset.XcbfPitch, "xcbf-pitch.lsg.databento.com" },
        { Dataset.OceaMemoir, "ocea-memoir.lsg.databento.com" },
        { Dataset.MainCgif, "main-cgif.lsg.databento.com" },
        { Dataset.EqusSip, "equs-sip.lsg.databento.com" },
        { Dataset.MsciCgif, "msci-cgif.lsg.databento.com" },
        { Dataset.FtseCgif, "ftse-cgif.lsg.databento.com" },
        { Dataset.InavCgif, "inav-cgif.lsg.databento.com" },
        { Dataset.MstarCgif, "mstar-cgif.lsg.databento.com" },
        { Dataset.CccyCgif, "cccy-cgif.lsg.databento.com" },
        { Dataset.CgiCgif, "cgi-cgif.lsg.databento.com" },
        { Dataset.XtksFlex, "xtks-flex.lsg.databento.com" },
        { Dataset.XtktItch, "xtkt-itch.lsg.databento.com" },
        { Dataset.XoseItch, "xose-itch.lsg.databento.com" },    };

    [Fact]
    public void For_TheExampleFromDatabentosDocumentation_Matches()
    {
        // GLBX.MDP3 -> glbx-mdp3.lsg.databento.com:13000, per ROADMAP.md §4 and upstream's
        // determine_gateway doc comment. The one value in this file not derived from the enum.
        var endPoint = LiveGateway.For("GLBX.MDP3");

        Assert.Equal("glbx-mdp3.lsg.databento.com", endPoint.Host);
        Assert.Equal(13_000, endPoint.Port);
    }

    [Theory]
    [MemberData(nameof(EveryDataset))]
    public void For_EveryKnownDataset_ProducesItsGatewayHost(Dataset dataset)
    {
        var endPoint = LiveGateway.For(dataset.ToWireString());

        Assert.Equal(Hosts[dataset], endPoint.Host);
        Assert.Equal(LiveGateway.DefaultPort, endPoint.Port);
    }

    [Fact]
    public void EveryDatasetInTheEnum_IsInTheHostTable()
    {
        var missing = Enum.GetValues<Dataset>().Where(dataset => !Hosts.ContainsKey(dataset)).ToList();

        Assert.True(
            missing.Count == 0,
            $"Datasets missing from the host table: {string.Join(", ", missing)}. Regenerating "
            + "the publisher tables added a dataset; add its host here so the transformation stays "
            + "asserted for all of them.");
        Assert.Equal(Enum.GetValues<Dataset>().Length, Hosts.Count);
    }

    [Fact]
    public void For_LowercasesAndReplacesEveryDot()
    {
        // A dataset code has one dot today. Asserting on two keeps the rule "every dot" rather
        // than "the dot", which is what a Replace-first-only implementation would satisfy.
        Assert.Equal("a-b-c.lsg.databento.com", LiveGateway.For("A.B.C").Host);
    }

    [Fact]
    public void For_DoesNotValidateAgainstTheDatasetEnum()
    {
        // Deliberate: Databento ships datasets faster than a generated enum tracks them, and
        // refusing to connect because our table is stale is worse than a DNS error. See the
        // remarks on LiveGateway.
        Assert.Equal("zzzz-notreal.lsg.databento.com", LiveGateway.For("ZZZZ.NOTREAL").Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void For_EmptyOrWhitespace_Throws(string dataset)
    {
        Assert.Throws<ArgumentException>(() => LiveGateway.For(dataset));
    }

    [Fact]
    public void For_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LiveGateway.For(null!));
    }

    [Theory]
    [InlineData("GLBX/MDP3")]     // a slash would silently graft a path onto the host
    [InlineData("GLBX MDP3")]     // a space cannot appear in a label
    [InlineData("GLBX_MDP3")]     // underscores are not legal in host names
    [InlineData("GLBX:MDP3")]     // a colon would read as a port
    [InlineData("GLBX.MDP3\n")]   // a newline is how a header injection starts
    [InlineData("GLBX.MDPÉ")]     // non-ASCII survives ToLowerInvariant and is not a label
    public void For_ADatasetThatCannotBecomeAHostLabel_Throws(string dataset)
    {
        var error = Assert.Throws<ArgumentException>(() => LiveGateway.For(dataset));

        Assert.Contains("DNS label", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".GLBX")]
    [InlineData("GLBX.")]
    public void For_ASubdomainStartingOrEndingWithAHyphen_Throws(string dataset)
    {
        var error = Assert.Throws<ArgumentException>(() => LiveGateway.For(dataset));

        Assert.Contains("begin or end with a hyphen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_ASubdomainLongerThanADnsLabel_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() => LiveGateway.For(new string('A', 64)));

        Assert.Contains("at most 63", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_ASubdomainExactlyAsLongAsADnsLabel_IsAccepted()
    {
        Assert.Equal(
            new string('a', 63) + ".lsg.databento.com",
            LiveGateway.For(new string('A', 63)).Host);
    }

    public static TheoryData<Dataset> EveryDataset()
    {
        var data = new TheoryData<Dataset>();
        foreach (var dataset in Enum.GetValues<Dataset>())
        {
            data.Add(dataset);
        }

        return data;
    }
}
