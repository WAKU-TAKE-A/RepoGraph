from sqlalchemy import Column, String, Integer, ForeignKey, Boolean, Float
from sqlalchemy.orm import relationship, DeclarativeBase

class Base(DeclarativeBase):
    pass

class AnalysisRun(Base):
    __tablename__ = "analysis_runs"
    id = Column(String, primary_key=True)
    solution_path = Column(String)
    started_at = Column(String)
    completed_at = Column(String)
    status = Column(String)

class Solution(Base):
    __tablename__ = "solutions"
    id = Column(String, primary_key=True)
    analysis_run_id = Column(String, ForeignKey("analysis_runs.id"))
    file_path = Column(String)
    name = Column(String)

class Project(Base):
    __tablename__ = "projects"
    id = Column(String, primary_key=True)
    solution_id = Column(String, ForeignKey("solutions.id"))
    analysis_run_id = Column(String, ForeignKey("analysis_runs.id"))
    name = Column(String)
    file_path = Column(String)
    project_type = Column(String)
    is_test_project = Column(Integer)

class Document(Base):
    __tablename__ = "documents"
    id = Column(String, primary_key=True)
    project_id = Column(String, ForeignKey("projects.id"))
    file_path = Column(String)
    file_name = Column(String)

class Symbol(Base):
    __tablename__ = "symbols"
    id = Column(String, primary_key=True)
    document_id = Column(String, ForeignKey("documents.id"))
    project_id = Column(String, ForeignKey("projects.id"))
    fqn = Column(String, unique=True)
    name = Column(String)
    kind = Column(String)
    namespace = Column(String)
    containing_type = Column(String)
    accessibility = Column(String)
    is_static = Column(Integer)
    is_abstract = Column(Integer)
    is_sealed = Column(Integer)
    is_async = Column(Integer)
    is_partial = Column(Integer)
    is_generic = Column(Integer)
    is_extension_method = Column(Integer)
    is_disposable = Column(Integer)
    is_volatile = Column(Integer)
    line_start = Column(Integer)
    line_end = Column(Integer)
    loc = Column(Integer)
    parameter_count = Column(Integer)
    return_type = Column(String)
    has_callback = Column(Integer)
    # Thread boundary flags
    has_ui_dispatch = Column(Integer)
    has_task_spawn = Column(Integer)
    has_background_worker = Column(Integer)
    has_do_events = Column(Integer)
    has_lock = Column(Integer)

class FieldAccess(Base):
    __tablename__ = "field_accesses"
    id = Column(Integer, primary_key=True, autoincrement=True)
    accessor_fqn = Column(String, nullable=False)
    target_fqn = Column(String, nullable=False)
    access_kind = Column(String, nullable=False)  # 'read', 'write', 'read_write'
    is_external = Column(Integer, nullable=False, default=0)
