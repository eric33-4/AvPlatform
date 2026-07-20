using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvPlatform.WebApi.Channels;

/// <summary>阅姝阁 Box 二进制信封的统一解密器。</summary>
internal static class YueShuGeBoxCodec
{
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("dnf45as45fs1ace1");
    private static readonly byte[] Iv = Encoding.ASCII.GetBytes("dn5as4fs1ac5f4e1");

    public static JsonDocument Decrypt(byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        var compressed = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

        using var source = new MemoryStream(compressed);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        return JsonDocument.Parse(zlib);
    }
}
