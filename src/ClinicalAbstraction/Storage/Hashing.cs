using System.Security.Cryptography;
using System.Text;

namespace ClinicalAbstraction.Storage;

public static class Hashing
{
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
}
