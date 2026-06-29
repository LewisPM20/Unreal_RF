using System.Text.Json;
using RenderFarm.Domain;
using RenderFarm.Shared;
using Xunit;

namespace RenderFarm.Tests;

public sealed class JsonContractTests
{
    [Fact]
    public void WorkerCanReadControllerAssignmentWithStringEnums()
    {
        var now = DateTimeOffset.UtcNow;
        var assignment = new JobAssignmentDto(
            true,
            new RenderJobDto("job-1", "project-1", "profile-1", "Main Render", JobState.Reserved, 0, "worker-1", FailureCategory.None, null, null, now, now, now, null, null, false),
            new JobAttemptDto("attempt-1", "job-1", 1, "worker-1", JobState.Reserved, FailureCategory.None, null, null, null, now, null, null),
            new JobLeaseDto("lease-1", "job-1", "attempt-1", "worker-1", now, now.AddSeconds(30), null, null, null, true),
            "Assigned");

        var json = JsonSerializer.Serialize(assignment, RenderFarmJson.SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<JobAssignmentDto>(json, RenderFarmJson.SerializerOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(JobState.Reserved, roundTripped.Job!.State);
        Assert.Equal(FailureCategory.None, roundTripped.Attempt!.FailureCategory);
    }

    [Fact]
    public void WorkerCanReadWebCamelCaseStringEnumPayload()
    {
        const string json = """
        {
          "assigned": true,
          "job": {
            "id": "job-1",
            "projectId": "project-1",
            "renderProfileId": "profile-1",
            "name": "Main Render",
            "state": "Reserved",
            "priority": 0,
            "assignedWorkerId": "worker-1",
            "failureCategory": "None",
            "error": null,
            "outputDirectory": null,
            "createdAtUtc": "2026-06-25T10:00:00+00:00",
            "updatedAtUtc": "2026-06-25T10:00:00+00:00",
            "queuedAtUtc": "2026-06-25T10:00:00+00:00",
            "startedAtUtc": null,
            "finishedAtUtc": null,
            "cancellationRequested": false
          },
          "attempt": null,
          "lease": null,
          "message": "Assigned"
        }
        """;

        var assignment = JsonSerializer.Deserialize<JobAssignmentDto>(json, RenderFarmJson.SerializerOptions);

        Assert.NotNull(assignment);
        Assert.Equal(JobState.Reserved, assignment.Job!.State);
    }
}
