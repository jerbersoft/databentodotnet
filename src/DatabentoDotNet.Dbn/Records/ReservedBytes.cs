using System.Runtime.CompilerServices;

namespace DatabentoDotNet.Dbn;

/*
 * Reserved (padding) byte blocks.
 *
 * Upstream declares these as explicit `[u8; N]` fields rather than letting the compiler insert
 * padding, so that every record has provably zero implicit padding. This port keeps them for the
 * same reason: the fields are declared, they occupy their wire bytes, and the layout tests can
 * prove the sum of declared field sizes equals the struct's size.
 *
 * They are modelled as `[InlineArray]` byte buffers rather than as an integer of the right width
 * because a `[u8; N]` has an alignment of 1. Substituting, say, a `ushort` for a 2-byte reserved
 * block would import that type's 2-byte alignment and could shift a later field.
 *
 * These types are internal and every field typed with them is `private readonly`: reserved bytes
 * are written as zero and ignored on read, so they carry no meaning worth putting in the public
 * API.
 */

/// <summary>A one-byte reserved block.</summary>
[InlineArray(1)]
internal struct ReservedBytes1
{
    private byte _element0;
}

/// <summary>A two-byte reserved block.</summary>
[InlineArray(2)]
internal struct ReservedBytes2
{
    private byte _element0;
}

/// <summary>A three-byte reserved block.</summary>
[InlineArray(3)]
internal struct ReservedBytes3
{
    private byte _element0;
}

/// <summary>A four-byte reserved block.</summary>
[InlineArray(4)]
internal struct ReservedBytes4
{
    private byte _element0;
}

/// <summary>A six-byte reserved block.</summary>
[InlineArray(6)]
internal struct ReservedBytes6
{
    private byte _element0;
}

/// <summary>A seven-byte reserved block.</summary>
[InlineArray(7)]
internal struct ReservedBytes7
{
    private byte _element0;
}

/// <summary>An eight-byte reserved block.</summary>
[InlineArray(8)]
internal struct ReservedBytes8
{
    private byte _element0;
}

/// <summary>A ten-byte reserved block.</summary>
[InlineArray(10)]
internal struct ReservedBytes10
{
    private byte _element0;
}

/// <summary>A seventeen-byte reserved block.</summary>
[InlineArray(17)]
internal struct ReservedBytes17
{
    private byte _element0;
}

/// <summary>An eighteen-byte reserved block.</summary>
[InlineArray(18)]
internal struct ReservedBytes18
{
    private byte _element0;
}
