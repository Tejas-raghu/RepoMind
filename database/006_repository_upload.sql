-- Independent of the Day 2-5 stored_embeddings table. Safe to apply repeatedly.
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS repositories (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL,
    status text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS repository_chunks (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id bigint NOT NULL REFERENCES repositories(id),
    source_path text NOT NULL,
    start_line integer NOT NULL,
    end_line integer NOT NULL,
    content text NOT NULL,
    model text NOT NULL,
    embedding vector(768) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
