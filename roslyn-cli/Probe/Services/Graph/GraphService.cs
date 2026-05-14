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

        private async Task ExportCallGraphAsync(string runId)
        {
            var nodes = new List<object>();
            var links = new List<object>();

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmdNodes = connection.CreateCommand();
            cmdNodes.CommandText = "SELECT id, fqn, kind FROM symbols WHERE kind = 'method' OR kind = 'constructor'";
            using var readerNodes = await cmdNodes.ExecuteReaderAsync();
            var symbolIdToFqn = new Dictionary<string, string>();
            while (await readerNodes.ReadAsync())
            {
                var id = readerNodes.GetString(0);
                var fqn = readerNodes.GetString(1);
                var kind = readerNodes.GetString(2);
                symbolIdToFqn[id] = fqn;
                nodes.Add(new { id = fqn, kind = kind });
            }

            using var cmdEdges = connection.CreateCommand();
            cmdEdges.CommandText = "SELECT caller_id, callee_id, call_count FROM method_calls";
            using var readerEdges = await cmdEdges.ExecuteReaderAsync();
            while (await readerEdges.ReadAsync())
            {
                var callerId = readerEdges.GetString(0);
                var calleeId = readerEdges.GetString(1);
                var count = readerEdges.GetInt32(2);

                if (symbolIdToFqn.TryGetValue(callerId, out var callerFqn) && 
                    symbolIdToFqn.TryGetValue(calleeId, out var calleeFqn))
                {
                    links.Add(new { source = callerFqn, target = calleeFqn, type = "calls", call_count = count });
                }
            }

            await WriteJsonAsync("call_graph.json", "call", runId, nodes, links);
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
