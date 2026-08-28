using System.Globalization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>The parameter set <c>batch.submit_job</c> takes.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>SubmitJobParams</c> (<c>batch.rs:436-498</c>). Every default here is
/// upstream's builder default, so a job submitted with only the four
/// <see langword="required"/> properties set is the same job upstream's
/// <c>SubmitJobParams::builder()</c> would submit.
/// </para>
/// <para>
/// <b>Submitting a job costs money</b>, which is what separates this parameter set from every other
/// one in this library. <see cref="ToQuery"/> narrows it to the billing endpoints' parameters so
/// the price can be asked for first; see <see cref="MetadataClient.GetCostAsync"/>.
/// </para>
/// <para>
/// <b>Where the validation lives is decided by how many properties a rule reads.</b>
/// <see cref="SplitSize"/> and <see cref="Limit"/> are checked in their own initializers, because
/// each rule reads one value. The other three rules — <see cref="SplitSymbols"/> against
/// <see cref="Symbols"/>, and the two <c>pretty_*</c> flags against <see cref="Encoding"/> —
/// each read two, and an <see langword="init"/> accessor cannot: object-initializer order is the
/// caller's to choose, so whichever property is assigned first would be validated against the
/// other's default rather than against the value the caller went on to set. Those three are
/// checked in <see cref="ToFormParameters"/>, which is the first moment the object is complete.
/// </para>
/// </remarks>
public sealed record SubmitJobParams
{
    private readonly ulong? _limit;
    private readonly ulong? _splitSize;

    /// <summary>The smallest <see cref="SplitSize"/> the API accepts: 1 GB.</summary>
    public const ulong MinimumSplitSize = 1_000_000_000;

    /// <summary>The largest <see cref="SplitSize"/> the API accepts: 10 GB.</summary>
    public const ulong MaximumSplitSize = 10_000_000_000;

    /// <summary>The dataset code, for example <c>GLBX.MDP3</c>.</summary>
    public required string Dataset { get; init; }

    /// <summary>The symbols to include.</summary>
    public required Symbols Symbols { get; init; }

    /// <summary>The record schema to produce.</summary>
    public required Schema Schema { get; init; }

    /// <summary>The request range: inclusive start, exclusive end.</summary>
    /// <remarks>
    /// The same convention <c>timeseries.get_range</c> uses, which #38 probed against the live API
    /// — see <see cref="GetRangeParams.DateTimeRange"/>. Upstream documents the batch endpoint's
    /// range identically (<c>batch.rs:450-452</c>), including that the filter is on <c>ts_recv</c>
    /// where the schema has one and on <c>ts_event</c> otherwise.
    /// </remarks>
    public required DateTimeRange DateTimeRange { get; init; }

    /// <summary>The encoding to write the files in. Defaults to <see cref="Encoding.Dbn"/>.</summary>
    /// <remarks>
    /// Unlike <see cref="GetRangeParams"/>, this is a real choice: a batch job's output is files on
    /// disk rather than a stream this library decodes, so CSV and JSON are as usable as DBN. That
    /// is also why <see cref="PrettyPx"/>, <see cref="PrettyTs"/> and <see cref="MapSymbols"/>
    /// exist at all — none of them means anything for a binary encoding.
    /// </remarks>
    public Encoding Encoding { get; init; } = Encoding.Dbn;

    /// <summary>The compression to write the files with. Defaults to <see cref="Compression.Zstd"/>.</summary>
    public Compression Compression { get; init; } = Compression.Zstd;

    /// <summary>
    /// Whether to write prices at their true scale rather than as fixed-precision integers.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Meaningful only for <see cref="Encoding.Csv"/> and <see cref="Encoding.Json"/>;
    /// <see cref="ToFormParameters"/> refuses it with <see cref="Encoding.Dbn"/>.
    /// </remarks>
    public bool PrettyPx { get; init; }

    /// <summary>
    /// Whether to write timestamps as ISO 8601 strings rather than as nanoseconds. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Meaningful only for <see cref="Encoding.Csv"/> and <see cref="Encoding.Json"/>;
    /// <see cref="ToFormParameters"/> refuses it with <see cref="Encoding.Dbn"/>.
    /// </remarks>
    public bool PrettyTs { get; init; }

    /// <summary>
    /// Whether to write a symbol field with each text-encoded record, or <see langword="null"/> to
    /// let the encoding decide.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is the default and is not the same as <see langword="false"/>: it
    /// renders as <see langword="true"/> for a text encoding and <see langword="false"/> for
    /// <see cref="Encoding.Dbn"/>, which is upstream's <c>unwrap_or(encoding != Encoding::Dbn)</c>
    /// (<c>batch.rs:76-82</c>). The field is always sent, so the choice is made here rather than
    /// left to the API's own default.
    /// </remarks>
    public bool? MapSymbols { get; init; }

    /// <summary>
    /// Whether to split the output into one file per raw symbol. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Cannot be combined with <see cref="DatabentoDotNet.Symbols.All"/> — there is no list of
    /// raw symbols to split by. <see cref="ToFormParameters"/> refuses the combination.
    /// </remarks>
    public bool SplitSymbols { get; init; }

    /// <summary>
    /// The interval to split the output at. Defaults to <see cref="SplitDuration.Day"/>, as
    /// upstream's builder does.
    /// </summary>
    public SplitDuration SplitDuration { get; init; } = SplitDuration.Day;

    /// <summary>
    /// The size in bytes to split each file at, or <see langword="null"/> for no size-based split.
    /// </summary>
    /// <remarks>
    /// Upstream's type is <c>Option&lt;NonZeroU64&gt;</c> and its doc comment gives the range —
    /// "an integer between 1e9 and 10e9 inclusive (1GB - 10GB)" (<c>batch.rs:476-478</c>) — but
    /// nothing in upstream enforces it, so a value outside the range becomes a round trip and a
    /// rejection. The bound is checked here instead, which is also what makes the non-zero
    /// constraint C# has no type for redundant: zero is below
    /// <see cref="MinimumSplitSize"/> anyway.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside <see cref="MinimumSplitSize"/>–<see cref="MaximumSplitSize"/>.
    /// </exception>
    public ulong? SplitSize
    {
        get => _splitSize;
        init
        {
            if (value is { } size && (size < MinimumSplitSize || size > MaximumSplitSize))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"A split size must be between {MinimumSplitSize} and {MaximumSplitSize} bytes "
                    + "(1 GB to 10 GB) inclusive; leave it unset for no size-based split.");
            }

            _splitSize = value;
        }
    }

    /// <summary>How to deliver the files. Defaults to <see cref="Delivery.Download"/>.</summary>
    public Delivery Delivery { get; init; } = Delivery.Download;

    /// <summary>
    /// The symbology <see cref="Symbols"/> is expressed in. Defaults to
    /// <see cref="SType.RawSymbol"/>.
    /// </summary>
    public SType StypeIn { get; init; } = SType.RawSymbol;

    /// <summary>
    /// The symbology the output names instruments in. Defaults to
    /// <see cref="SType.InstrumentId"/>.
    /// </summary>
    public SType StypeOut { get; init; } = SType.InstrumentId;

    /// <summary>
    /// The maximum number of records, or <see langword="null"/> for no limit, in which case the
    /// field is omitted rather than sent empty.
    /// </summary>
    /// <remarks>
    /// Zero is refused for the reason <see cref="GetRangeParams.Limit"/> gives at length: #38
    /// probed it, and the API's answer to <c>limit=0</c> contradicts itself — the body holds the
    /// data and the header warns that none was found.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero.</exception>
    public ulong? Limit
    {
        get => _limit;
        init
        {
            if (value == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "A limit of zero is not a limit; leave it unset instead. See "
                    + "GetRangeParams.Limit for what the API does with one.");
            }

            _limit = value;
        }
    }

    /// <summary>
    /// Narrows this to the parameters the three <c>metadata.*</c> billing endpoints take, so a
    /// caller can price exactly the job they are about to submit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same conversion <see cref="GetRangeParams.ToQuery"/> makes, and for a stronger reason:
    /// <c>timeseries.get_range</c> bills for what it streams, while a batch job bills for the whole
    /// range at once and cannot be interrupted part-way. Upstream has no equivalent and leaves its
    /// callers to build the billing object by hand.
    /// </para>
    /// <para>
    /// Drops <see cref="StypeOut"/> and every formatting and splitting option, none of which the
    /// billing endpoints take or could use — a price depends on how much data is in the range, not
    /// on how it is written out.
    /// </para>
    /// </remarks>
    /// <returns>The same request, priced rather than submitted.</returns>
    public MetadataQueryParams ToQuery() =>
        new()
        {
            Dataset = Dataset,
            Symbols = Symbols,
            Schema = Schema,
            DateTimeRange = DateTimeRange,
            StypeIn = StypeIn,
            Limit = Limit,
        };

    /// <summary>Renders this parameter set as the form body <c>batch.submit_job</c> posts.</summary>
    /// <remarks>
    /// The order is upstream's push order (<c>batch.rs:68-89</c>), which is neither the declaration
    /// order here nor upstream's own field order: the three optional fields are appended last
    /// because upstream adds them through separate <c>add_to_form</c> calls, and
    /// <c>split_duration</c> follows <c>split_size</c> for the same reason. It makes no difference
    /// to the API and it makes the rendered body byte-comparable with upstream's, which is the
    /// cheapest way to tell this rendering apart from a plausible one.
    /// </remarks>
    /// <returns>The form fields, in upstream's push order.</returns>
    /// <exception cref="InvalidOperationException">
    /// A combination the API rejects was requested — see this type's remarks for why these three
    /// rules are checked here rather than in an initializer — or <see cref="Symbols"/> or
    /// <see cref="DateTimeRange"/> was left at its type's default value.
    /// </exception>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormParameters()
    {
        Validate();

        var parameters = new List<KeyValuePair<string, string>>(15)
        {
            new("dataset", Dataset),
            new("schema", Schema.ToWireString()),
            new("encoding", Encoding.ToWireString()),
            new("compression", Compression.ToWireString()),
            new("pretty_px", Boolean(PrettyPx)),
            new("pretty_ts", Boolean(PrettyTs)),
            new("map_symbols", Boolean(MapSymbols ?? Encoding != Encoding.Dbn)),
            new("split_symbols", Boolean(SplitSymbols)),
            new("delivery", Delivery.ToWireString()),
            new("stype_in", StypeIn.ToWireString()),
            new("stype_out", StypeOut.ToWireString()),
            new("symbols", Symbols.ToApiString()),
            new("start", DateTimeRange.StartUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
            new("end", DateTimeRange.EndUnixNanoseconds.ToString(CultureInfo.InvariantCulture)),
        };

        if (Limit is { } limit)
        {
            parameters.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        }

        if (SplitSize is { } splitSize)
        {
            parameters.Add(new("split_size", splitSize.ToString(CultureInfo.InvariantCulture)));
        }

        parameters.Add(new("split_duration", SplitDuration.ToWireString()));

        return parameters;
    }

    /// <summary>
    /// Renders a flag the way upstream's <c>bool::to_string</c> does — <c>true</c> and
    /// <c>false</c>, lower case.
    /// </summary>
    /// <remarks>
    /// Not <see cref="bool.ToString()"/>, which is <c>True</c> and <c>False</c>. The difference is
    /// invisible in C# and load-bearing on the wire.
    /// </remarks>
    private static string Boolean(bool value) => value ? "true" : "false";

    private void Validate()
    {
        if (SplitSymbols && Symbols.Kind == SymbolsKind.All)
        {
            throw new InvalidOperationException(
                "Splitting by raw symbol needs a list of symbols to split by, and Symbols.All is "
                + "not one. Name the symbols, or leave SplitSymbols unset.");
        }

        if (Encoding == Encoding.Dbn && (PrettyPx || PrettyTs))
        {
            var flags = (PrettyPx, PrettyTs) switch
            {
                (true, true) => "PrettyPx and PrettyTs are",
                (true, false) => "PrettyPx is",
                _ => "PrettyTs is",
            };

            throw new InvalidOperationException(
                $"{flags} a text-encoding option, and this job is encoded as DBN, whose prices and "
                + "timestamps are fixed-width binary fields with no other spelling. Set Encoding to "
                + "Csv or Json, or leave the flag unset.");
        }
    }
}
