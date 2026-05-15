using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
INSERT OR IGNORE INTO symbols (
    id, document_id, project_id, fqn, name, kind, namespace, containing_type,
    accessibility, is_static, is_abstract, is_sealed, is_async, is_partial,
    is_generic, is_extension_method, is_disposable, is_volatile,
    line_start, line_end, loc, parameter_count, return_type,
    has_callback, has_ui_dispatch, has_task_spawn, has_background_worker,
    has_do_events, has_lock
) VALUES (
    @id, @docId, @projId, @fqn, @name, @kind, @ns, @parent,
    @acc, @static, @abstract, @sealed, @async, @partial,
    @generic, @ext, @disposable, @volatile,
    @lstart, @lend, @loc, @pcount, @ret,
    @hascb, @uidisp, @taskspawn, @bgworker,
    @doevents, @haslock
)";
            
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

            foreach (var call in methodCalls)
            {
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

            foreach (var inheritance in inheritances)
            {
                pDerived.Value = inheritance.DerivedId;
                pBase.Value = inheritance.BaseId;
                pKind.Value = inheritance.Kind;
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
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
    }
}
