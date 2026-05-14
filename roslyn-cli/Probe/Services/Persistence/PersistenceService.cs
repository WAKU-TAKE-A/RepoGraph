using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using Probe.Services.Analysis;

namespace Probe.Services.Persistence
{
    public class PersistenceService
    {
        private readonly string _connectionString;
        private readonly ILogger<PersistenceService> _logger;

        public PersistenceService(string dbPath, ILogger<PersistenceService> logger)
        {
            _connectionString = $"Data Source={dbPath}";
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = SqliteSchema.Tables + SqliteSchema.Indexes;
            await command.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Database initialized at {Path}", _connectionString);
        }

        public async Task SaveSymbolAsync(SymbolData data)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR REPLACE INTO symbols (
    id, document_id, project_id, fqn, name, kind, namespace, containing_type,
    accessibility, is_static, is_abstract, is_sealed, is_async, is_partial,
    is_generic, is_extension_method, is_disposable, line_start, line_end,
    loc, parameter_count, return_type
) VALUES (
    @id, @docId, @projId, @fqn, @name, @kind, @ns, @parent,
    @acc, @static, @abstract, @sealed, @async, @partial,
    @generic, @ext, @disposable, @lstart, @lend,
    @loc, @pcount, @ret
)";
            command.Parameters.AddWithValue("@id", data.Id);
            command.Parameters.AddWithValue("@docId", (object)data.DocumentId ?? DBNull.Value);
            command.Parameters.AddWithValue("@projId", data.ProjectId);
            command.Parameters.AddWithValue("@fqn", data.Fqn);
            command.Parameters.AddWithValue("@name", data.Name);
            command.Parameters.AddWithValue("@kind", data.Kind);
            command.Parameters.AddWithValue("@ns", (object)data.Namespace ?? DBNull.Value);
            command.Parameters.AddWithValue("@parent", (object)data.ContainingType ?? DBNull.Value);
            command.Parameters.AddWithValue("@acc", data.Accessibility);
            command.Parameters.AddWithValue("@static", data.IsStatic ? 1 : 0);
            command.Parameters.AddWithValue("@abstract", data.IsAbstract ? 1 : 0);
            command.Parameters.AddWithValue("@sealed", data.IsSealed ? 1 : 0);
            command.Parameters.AddWithValue("@async", data.IsAsync ? 1 : 0);
            command.Parameters.AddWithValue("@partial", data.IsPartial ? 1 : 0);
            command.Parameters.AddWithValue("@generic", data.IsGeneric ? 1 : 0);
            command.Parameters.AddWithValue("@ext", data.IsExtensionMethod ? 1 : 0);
            command.Parameters.AddWithValue("@disposable", data.IsDisposable ? 1 : 0);
            command.Parameters.AddWithValue("@lstart", data.LineStart);
            command.Parameters.AddWithValue("@lend", data.LineEnd);
            command.Parameters.AddWithValue("@loc", data.Loc);
            command.Parameters.AddWithValue("@pcount", data.ParameterCount);
            command.Parameters.AddWithValue("@ret", (object)data.ReturnType ?? DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveProjectAsync(string id, string solutionId, string runId, string name, string path)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR REPLACE INTO projects (id, solution_id, analysis_run_id, name, file_path, analysis_status)
VALUES (@id, @sid, @rid, @name, @path, 'success')";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@sid", solutionId);
            command.Parameters.AddWithValue("@rid", runId);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@path", path);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveDocumentAsync(string id, string projectId, string path, string name)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR REPLACE INTO documents (id, project_id, file_path, file_name)
VALUES (@id, @pid, @path, @name)";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@pid", projectId);
            command.Parameters.AddWithValue("@path", path);
            command.Parameters.AddWithValue("@name", name);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveAnalysisRunAsync(string id, string path)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR REPLACE INTO analysis_runs (id, solution_path, started_at, status)
VALUES (@id, @path, @start, 'running')";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@path", path);
            command.Parameters.AddWithValue("@start", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveSolutionAsync(string id, string runId, string path, string name)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR REPLACE INTO solutions (id, analysis_run_id, file_path, name)
VALUES (@id, @rid, @path, @name)";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@rid", runId);
            command.Parameters.AddWithValue("@path", path);
            command.Parameters.AddWithValue("@name", name);
            await command.ExecuteNonQueryAsync();
        }
    }
}
