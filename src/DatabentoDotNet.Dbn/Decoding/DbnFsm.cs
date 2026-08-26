using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The incremental DBN decoder: a sans-I/O state machine that turns a byte stream arriving in
/// arbitrary-sized pieces into metadata and records, without ever touching a
/// <see cref="Stream"/> or a socket itself.
/// </summary>
/// <remarks>
/// <para>
/// The port of upstream's <c>DbnFsm</c> (<c>decode/dbn/fsm.rs</c>). The caller owns the I/O: it
/// writes bytes into the span <see cref="Space"/> returns, tells the machine how many with
/// <see cref="Fill"/>, and pulls records out with <see cref="TryNextRecord"/>. A TCP socket
/// delivers a DBN stream in pieces that have nothing to do with record boundaries — a single byte
/// is a perfectly ordinary read — which is the entire reason this is a state machine and not a
/// loop over a reader.
/// </para>
/// <para>
/// <b>Three states, not upstream's four.</b> Upstream has <c>Prelude</c>,
/// <c>Metadata { length }</c>, <c>Record</c>, and a fourth, <c>Consume { read, compat,
/// compat_fill, expand_compat }</c>, whose own doc comment says it exists to "get around
/// mutability requirements" (<c>fsm.rs:62</c>). It models nothing about DBN: it defers the buffer
/// index advance to the <em>next</em> call so that Rust's borrow checker will let the just-decoded
/// record still be read through <c>last_record()</c>. C# has no such restriction, so at each of
/// the three points where upstream constructs <c>State::Consume</c> this port performs the
/// advance inline and hands the record back from the same call. Nothing replaces the state.
/// </para>
/// <para>
/// <b>Why consuming immediately is safe.</b> <see cref="AlignedBuffer.Consume"/> and
/// <see cref="AlignedBuffer.Fill"/> move indices only — they never copy or clear a byte. So a
/// span captured over the record before the advance still points at the same untouched bytes
/// after it.
/// </para>
/// <para>
/// <b>A record is only valid until the next call on this machine.</b> Read it, or copy what you
/// need out of it, before calling anything else here. Two things end its life, and they are not
/// the same thing:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="Space"/> may <see cref="AlignedBuffer.Shift"/> the read buffer's unconsumed tail
/// down to offset 0, moving the bytes a read-buffer-backed record points at.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>An upgraded record lives in the compat buffer, and the next upgraded record overwrites
/// it.</b> The compat buffer is a single-record scratchpad: it is reset to offset 0 as each
/// upgraded record is handed out, so the next one is written straight over the previous one's
/// bytes. No shift is involved and no memory is freed — the bytes simply become a different
/// record. On a v1 or v2 definition or statistics stream, holding two records across two
/// <see cref="TryNextRecord"/> calls therefore gives two references to the same, latest record.
/// </description>
/// </item>
/// </list>
/// <para>
/// This is the same one-record-at-a-time contract upstream enforces through Rust's borrow
/// checker, which will not let a <c>RecordRef</c> from <c>last_record()</c> outlive the next
/// <c>&amp;mut self</c> call. C# cannot enforce it, so it is stated here instead;
/// <see cref="RecordRef"/> being a <c>ref struct</c> narrows the blast radius by making a record
/// impossible to box, store in a field, or capture in a closure, but it does not stop a caller
/// holding two of them in the same method. Callers that genuinely need several records at once
/// must copy the bytes out, which is what the decoder tests do.
/// </para>
/// <para>
/// <b>No async here, on purpose.</b> <see cref="TryNextRecord"/> is synchronous and
/// <see cref="RecordRef"/> is a <c>ref struct</c>, so neither can cross an <c>await</c>. The
/// asynchronous I/O layer belongs above this type and calls <see cref="Fill"/> itself.
/// </para>
/// </remarks>
public sealed class DbnFsm
{
    /// <summary>The default read-buffer size: 64 KiB, matching upstream's <c>DEFAULT_BUF_SIZE</c>.</summary>
    public const int DefaultBufferSize = AlignedBuffer.DefaultCapacity;

    private readonly AlignedBuffer _buffer;
    private readonly AlignedBuffer _compatBuffer;
    private readonly VersionUpgradePolicy _upgradePolicy;
    private readonly byte? _configuredVersion;
    private readonly bool _configuredTsOut;
    private readonly bool _skipMetadata;

    private State _state;
    private int _metadataLength;
    private byte? _inputVersion;
    private bool _needsUpgrade;
    private bool _tsOut;

    /// <summary>
    /// Creates a state machine.
    /// </summary>
    /// <param name="upgradePolicy">
    /// How to present records from an older DBN version. The default matches upstream's and
    /// converts v1 and v2 records to v3 as they are decoded.
    /// </param>
    /// <param name="skipMetadata">
    /// <see langword="true"/> to start directly in the record state, for a DBN <em>fragment</em>:
    /// a bare run of records with no magic prelude and no metadata block.
    /// </param>
    /// <param name="inputDbnVersion">
    /// The DBN version of the input, when it is known ahead of time. Only meaningful together
    /// with <paramref name="skipMetadata"/> — otherwise the metadata block states the version and
    /// overwrites this. When it is <see langword="null"/> and records need upgrading, the version
    /// is inferred from each record's size, exactly as upstream does.
    /// </param>
    /// <param name="tsOut">
    /// Whether every record carries an appended 8-byte <c>ts_out</c> send timestamp. Only
    /// meaningful together with <paramref name="skipMetadata"/>; otherwise the metadata says.
    /// </param>
    /// <param name="bufferSize">
    /// The read buffer's size in bytes. Never smaller than
    /// <see cref="DbnConstants.MaxRecordLength"/> — <see cref="AlignedBuffer"/> enforces that
    /// floor — because a buffer that cannot hold the largest possible record could never present
    /// one contiguously.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="upgradePolicy"/> is not a defined value, <paramref name="inputDbnVersion"/>
    /// is outside the versions this codec decodes, or <paramref name="bufferSize"/> is negative.
    /// </exception>
    /// <exception cref="DbnDecodeException">
    /// <paramref name="inputDbnVersion"/> and <paramref name="upgradePolicy"/> are incompatible —
    /// asking to upgrade a v3 stream to v2, which is a downgrade.
    /// </exception>
    public DbnFsm(
        VersionUpgradePolicy upgradePolicy = VersionUpgradePolicy.UpgradeToV3,
        bool skipMetadata = false,
        byte? inputDbnVersion = null,
        bool tsOut = false,
        int bufferSize = DefaultBufferSize)
    {
        ValidateDefined(upgradePolicy);

        if (inputDbnVersion is byte version)
        {
            if (version == 0 || version > DbnConstants.Version)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inputDbnVersion),
                    version,
                    $"This codec decodes DBN versions 1 through {DbnConstants.Version}.");
            }

            ValidateCompatibility(upgradePolicy, version);
        }

        _upgradePolicy = upgradePolicy;
        _configuredVersion = inputDbnVersion;
        _configuredTsOut = tsOut;
        _skipMetadata = skipMetadata;
        _buffer = new AlignedBuffer(bufferSize);

        // Fixed, never grown. Upstream starts the compat buffer at zero and doubles it on demand
        // (`double_compat_buffer`, fsm.rs:402-405) because it allocates exactly what it is asked
        // for. This port's AlignedBuffer floors every capacity at MaxRecordLength, and
        // MaxRecordLength is by definition the largest record that can exist — a v3
        // InstrumentDefMsg plus ts_out — so an upgraded record can never fail to fit and
        // upstream's grow-and-retry loop is unreachable here.
        _compatBuffer = new AlignedBuffer(DbnConstants.MaxRecordLength);

        ResetState();
    }

    private enum State
    {
        /// <summary>Waiting for the 8-byte magic prelude.</summary>
        Prelude,

        /// <summary>
        /// Waiting for <see cref="_metadataLength"/> more bytes of metadata. Upstream's
        /// <c>Metadata { length }</c> carries the length in the enum variant; C# enums carry no
        /// payload, so it lives in a field beside the state.
        /// </summary>
        Metadata,

        /// <summary>Waiting for, or decoding, the next record.</summary>
        Record,
    }

    /// <summary>
    /// The stream's decoded metadata, or <see langword="null"/> until it has been decoded — which
    /// it never is for a fragment.
    /// </summary>
    /// <remarks>
    /// Already presented according to the upgrade policy, so a v1 stream decoded under
    /// <see cref="VersionUpgradePolicy.UpgradeToV3"/> reports version 3 here. The <em>input</em>
    /// version, which is what drives record upgrades, is <see cref="InputDbnVersion"/>.
    /// </remarks>
    public Metadata? Metadata { get; private set; }

    /// <summary>
    /// <see langword="true"/> once the metadata block has been decoded, or immediately when the
    /// machine was told to skip it.
    /// </summary>
    public bool HasDecodedMetadata => _state == State.Record;

    /// <summary>
    /// The DBN version of the input as currently known: from the prelude, from the constructor,
    /// or inferred from a record's size. <see langword="null"/> when still unknown.
    /// </summary>
    public byte? InputDbnVersion => _inputVersion;

    /// <summary>The upgrade policy this machine applies to records from older DBN versions.</summary>
    public VersionUpgradePolicy UpgradePolicy => _upgradePolicy;

    /// <summary>Whether every record on this stream carries an appended 8-byte <c>ts_out</c>.</summary>
    public bool TsOut => _tsOut;

    /// <summary>
    /// The writable tail of the read buffer. Write bytes here, then call <see cref="Fill"/> with
    /// how many.
    /// </summary>
    /// <returns>
    /// A span of at least <see cref="DbnConstants.MaxRecordLength"/> bytes whenever the buffer's
    /// capacity allows, reclaiming the consumed prefix first if it has to.
    /// </returns>
    /// <remarks>
    /// <b>This is the one call that can move buffered bytes,</b> because it may shift the
    /// unconsumed tail down to offset 0 to make room. Any <see cref="RecordRef"/> obtained before
    /// it is stale afterwards. Reclaiming here rather than after every record is deliberate:
    /// <see cref="AlignedBuffer.Consume"/> only moves an index, so the memory move is paid once
    /// per refill instead of once per record.
    /// </remarks>
    public Span<byte> Space()
    {
        _buffer.ShiftForSpace(DbnConstants.MaxRecordLength);
        return _buffer.Space;
    }

    /// <summary>
    /// Records that <paramref name="nbytes"/> bytes were written into the span the last
    /// <see cref="Space"/> call returned.
    /// </summary>
    /// <param name="nbytes">How many bytes were written. Capped at the space actually available.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nbytes"/> is negative.</exception>
    public void Fill(int nbytes) => _buffer.Fill(nbytes);

    /// <summary>
    /// Decodes the next record, if the buffered bytes hold a complete one.
    /// </summary>
    /// <param name="record">
    /// Receives the decoded record. <b>Valid only until the next call on this machine</b> — read
    /// it, or copy what you need out of it, before calling anything else here. An upgraded record
    /// in particular is overwritten by the next upgraded record, without any shift; see the
    /// remarks on <see cref="DbnFsm"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a record was decoded. <see langword="false"/> means "not enough
    /// bytes yet" — write more into <see cref="Space"/>, <see cref="Fill"/>, and call again. A
    /// stream that has ended simply keeps returning <see langword="false"/>; that is not an error.
    /// </returns>
    /// <exception cref="DbnDecodeException">The buffered bytes are not valid DBN.</exception>
    /// <remarks>
    /// Metadata decoded along the way is not returned here — it lands in <see cref="Metadata"/>
    /// and the machine carries straight on to the first record.
    /// </remarks>
    public bool TryNextRecord(out RecordRef record)
    {
        while (true)
        {
            switch (Process(out _, out record))
            {
                case ProcessStatus.Record:
                    return true;

                case ProcessStatus.Metadata:
                    continue;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Advances the state machine by one step: decodes the metadata block, or one record, or
    /// reports that more bytes are needed.
    /// </summary>
    /// <param name="bytesNeeded">
    /// When the result is <see cref="ProcessStatus.NeedMoreData"/>, how many more bytes are known
    /// to be required before this step can complete; zero otherwise. A hint for sizing the next
    /// read, not a contract — supplying fewer just means another
    /// <see cref="ProcessStatus.NeedMoreData"/>, and supplying more is fine.
    /// </param>
    /// <param name="record">
    /// When the result is <see cref="ProcessStatus.Record"/>, the decoded record; otherwise
    /// <see langword="default"/>. <b>Valid only until the next call on this machine</b> — an
    /// upgraded record is overwritten by the next upgraded record, without any shift; see the
    /// remarks on <see cref="DbnFsm"/>.
    /// </param>
    /// <returns>What this step produced.</returns>
    /// <exception cref="DbnDecodeException">The buffered bytes are not valid DBN.</exception>
    /// <remarks>
    /// The record comes back from the same call that decoded it rather than from a separate
    /// "last record" accessor. Upstream needs the accessor because its <c>State::Consume</c>
    /// defers the buffer advance across the call boundary; with the advance done inline there is
    /// no second call to hang the result off, and no window in which a caller could ask for "the
    /// last record" after the machine has moved on.
    /// </remarks>
    public ProcessStatus Process(out int bytesNeeded, out RecordRef record)
    {
        record = default;
        bytesNeeded = 0;

        while (true)
        {
            switch (_state)
            {
                case State.Prelude:
                {
                    var available = _buffer.AvailableData;
                    if (available < DbnConstants.MetadataPreludeLength)
                    {
                        // Pre-checked rather than caught: MetadataDecoder.DecodePrelude throws on
                        // a short buffer, and for a socket reader "not enough bytes yet" is the
                        // canonical expected outcome, which this codec spells with Try*, not with
                        // an exception.
                        bytesNeeded = DbnConstants.MetadataPreludeLength - available;
                        return ProcessStatus.NeedMoreData;
                    }

                    DecodePrelude();
                    continue;
                }

                case State.Metadata:
                {
                    var available = _buffer.AvailableData;
                    if (available < _metadataLength)
                    {
                        bytesNeeded = _metadataLength - available;
                        return ProcessStatus.NeedMoreData;
                    }

                    DecodeMetadata();
                    return ProcessStatus.Metadata;
                }

                default:
                    return ProcessRecord(out bytesNeeded, out record);
            }
        }
    }

    /// <summary>
    /// Returns the machine to its starting state — the prelude, or the first record when it was
    /// built to skip metadata — and discards every buffered byte and everything learned about the
    /// stream.
    /// </summary>
    /// <remarks>
    /// Buffer capacity is kept, so a reset costs no allocation. Unlike upstream's <c>reset()</c>,
    /// which always returns to the prelude state, this returns to whichever state the machine
    /// started in: resetting a fragment decoder into a prelude it will never see would leave it
    /// permanently stuck.
    /// </remarks>
    public void Reset()
    {
        _buffer.Reset();
        _compatBuffer.Reset();
        Metadata = null;
        ResetState();
    }

    /// <summary>
    /// Rejects an undefined <see cref="VersionUpgradePolicy"/> with an exhaustive switch rather
    /// than <c>Enum.IsDefined</c>, which reflects over the enum's metadata — the codec is
    /// reflection-free, and every other enum check in this library is written this way.
    /// </summary>
    private static void ValidateDefined(VersionUpgradePolicy upgradePolicy)
    {
        switch (upgradePolicy)
        {
            case VersionUpgradePolicy.AsIs:
            case VersionUpgradePolicy.UpgradeToV2:
            case VersionUpgradePolicy.UpgradeToV3:
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(upgradePolicy),
                    upgradePolicy,
                    "Undefined VersionUpgradePolicy.");
        }
    }

    private static bool IsUpgradeSituation(VersionUpgradePolicy policy, byte version) => policy switch
    {
        VersionUpgradePolicy.UpgradeToV2 => version < 2,
        VersionUpgradePolicy.UpgradeToV3 => version < 3,

        // AsIs.
        _ => false,
    };

    private static bool ComputeNeedsUpgrade(VersionUpgradePolicy policy, byte? version)
    {
        if (policy == VersionUpgradePolicy.AsIs)
        {
            return false;
        }

        // Conservative when the version is still unknown: assume an upgrade is needed, and let
        // the per-record size inference sort it out. Upstream does the same (fsm.rs:125-134).
        return version is byte v ? IsUpgradeSituation(policy, v) : true;
    }

    private static void ValidateCompatibility(VersionUpgradePolicy policy, byte version)
    {
        // Duplicated from MetadataDecoder's private check rather than shared, because the FSM has
        // to reject the combination at the prelude — as upstream does — rather than after
        // buffering an entire metadata block that was never going to be usable.
        if (version > 2 && policy == VersionUpgradePolicy.UpgradeToV2)
        {
            throw new DbnDecodeException(
                $"Invalid combination of VersionUpgradePolicy.UpgradeToV2 and input version {version}: " +
                "the policies only move forward. Use AsIs or UpgradeToV3.");
        }
    }

    private void ResetState()
    {
        _state = _skipMetadata ? State.Record : State.Prelude;
        _metadataLength = 0;
        _inputVersion = _configuredVersion;
        _tsOut = _configuredTsOut;
        _needsUpgrade = ComputeNeedsUpgrade(_upgradePolicy, _inputVersion);
    }

    private void DecodePrelude()
    {
        MetadataDecoder.DecodePrelude(_buffer.Data, out var version, out var length);
        ValidateCompatibility(_upgradePolicy, version);

        _inputVersion = version;
        _needsUpgrade = ComputeNeedsUpgrade(_upgradePolicy, version);
        _metadataLength = length;
        _state = State.Metadata;

        _buffer.Consume(DbnConstants.MetadataPreludeLength);

        // The whole metadata block has to be present at once — its variable-length sections
        // cannot be bounds-checked piecemeal — so make room for it now rather than discovering
        // mid-block that the buffer is too small.
        _buffer.Grow(length + DbnConstants.MetadataPreludeLength);
    }

    private void DecodeMetadata()
    {
        var metadata = MetadataDecoder.DecodeAfterPrelude(
            _buffer.Data[.._metadataLength],
            _inputVersion!.Value,
            _upgradePolicy);

        _tsOut = metadata.TsOut;
        Metadata = metadata;
        _buffer.Consume(_metadataLength);

        // Realign. The metadata block's length is whatever the variable-length sections summed
        // to, so the first record would otherwise start at an arbitrary offset — and records are
        // reinterpreted in place, which needs 8-byte alignment. Shifting to offset 0 restores it,
        // and every record size is a multiple of 8, so it stays restored from here on.
        _buffer.Shift();
        _state = State.Record;
    }

    private ProcessStatus ProcessRecord(out int bytesNeeded, out RecordRef record)
    {
        bytesNeeded = 0;
        record = default;

        var data = _buffer.Data;
        if (data.Length < RecordRef.HeaderLength)
        {
            bytesNeeded = RecordRef.HeaderLength - data.Length;
            return ProcessStatus.NeedMoreData;
        }

        // One byte is enough to know the whole record's length: `length` is a word count.
        var length = data[0] * DbnConstants.RecordLengthMultiplier;
        if (length < RecordRef.HeaderLength)
        {
            throw new DbnDecodeException(
                $"Invalid DBN record: the declared length {length} is shorter than the " +
                $"{RecordRef.HeaderLength}-byte header, which no record can be.");
        }

        if (length > DbnConstants.MaxRecordLength)
        {
            // Upstream has no equivalent check; its 64 KiB default buffer means an over-long
            // length merely asks for bytes that never arrive. Here it is worth rejecting
            // outright: MaxRecordLength is the largest record the format can express, so a longer
            // one is corrupt, and without this a small buffer would spin asking for space it can
            // never provide.
            throw new DbnDecodeException(
                $"Invalid DBN record: the declared length {length} exceeds the " +
                $"{DbnConstants.MaxRecordLength}-byte maximum record size.");
        }

        if (length > data.Length)
        {
            bytesNeeded = length - data.Length;
            return ProcessStatus.NeedMoreData;
        }

        var raw = data[..length];

        if (!_needsUpgrade)
        {
            record = new RecordRef(raw, _tsOut);

            // Upstream sets State::Consume { read: length, .. } here and returns; the advance
            // happens on the next call. Consume only moves an index, so doing it now leaves
            // `raw` pointing at the same untouched bytes.
            _buffer.Consume(length);
            return ProcessStatus.Record;
        }

        var written = UpgradeRecord(raw, _compatBuffer.Space);
        if (written == 0)
        {
            // No conversion applies to this rtype: the record passes through untouched and the
            // reference points into the read buffer, not the compat buffer. Upstream's
            // `upgrade_record_with_version` catch-all does the same (fsm.rs:886).
            record = new RecordRef(raw, _tsOut);
            _buffer.Consume(length);
            return ProcessStatus.Record;
        }

        _compatBuffer.Fill(written);
        record = new RecordRef(_compatBuffer.Data, _tsOut);

        // The second and third places upstream builds State::Consume, inlined: advance the read
        // buffer past the original record, then drain the compat buffer. Fill and Consume are
        // kept in lockstep so the buffer is provably empty before the reset, which is exactly
        // what upstream asserts (fsm.rs:541-544) before doing the same.
        //
        // The reset rewinds the compat buffer to offset 0, which is where the *next* upgraded
        // record will be written — straight over the one being handed out here. That is what
        // makes an upgraded record valid only until the next call on this machine, and it is a
        // plain overwrite, not a shift. Documented on the class and on both public entry points.
        _buffer.Consume(length);
        _compatBuffer.Consume(written);
        Debug.Assert(_compatBuffer.IsEmpty, "The compat buffer must be fully drained before it is reset.");
        _compatBuffer.Reset();
        return ProcessStatus.Record;
    }

    /// <summary>
    /// Writes an upgraded copy of <paramref name="source"/> into <paramref name="destination"/>,
    /// returning how many bytes it wrote, or 0 when no conversion applies to this record.
    /// </summary>
    private int UpgradeRecord(ReadOnlySpan<byte> source, Span<byte> destination)
        => _inputVersion is byte version
            ? UpgradeKnownVersion(version, source, destination)
            : UpgradeInferringVersion(source, destination);

    private int UpgradeKnownVersion(byte version, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        // Upstream's `upgrade_record_with_version` (fsm.rs:837-889), case for case. Every record
        // type not listed is byte-identical across versions and passes through untouched.
        switch (version, _upgradePolicy, (RType)source[1])
        {
            case (1, VersionUpgradePolicy.UpgradeToV2, RType.InstrumentDef):
                return Write(destination, Read<InstrumentDefMsgV1>(source).UpgradeToV2(), source);

            case (1, VersionUpgradePolicy.UpgradeToV3, RType.InstrumentDef):
                return Write(destination, Read<InstrumentDefMsgV1>(source).UpgradeTo(), source);

            case (2, VersionUpgradePolicy.UpgradeToV3, RType.InstrumentDef):
                return Write(destination, Read<InstrumentDefMsgV2>(source).UpgradeTo(), source);

            // StatMsg is unchanged between v1 and v2, so one source struct covers both.
            case (1 or 2, VersionUpgradePolicy.UpgradeToV3, RType.Statistics):
                return Write(destination, Read<StatMsgV1>(source).UpgradeTo(), source);

            // These three changed in v2 and not again in v3, so both policies land on the same
            // target struct.
            case (1, VersionUpgradePolicy.UpgradeToV2 or VersionUpgradePolicy.UpgradeToV3, RType.SymbolMapping):
                return Write(destination, Read<SymbolMappingMsgV1>(source).UpgradeTo(), source);

            case (1, VersionUpgradePolicy.UpgradeToV2 or VersionUpgradePolicy.UpgradeToV3, RType.Error):
                return Write(destination, Read<ErrorMsgV1>(source).UpgradeTo(), source);

            case (1, VersionUpgradePolicy.UpgradeToV2 or VersionUpgradePolicy.UpgradeToV3, RType.System):
                return Write(destination, Read<SystemMsgV1>(source).UpgradeTo(), source);

            default:
                return 0;
        }
    }

    /// <summary>
    /// Upgrades a record when the input version is not known — a fragment stream with no metadata
    /// and no version supplied — by inferring the version from the record's size.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>upgrade_record_detect_version</c> (<c>fsm.rs:898-961</c>). The inference is
    /// "smaller than the current version's struct implies an older version", and the sizes it
    /// compares against are the record's <em>full</em> on-wire size, <c>ts_out</c> included —
    /// which still lands on the right answer because the version-to-version size steps are all
    /// far larger than 8 bytes.
    /// <para>
    /// Two invariants are ported deliberately: this only ever concludes "version 1" or
    /// "version 2", never "already current", and it does not recompute <c>needsUpgrade</c>
    /// afterwards — which is why it may only write an <em>older</em> version.
    /// </para>
    /// </remarks>
    private int UpgradeInferringVersion(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var size = source.Length;
        switch ((RType)source[1], _upgradePolicy)
        {
            case (RType.InstrumentDef, VersionUpgradePolicy.UpgradeToV2)
                when size < InstrumentDefMsgV2.WireSize:
                _inputVersion = 1;
                return Write(destination, Read<InstrumentDefMsgV1>(source).UpgradeToV2(), source);

            case (RType.InstrumentDef, VersionUpgradePolicy.UpgradeToV3)
                when size < InstrumentDefMsgV2.WireSize:
                _inputVersion = 1;
                return Write(destination, Read<InstrumentDefMsgV1>(source).UpgradeTo(), source);

            case (RType.InstrumentDef, VersionUpgradePolicy.UpgradeToV3)
                when size < InstrumentDefMsg.WireSize:
                _inputVersion = 2;
                return Write(destination, Read<InstrumentDefMsgV2>(source).UpgradeTo(), source);

            case (RType.Statistics, VersionUpgradePolicy.UpgradeToV3)
                when size < StatMsg.WireSize:
                // The input could be v1 or v2. It makes no difference to StatMsg but it does to
                // InstrumentDefMsg, so deliberately leave the version unrecorded rather than
                // guess and mis-upgrade a later definition.
                return Write(destination, Read<StatMsgV1>(source).UpgradeTo(), source);

            case (RType.SymbolMapping, VersionUpgradePolicy.UpgradeToV2 or VersionUpgradePolicy.UpgradeToV3)
                when size < SymbolMappingMsg.WireSize:
                _inputVersion = 1;
                return Write(destination, Read<SymbolMappingMsgV1>(source).UpgradeTo(), source);

            case (RType.Error, VersionUpgradePolicy.UpgradeToV2 or VersionUpgradePolicy.UpgradeToV3)
                when size < ErrorMsg.WireSize:
                _inputVersion = 1;
                return Write(destination, Read<ErrorMsgV1>(source).UpgradeTo(), source);

            case (RType.System, VersionUpgradePolicy.UpgradeToV2 or VersionUpgradePolicy.UpgradeToV3)
                when size < SystemMsg.WireSize:
                _inputVersion = 1;
                return Write(destination, Read<SystemMsgV1>(source).UpgradeTo(), source);

            default:
                return 0;
        }
    }

    /// <summary>
    /// Reinterprets <paramref name="source"/> as the older record struct an upgrade reads from,
    /// after checking it really is exactly that size.
    /// </summary>
    private ref readonly T Read<T>(ReadOnlySpan<byte> source)
        where T : unmanaged, IRecord<T>
    {
        var expected = T.WireSize + (_tsOut ? sizeof(ulong) : 0);
        if (source.Length != expected)
        {
            throw new DbnDecodeException(
                $"Malformed {typeof(T).Name} record: expected exactly {expected} bytes " +
                $"(a {T.WireSize}-byte record{(_tsOut ? " plus ts_out" : string.Empty)}) but the header declares " +
                $"{source.Length}.");
        }

        return ref MemoryMarshal.AsRef<T>(source);
    }

    /// <summary>
    /// Writes an upgraded record — re-wrapping its <c>ts_out</c> when the stream carries one —
    /// into the compat buffer, and returns the number of bytes written.
    /// </summary>
    /// <remarks>
    /// The upgrade is a value-level conversion into a <em>different, larger</em> struct, never an
    /// in-place reinterpret, which is why the compat buffer is sized for the target version and
    /// not the source. The conversion itself recomputes the header's length word from the target
    /// type's size, so the wire length grows with the struct rather than being carried over.
    /// </remarks>
    private int Write<T>(Span<byte> destination, in T value, ReadOnlySpan<byte> source)
        where T : unmanaged, IRecord<T>
    {
        if (!_tsOut)
        {
            var size = Unsafe.SizeOf<T>();
            RequireCompatRoom(size, destination.Length);
            MemoryMarshal.Write(destination, in value);
            return size;
        }

        // `Read<T>` has already pinned `source.Length` at the source struct's size plus 8, so the
        // trailing eight bytes are the timestamp and nothing else.
        var tsOut = BinaryPrimitives.ReadUInt64LittleEndian(source[^sizeof(ulong)..]);
        var wrapped = new WithTsOut<T>(value, tsOut);
        var wrappedSize = Unsafe.SizeOf<WithTsOut<T>>();
        RequireCompatRoom(wrappedSize, destination.Length);
        MemoryMarshal.Write(destination, in wrapped);
        return wrappedSize;
    }

    private static void RequireCompatRoom(int needed, int available)
    {
        // Defensive only: the compat buffer is MaxRecordLength bytes and MaxRecordLength is the
        // largest record the format can express, so this cannot fire for any record type that
        // exists. It is here so that adding a larger record type in future fails with a sentence
        // rather than an ArgumentOutOfRangeException from deep inside MemoryMarshal.
        if (needed > available)
        {
            throw new DbnDecodeException(
                $"The upgrade buffer holds {available} bytes but the upgraded record needs {needed}.");
        }
    }
}
