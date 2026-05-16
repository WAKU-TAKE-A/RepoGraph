namespace Probe.Services.Persistence
{
    public static class SqliteSchema
    {
        public const string Tables = @"
CREATE TABLE IF NOT EXISTS analysis_runs (
    id           TEXT PRIMARY KEY,
    solution_path TEXT NOT NULL,
    started_at   TEXT NOT NULL,
    completed_at TEXT,
    status       TEXT NOT NULL,
    total_projects   INTEGER,
    analyzed_projects INTEGER,
    failed_projects  INTEGER,
    command_line TEXT,
    config_hash  TEXT
);

CREATE TABLE IF NOT EXISTS solutions (
    id              TEXT PRIMARY KEY,
    analysis_run_id TEXT NOT NULL,
    file_path       TEXT NOT NULL,
    name            TEXT NOT NULL,
    project_count   INTEGER,
    FOREIGN KEY (analysis_run_id) REFERENCES analysis_runs(id)
);

CREATE TABLE IF NOT EXISTS projects (
    id              TEXT PRIMARY KEY,
    solution_id     TEXT NOT NULL,
    analysis_run_id TEXT NOT NULL,
    name            TEXT NOT NULL,
    file_path       TEXT NOT NULL,
    assembly_name   TEXT,
    target_framework TEXT,
    project_type    TEXT,
    is_test_project INTEGER NOT NULL DEFAULT 0,
    is_sdk_style    INTEGER NOT NULL DEFAULT 0,
    document_count  INTEGER,
    analysis_status TEXT,
    error_message   TEXT,
    FOREIGN KEY (solution_id) REFERENCES solutions(id),
    FOREIGN KEY (analysis_run_id) REFERENCES analysis_runs(id)
);

CREATE TABLE IF NOT EXISTS project_dependencies (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    source_project_id TEXT NOT NULL,
    target_project_id TEXT NOT NULL,
    UNIQUE(source_project_id, target_project_id),
    FOREIGN KEY (source_project_id) REFERENCES projects(id),
    FOREIGN KEY (target_project_id) REFERENCES projects(id)
);

CREATE TABLE IF NOT EXISTS documents (
    id           TEXT PRIMARY KEY,
    project_id   TEXT NOT NULL,
    file_path    TEXT NOT NULL,
    file_name    TEXT NOT NULL,
    namespace    TEXT,
    line_count   INTEGER,
    is_generated INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (project_id) REFERENCES projects(id)
);

CREATE TABLE IF NOT EXISTS symbols (
    id               TEXT PRIMARY KEY,
    document_id      TEXT,
    project_id       TEXT NOT NULL,
    fqn              TEXT NOT NULL,
    name             TEXT NOT NULL,
    kind             TEXT NOT NULL,
    namespace        TEXT,
    containing_type  TEXT,
    accessibility    TEXT,
    is_static        INTEGER NOT NULL DEFAULT 0,
    is_abstract      INTEGER NOT NULL DEFAULT 0,
    is_sealed        INTEGER NOT NULL DEFAULT 0,
    is_async         INTEGER NOT NULL DEFAULT 0,
    is_partial       INTEGER NOT NULL DEFAULT 0,
    is_generic       INTEGER NOT NULL DEFAULT 0,
    is_extension_method INTEGER NOT NULL DEFAULT 0,
    is_disposable    INTEGER NOT NULL DEFAULT 0,
    is_volatile      INTEGER NOT NULL DEFAULT 0,
    line_start       INTEGER,
    line_end         INTEGER,
    loc              INTEGER,
    parameter_count  INTEGER,
    return_type      TEXT,
    has_callback     INTEGER NOT NULL DEFAULT 0,
    has_ui_dispatch      INTEGER NOT NULL DEFAULT 0,
    has_task_spawn       INTEGER NOT NULL DEFAULT 0,
    has_background_worker INTEGER NOT NULL DEFAULT 0,
    has_do_events        INTEGER NOT NULL DEFAULT 0,
    has_lock             INTEGER NOT NULL DEFAULT 0,
    has_thread_start     INTEGER NOT NULL DEFAULT 0,
    has_blocking_wait    INTEGER NOT NULL DEFAULT 0,
    fan_in               INTEGER NOT NULL DEFAULT 0,
    UNIQUE (fqn),
    FOREIGN KEY (document_id) REFERENCES documents(id),
    FOREIGN KEY (project_id) REFERENCES projects(id)
);

CREATE TABLE IF NOT EXISTS symbol_relationships (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    source_id         TEXT NOT NULL,
    target_id         TEXT NOT NULL,
    relationship_type TEXT NOT NULL,
    FOREIGN KEY (source_id) REFERENCES symbols(id),
    FOREIGN KEY (target_id) REFERENCES symbols(id)
);

CREATE TABLE IF NOT EXISTS method_calls (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    caller_id  TEXT NOT NULL,
    callee_id  TEXT NOT NULL,
    call_count INTEGER NOT NULL DEFAULT 1,
    call_type  TEXT NOT NULL DEFAULT 'calls',
    FOREIGN KEY (caller_id) REFERENCES symbols(id),
    FOREIGN KEY (callee_id) REFERENCES symbols(id)
);

CREATE TABLE IF NOT EXISTS inheritance (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    derived_id TEXT NOT NULL,
    base_id    TEXT NOT NULL,
    kind       TEXT NOT NULL,
    FOREIGN KEY (derived_id) REFERENCES symbols(id),
    FOREIGN KEY (base_id) REFERENCES symbols(id)
);

CREATE TABLE IF NOT EXISTS field_accesses (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    accessor_fqn  TEXT NOT NULL,
    target_fqn    TEXT NOT NULL,
    access_kind   TEXT NOT NULL,
    is_external   INTEGER NOT NULL DEFAULT 0,
    UNIQUE(accessor_fqn, target_fqn, access_kind)
);

CREATE TABLE IF NOT EXISTS diagnostics (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    analysis_run_id TEXT NOT NULL,
    project_id      TEXT,
    document_path   TEXT,
    severity        TEXT NOT NULL,
    category        TEXT,
    message         TEXT NOT NULL,
    timestamp       TEXT NOT NULL,
    FOREIGN KEY (analysis_run_id) REFERENCES analysis_runs(id)
);
";

        public const string Indexes = @"
CREATE INDEX IF NOT EXISTS idx_symbols_fqn        ON symbols(fqn);
CREATE INDEX IF NOT EXISTS idx_symbols_kind       ON symbols(kind);
CREATE INDEX IF NOT EXISTS idx_symbols_project    ON symbols(project_id);
CREATE INDEX IF NOT EXISTS idx_symbols_namespace  ON symbols(namespace);
CREATE INDEX IF NOT EXISTS idx_method_calls_caller ON method_calls(caller_id);
CREATE INDEX IF NOT EXISTS idx_method_calls_callee ON method_calls(callee_id);
CREATE INDEX IF NOT EXISTS idx_method_calls_type   ON method_calls(call_type);
CREATE INDEX IF NOT EXISTS idx_inheritance_derived ON inheritance(derived_id);
CREATE INDEX IF NOT EXISTS idx_inheritance_base    ON inheritance(base_id);
CREATE INDEX IF NOT EXISTS idx_documents_project   ON documents(project_id);
CREATE INDEX IF NOT EXISTS idx_projects_solution   ON projects(solution_id);
CREATE INDEX IF NOT EXISTS idx_project_deps_source ON project_dependencies(source_project_id);
CREATE INDEX IF NOT EXISTS idx_project_deps_target ON project_dependencies(target_project_id);
CREATE INDEX IF NOT EXISTS idx_field_accesses_accessor ON field_accesses(accessor_fqn);
CREATE INDEX IF NOT EXISTS idx_field_accesses_target   ON field_accesses(target_fqn);
CREATE INDEX IF NOT EXISTS idx_field_accesses_external ON field_accesses(is_external);
";
    }
}
