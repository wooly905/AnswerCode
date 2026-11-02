using System.Reflection;
using AnswerCode.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace AnswerCode.Tests.Controllers;

public class CodeQARouteTests
{
    [Theory]
    [InlineData(typeof(UploadController), "UploadSourceCode", "POST", "upload")]
    [InlineData(typeof(UploadController), "DeleteSourceCode", "DELETE", "upload/{folderId}")]
    [InlineData(typeof(UploadController), "CleanupSourceCode", "POST", "upload/{folderId}/cleanup")]
    [InlineData(typeof(UploadController), "ListUploads", "GET", "uploads")]
    [InlineData(typeof(UploadController), "ListUserFolders", "GET", "user-folders")]
    [InlineData(typeof(AskController), "AskQuestion", "POST", "ask")]
    [InlineData(typeof(AskController), "AskQuestionStream", "POST", "ask/stream")]
    [InlineData(typeof(AskController), "SubmitUserAnswer", "POST", "ask/answer")]
    [InlineData(typeof(AskController), "GetProviders", "GET", "providers")]
    [InlineData(typeof(FileController), "GetProjectStructure", "GET", "structure")]
    [InlineData(typeof(FileController), "ReadFile", "GET", "file")]
    [InlineData(typeof(HistoryController), "GetHistory", "GET", "history/{sessionId}")]
    public void Endpoint_PreservesCodeQARoute(Type controllerType, string methodName, string verb, string template)
    {
        Assert.Equal("api/CodeQA", controllerType.GetCustomAttribute<RouteAttribute>()?.Template);
        MethodInfo method = controllerType.GetMethod(methodName)!;
        HttpMethodAttribute attribute = Assert.Single(method.GetCustomAttributes<HttpMethodAttribute>());

        Assert.Equal(template, attribute.Template);
        Assert.Contains(verb, attribute.HttpMethods);
    }

    [Theory]
    [InlineData("GetTree", "GET", "tree")]
    [InlineData("ListEntries", "GET", "entries")]
    [InlineData("DeleteFile", "DELETE", "file")]
    [InlineData("DeleteFolder", "DELETE", "folder")]
    [InlineData("Rename", "PATCH", "rename")]
    [InlineData("CreateFolder", "POST", "folder")]
    public void ManageEndpoint_RequiresAuthorization(string methodName, string verb, string template)
    {
        Assert.Equal("api/manage", typeof(ManageController).GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(typeof(ManageController).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        HttpMethodAttribute attribute = Assert.Single(typeof(ManageController).GetMethod(methodName)!.GetCustomAttributes<HttpMethodAttribute>());

        Assert.Equal(template, attribute.Template);
        Assert.Contains(verb, attribute.HttpMethods);
    }
}