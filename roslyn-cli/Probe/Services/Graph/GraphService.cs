using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Probe.Services.Graph
{
    public class GraphService
    {
        private readonly string _connectionString;
        private readonly ILogger<GraphService> _logger;
        private readonly string _outputDir;

        public GraphService(string dbPath, string outputDir, ILogger<GraphService> logger)
        {
            _connectionString = $"Data Source={dbPath}";
            _outputDir = outputDir;
            _logger = logger;
            Directory.CreateDirectory(_outputDir);
        }

        public async Task ExportGraphsAsync(string runId)
        {
            _logger.LogInformation("Exporting graphs for run {RunId}...", runId);
            await ExportInheritanceGraphAsync(runId);
            await ExportCallGraphAsync(runId);
            await ExportFieldAccessGraphAsync(runId);
        }

        private async Task ExportInheritanceGraphAsync(string runId)
        {
            var nodes = new List<object>();
            var links = new List<object>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmdNodes = connection.CreateCommand();
            cmdNodes.CommandText = "SELECT id, fqn, kind FROM symbols";
            using var readerNodes = await cmdNodes.ExecuteReaderAsync();
            var symbolIdToFqn = new Dictionary<string, string>();
            while (await readerNodes.ReadAsync())
            {
                var id = readerNodes.GetString(0);
                var fqn = readerNodes.GetString(1);
                var kind = readerNodes.GetString(2);
                symbolIdToFqn[id] = fqn;
                
                if (kind == "class" || kind == "interface")
                {
                    nodes.Add(new { id = fqn, kind = kind });
                }
            }

            using var cmdEdges = connection.CreateCommand();
            cmdEdges.CommandText = "SELECT derived_id, base_id, kind FROM inheritance";
            using var readerEdges = await cmdEdges.ExecuteReaderAsync();
            while (await readerEdges.ReadAsync())
            {
                var derivedId = readerEdges.GetString(0);
                var baseId = readerEdges.GetString(1);
                var kind = readerEdges.GetString(2);

                if (symbolIdToFqn.TryGetValue(derivedId, out var derivedFqn) && 
                    symbolIdToFqn.TryGetValue(baseId, out var baseFqn))
                {
                    links.Add(new { source = derivedFqn, target = baseFqn, type = kind });
                }
            }

            await WriteJsonAsync("inheritance_graph.json", "inheritance", runId, nodes, links);
        }

        /// <summary>
        /// Export call graph with thread boundary metadata on nodes.
        /// </summary>
        private async Task ExportCallGraphAsync(string runId)
        {
            var nodes = new List<object>();
            var links = new List<object>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // Include thread boundary flags in node data
            using var cmdNodes = connection.CreateCommand();
            cmdNodes.CommandText = @"
SELECT id, fqn, kind, 
       has_ui_dispatch, has_task_spawn, has_background_worker, has_do_events, has_lock,
       has_callback, is_async
FROM symbols 
WHERE kind = 'method' OR kind = 'constructor'";
            using var readerNodes = await cmdNodes.ExecuteReaderAsync();
            var symbolIdToFqn = new Dictionary<string, string>();
            while (await readerNodes.ReadAsync())
            {
                var id = readerNodes.GetString(0);
                var fqn = readerNodes.GetString(1);
                var kind = readerNodes.GetString(2);
                var hasUiDispatch = readerNodes.GetInt32(3) == 1;
                var hasTaskSpawn = readerNodes.GetInt32(4) == 1;
                var hasBgWorker = readerNodes.GetInt32(5) == 1;
                var hasDoEvents = readerNodes.GetInt32(6) == 1;
                var hasLock = readerNodes.GetInt32(7) == 1;
                var hasCallback = readerNodes.GetInt32(8) == 1;
                var isAsync = readerNodes.GetInt32(9) == 1;

                symbolIdToFqn[id] = fqn;
                nodes.Add(new
                {
                    id = fqn,
                    kind = kind,
                    has_ui_dispatch = hasUiDispatch,
                    has_task_spawn = hasTaskSpawn,
                    has_background_worker = hasBgWorker,
                    has_do_events = hasDoEvents,
                    has_lock = hasLock,
                    has_callback = hasCallback,
                    is_async = isAsync
                });
            }

            using var cmdEdges = connection.CreateCommand();
            cmdEdges.CommandText = "SELECT caller_id, callee_id, call_count, call_type FROM method_calls";
            using var readerEdges = await cmdEdges.ExecuteReaderAsync();
            while (await readerEdges.ReadAsync())
            {
                var callerId = readerEdges.GetString(0);
                var calleeId = readerEdges.GetString(1);
                var count = readerEdges.GetInt32(2);
                var callType = readerEdges.GetString(3);

                if (symbolIdToFqn.TryGetValue(callerId, out var callerFqn) && 
                    symbolIdToFqn.TryGetValue(calleeId, out var calleeFqn))
                {
                    links.Add(new { source = callerFqn, target = calleeFqn, type = callType, call_count = count });
                }
            }

            await WriteJsonAsync("call_graph.json", "call", runId, nodes, links);
        }

        /// <summary>
        /// Export field access graph showing which methods read/write which fields.
        /// </summary>
        private async Task ExportFieldAccessGraphAsync(string runId)
        {
            var nodes = new HashSet<string>();
            var nodeList = new List<object>();
            var links = new List<object>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT accessor_fqn, target_fqn, access_kind, is_external FROM field_accesses";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var accessorFqn = reader.GetString(0);
                var targetFqn = reader.GetString(1);
                var accessKind = reader.GetString(2);
                var isExternal = reader.GetInt32(3) == 1;

                nodes.Add(accessorFqn);
                nodes.Add(targetFqn);

                links.Add(new
                {
                    source = accessorFqn,
                    target = targetFqn,
                    type = accessKind,
                    is_external = isExternal
                });
            }

            // Build node list with kind info from symbols table
            var symbolKinds = new Dictionary<string, string>();
            using var cmdSymbols = connection.CreateCommand();
            cmdSymbols.CommandText = "SELECT fqn, kind, is_volatile FROM symbols";
            using var readerSymbols = await cmdSymbols.ExecuteReaderAsync();
            while (await readerSymbols.ReadAsync())
            {
                var fqn = readerSymbols.GetString(0);
                var kind = readerSymbols.GetString(1);
                var isVolatile = readerSymbols.GetInt32(2) == 1;
                symbolKinds[fqn] = kind;
                if (nodes.Contains(fqn))
                {
                    nodeList.Add(new { id = fqn, kind = kind, is_volatile = isVolatile });
                }
            }

            // Add nodes not found in symbols (external references)
            foreach (var fqn in nodes)
            {
                if (!symbolKinds.ContainsKey(fqn))
                {
                    nodeList.Add(new { id = fqn, kind = "unknown", is_volatile = false });
                }
            }

            await WriteJsonAsync("field_access_graph.json", "field_access", runId, nodeList, links);
            _logger.LogInformation("Field access graph: {NodeCount} nodes, {LinkCount} links", nodeList.Count, links.Count);
        }

        private async Task WriteJsonAsync(string filename, string graphType, string runId, object nodes, object links)
        {
            var graphObj = new
            {
                directed = true,
                multigraph = false,
                graph = new
                {
                    type = graphType,
                    generated_at = DateTime.UtcNow.ToString("O"),
                    analysis_run_id = runId
                },
                nodes = nodes,
                links = links
            };

            var path = Path.Combine(_outputDir, filename);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(graphObj, options);
            await File.WriteAllTextAsync(path, json);
            _logger.LogInformation("Exported {File}", path);
        }
    }
}
