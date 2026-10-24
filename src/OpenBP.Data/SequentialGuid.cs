using System.Security.Cryptography;

namespace OpenBP.Data;

/// <summary>
/// Монотонні UUID версії 7 (RFC 9562, метод 1 — лічильник у <c>rand_a</c>/<c>rand_b</c>): 48 біт мілісекунд Unix,
/// 42 біти лічильника в межах мілісекунди, 32 випадкові біти. У межах процесу кожен наступний id строго більший
/// за попередній — і в порівнянні <see cref="Guid"/>, і в порядку <c>uuid</c> Postgres. Між процесами порядок
/// задає годинник; <c>execution_steps</c> сортуються за <c>id</c>, щоб знімок повертав кроки в порядку збереження.
/// </summary>
public static class SequentialGuid
{
    private const long MaxCounter = (1L << 42) - 1;

    private static readonly Lock Sync = new();
    private static long _lastMs;
    private static long _counter;

    /// <summary>Наступний id.</summary>
    /// <param name="time">Годинник; за замовчуванням системний.</param>
    public static Guid Next(TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        long ms;
        long counter;
        lock (Sync)
        {
            if (now > _lastMs)
            {
                _lastMs = now;
                _counter = 0;
            }
            else if (++_counter > MaxCounter)
            {
                _lastMs++;
                _counter = 0;
            }

            ms = _lastMs;
            counter = _counter;
        }

        Span<byte> b = stackalloc byte[16];
        b[0] = (byte)(ms >> 40);
        b[1] = (byte)(ms >> 32);
        b[2] = (byte)(ms >> 24);
        b[3] = (byte)(ms >> 16);
        b[4] = (byte)(ms >> 8);
        b[5] = (byte)ms;
        b[6] = (byte)(0x70 | ((counter >> 38) & 0x0F));
        b[7] = (byte)(counter >> 30);
        b[8] = (byte)(0x80 | ((counter >> 24) & 0x3F));
        b[9] = (byte)(counter >> 16);
        b[10] = (byte)(counter >> 8);
        b[11] = (byte)counter;
        RandomNumberGenerator.Fill(b[12..]);
        return new Guid(b, bigEndian: true);
    }
}
