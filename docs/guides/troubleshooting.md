# Troubleshooting

**Error messages and compiler diagnostics, with what each one actually means.** Grouped by where
you hit them: building, connecting, streaming, or decoding.

If your problem is not here, [open an issue](https://github.com/jerbersoft/databentodotnet/issues/new/choose)
with the exception's full text and the call sequence that produced it.

---

## Compiler errors

These are the zero-copy lifetime rule doing its job. Full explanation in
[Zero-Copy and Allocation](zero-copy-and-allocation.md).

### CS4007 — cannot be preserved across an `await` or `yield` boundary

```csharp
while (client.TryNextRecord(out var record))
{
    await Handle(record);      // ✗ CS4007
}
```

A `RecordRef` points into a buffer that the next read will overwrite. The compiler is refusing to
let you hold it across a suspension point.

**Fix.** Copy out what you need before awaiting. `TryGet` gives you an ordinary struct with no
lifetime restrictions:

```csharp
if (record.TryGet(out TradeMsg trade))
{
    await Handle(trade);       // ✓ `trade` is a value
}
```

Or use `RecordsAsync()`, which yields heap copies, if you can afford ~110 bytes per record.

### CS8345 — field cannot be of type `RecordRef`

You tried to store a record in a field, a `List<>`, an array, or a tuple that outlives the loop.

**Fix.** Store `OwnedRecord.CopyOf(record)`, or the concrete struct from `TryGet`.

### CS4013 — cannot be used inside a nested function or iterator

A `RecordRef` in a lambda, a local function, a LINQ query, or a `yield return`.

**Fix.** Pass the decoded struct rather than the ref, or restructure so the ref never leaves the
loop body. LINQ over records is not available by design — deferred execution and a buffer that
moves underneath it do not combine.

### CS8352 — may expose referenced variables outside their declaration scope

You returned a `RecordRef` from a method whose buffer is about to go out of scope.

**Fix.** Return `OwnedRecord`, or a decoded struct.

### RS0030 — `DateTime`/`TimeSpan` is banned

Only when building **this repository**, not when consuming it. The project bans the five BCL
date/time types outright; the error message names the NodaTime replacement.

**Fix.** Use `Instant`, `LocalDate`, `LocalDateTime`, `LocalTime`, or `Duration`. Converting DBN's
`ulong` nanoseconds goes through `DbnTime` — see [Timestamps and Prices](timestamps-and-prices.md).

In your own code you are free to use whatever you like; just know that `DateTime` cannot represent
a nanosecond timestamp.

---

## Restore and build

### Packages resolve from an unexpected feed

The repository's `nuget.config` pins restore to nuget.org with `<clear />`. If you have removed it
or are restoring from a different working directory, a globally configured private feed can
satisfy a public package.

**Fix.** Keep the `<clear />`, or check `dotnet nuget list source`.

### `net10.0` is not a known target framework

You are on an older SDK. `net10.0` is the only target framework and there is no `netstandard2.0`
fallback.

**Fix.** Install the .NET 10 SDK or newer. `dotnet --version` should report `10.0.x` or above.

---

## Connecting and authenticating

### `ConnectTimeoutException`

Nothing answered at the gateway address inside `ConnectTimeout` (10 s by default).

Check that outbound TCP to **port 13000** is open. This is not HTTPS on 443, and a corporate
firewall or a container network policy that allows web traffic will still block it. The address
that was tried is in the exception message and in `client.Endpoint`.

### `DatabentoAuthenticationException: The live gateway rejected the API key …a1b2c`

The key reached the gateway and was refused. The gateway's own reason follows the colon.

- **`A live data license is required to access XNAS.ITCH.`** — the account is not licensed for
  *live* data on that dataset. **A live license is a separate entitlement from historical
  access**; an account with full historical access is still refused here. Check the dataset's
  entitlements in the Databento portal.
- **No reason given** — the message quotes what the gateway said verbatim. Usually a key from a
  different environment, or one that has been revoked.

The key renders as its last five characters (`…a1b2c`, the bucket ID) so it cannot leak through a
log line. Compare that against the key you meant to use.

### `AuthTimeoutException`

The gateway accepted the connection and then stalled part-way through the handshake. The budget is
for the whole exchange, not per line, so this also fires when the greeting arrives promptly and
the challenge never does.

`AuthTimeout` defaults to 10 s. Raise it if you are behind a slow link; if it fires consistently
on a fast one, the gateway is the problem.

### `LiveProtocolException: Expected a 'cram=' challenge from the live gateway, got: '…'`

Whatever answered is not a Databento live gateway. Usually a `Gateway` override pointing at the
wrong address, or a proxy intercepting the connection.

### The client is disconnected after a cancelled call

Expected. `AuthenticateAsync`, `SubscribeAsync`, `ResubscribeAsync`, and `StartAsync` are **not
cancel-safe**: half a control line desynchronises the gateway, which then closes the connection.
They cancel by tearing the socket down rather than by abandoning a partial write.

**Fix.** After any failure in those four, reconnect. There is no resuming.

---

## Streaming

### `HeartbeatTimeoutException`

Nothing arrived within `EffectiveReadTimeout`, and the connection has been torn down.

First check what that budget actually is — `client.EffectiveReadTimeout` resolves to
`ReadTimeout`, else `HeartbeatInterval + 5s`, else 35 s.

**The most common cause is a `ReadTimeout` shorter than the gateway's heartbeat interval.** On a
quiet feed a heartbeat is the only guaranteed traffic, so a budget below the interval expires
between heartbeats every time. Nothing rejects that combination, because `HeartbeatInterval` may
be unset and the gateway's own default is then unknown to the client.

**Fix.** If you shorten `ReadTimeout`, set `HeartbeatInterval` too, and keep the timeout above it.

### The loop ends immediately, with no records

`FillBufferAsync` returned `0`, meaning the gateway closed the stream. Check in order:

1. **Did you call `StartAsync`?** Without it the gateway sends nothing. `client.IsSessionStarted`
   reports this; `FillBufferAsync` throws `InvalidOperationException` if you never did.
2. **Did the subscription resolve?** `metadata.NotFound` lists symbols the gateway never resolved
   — usually a typo or an `stype_in` that does not match the symbols supplied.
3. **Is the market open?** Outside session hours a live subscription is legitimately silent.
   Heartbeats still arrive; market data does not.

### `InvalidOperationException: This session has not started`

Calls out of order. The sequence is `ConnectAsync` → `AuthenticateAsync` → `SubscribeAsync` →
`StartAsync` → records. `IsConnected`, `IsAuthenticated`, and `IsSessionStarted` report where you
are.

A session that has *ended* is different: `FillBufferAsync` returns `0` and `TryNextRecord` returns
`false` rather than throwing. `IsClosed` tells the two apart.

### Records arrive but symbols do not resolve

A `PitSymbolMap` holds nothing for an instrument until its `SymbolMappingMsg` arrives, and those
are not guaranteed to come first. Early misses are normal.

Check you are calling `symbols.OnRecord(record)` for **every** record, not only the ones you care
about — the mapping records are the ones you would otherwise skip. See
[Symbol Resolution](symbol-resolution.md).

### `ArgumentOutOfRangeException` on `HeartbeatInterval`

The gateway takes 5–1800 seconds, in whole seconds. This is validated client-side rather than
letting the gateway reject it a round trip later, and a fractional value raises `ArgumentException`
rather than being silently truncated.

### Getting the same records twice after a reconnect

You resubscribed with the original `Start` still set. `ResubscribeAsync` clears `Start` on every
subscription for exactly this reason — replaying an intraday-replay start re-delivers, and re-bills,
everything you already have.

**Fix.** Use `ResubscribeAsync` rather than re-sending your original `Subscription` objects.

---

## Decoding

### `DbnDecodeException` from the constructor

The stream does not begin with valid DBN metadata. Check that the file is not HTML (an error page
saved with a `.dbn` extension is common), and that it is not a **fragment** — a `.dbn.frag` has no
metadata block and needs `skipMetadata: true`.

A stream that ends between *records* is not an error; only one that ends inside the metadata is.

### `TryGet<InstrumentDefMsg>` returns `false` on a file full of definitions

They are v1 or v2 definitions and you asked for the v3 struct. The match rule is
`HasRType(rtype) && wireLength == T.WireSize` with **exact** length equality — a `>=` comparison
would let a 520-byte v3 record decode as the 360-byte v1 struct and silently misread every field.

**Fix.** Decode with the default `UpgradeToV3` policy, or ask for `InstrumentDefMsgV1` /
`InstrumentDefMsgV2` explicitly. The same applies to `SymbolMapping`, `Error`, `System`, and
`Statistics`.

### Every field in a fragment is wrong

The `tsOut` flag passed to the decoder does not match what the fragment carries. A `ts_out` adds
eight bytes to every record, so getting it wrong shifts every field — and nothing throws, because
the headers still look plausible.

**Fix.** Get the flag from whatever produced the fragment. Do not guess.

### Timestamps land on 1969-12-31, or prices are about 9.2 billion

You converted a sentinel. `UndefTimestamp` is `ulong.MaxValue` and `UndefPrice` is `long.MaxValue`;
neither throws, and both convert to plausible-looking values.

**Fix.** `DbnTime.TryToInstant` / `IsUndefined` for timestamps, and compare against
`DbnConstants.UndefPrice` before computing anything from a price. See
[Timestamps and Prices](timestamps-and-prices.md).

### Symbols resolve to the previous day's value

You keyed the symbol map on `Header.TsEvent` instead of `IndexTs`. The two can fall on opposite
sides of UTC midnight.

**Fix.** Use `record.IndexTs`, or the `TryGetSymbol(RecordRef, …)` overload, which derives the date
correctly for you.

---

## Tests

### Live tests are skipped

By design. They need `DATABENTO_API_KEY` and skip themselves without it, and CI is never given
one. Copy `.env.example` to `.env` and fill it in to run them locally.

### `RealGatewaySessionTests` still does not run

It sits behind a **second** gate, `DATABENTO_LIVE_SESSION=1`, on top of `Category=Live`. It is the
only test in the repository that starts a session, and therefore the only one that moves billable
data. Everything in `RealGatewaySmokeTests` stops short of that line and is free.

---

## See also

- [FAQ](faq.md) — shorter answers to commoner questions
- [Live Streaming](live-streaming.md) — the session lifecycle in full
- [Zero-Copy and Allocation](zero-copy-and-allocation.md) — why the compiler errors above exist
