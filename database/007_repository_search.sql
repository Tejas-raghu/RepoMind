-- A regular B-tree index helps restrict searches to one repository.
-- Exact cosine ranking still scans that repository's chunks; no HNSW yet.
CREATE INDEX IF NOT EXISTS repository_chunks_repository_id_idx
    ON repository_chunks (repository_id);
