-- Apply once to an existing database before enabling Cloudflare mode.
-- Old 768-dimensional values remain in their original columns.
ALTER TABLE repositories ADD COLUMN IF NOT EXISTS embedding_provider text;
ALTER TABLE repositories ADD COLUMN IF NOT EXISTS embedding_model text;
UPDATE repositories r SET embedding_provider = 'ollama',
    embedding_model = (SELECT c.model FROM repository_chunks c
        WHERE c.repository_id = r.id ORDER BY c.id LIMIT 1)
WHERE r.embedding_model IS NULL AND EXISTS
    (SELECT 1 FROM repository_chunks c WHERE c.repository_id = r.id);

ALTER TABLE repository_chunks ADD COLUMN IF NOT EXISTS embedding_cf vector(1024);
ALTER TABLE repository_chunks ALTER COLUMN embedding DROP NOT NULL;
ALTER TABLE stored_embeddings ADD COLUMN IF NOT EXISTS embedding_cf vector(1024);
ALTER TABLE stored_embeddings ALTER COLUMN embedding DROP NOT NULL;

-- Each row has exactly one vector; old rows satisfy this automatically.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'repository_chunks_one_embedding') THEN
        ALTER TABLE repository_chunks ADD CONSTRAINT repository_chunks_one_embedding
            CHECK ((embedding IS NULL) <> (embedding_cf IS NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'stored_embeddings_one_embedding') THEN
        ALTER TABLE stored_embeddings ADD CONSTRAINT stored_embeddings_one_embedding
            CHECK ((embedding IS NULL) <> (embedding_cf IS NULL));
    END IF;
END $$;
