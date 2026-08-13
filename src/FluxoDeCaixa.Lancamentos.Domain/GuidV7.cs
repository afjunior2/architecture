using System.Security.Cryptography;

namespace FluxoDeCaixa.Lancamentos.Domain;

/// <summary>
/// UUID v7 (ordenado no tempo). Guid v4 aleatório fragmenta o índice B-tree da chave
/// primária e degrada inserção com o crescimento da tabela. O .NET 8 não tem
/// Guid.CreateVersion7 (chegou no .NET 9), então geramos manualmente conforme a RFC 9562.
/// </summary>
public static class GuidV7
{
    public static Guid Novo() => Novo(DateTimeOffset.UtcNow);

    public static Guid Novo(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[6..]);

        var ms = (ulong)timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // versão 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variante RFC

        return new Guid(bytes, bigEndian: true);
    }
}
