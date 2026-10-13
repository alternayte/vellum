using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Vellum.Modules.Stress.Engine;

/// <summary>
/// SplitMix64 (Steele, Lea, Flood), as in the reference <c>splitmix64.c</c> of Vigna. It turns
/// one seed into the state of a <see cref="Xoshiro256"/>.
/// </summary>
public struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>
/// xoshiro256** 1.0 (Blackman, Vigna), as in the reference <c>xoshiro256starstar.c</c>. The
/// engine owns its generator, so a result does not depend on the random source of a runtime.
/// </summary>
public sealed class Xoshiro256
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256(ulong s0, ulong s1, ulong s2, ulong s3) => (_s0, _s1, _s2, _s3) = (s0, s1, s2, s3);

    /// <summary>The state is the first four outputs of SplitMix64 over <paramref name="seed"/>.</summary>
    public static Xoshiro256 FromSeed(ulong seed)
    {
        var mix = new SplitMix64(seed);
        return new Xoshiro256(mix.Next(), mix.Next(), mix.Next(), mix.Next());
    }

    public ulong Next()
    {
        var result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>A double in [0, 1): the top 53 bits times 2^-53.</summary>
    public double NextUnit() => (Next() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>An integer in [0, <paramref name="bound"/>), without modulo bias: values past the last whole multiple are drawn again.</summary>
    public long NextBelow(long bound)
    {
        var range = (ulong)bound;
        var limit = ulong.MaxValue - ulong.MaxValue % range;
        while (true)
        {
            var value = Next();
            if (value < limit)
                return (long)(value % range);
        }
    }
}

/// <summary>
/// One generator per purpose and target, all derived from the seed of the run. The arrivals,
/// each service time and each branch draw from their own stream, so the order in which
/// events of different resources run does not change what any of them draws.
/// </summary>
public sealed class StressStreams(ulong seed)
{
    public Xoshiro256 For(string purpose, Guid target)
    {
        Span<byte> bytes = stackalloc byte[16];
        target.TryWriteBytes(bytes, bigEndian: true, out _);
        var mix = new SplitMix64(seed ^ Fnv1A(purpose));
        var a = mix.Next() ^ BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var b = new SplitMix64(a).Next() ^ BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        return Xoshiro256.FromSeed(new SplitMix64(b).Next());
    }

    public Xoshiro256 For(string purpose, string target) =>
        Xoshiro256.FromSeed(new SplitMix64(new SplitMix64(seed ^ Fnv1A(purpose)).Next() ^ Fnv1A(target)).Next());

    /// <summary>FNV-1a, 64 bit, over the UTF-8 bytes.</summary>
    private static ulong Fnv1A(string text)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var value in Encoding.UTF8.GetBytes(text))
            hash = (hash ^ value) * 0x100000001B3UL;
        return hash;
    }
}

/// <summary>
/// Logarithm and exponential from IEEE 754 addition, multiplication, division and square
/// root only. Those four are rounded the same way on every platform; the library functions
/// of a platform are not. A sampled time is therefore the same number of nanoseconds on
/// every machine and every runtime version.
/// </summary>
public static class StressMath
{
    private const double Ln2Hi = 6.93147180369123816490e-01;
    private const double Ln2Lo = 1.90821492927058770002e-10;
    private const double Ln2 = 0.6931471805599453;

    /// <summary>
    /// ln(x) for a finite x &gt; 0. x = m * 2^e with m in [sqrt(1/2), sqrt(2)); with
    /// s = (m - 1) / (m + 1), ln(m) = 2 * (s + s^3/3 + s^5/5 + ...). s^2 is at most 0.0295,
    /// so fourteen terms leave an error below one unit in the last place.
    /// </summary>
    public static double Log(double x)
    {
        var exponent = 0;
        // A subnormal has no implicit leading bit; 2^54 makes it normal.
        if (x < 2.2250738585072014e-308)
        {
            x *= 18014398509481984.0;
            exponent = -54;
        }

        var bits = BitConverter.DoubleToInt64Bits(x);
        exponent += (int)((bits >> 52) & 0x7FF) - 1023;
        var m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > 1.4142135623730951)
        {
            m *= 0.5;
            exponent++;
        }

        var s = (m - 1.0) / (m + 1.0);
        var z = s * s;
        var sum = 0.0;
        for (var k = 13; k >= 0; k--)
            sum = sum * z + 1.0 / (2 * k + 1);

        return exponent * Ln2Hi + (2.0 * s * sum + exponent * Ln2Lo);
    }

    /// <summary>
    /// e^x. x = k * ln 2 + r with |r| at most 0.347; e^r is its Taylor series to degree 17,
    /// and the result is scaled by 2^k, which is exact.
    /// </summary>
    public static double Exp(double x)
    {
        if (x > 709.0)
            return double.PositiveInfinity;
        if (x < -745.0)
            return 0.0;

        var k = (int)Math.Round(x / Ln2);
        var r = x - k * Ln2Hi - k * Ln2Lo;
        var sum = 1.0;
        for (var i = 17; i >= 1; i--)
            sum = 1.0 + r * sum / i;

        return Math.ScaleB(sum, k);
    }

    /// <summary>
    /// A standard normal value by the polar form of the Box–Muller transform (Marsaglia):
    /// draw u, v uniform in (-1, 1) until s = u² + v² is in (0, 1); the value is
    /// u * sqrt(-2 ln(s) / s). The second value of the pair is not used.
    /// </summary>
    public static double Normal(Xoshiro256 random)
    {
        while (true)
        {
            var u = 2.0 * random.NextUnit() - 1.0;
            var v = 2.0 * random.NextUnit() - 1.0;
            var s = u * u + v * v;
            if (s > 0.0 && s < 1.0)
                return u * Math.Sqrt(-2.0 * Log(s) / s);
        }
    }

    /// <summary>An exponential value with mean 1 by inverse transform: -ln(1 - u), u uniform in [0, 1).</summary>
    public static double Exponential(Xoshiro256 random) => -Log(1.0 - random.NextUnit());
}
