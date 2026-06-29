using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RenderFarm.Domain;
using RenderFarm.Persistence;
using Xunit;

namespace RenderFarm.Tests;

public sealed class SqliteRepositorySchemaTests
{
    [Fact]
    public async Task InitializeCreatesSchemaMigrationAndSchedulerIndexes()
    {
        var databasePath = CreateDatabasePath();
        var repository = CreateRepository(databasePath);

        await repository.InitializeAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;"));
        var indexes = await ReadStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name;");

        Assert.Contains("idx_jobs_state_priority_created", indexes);
        Assert.Contains("idx_jobs_assigned_worker", indexes);
        Assert.Contains("idx_job_leases_active", indexes);
        Assert.Contains("idx_job_leases_expiry", indexes);
        Assert.Contains("idx_job_events_job_time", indexes);
    }

    [Fact]
    public async Task RenderProfileRequiresExistingProject()
    {
        var databasePath = CreateDatabasePath();
        var repository = CreateRepository(databasePath);
        await repository.InitializeAsync(CancellationToken.None);

        var orphanProfile = new RenderProfile(
            "profile-orphan",
            "missing-project",
            "Orphan",
            RenderProfileType.MrqQueue,
            "/Game/Queue",
            null,
            "png",
            false,
            new Dictionary<string, string>());

        await Assert.ThrowsAsync<SqliteException>(() => ((IRenderProfileRepository)repository).UpsertAsync(orphanProfile, CancellationToken.None));
    }

    [Fact]
    public async Task SchedulerStateMutationPersistsRelatedRowsInOneTransaction()
    {
        var databasePath = CreateDatabasePath();
        var repository = CreateRepository(databasePath);
        await repository.InitializeAsync(CancellationToken.None);
        await SeedProjectProfileAndWorkerAsync(repository);

        var now = DateTimeOffset.UtcNow;
        var job = new RenderJob("job-1", "project-1", "profile-1", "Render", JobState.Reserved, 5, "worker-1", FailureCategory.None, null, null, now, now, now, null, null, false);
        var attempt = new JobAttempt("attempt-1", job.Id, 1, "worker-1", JobState.Reserved, FailureCategory.None, null, null, null, now, null, null);
        var lease = new JobLease("lease-1", job.Id, attempt.Id, "worker-1", now, now.AddMinutes(2), null, null, null, true);
        var evt = new JobEvent("event-1", job.Id, attempt.Id, "worker-1", JobState.Reserved, FailureCategory.None, "Reserved", now, null);

        await ((ISchedulerStateRepository)repository).ApplyAsync(new SchedulerStateMutation(job, attempt, lease, evt), CancellationToken.None);

        Assert.NotNull(await ((IJobRepository)repository).GetAsync(job.Id, CancellationToken.None));
        Assert.NotNull(await ((IJobAttemptRepository)repository).GetAsync(attempt.Id, CancellationToken.None));
        Assert.NotNull(await ((IJobLeaseRepository)repository).GetAsync(lease.Id, CancellationToken.None));
        Assert.Single(await ((IJobEventRepository)repository).ListForJobAsync(job.Id, CancellationToken.None));
    }


    [Fact]
    public async Task InitializeCreatesConfiguredDatabaseDirectory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "rf_schema_tests", Guid.NewGuid().ToString("N"), "nested", "renderfarm.db");
        var repository = new SqliteRenderFarmRepository(Options.Create(new RenderFarmDatabaseOptions { Path = databasePath }));

        await repository.InitializeAsync(CancellationToken.None);

        Assert.True(File.Exists(databasePath), $"Expected SQLite database to be created at {databasePath}");
    }
    private static SqliteRenderFarmRepository CreateRepository(string databasePath) =>
        new(Options.Create(new RenderFarmDatabaseOptions { ConnectionString = $"Data Source={databasePath}" }));

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rf_schema_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "renderfarm.db");
    }

    private static async Task SeedProjectProfileAndWorkerAsync(SqliteRenderFarmRepository repository)
    {
        var now = DateTimeOffset.UtcNow;
        await ((IProjectRepository)repository).UpsertAsync(new ProjectProfile("project-1", "Project", "D:\\Project\\Project.uproject", "5.7", ["5.7"], null, []), CancellationToken.None);
        await ((IRenderProfileRepository)repository).UpsertAsync(new RenderProfile("profile-1", "project-1", "Main", RenderProfileType.MrqQueue, "/Game/Main", null, "png", false, new Dictionary<string, string>()), CancellationToken.None);
        await ((IWorkerRepository)repository).UpsertAsync(new RenderFarm.Domain.Worker("worker-1", "Worker", "host", "127.0.0.1", null, WorkerStatus.Idle, null, null, "test", WorkerCapabilities.Empty, null, now, now), CancellationToken.None);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
