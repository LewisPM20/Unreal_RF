using Microsoft.AspNetCore.Http;
using RenderFarm.Controller.Api;
using Xunit;

namespace RenderFarm.Tests;

public sealed class ApiTokenProtectionTests
{
    [Fact]
    public void ReadOnlyEndpointsDoNotRequireToken()
    {
        Assert.False(ApiTokenProtection.RequiresToken("GET", new PathString("/api/jobs")));
    }

    [Fact]
    public void ProtectedMutationRequiresToken()
    {
        Assert.True(ApiTokenProtection.RequiresToken("POST", new PathString("/api/workers/heartbeat")));
        Assert.True(ApiTokenProtection.RequiresToken("DELETE", new PathString("/api/admin/state")));
    }

    [Fact]
    public void MissingOrWrongTokenIsRejected()
    {
        Assert.False(ApiTokenProtection.IsAuthorized(null, "secret"));
        Assert.False(ApiTokenProtection.IsAuthorized("Bearer wrong", "secret"));
    }

    [Fact]
    public void CorrectBearerTokenIsAccepted()
    {
        Assert.True(ApiTokenProtection.IsAuthorized("Bearer secret", "secret"));
    }
}