//Common/Security/TokenHashing.cs
using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Common.Security
{
    public static class TokenHashing
    {
        public static string Sha256(string raw)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes); // HEX
        }
    }
}
