namespace AnswerCode.Services.Uploads;

public interface IDeleteTokenService
{
    string Issue(string folderId);

    bool Validate(string folderId, string? token);
}