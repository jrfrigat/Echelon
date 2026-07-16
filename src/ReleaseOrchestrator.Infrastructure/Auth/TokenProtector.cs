using Microsoft.AspNetCore.DataProtection;

namespace ReleaseOrchestrator.Infrastructure.Auth;

public class TokenProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("ConnectionTokens.v1");

    public byte[] Protect(string plaintext) =>
        _protector.Protect(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(byte[] ciphertext) =>
        System.Text.Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));
}
