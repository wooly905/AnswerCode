using Microsoft.AspNetCore.DataProtection;

namespace AnswerCode.Services.Uploads;

public sealed class DeleteTokenService(IDataProtectionProvider dataProtectionProvider) : IDeleteTokenService
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("AnswerCode.AnonymousUploadDeleteToken.v1");

    public string Issue(string folderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        return _protector.Protect(folderId);
    }

    public bool Validate(string folderId, string? token)
    {
        if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            return string.Equals(_protector.Unprotect(token), folderId, StringComparison.Ordinal);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}