using System.Security.Cryptography;
using System.Text;

namespace YumeShelf.Infrastructure;

public static class SecureSecretStore
{
    public static string Protect(string value)
        => string.IsNullOrEmpty(value) ? string.Empty : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return string.Empty; }
        catch (FormatException) { return string.Empty; }
    }
}
