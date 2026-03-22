using System.Numerics;

namespace PiGenerator;

/// <summary>
/// Computes π to arbitrary precision using the Chudnovsky algorithm
/// with binary splitting and pure BigInteger arithmetic.
/// Formula: π = 426880 · √10005 · Q / T
/// ~14.18 decimal digits per term.
/// </summary>
public static class PiEngine
{
    private static readonly BigInteger C3_OVER_24 = 10939058860032000; // 640320³/24

    /// <summary>
    /// Compute decimal digits of π after "3." up to <paramref name="digits"/> places.
    /// Runs on a thread-pool thread; respects cancellation.
    /// </summary>
    public static Task<string> ComputeDecimalDigitsAsync(int digits, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Compute(digits);
        }, ct);

    public static string Compute(int digits)
    {
        int guard = 20;
        int prec  = digits + guard;
        int terms = (int)Math.Ceiling(prec / 14.181647) + 5;

        var (_, Q, T) = BinarySplit(0, terms);

        BigInteger scale   = BigInteger.Pow(10, prec);
        BigInteger sqrtVal = IntegerSqrt(10005 * scale * scale);
        BigInteger piScaled = 426880 * sqrtVal * Q / T;

        string raw      = piScaled.ToString();
        string decimals = raw.Length > 1 ? raw[1..] : "";
        if (decimals.Length > digits) decimals = decimals[..digits];
        return decimals;
    }

    private static (BigInteger P, BigInteger Q, BigInteger T) BinarySplit(int a, int b)
    {
        if (b - a == 1)
        {
            if (a == 0) return (BigInteger.One, BigInteger.One, 13591409);
            BigInteger P = (BigInteger)(6*a-5) * (2*a-1) * (6*a-1);
            BigInteger Q = C3_OVER_24 * (BigInteger)a * a * a;
            BigInteger T = (13591409 + 545140134 * (BigInteger)a) * P;
            if ((a & 1) == 1) T = -T;
            return (P, Q, T);
        }
        int mid = (a + b) / 2;
        var (Pl, Ql, Tl) = BinarySplit(a, mid);
        var (Pr, Qr, Tr) = BinarySplit(mid, b);
        return (Pl * Pr, Ql * Qr, Tl * Qr + Pl * Tr);
    }

    private static BigInteger IntegerSqrt(BigInteger n)
    {
        if (n <= 0) return 0;
        BigInteger x = BigInteger.One << ((int)BigInteger.Log(n, 2) / 2 + 1);
        while (true)
        {
            BigInteger next = (x + n / x) >> 1;
            if (next >= x) break;
            x = next;
        }
        return x;
    }
}
