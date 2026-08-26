using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// The shared wire-layout assertion used by every record struct's layout test.
/// </summary>
/// <remarks>
/// <para>
/// Records are reinterpreted in place over the read buffer, so a field at the wrong offset is
/// silent data corruption rather than an exception. <see cref="AssertLayout{T}"/> is what turns
/// that back into a build failure, and it makes three independent claims:
/// </para>
/// <list type="number">
/// <item>the struct is exactly the size <c>databento-cpp</c> pins with a <c>static_assert</c>;</item>
/// <item>its alignment is 8, which upstream enforces for every record;</item>
/// <item>the sum of its <em>declared</em> field sizes equals its runtime size — i.e. the CLR
/// inserted no padding beyond the reserved fields the port declares explicitly.</item>
/// </list>
/// <para>
/// Claim 3 is computed structurally, from field declarations, rather than by asking the runtime
/// for a size a second time; it is an independent calculation, not a restatement. It composes:
/// a nested struct field contributes its own runtime size, and because every nested struct is
/// itself asserted padding-free, a clean result at every level proves the whole tree is clean.
/// </para>
/// <para>
/// <c>[InlineArray]</c> needs care here. Reflection reports one element field, so summing
/// declared fields naively would value a ten-level <c>BidAskPairArray10</c> at 32 bytes instead
/// of 320 and understate <c>Mbp10Msg</c> by 288 — on correct code.
/// <see cref="DeclaredSizeOf"/> therefore reads the buffer's <c>[InlineArray]</c> length and
/// multiplies, sizing the buffer rather than its element.
/// </para>
/// <para>
/// Reflection is deliberate and confined to this file. The no-reflection rule governs the decode
/// path; a test that walks field declarations is exactly how the decode path gets proved.
/// </para>
/// </remarks>
internal static class RecordLayout
{
    private const BindingFlags InstanceFields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Every DBN record and book pair is 8-byte aligned; upstream enforces it too.</summary>
    private const int RecordAlignment = 8;

    /// <summary>
    /// Asserts the three layout claims for <typeparamref name="T"/> against the size
    /// <c>databento-cpp</c> pins for it.
    /// </summary>
    public static void AssertLayout<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] T>(
        int cppStaticAssertSize)
        where T : unmanaged
    {
        Assert.Equal(cppStaticAssertSize, Unsafe.SizeOf<T>());
        Assert.Equal(RecordAlignment, AlignmentOf<T>());
        Assert.Equal(Unsafe.SizeOf<T>(), DeclaredSizeOf(typeof(T)));
    }

    /// <summary>
    /// The alignment of <typeparamref name="T"/> in bytes, measured rather than inspected: a
    /// probe struct puts a single byte in front of a <typeparamref name="T"/>, so the padding
    /// the CLR inserts between them is the alignment.
    /// </summary>
    public static int AlignmentOf<T>()
        where T : unmanaged
        => Unsafe.SizeOf<AlignmentProbe<T>>() - Unsafe.SizeOf<T>();

    /// <summary>
    /// The sum of <paramref name="type"/>'s declared field sizes, computed from the field
    /// declarations rather than from the runtime's size for the type.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:UnrecognizedReflectionPattern",
        Justification = "The test assembly is never trimmed or AOT-published; only the library is.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "The test assembly is never trimmed or AOT-published; only the library is.")]
    public static int DeclaredSizeOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.IsEnum)
        {
            return PrimitiveSizeOf(type.GetEnumUnderlyingType());
        }

        if (type.IsPrimitive)
        {
            return PrimitiveSizeOf(type);
        }

        var fields = type.GetFields(InstanceFields);

        // An [InlineArray] buffer declares one element field but occupies Length of them. Size
        // the buffer, never its element, or a ten-level book reads as one.
        var inlineArray = type.GetCustomAttribute<InlineArrayAttribute>();
        if (inlineArray is not null)
        {
            Assert.Single(fields);
            return inlineArray.Length * DeclaredSizeOf(fields[0].FieldType);
        }

        var total = 0;
        foreach (var field in fields)
        {
            total += DeclaredSizeOf(field.FieldType);
        }

        return total;
    }

    /// <summary>The byte offset of <paramref name="fieldName"/> within <typeparamref name="T"/>.</summary>
    public static int OffsetOf<T>(string fieldName)
        where T : unmanaged
        => (int)Marshal.OffsetOf<T>(fieldName);

    /// <summary>
    /// Asserts that <typeparamref name="T"/> declares exactly <paramref name="expected"/> —
    /// these field names, at these byte offsets, in this order, and nothing else — and that the
    /// last field ends exactly at <paramref name="cppStaticAssertSize"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="AssertLayout{T}"/> cannot see a transposition of two equal-size neighbours:
    /// the size, the alignment, and the sum of declared field sizes are all unchanged, and the
    /// decoder then reads a side byte as an action byte forever. Naming every field at its
    /// offset is what closes that hole. Offsets are transcribed from the <c>#[repr(C)]</c> field
    /// declaration order in the Rust source, never from an <c>encode_order</c> attribute.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:UnrecognizedReflectionPattern",
        Justification = "The test assembly is never trimmed or AOT-published; only the library is.")]
    public static void AssertFieldOffsets<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] T>(
        int cppStaticAssertSize,
        params (string Name, int Offset)[] expected)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(expected);

        var declared = typeof(T).GetFields(InstanceFields);
        var actual = declared
            .Select(f => (f.Name, Offset: (int)Marshal.OffsetOf<T>(f.Name)))
            .OrderBy(f => f.Offset)
            .ToArray();

        Assert.Equal(expected, actual);

        // The last field must reach the end of the struct: an offset table alone would not
        // notice trailing padding, and Marshal.OffsetOf cannot report an offset past the end.
        var lastType = declared.Single(f => f.Name == expected[^1].Name).FieldType;
        Assert.Equal(cppStaticAssertSize, expected[^1].Offset + DeclaredSizeOf(lastType));
        Assert.Equal(cppStaticAssertSize, Unsafe.SizeOf<T>());
    }

    private static int PrimitiveSizeOf(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean or TypeCode.SByte or TypeCode.Byte => 1,
        TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Char => 2,
        TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 4,
        TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Double => 8,

        // nint/nuint and anything else would be platform-dependent on the wire. Refuse to guess.
        _ => throw new NotSupportedException($"No fixed wire size for '{type}'."),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct AlignmentProbe<T>
        where T : unmanaged
    {
        public byte Lead;
        public T Value;
    }
}
