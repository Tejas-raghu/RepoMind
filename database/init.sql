-- The pgvector image installs the extension files; each database must enable it.
CREATE EXTENSION IF NOT EXISTS vector;

-- Run this file again on existing Day 3 volumes; both statements are idempotent.
CREATE TABLE IF NOT EXISTS stored_embeddings (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    content text NOT NULL,
    model text NOT NULL,
    embedding vector(768) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
