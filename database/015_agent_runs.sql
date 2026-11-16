-- Day 15+: persisted multi-agent execution traces. Additive and safe to apply repeatedly.
-- Each row keeps the complete response (route, plan, trace, findings, evidence)
-- so earlier runs can be reopened and compared without rerunning a model.
CREATE TABLE IF NOT EXISTS agent_runs (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id bigint NOT NULL REFERENCES repositories(id),
    task text NOT NULL,
    route text NOT NULL,
    status text NOT NULL,
    summary text NOT NULL,
    duration_ms double precision NOT NULL,
    response jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS agent_runs_repository_id_idx
    ON agent_runs (repository_id, id DESC);
