using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
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

            await EnsureSchemaAsync(connection);
            
            _logger.LogInformation("Database initialized at {Path}", _connectionString);
        }

        private async Task EnsureSchemaAsync(SqliteConnection connection)
        {
            await EnsureColumnsAsync(connection, "symbols", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["has_ui_dispatch"] = "ALTER TABLE symbols ADD COLUMN has_ui_dispatch INTEGER NOT NULL DEFAULT 0",
                ["has_task_spawn"] = "ALTER TABLE symbols ADD COLUMN has_task_spawn INTEGER NOT NULL DEFAULT 0",
                ["has_background_worker"] = "ALTER TABLE symbols ADD COLUMN has_background_worker INTEGER NOT NULL DEFAULT 0",
                ["has_do_events"] = "ALTER TABLE symbols ADD COLUMN has_do_events INTEGER NOT NULL DEFAULT 0",
                ["has_lock"] = "ALTER TABLE symbols ADD COLUMN has_lock INTEGER NOT NULL DEFAULT 0",
                ["has_thread_start"] = "ALTER TABLE symbols ADD COLUMN has_thread_start INTEGER NOT NULL DEFAULT 0",
                ["has_blocking_wait"] = "ALTER TABLE symbols ADD COLUMN has_blocking_wait INTEGER NOT NULL DEFAULT 0",
                ["fan_in"] = "ALTER TABLE symbols ADD COLUMN fan_in INTEGER NOT NULL DEFAULT 0"
            });
        }

        private static async Task EnsureColumnsAsync(SqliteConnection connection, string tableName, IReadOnlyDictionary<string, string> migrations)
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info({tableName})";
                using var reader = await pragma.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            var missingColumns = migrations
                .Where(kvp => !existingColumns.Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();

            if (missingColumns.Count == 0)
            {
                return;
            }

            foreach (var migration in missingColumns)
            {
                using var command = connection.CreateCommand();
                command.CommandText = migration;
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task ResetAnalysisDataAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
DELETE FROM project_dependencies;
DELETE FROM field_accesses;
DELETE FROM inheritance;
DELETE FROM method_calls;
DELETE FROM symbol_relationships;
DELETE FROM symbols;
DELETE FROM documents;
DELETE FROM projects;
DELETE FROM solutions;";
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        public async Task ResetProjectAnalysisDataAsync(string projectId)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
DELETE FROM project_dependencies WHERE source_project_id = @projectId;

DELETE FROM method_calls
WHERE caller_id IN (SELECT id FROM symbols WHERE project_id = @projectId)
   OR callee_id IN (SELECT id FROM symbols WHERE project_id = @projectId);

DELETE FROM inheritance
WHERE derived_id IN (SELECT id FROM symbols WHERE project_id = @projectId)
   OR base_id IN (SELECT id FROM symbols WHERE project_id = @projectId);

DELETE FROM symbol_relationships
WHERE source_id IN (SELECT id FROM symbols WHERE project_id = @projectId)
   OR target_id IN (SELECT id FROM symbols WHERE project_id = @projectId);

DELETE FROM field_accesses
WHERE accessor_fqn IN (SELECT fqn FROM symbols WHERE project_id = @projectId)
   OR target_fqn IN (SELECT fqn FROM symbols WHERE project_id = @projectId);

DELETE FROM symbols WHERE project_id = @projectId;
DELETE FROM documents WHERE project_id = @projectId;
DELETE FROM projects WHERE id = @projectId;";
            command.Parameters.AddWithValue("@projectId", projectId);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        public async Task<DateTime?> GetLastRunTimeAsync(string solutionPath)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT MAX(started_at)
FROM analysis_runs
WHERE solution_path = @path AND status = 'completed'";
            command.Parameters.AddWithValue("@path", solutionPath);

            var result = await command.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
            {
                if (DateTime.TryParse(result.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dateTime))
                {
                    return dateTime;
                }
            }
            return null;
        }

        public async Task SaveSymbolsAsync(IEnumerable<SymbolData> symbols)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            
            command.CommandText = @"
INSERT INTO symbols (
    id, document_id, project_id, fqn, name, kind, namespace, containing_type,
    accessibility, is_static, is_abstract, is_sealed, is_async, is_partial,
    is_generic, is_extension_method, is_disposable, is_volatile,
    line_start, line_end, loc, parameter_count, return_type,
    has_callback, has_ui_dispatch, has_task_spawn, has_background_worker,
    has_do_events, has_lock, has_thread_start, has_blocking_wait, fan_in
) VALUES (
    @id, @docId, @projId, @fqn, @name, @kind, @ns, @parent,
    @acc, @static, @abstract, @sealed, @async, @partial,
    @generic, @ext, @disposable, @volatile,
    @lstart, @lend, @loc, @pcount, @ret,
    @hascb, @uidisp, @taskspawn, @bgworker,
    @doevents, @haslock, @threadstart, @blockingwait, @fanin
)
ON CONFLICT(fqn) DO UPDATE SET
    id = excluded.id,
    document_id = COALESCE(symbols.document_id, excluded.document_id),
    project_id = excluded.project_id,
    name = excluded.name,
    kind = excluded.kind,
    namespace = COALESCE(symbols.namespace, excluded.namespace),
    containing_type = COALESCE(symbols.containing_type, excluded.containing_type),
    accessibility = excluded.accessibility,
    is_static = CASE WHEN symbols.is_static = 1 OR excluded.is_static = 1 THEN 1 ELSE 0 END,
    is_abstract = CASE WHEN symbols.is_abstract = 1 OR excluded.is_abstract = 1 THEN 1 ELSE 0 END,
    is_sealed = CASE WHEN symbols.is_sealed = 1 OR excluded.is_sealed = 1 THEN 1 ELSE 0 END,
    is_async = CASE WHEN symbols.is_async = 1 OR excluded.is_async = 1 THEN 1 ELSE 0 END,
    is_partial = CASE WHEN symbols.is_partial = 1 OR excluded.is_partial = 1 THEN 1 ELSE 0 END,
    is_generic = CASE WHEN symbols.is_generic = 1 OR excluded.is_generic = 1 THEN 1 ELSE 0 END,
    is_extension_method = CASE WHEN symbols.is_extension_method = 1 OR excluded.is_extension_method = 1 THEN 1 ELSE 0 END,
    is_disposable = CASE WHEN symbols.is_disposable = 1 OR excluded.is_disposable = 1 THEN 1 ELSE 0 END,
    is_volatile = CASE WHEN symbols.is_volatile = 1 OR excluded.is_volatile = 1 THEN 1 ELSE 0 END,
    line_start = CASE
        WHEN symbols.line_start IS NULL THEN excluded.line_start
        WHEN excluded.line_start IS NULL THEN symbols.line_start
        ELSE MIN(symbols.line_start, excluded.line_start)
    END,
    line_end = CASE
        WHEN symbols.line_end IS NULL THEN excluded.line_end
        WHEN excluded.line_end IS NULL THEN symbols.line_end
        ELSE MAX(symbols.line_end, excluded.line_end)
    END,
    loc = COALESCE(symbols.loc, 0) + COALESCE(excluded.loc, 0),
    parameter_count = MAX(COALESCE(symbols.parameter_count, 0), COALESCE(excluded.parameter_count, 0)),
    return_type = COALESCE(symbols.return_type, excluded.return_type),
    has_callback = CASE WHEN symbols.has_callback = 1 OR excluded.has_callback = 1 THEN 1 ELSE 0 END,
    has_ui_dispatch = CASE WHEN symbols.has_ui_dispatch = 1 OR excluded.has_ui_dispatch = 1 THEN 1 ELSE 0 END,
    has_task_spawn = CASE WHEN symbols.has_task_spawn = 1 OR excluded.has_task_spawn = 1 THEN 1 ELSE 0 END,
    has_background_worker = CASE WHEN symbols.has_background_worker = 1 OR excluded.has_background_worker = 1 THEN 1 ELSE 0 END,
    has_do_events = CASE WHEN symbols.has_do_events = 1 OR excluded.has_do_events = 1 THEN 1 ELSE 0 END,
    has_lock = CASE WHEN symbols.has_lock = 1 OR excluded.has_lock = 1 THEN 1 ELSE 0 END,
    has_thread_start = CASE WHEN symbols.has_thread_start = 1 OR excluded.has_thread_start = 1 THEN 1 ELSE 0 END,
    has_blocking_wait = CASE WHEN symbols.has_blocking_wait = 1 OR excluded.has_blocking_wait = 1 THEN 1 ELSE 0 END,
    fan_in = excluded.fan_in";
            
            var pId = command.Parameters.Add("@id", SqliteType.Text);
            var pDocId = command.Parameters.Add("@docId", SqliteType.Text);
            var pProjId = command.Parameters.Add("@projId", SqliteType.Text);
            var pFqn = command.Parameters.Add("@fqn", SqliteType.Text);
            var pName = command.Parameters.Add("@name", SqliteType.Text);
            var pKind = command.Parameters.Add("@kind", SqliteType.Text);
            var pNs = command.Parameters.Add("@ns", SqliteType.Text);
            var pParent = command.Parameters.Add("@parent", SqliteType.Text);
            var pAcc = command.Parameters.Add("@acc", SqliteType.Text);
            var pStatic = command.Parameters.Add("@static", SqliteType.Integer);
            var pAbstract = command.Parameters.Add("@abstract", SqliteType.Integer);
            var pSealed = command.Parameters.Add("@sealed", SqliteType.Integer);
            var pAsync = command.Parameters.Add("@async", SqliteType.Integer);
            var pPartial = command.Parameters.Add("@partial", SqliteType.Integer);
            var pGeneric = command.Parameters.Add("@generic", SqliteType.Integer);
            var pExt = command.Parameters.Add("@ext", SqliteType.Integer);
            var pDisp = command.Parameters.Add("@disposable", SqliteType.Integer);
            var pVolatile = command.Parameters.Add("@volatile", SqliteType.Integer);
            var pLstart = command.Parameters.Add("@lstart", SqliteType.Integer);
            var pLend = command.Parameters.Add("@lend", SqliteType.Integer);
            var pLoc = command.Parameters.Add("@loc", SqliteType.Integer);
            var pCount = command.Parameters.Add("@pcount", SqliteType.Integer);
            var pRet = command.Parameters.Add("@ret", SqliteType.Text);
            var pHasCb = command.Parameters.Add("@hascb", SqliteType.Integer);
            var pUiDisp = command.Parameters.Add("@uidisp", SqliteType.Integer);
            var pTaskSpawn = command.Parameters.Add("@taskspawn", SqliteType.Integer);
            var pBgWorker = command.Parameters.Add("@bgworker", SqliteType.Integer);
            var pDoEvents = command.Parameters.Add("@doevents", SqliteType.Integer);
            var pHasLock = command.Parameters.Add("@haslock", SqliteType.Integer);
            var pThreadStart = command.Parameters.Add("@threadstart", SqliteType.Integer);
            var pBlockingWait = command.Parameters.Add("@blockingwait", SqliteType.Integer);
            var pFanIn = command.Parameters.Add("@fanin", SqliteType.Integer);

            foreach (var data in symbols)
            {
                pId.Value = data.Id;
                pDocId.Value = (object?)data.DocumentId ?? DBNull.Value;
                pProjId.Value = data.ProjectId;
                pFqn.Value = data.Fqn;
                pName.Value = data.Name;
                pKind.Value = data.Kind;
                pNs.Value = (object?)data.Namespace ?? DBNull.Value;
                pParent.Value = (object?)data.ContainingType ?? DBNull.Value;
                pAcc.Value = data.Accessibility;
                pStatic.Value = data.IsStatic ? 1 : 0;
                pAbstract.Value = data.IsAbstract ? 1 : 0;
                pSealed.Value = data.IsSealed ? 1 : 0;
                pAsync.Value = data.IsAsync ? 1 : 0;
                pPartial.Value = data.IsPartial ? 1 : 0;
                pGeneric.Value = data.IsGeneric ? 1 : 0;
                pExt.Value = data.IsExtensionMethod ? 1 : 0;
                pDisp.Value = data.IsDisposable ? 1 : 0;
                pVolatile.Value = data.IsVolatile ? 1 : 0;
                pLstart.Value = data.LineStart;
                pLend.Value = data.LineEnd;
                pLoc.Value = data.Loc;
                pCount.Value = data.ParameterCount;
                pRet.Value = (object?)data.ReturnType ?? DBNull.Value;
                pHasCb.Value = data.HasCallback ? 1 : 0;
                pUiDisp.Value = data.HasUiDispatch ? 1 : 0;
                pTaskSpawn.Value = data.HasTaskSpawn ? 1 : 0;
                pBgWorker.Value = data.HasBackgroundWorker ? 1 : 0;
                pDoEvents.Value = data.HasDoEvents ? 1 : 0;
                pHasLock.Value = data.HasLock ? 1 : 0;
                pThreadStart.Value = data.HasThreadStart ? 1 : 0;
                pBlockingWait.Value = data.HasBlockingWait ? 1 : 0;
                pFanIn.Value = data.FanIn;
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public async Task SaveMethodCallsAsync(IEnumerable<MethodCallData> methodCalls)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = @"
INSERT INTO method_calls (caller_id, callee_id, call_count, call_type)
SELECT s1.id, s2.id, @count, @callType
FROM symbols s1, symbols s2
WHERE s1.fqn = @callerFqn AND s2.fqn = @calleeFqn";

            var pCaller = command.Parameters.Add("@callerFqn", SqliteType.Text);
            var pCallee = command.Parameters.Add("@calleeFqn", SqliteType.Text);
            var pCount = command.Parameters.Add("@count", SqliteType.Integer);
            var pCallType = command.Parameters.Add("@callType", SqliteType.Text);

            var uniqueCalls = new HashSet<string>();
            foreach (var call in methodCalls)
            {
                var key = $"{call.CallerId}|{call.CalleeId}|{call.CallType}";
                if (!uniqueCalls.Add(key))
                {
                    continue;
                }
                pCaller.Value = call.CallerId;
                pCallee.Value = call.CalleeId;
                pCount.Value = call.CallCount;
                pCallType.Value = call.CallType;
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public async Task SaveFieldAccessesAsync(IEnumerable<FieldAccessData> fieldAccesses)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = @"
INSERT OR IGNORE INTO field_accesses (accessor_fqn, target_fqn, access_kind, is_external)
VALUES (@accessor, @target, @kind, @external)";

            var pAccessor = command.Parameters.Add("@accessor", SqliteType.Text);
            var pTarget = command.Parameters.Add("@target", SqliteType.Text);
            var pKind = command.Parameters.Add("@kind", SqliteType.Text);
            var pExternal = command.Parameters.Add("@external", SqliteType.Integer);

            foreach (var access in fieldAccesses)
            {
                pAccessor.Value = access.AccessorFqn;
                pTarget.Value = access.TargetFqn;
                pKind.Value = access.AccessKind;
                pExternal.Value = access.IsExternal ? 1 : 0;
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public async Task SaveProjectDependenciesAsync(IEnumerable<ProjectDependencyData> dependencies)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = @"
INSERT OR IGNORE INTO project_dependencies (source_project_id, target_project_id)
VALUES (@source, @target)";

            var pSource = command.Parameters.Add("@source", SqliteType.Text);
            var pTarget = command.Parameters.Add("@target", SqliteType.Text);

            foreach (var dependency in dependencies)
            {
                pSource.Value = dependency.SourceProjectId;
                pTarget.Value = dependency.TargetProjectId;
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        public async Task SaveInheritancesAsync(IEnumerable<InheritanceData> inheritances)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = @"
INSERT INTO inheritance (derived_id, base_id, kind)
SELECT s1.id, s2.id, @kind
FROM symbols s1, symbols s2
WHERE s1.fqn = @derivedFqn AND s2.fqn = @baseFqn";

            var pDerived = command.Parameters.Add("@derivedFqn", SqliteType.Text);
            var pBase = command.Parameters.Add("@baseFqn", SqliteType.Text);
            var pKind = command.Parameters.Add("@kind", SqliteType.Text);

            var uniqueInheritances = new HashSet<string>();
            foreach (var inheritance in inheritances)
            {
                var key = $"{inheritance.DerivedId}|{inheritance.BaseId}|{inheritance.Kind}";
                if (!uniqueInheritances.Add(key))
                {
                    continue;
                }
                pDerived.Value = inheritance.DerivedId;
                pBase.Value = inheritance.BaseId;
                pKind.Value = inheritance.Kind;
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public async Task SaveTypeDependenciesAsync(IEnumerable<TypeDependencyData> dependencies)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = @"
INSERT INTO symbol_relationships (source_id, target_id, relationship_type)
SELECT s1.id, s2.id, @kind
FROM symbols s1, symbols s2
WHERE s1.fqn = @sourceFqn AND s2.fqn = @targetFqn
  AND NOT EXISTS (
      SELECT 1
      FROM symbol_relationships sr
      WHERE sr.source_id = s1.id
        AND sr.target_id = s2.id
        AND sr.relationship_type = @kind
  )";

            var pSource = command.Parameters.Add("@sourceFqn", SqliteType.Text);
            var pTarget = command.Parameters.Add("@targetFqn", SqliteType.Text);
            var pKind = command.Parameters.Add("@kind", SqliteType.Text);

            var uniqueDependencies = new HashSet<string>();
            foreach (var dep in dependencies)
            {
                var key = $"{dep.SourceFqn}|{dep.TargetFqn}|{dep.Kind}";
                if (!uniqueDependencies.Add(key))
                {
                    continue;
                }
                pSource.Value = dep.SourceFqn;
                pTarget.Value = dep.TargetFqn;
                pKind.Value = dep.Kind;
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        public async Task SaveProjectAsync(
            string id,
            string solutionId,
            string runId,
            string name,
            string path,
            string? assemblyName,
            string? targetFramework,
            string? projectType,
            bool isTestProject,
            bool isSdkStyle,
            int documentCount)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR REPLACE INTO projects (
    id, solution_id, analysis_run_id, name, file_path,
    assembly_name, target_framework, project_type, is_test_project,
    is_sdk_style, document_count, analysis_status
)
VALUES (
    @id, @sid, @rid, @name, @path,
    @assemblyName, @targetFramework, @projectType, @isTestProject,
    @isSdkStyle, @documentCount, 'success'
)";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@sid", solutionId);
            command.Parameters.AddWithValue("@rid", runId);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@path", path);
            command.Parameters.AddWithValue("@assemblyName", (object?)assemblyName ?? DBNull.Value);
            command.Parameters.AddWithValue("@targetFramework", (object?)targetFramework ?? DBNull.Value);
            command.Parameters.AddWithValue("@projectType", (object?)projectType ?? DBNull.Value);
            command.Parameters.AddWithValue("@isTestProject", isTestProject ? 1 : 0);
            command.Parameters.AddWithValue("@isSdkStyle", isSdkStyle ? 1 : 0);
            command.Parameters.AddWithValue("@documentCount", documentCount);
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

        public async Task UpdateAnalysisRunStatusAsync(string id, string status)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE analysis_runs 
SET status = @status, completed_at = @completed 
WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@completed", DateTime.UtcNow.ToString("O"));
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

        public async Task UpdateMetricsAsync()
        {
            _logger.LogInformation("Calculating symbol metrics (fan-in)...");
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = @"
UPDATE symbols
SET fan_in = (
    SELECT COUNT(*) FROM method_calls mc WHERE mc.callee_id = symbols.id
) + (
    SELECT COUNT(*) FROM inheritance i WHERE i.base_id = symbols.id
) + (
    SELECT COUNT(*) FROM field_accesses fa WHERE fa.target_fqn = symbols.fqn
) + (
    SELECT COUNT(*) FROM symbol_relationships sr WHERE sr.target_id = symbols.id
)";
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            _logger.LogInformation("Metrics updated.");
        }
    }
}
