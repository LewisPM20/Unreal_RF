using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RenderFarm.Domain;

namespace RenderFarm.Persistence;

/// <summary>
/// SQLite connection settings for the render farm controller database.
/// </summary>
public sealed class RenderFarmDatabaseOptions
{
    /// <summary>
    /// Optional SQLite connection string. When supplied, it takes precedence over <see cref="Path"/>.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Optional SQLite database file path. Environment variables are expanded before use.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Builds the SQLite connection string used by the controller.
    /// </summary>
    public string ResolveConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            return ConnectionString;
        }

        var databasePath = string.IsNullOrWhiteSpace(Path) ? GetDefaultDatabasePath() : ExpandConfiguredPath(Path);
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    /// <summary>
    /// Returns the default per-user controller database path.
    /// </summary>
    public static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "data")
            : System.IO.Path.Combine(localAppData, "RenderFarm", "Controller");

        return System.IO.Path.Combine(root, "renderfarm.db");
    }

    private static string ExpandConfiguredPath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return System.IO.Path.IsPathFullyQualified(expanded) ? expanded : System.IO.Path.GetFullPath(expanded);
    }
}

public sealed record MaintenanceClearResult(
    int Workers,
    int Projects,
    int RenderProfiles,
    int Jobs,
    int JobAttempts,
    int JobLeases,
    int JobEvents,
    int Settings);


public interface IRenderFarmDatabase
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IWorkerRepository
{
    Task UpsertAsync(Worker worker, CancellationToken cancellationToken);
    Task<Worker?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Worker>> ListAsync(CancellationToken cancellationToken);
}

public interface IProjectRepository
{
    Task UpsertAsync(ProjectProfile project, CancellationToken cancellationToken);
    Task<ProjectProfile?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectProfile>> ListAsync(CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
}

public interface IRenderProfileRepository
{
    Task UpsertAsync(RenderProfile profile, CancellationToken cancellationToken);
    Task<RenderProfile?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<RenderProfile>> ListAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RenderProfile>> ListForProjectAsync(string projectId, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
}

public interface IJobRepository
{
    Task UpsertAsync(RenderJob job, CancellationToken cancellationToken);
    Task<RenderJob?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<RenderJob>> ListAsync(CancellationToken cancellationToken);
}

public interface IJobAttemptRepository
{
    Task UpsertAsync(JobAttempt attempt, CancellationToken cancellationToken);
    Task<JobAttempt?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobAttempt>> ListForJobAsync(string jobId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, int>> CountByJobAsync(CancellationToken cancellationToken);
}

public interface IJobLeaseRepository
{
    Task UpsertAsync(JobLease lease, CancellationToken cancellationToken);
    Task<JobLease?> GetAsync(string id, CancellationToken cancellationToken);
    Task<JobLease?> GetActiveForJobAsync(string jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobLease>> ListActiveAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JobLease>> ListExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

public interface IJobEventRepository
{
    Task AppendAsync(JobEvent evt, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobEvent>> ListForJobAsync(string jobId, CancellationToken cancellationToken);
}

public interface ISettingsRepository
{
    Task UpsertAsync(FarmSetting setting, CancellationToken cancellationToken);
    Task<FarmSetting?> GetAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<FarmSetting>> ListAsync(CancellationToken cancellationToken);
}

public sealed record SchedulerStateMutation(RenderJob? Job = null, JobAttempt? Attempt = null, JobLease? Lease = null, JobEvent? Event = null);

public interface ISchedulerStateRepository
{
    Task ApplyAsync(SchedulerStateMutation mutation, CancellationToken cancellationToken);
}

/// <summary>
/// SQLite-backed repository set for controller-owned render farm state.
/// </summary>
public sealed class SqliteRenderFarmRepository(IOptions<RenderFarmDatabaseOptions> options) :
    IRenderFarmDatabase,
    IWorkerRepository,
    IProjectRepository,
    IRenderProfileRepository,
    IJobRepository,
    IJobAttemptRepository,
    IJobLeaseRepository,
    IJobEventRepository,
    ISettingsRepository,
    ISchedulerStateRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString = options.Value.ResolveConnectionString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS workers (id TEXT PRIMARY KEY, name TEXT NOT NULL, hostname TEXT NULL, ip_address TEXT NULL, service_url TEXT NULL, status TEXT NOT NULL, stage TEXT NULL, current_job_id TEXT NULL, agent_version TEXT NULL, capabilities_json TEXT NOT NULL, last_error TEXT NULL, registered_at_utc TEXT NOT NULL, last_heartbeat_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, uproject_path TEXT NULL, preferred_engine_version TEXT NULL, allowed_engine_versions_json TEXT NOT NULL, shared_output_policy_id TEXT NULL, worker_paths_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS render_profiles (id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE, display_name TEXT NOT NULL, type TEXT NOT NULL, asset_path TEXT NULL, command_template TEXT NULL, default_output_type TEXT NOT NULL, supports_chunking INTEGER NOT NULL, settings_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS jobs (id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE RESTRICT, render_profile_id TEXT NOT NULL REFERENCES render_profiles(id) ON DELETE RESTRICT, name TEXT NOT NULL, state TEXT NOT NULL, priority INTEGER NOT NULL, assigned_worker_id TEXT NULL REFERENCES workers(id) ON DELETE SET NULL, failure_category TEXT NOT NULL, error TEXT NULL, output_directory TEXT NULL, validation_json TEXT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, queued_at_utc TEXT NULL, started_at_utc TEXT NULL, finished_at_utc TEXT NULL, cancellation_requested INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS job_attempts (id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE, attempt_number INTEGER NOT NULL, worker_id TEXT NULL REFERENCES workers(id) ON DELETE SET NULL, state TEXT NOT NULL, failure_category TEXT NOT NULL, error TEXT NULL, command_line TEXT NULL, log_file_path TEXT NULL, started_at_utc TEXT NOT NULL, finished_at_utc TEXT NULL, exit_code INTEGER NULL);
            CREATE TABLE IF NOT EXISTS job_leases (id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE, job_attempt_id TEXT NOT NULL REFERENCES job_attempts(id) ON DELETE CASCADE, worker_id TEXT NOT NULL REFERENCES workers(id) ON DELETE RESTRICT, acquired_at_utc TEXT NOT NULL, expires_at_utc TEXT NOT NULL, renewed_at_utc TEXT NULL, released_at_utc TEXT NULL, release_reason TEXT NULL, is_active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS job_events (id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE, job_attempt_id TEXT NULL REFERENCES job_attempts(id) ON DELETE SET NULL, worker_id TEXT NULL REFERENCES workers(id) ON DELETE SET NULL, state TEXT NULL, failure_category TEXT NOT NULL, message TEXT NOT NULL, created_at_utc TEXT NOT NULL, data_json TEXT NULL);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value_json TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_workers_status_heartbeat ON workers (status, last_heartbeat_utc);
            CREATE INDEX IF NOT EXISTS idx_render_profiles_project ON render_profiles (project_id, display_name);
            CREATE INDEX IF NOT EXISTS idx_jobs_state_priority_created ON jobs (state, priority DESC, created_at_utc);
            CREATE INDEX IF NOT EXISTS idx_jobs_assigned_worker ON jobs (assigned_worker_id, state);
            CREATE INDEX IF NOT EXISTS idx_job_attempts_job ON job_attempts (job_id, attempt_number);
            CREATE INDEX IF NOT EXISTS idx_job_leases_active ON job_leases (job_id, is_active, expires_at_utc);
            CREATE INDEX IF NOT EXISTS idx_job_leases_expiry ON job_leases (is_active, expires_at_utc);
            CREATE INDEX IF NOT EXISTS idx_job_events_job_time ON job_events (job_id, created_at_utc);
            INSERT OR IGNORE INTO schema_migrations (version, name, applied_at_utc) VALUES (1, 'baseline-schema-and-indexes', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "jobs", "validation_json", "TEXT NULL", cancellationToken);
    }


    private static async Task EnsureColumnAsync(SqliteConnection connection, string tableName, string columnName, string columnDefinition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName})";
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
    async Task IWorkerRepository.UpsertAsync(Worker worker, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workers (id, name, hostname, ip_address, service_url, status, stage, current_job_id, agent_version, capabilities_json, last_error, registered_at_utc, last_heartbeat_utc)
            VALUES ($id, $name, $hostname, $ip_address, $service_url, $status, $stage, $current_job_id, $agent_version, $capabilities_json, $last_error, $registered_at_utc, $last_heartbeat_utc)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, hostname = excluded.hostname, ip_address = excluded.ip_address, service_url = excluded.service_url, status = excluded.status, stage = excluded.stage, current_job_id = excluded.current_job_id, agent_version = excluded.agent_version, capabilities_json = excluded.capabilities_json, last_error = excluded.last_error, last_heartbeat_utc = excluded.last_heartbeat_utc;
            """;
        AddWorkerParameters(command, worker);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<Worker?> IWorkerRepository.GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM workers WHERE id = $id";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWorker(reader) : null;
    }

    async Task<IReadOnlyList<Worker>> IWorkerRepository.ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM workers ORDER BY name";
        return await ReadManyAsync(command, ReadWorker, cancellationToken);
    }

    async Task IProjectRepository.UpsertAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects (id, display_name, uproject_path, preferred_engine_version, allowed_engine_versions_json, shared_output_policy_id, worker_paths_json)
            VALUES ($id, $display_name, $uproject_path, $preferred_engine_version, $allowed_engine_versions_json, $shared_output_policy_id, $worker_paths_json)
            ON CONFLICT(id) DO UPDATE SET display_name = excluded.display_name, uproject_path = excluded.uproject_path, preferred_engine_version = excluded.preferred_engine_version, allowed_engine_versions_json = excluded.allowed_engine_versions_json, shared_output_policy_id = excluded.shared_output_policy_id, worker_paths_json = excluded.worker_paths_json;
            """;
        Add(command, "$id", project.Id);
        Add(command, "$display_name", project.DisplayName);
        Add(command, "$uproject_path", project.UProjectPath);
        Add(command, "$preferred_engine_version", project.PreferredEngineVersion);
        Add(command, "$allowed_engine_versions_json", ToJson(project.AllowedEngineVersions));
        Add(command, "$shared_output_policy_id", project.SharedOutputPolicyId);
        Add(command, "$worker_paths_json", ToJson(project.WorkerPaths));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<ProjectProfile?> IProjectRepository.GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM projects WHERE id = $id";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProject(reader) : null;
    }

    async Task<IReadOnlyList<ProjectProfile>> IProjectRepository.ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM projects ORDER BY display_name";
        return await ReadManyAsync(command, ReadProject, cancellationToken);
    }

    async Task IProjectRepository.DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE id = $id";
        Add(command, "$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task IRenderProfileRepository.UpsertAsync(RenderProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO render_profiles (id, project_id, display_name, type, asset_path, command_template, default_output_type, supports_chunking, settings_json)
            VALUES ($id, $project_id, $display_name, $type, $asset_path, $command_template, $default_output_type, $supports_chunking, $settings_json)
            ON CONFLICT(id) DO UPDATE SET project_id = excluded.project_id, display_name = excluded.display_name, type = excluded.type, asset_path = excluded.asset_path, command_template = excluded.command_template, default_output_type = excluded.default_output_type, supports_chunking = excluded.supports_chunking, settings_json = excluded.settings_json;
            """;
        AddProfileParameters(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<RenderProfile?> IRenderProfileRepository.GetAsync(string id, CancellationToken cancellationToken) => await GetProfileAsync("id", id, cancellationToken);

    async Task<IReadOnlyList<RenderProfile>> IRenderProfileRepository.ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM render_profiles ORDER BY project_id, display_name";
        return await ReadManyAsync(command, ReadProfile, cancellationToken);
    }

    async Task<IReadOnlyList<RenderProfile>> IRenderProfileRepository.ListForProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM render_profiles WHERE project_id = $project_id ORDER BY display_name";
        Add(command, "$project_id", projectId);
        return await ReadManyAsync(command, ReadProfile, cancellationToken);
    }

    async Task IRenderProfileRepository.DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM render_profiles WHERE id = $id";
        Add(command, "$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task IJobRepository.UpsertAsync(RenderJob job, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (id, project_id, render_profile_id, name, state, priority, assigned_worker_id, failure_category, error, output_directory, validation_json, created_at_utc, updated_at_utc, queued_at_utc, started_at_utc, finished_at_utc, cancellation_requested)
            VALUES ($id, $project_id, $render_profile_id, $name, $state, $priority, $assigned_worker_id, $failure_category, $error, $output_directory, $validation_json, $created_at_utc, $updated_at_utc, $queued_at_utc, $started_at_utc, $finished_at_utc, $cancellation_requested)
            ON CONFLICT(id) DO UPDATE SET project_id = excluded.project_id, render_profile_id = excluded.render_profile_id, name = excluded.name, state = excluded.state, priority = excluded.priority, assigned_worker_id = excluded.assigned_worker_id, failure_category = excluded.failure_category, error = excluded.error, output_directory = excluded.output_directory, validation_json = excluded.validation_json, updated_at_utc = excluded.updated_at_utc, queued_at_utc = excluded.queued_at_utc, started_at_utc = excluded.started_at_utc, finished_at_utc = excluded.finished_at_utc, cancellation_requested = excluded.cancellation_requested;
            """;
        AddJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<RenderJob?> IJobRepository.GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE id = $id";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    async Task<IReadOnlyList<RenderJob>> IJobRepository.ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs ORDER BY priority DESC, created_at_utc";
        return await ReadManyAsync(command, ReadJob, cancellationToken);
    }

    async Task IJobAttemptRepository.UpsertAsync(JobAttempt attempt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO job_attempts (id, job_id, attempt_number, worker_id, state, failure_category, error, command_line, log_file_path, started_at_utc, finished_at_utc, exit_code)
            VALUES ($id, $job_id, $attempt_number, $worker_id, $state, $failure_category, $error, $command_line, $log_file_path, $started_at_utc, $finished_at_utc, $exit_code)
            ON CONFLICT(id) DO UPDATE SET job_id = excluded.job_id, attempt_number = excluded.attempt_number, worker_id = excluded.worker_id, state = excluded.state, failure_category = excluded.failure_category, error = excluded.error, command_line = excluded.command_line, log_file_path = excluded.log_file_path, finished_at_utc = excluded.finished_at_utc, exit_code = excluded.exit_code;
            """;
        AddAttemptParameters(command, attempt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<JobAttempt?> IJobAttemptRepository.GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_attempts WHERE id = $id";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttempt(reader) : null;
    }

    async Task<IReadOnlyList<JobAttempt>> IJobAttemptRepository.ListForJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_attempts WHERE job_id = $job_id ORDER BY attempt_number";
        Add(command, "$job_id", jobId);
        return await ReadManyAsync(command, ReadAttempt, cancellationToken);
    }

    async Task<IReadOnlyDictionary<string, int>> IJobAttemptRepository.CountByJobAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id, COUNT(*) FROM job_attempts GROUP BY job_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetString(0)] = Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
        }

        return counts;
    }


    async Task IJobLeaseRepository.UpsertAsync(JobLease lease, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO job_leases (id, job_id, job_attempt_id, worker_id, acquired_at_utc, expires_at_utc, renewed_at_utc, released_at_utc, release_reason, is_active)
            VALUES ($id, $job_id, $job_attempt_id, $worker_id, $acquired_at_utc, $expires_at_utc, $renewed_at_utc, $released_at_utc, $release_reason, $is_active)
            ON CONFLICT(id) DO UPDATE SET job_id = excluded.job_id, job_attempt_id = excluded.job_attempt_id, worker_id = excluded.worker_id, expires_at_utc = excluded.expires_at_utc, renewed_at_utc = excluded.renewed_at_utc, released_at_utc = excluded.released_at_utc, release_reason = excluded.release_reason, is_active = excluded.is_active;
            """;
        AddLeaseParameters(command, lease);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<JobLease?> IJobLeaseRepository.GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_leases WHERE id = $id";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLease(reader) : null;
    }

    async Task<JobLease?> IJobLeaseRepository.GetActiveForJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_leases WHERE job_id = $job_id AND is_active = 1 ORDER BY expires_at_utc DESC LIMIT 1";
        Add(command, "$job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLease(reader) : null;
    }

    async Task<IReadOnlyList<JobLease>> IJobLeaseRepository.ListActiveAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_leases WHERE is_active = 1 ORDER BY expires_at_utc";
        return await ReadManyAsync(command, ReadLease, cancellationToken);
    }

    async Task<IReadOnlyList<JobLease>> IJobLeaseRepository.ListExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_leases WHERE is_active = 1 AND expires_at_utc <= $now_utc ORDER BY expires_at_utc";
        Add(command, "$now_utc", Format(nowUtc));
        return await ReadManyAsync(command, ReadLease, cancellationToken);
    }

    async Task IJobEventRepository.AppendAsync(JobEvent evt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO job_events (id, job_id, job_attempt_id, worker_id, state, failure_category, message, created_at_utc, data_json) VALUES ($id, $job_id, $job_attempt_id, $worker_id, $state, $failure_category, $message, $created_at_utc, $data_json);";
        AddEventParameters(command, evt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlyList<JobEvent>> IJobEventRepository.ListForJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM job_events WHERE job_id = $job_id ORDER BY created_at_utc";
        Add(command, "$job_id", jobId);
        return await ReadManyAsync(command, ReadEvent, cancellationToken);
    }

    public async Task<MaintenanceClearResult> ClearJobsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var before = await CountPersistedStateAsync(connection, cancellationToken);
        await DeleteTablesAsync(connection, ["job_events", "job_leases", "job_attempts", "jobs"], cancellationToken);
        await VacuumAsync(connection, cancellationToken);
        return before with { Workers = 0, Projects = 0, RenderProfiles = 0, Settings = 0 };
    }

    public async Task<MaintenanceClearResult> ClearAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var before = await CountPersistedStateAsync(connection, cancellationToken);
        await DeleteTablesAsync(connection, ["job_events", "job_leases", "job_attempts", "jobs", "render_profiles", "projects", "workers", "settings"], cancellationToken);
        await VacuumAsync(connection, cancellationToken);
        return before;
    }

    public async Task ApplyAsync(SchedulerStateMutation mutation, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (mutation.Job is not null)
            {
                await UpsertJobAsync(connection, transaction, mutation.Job, cancellationToken);
            }

            if (mutation.Attempt is not null)
            {
                await UpsertAttemptAsync(connection, transaction, mutation.Attempt, cancellationToken);
            }

            if (mutation.Lease is not null)
            {
                await UpsertLeaseAsync(connection, transaction, mutation.Lease, cancellationToken);
            }

            if (mutation.Event is not null)
            {
                await InsertEventAsync(connection, transaction, mutation.Event, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    async Task ISettingsRepository.UpsertAsync(FarmSetting setting, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (key, value_json, updated_at_utc) VALUES ($key, $value_json, $updated_at_utc) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at_utc = excluded.updated_at_utc;";
        Add(command, "$key", setting.Key);
        Add(command, "$value_json", setting.ValueJson);
        Add(command, "$updated_at_utc", Format(setting.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<FarmSetting?> ISettingsRepository.GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM settings WHERE key = $key";
        Add(command, "$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSetting(reader) : null;
    }

    async Task<IReadOnlyList<FarmSetting>> ISettingsRepository.ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM settings ORDER BY key";
        return await ReadManyAsync(command, ReadSetting, cancellationToken);
    }

    private static async Task<MaintenanceClearResult> CountPersistedStateAsync(SqliteConnection connection, CancellationToken cancellationToken) => new(
        Workers: await CountTableAsync(connection, "workers", cancellationToken),
        Projects: await CountTableAsync(connection, "projects", cancellationToken),
        RenderProfiles: await CountTableAsync(connection, "render_profiles", cancellationToken),
        Jobs: await CountTableAsync(connection, "jobs", cancellationToken),
        JobAttempts: await CountTableAsync(connection, "job_attempts", cancellationToken),
        JobLeases: await CountTableAsync(connection, "job_leases", cancellationToken),
        JobEvents: await CountTableAsync(connection, "job_events", cancellationToken),
        Settings: await CountTableAsync(connection, "settings", cancellationToken));

    private static async Task<int> CountTableAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task DeleteTablesAsync(SqliteConnection connection, IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task VacuumAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<RenderProfile?> GetProfileAsync(string column, string value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM render_profiles WHERE {column} = $value";
        Add(command, "$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureDatabaseDirectory(_connectionString);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    private static void EnsureDatabaseDirectory(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fullPath = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataSource));
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
        await ConfigureJournalModeAsync(connection, cancellationToken);
    }

    private static async Task ConfigureJournalModeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (await TryExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken))
        {
            return;
        }

        await TryExecutePragmaAsync(connection, "PRAGMA journal_mode = DELETE;", cancellationToken);
    }

    private static async Task<bool> TryExecutePragmaAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await ExecutePragmaAsync(connection, sql, cancellationToken);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task ExecutePragmaAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertJobAsync(SqliteConnection connection, SqliteTransaction transaction, RenderJob job, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs (id, project_id, render_profile_id, name, state, priority, assigned_worker_id, failure_category, error, output_directory, validation_json, created_at_utc, updated_at_utc, queued_at_utc, started_at_utc, finished_at_utc, cancellation_requested)
            VALUES ($id, $project_id, $render_profile_id, $name, $state, $priority, $assigned_worker_id, $failure_category, $error, $output_directory, $validation_json, $created_at_utc, $updated_at_utc, $queued_at_utc, $started_at_utc, $finished_at_utc, $cancellation_requested)
            ON CONFLICT(id) DO UPDATE SET project_id = excluded.project_id, render_profile_id = excluded.render_profile_id, name = excluded.name, state = excluded.state, priority = excluded.priority, assigned_worker_id = excluded.assigned_worker_id, failure_category = excluded.failure_category, error = excluded.error, output_directory = excluded.output_directory, validation_json = excluded.validation_json, updated_at_utc = excluded.updated_at_utc, queued_at_utc = excluded.queued_at_utc, started_at_utc = excluded.started_at_utc, finished_at_utc = excluded.finished_at_utc, cancellation_requested = excluded.cancellation_requested;
            """;
        AddJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertAttemptAsync(SqliteConnection connection, SqliteTransaction transaction, JobAttempt attempt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_attempts (id, job_id, attempt_number, worker_id, state, failure_category, error, command_line, log_file_path, started_at_utc, finished_at_utc, exit_code)
            VALUES ($id, $job_id, $attempt_number, $worker_id, $state, $failure_category, $error, $command_line, $log_file_path, $started_at_utc, $finished_at_utc, $exit_code)
            ON CONFLICT(id) DO UPDATE SET job_id = excluded.job_id, attempt_number = excluded.attempt_number, worker_id = excluded.worker_id, state = excluded.state, failure_category = excluded.failure_category, error = excluded.error, command_line = excluded.command_line, log_file_path = excluded.log_file_path, finished_at_utc = excluded.finished_at_utc, exit_code = excluded.exit_code;
            """;
        AddAttemptParameters(command, attempt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertLeaseAsync(SqliteConnection connection, SqliteTransaction transaction, JobLease lease, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_leases (id, job_id, job_attempt_id, worker_id, acquired_at_utc, expires_at_utc, renewed_at_utc, released_at_utc, release_reason, is_active)
            VALUES ($id, $job_id, $job_attempt_id, $worker_id, $acquired_at_utc, $expires_at_utc, $renewed_at_utc, $released_at_utc, $release_reason, $is_active)
            ON CONFLICT(id) DO UPDATE SET job_id = excluded.job_id, job_attempt_id = excluded.job_attempt_id, worker_id = excluded.worker_id, expires_at_utc = excluded.expires_at_utc, renewed_at_utc = excluded.renewed_at_utc, released_at_utc = excluded.released_at_utc, release_reason = excluded.release_reason, is_active = excluded.is_active;
            """;
        AddLeaseParameters(command, lease);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction, JobEvent evt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO job_events (id, job_id, job_attempt_id, worker_id, state, failure_category, message, created_at_utc, data_json) VALUES ($id, $job_id, $job_attempt_id, $worker_id, $state, $failure_category, $message, $created_at_utc, $data_json);";
        AddEventParameters(command, evt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<T>> ReadManyAsync<T>(SqliteCommand command, Func<SqliteDataReader, T> read, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(read(reader));
        }
        return results;
    }

    private static void AddWorkerParameters(SqliteCommand command, Worker worker)
    {
        Add(command, "$id", worker.Id); Add(command, "$name", worker.Name); Add(command, "$hostname", worker.Hostname); Add(command, "$ip_address", worker.IpAddress); Add(command, "$service_url", worker.ServiceUrl); Add(command, "$status", worker.Status.ToString()); Add(command, "$stage", worker.Stage); Add(command, "$current_job_id", worker.CurrentJobId); Add(command, "$agent_version", worker.AgentVersion); Add(command, "$capabilities_json", ToJson(worker.Capabilities)); Add(command, "$last_error", worker.LastError); Add(command, "$registered_at_utc", Format(worker.RegisteredAtUtc)); Add(command, "$last_heartbeat_utc", Format(worker.LastHeartbeatUtc));
    }

    private static void AddProfileParameters(SqliteCommand command, RenderProfile profile)
    {
        Add(command, "$id", profile.Id); Add(command, "$project_id", profile.ProjectId); Add(command, "$display_name", profile.DisplayName); Add(command, "$type", profile.Type.ToString()); Add(command, "$asset_path", profile.AssetPath); Add(command, "$command_template", profile.CommandTemplate); Add(command, "$default_output_type", profile.DefaultOutputType); Add(command, "$supports_chunking", profile.SupportsChunking ? 1 : 0); Add(command, "$settings_json", ToJson(profile.Settings));
    }

    private static void AddJobParameters(SqliteCommand command, RenderJob job)
    {
        Add(command, "$id", job.Id); Add(command, "$project_id", job.ProjectId); Add(command, "$render_profile_id", job.RenderProfileId); Add(command, "$name", job.Name); Add(command, "$state", job.State.ToString()); Add(command, "$priority", job.Priority); Add(command, "$assigned_worker_id", job.AssignedWorkerId); Add(command, "$failure_category", job.FailureCategory.ToString()); Add(command, "$error", job.Error); Add(command, "$output_directory", job.OutputDirectory); Add(command, "$validation_json", job.ValidationJson); Add(command, "$created_at_utc", Format(job.CreatedAtUtc)); Add(command, "$updated_at_utc", Format(job.UpdatedAtUtc)); Add(command, "$queued_at_utc", FormatNullable(job.QueuedAtUtc)); Add(command, "$started_at_utc", FormatNullable(job.StartedAtUtc)); Add(command, "$finished_at_utc", FormatNullable(job.FinishedAtUtc)); Add(command, "$cancellation_requested", job.CancellationRequested ? 1 : 0);
    }

    private static void AddAttemptParameters(SqliteCommand command, JobAttempt attempt)
    {
        Add(command, "$id", attempt.Id); Add(command, "$job_id", attempt.JobId); Add(command, "$attempt_number", attempt.AttemptNumber); Add(command, "$worker_id", attempt.WorkerId); Add(command, "$state", attempt.State.ToString()); Add(command, "$failure_category", attempt.FailureCategory.ToString()); Add(command, "$error", attempt.Error); Add(command, "$command_line", attempt.CommandLine); Add(command, "$log_file_path", attempt.LogFilePath); Add(command, "$started_at_utc", Format(attempt.StartedAtUtc)); Add(command, "$finished_at_utc", FormatNullable(attempt.FinishedAtUtc)); Add(command, "$exit_code", attempt.ExitCode);
    }

    private static void AddLeaseParameters(SqliteCommand command, JobLease lease)
    {
        Add(command, "$id", lease.Id); Add(command, "$job_id", lease.JobId); Add(command, "$job_attempt_id", lease.JobAttemptId); Add(command, "$worker_id", lease.WorkerId); Add(command, "$acquired_at_utc", Format(lease.AcquiredAtUtc)); Add(command, "$expires_at_utc", Format(lease.ExpiresAtUtc)); Add(command, "$renewed_at_utc", FormatNullable(lease.RenewedAtUtc)); Add(command, "$released_at_utc", FormatNullable(lease.ReleasedAtUtc)); Add(command, "$release_reason", lease.ReleaseReason); Add(command, "$is_active", lease.IsActive ? 1 : 0);
    }

    private static void AddEventParameters(SqliteCommand command, JobEvent evt)
    {
        Add(command, "$id", evt.Id); Add(command, "$job_id", evt.JobId); Add(command, "$job_attempt_id", evt.JobAttemptId); Add(command, "$worker_id", evt.WorkerId); Add(command, "$state", evt.State?.ToString()); Add(command, "$failure_category", evt.FailureCategory.ToString()); Add(command, "$message", evt.Message); Add(command, "$created_at_utc", Format(evt.CreatedAtUtc)); Add(command, "$data_json", evt.DataJson);
    }

    private static Worker ReadWorker(SqliteDataReader reader) => new(GetString(reader, "id"), GetString(reader, "name"), GetNullableString(reader, "hostname"), GetNullableString(reader, "ip_address"), GetNullableString(reader, "service_url"), ParseEnum(GetString(reader, "status"), WorkerStatus.Unknown), GetNullableString(reader, "stage"), GetNullableString(reader, "current_job_id"), GetNullableString(reader, "agent_version"), FromJson(GetString(reader, "capabilities_json"), WorkerCapabilities.Empty), GetNullableString(reader, "last_error"), ParseDate(GetString(reader, "registered_at_utc")), ParseDate(GetString(reader, "last_heartbeat_utc")));

    private static ProjectProfile ReadProject(SqliteDataReader reader) => new(GetString(reader, "id"), GetString(reader, "display_name"), GetNullableString(reader, "uproject_path"), GetNullableString(reader, "preferred_engine_version"), FromJson<IReadOnlyList<string>>(GetString(reader, "allowed_engine_versions_json"), Array.Empty<string>()), GetNullableString(reader, "shared_output_policy_id"), FromJson<IReadOnlyList<WorkerProjectPath>>(GetString(reader, "worker_paths_json"), Array.Empty<WorkerProjectPath>()));

    private static RenderProfile ReadProfile(SqliteDataReader reader) => new(GetString(reader, "id"), GetString(reader, "project_id"), GetString(reader, "display_name"), ParseEnum(GetString(reader, "type"), RenderProfileType.Manual), GetNullableString(reader, "asset_path"), GetNullableString(reader, "command_template"), GetString(reader, "default_output_type"), GetInt64(reader, "supports_chunking") == 1, FromJson<IReadOnlyDictionary<string, string>>(GetString(reader, "settings_json"), new Dictionary<string, string>()));

    private static RenderJob ReadJob(SqliteDataReader reader) => new(GetString(reader, "id"), GetString(reader, "project_id"), GetString(reader, "render_profile_id"), GetString(reader, "name"), ParseEnum(GetString(reader, "state"), JobState.Created), (int)GetInt64(reader, "priority"), GetNullableString(reader, "assigned_worker_id"), ParseEnum(GetString(reader, "failure_category"), FailureCategory.None), GetNullableString(reader, "error"), GetNullableString(reader, "output_directory"), ParseDate(GetString(reader, "created_at_utc")), ParseDate(GetString(reader, "updated_at_utc")), ParseNullableDate(GetNullableString(reader, "queued_at_utc")), ParseNullableDate(GetNullableString(reader, "started_at_utc")), ParseNullableDate(GetNullableString(reader, "finished_at_utc")), GetInt64(reader, "cancellation_requested") == 1, GetNullableString(reader, "validation_json"));

    private static JobAttempt ReadAttempt(SqliteDataReader reader) => new(GetString(reader, "id"), GetString(reader, "job_id"), (int)GetInt64(reader, "attempt_number"), GetNullableString(reader, "worker_id"), ParseEnum(GetString(reader, "state"), JobState.Created), ParseEnum(GetString(reader, "failure_category"), FailureCategory.None), GetNullableString(reader, "error"), GetNullableString(reader, "command_line"), GetNullableString(reader, "log_file_path"), ParseDate(GetString(reader, "started_at_utc")), ParseNullableDate(GetNullableString(reader, "finished_at_utc")), GetNullableInt(reader, "exit_code"));

    private static JobLease ReadLease(SqliteDataReader reader) => new(GetString(reader, "id"), GetString(reader, "job_id"), GetString(reader, "job_attempt_id"), GetString(reader, "worker_id"), ParseDate(GetString(reader, "acquired_at_utc")), ParseDate(GetString(reader, "expires_at_utc")), ParseNullableDate(GetNullableString(reader, "renewed_at_utc")), ParseNullableDate(GetNullableString(reader, "released_at_utc")), GetNullableString(reader, "release_reason"), GetInt64(reader, "is_active") == 1);

    private static JobEvent ReadEvent(SqliteDataReader reader) => new(GetString(reader, "id"), GetString(reader, "job_id"), GetNullableString(reader, "job_attempt_id"), GetNullableString(reader, "worker_id"), ParseNullableEnum<JobState>(GetNullableString(reader, "state")), ParseEnum(GetString(reader, "failure_category"), FailureCategory.None), GetString(reader, "message"), ParseDate(GetString(reader, "created_at_utc")), GetNullableString(reader, "data_json"));

    private static FarmSetting ReadSetting(SqliteDataReader reader) => new(GetString(reader, "key"), GetString(reader, "value_json"), ParseDate(GetString(reader, "updated_at_utc")));

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T FromJson<T>(string json, T fallback) => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? FormatNullable(DateTimeOffset? value) => value is null ? null : Format(value.Value);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ParseNullableDate(string? value) => string.IsNullOrWhiteSpace(value) ? null : ParseDate(value);
    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum => Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
    private static T? ParseNullableEnum<T>(string? value) where T : struct, Enum => string.IsNullOrWhiteSpace(value) ? null : ParseEnum<T>(value, default);
    private static string GetString(SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long GetInt64(SqliteDataReader reader, string name) => reader.GetInt64(reader.GetOrdinal(name));

    private static int? GetNullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}





