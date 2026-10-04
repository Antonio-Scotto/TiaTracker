-- Hardware, rete e tag letti da TIA (hardware.json del worker, tag compresi), uno per export.
-- I dati stanno nel content store (data_hash, kind 'hardware'); qui solo cio' che serve a elencarli.
CREATE TABLE hw_snapshot (
    id               INTEGER PRIMARY KEY,
    version_id       INTEGER NOT NULL REFERENCES version(id) ON DELETE CASCADE,
    snapshot_id      INTEGER REFERENCES snapshot(id) ON DELETE SET NULL,   -- lo snapshot dei blocchi dello stesso export, se c'e'
    created_utc      TEXT NOT NULL,
    source           TEXT NOT NULL,
    source_detail    TEXT,
    project_path     TEXT,
    project_modified INTEGER,
    worker_version   TEXT,
    tia_major        INTEGER,
    format           INTEGER NOT NULL,
    data_hash        TEXT NOT NULL REFERENCES content(hash),
    device_count     INTEGER NOT NULL DEFAULT 0,
    module_count     INTEGER NOT NULL DEFAULT 0,
    ip_count         INTEGER NOT NULL DEFAULT 0,
    tag_count        INTEGER NOT NULL DEFAULT 0,
    io_tag_count     INTEGER NOT NULL DEFAULT 0,
    partial          INTEGER NOT NULL DEFAULT 0,
    warnings_json    TEXT,
    timings_json     TEXT
);
CREATE INDEX ix_hw_snapshot_version ON hw_snapshot (version_id, created_utc);

-- Righe IP scritte a mano o lette da TIA; quelle di TIA non si cancellano da sole (tia_missing).
ALTER TABLE ip_device ADD COLUMN source TEXT NOT NULL DEFAULT 'manuale';
ALTER TABLE ip_device ADD COLUMN tia_key TEXT;
ALTER TABLE ip_device ADD COLUMN tia_version_id INTEGER REFERENCES version(id) ON DELETE SET NULL;
ALTER TABLE ip_device ADD COLUMN tia_seen_utc TEXT;
ALTER TABLE ip_device ADD COLUMN tia_missing INTEGER NOT NULL DEFAULT 0;

-- Impostazioni della commessa (automazioni, calendario, versione di riferimento forzata) in JSON.
ALTER TABLE commessa ADD COLUMN settings_json TEXT;
