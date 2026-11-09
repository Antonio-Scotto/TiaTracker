-- TiaTracker: schema iniziale. Date in UTC ISO-8601 (yyyy-MM-ddTHH:mm:ss.fffZ),
-- date di calendario come yyyy-MM-dd. Booleani 0/1.

CREATE TABLE commessa (
    id           INTEGER PRIMARY KEY,
    code         TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name         TEXT NOT NULL,
    customer     TEXT,
    note         TEXT,
    created_utc  TEXT NOT NULL
);

-- Il percorso e' l'unico dato legato al PC: "Riaggancia cartella" lo cambia.
CREATE TABLE scan_root (
    id             INTEGER PRIMARY KEY,
    commessa_id    INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    path           TEXT NOT NULL,
    name_regex     TEXT,
    history_globs  TEXT,
    last_scan_utc  TEXT
);

CREATE TABLE version (
    id                INTEGER PRIMARY KEY,
    commessa_id       INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    scan_root_id      INTEGER REFERENCES scan_root(id) ON DELETE SET NULL,
    label             TEXT NOT NULL,
    sort_key          TEXT NOT NULL,
    project_name      TEXT NOT NULL,
    tia_major         INTEGER,
    tia_version_text  TEXT,
    folder_rel        TEXT,
    project_file_rel  TEXT,
    rar_rel           TEXT,
    backup_rel        TEXT,
    has_folder        INTEGER NOT NULL DEFAULT 0,
    has_rar           INTEGER NOT NULL DEFAULT 0,
    has_backup        INTEGER NOT NULL DEFAULT 0,
    backup_count      INTEGER NOT NULL DEFAULT 0,
    last_backup_utc   TEXT,
    last_saved_utc    TEXT,
    -- flag manuali
    is_loaded         INTEGER NOT NULL DEFAULT 0,
    loaded_utc        TEXT,
    loaded_plc        TEXT,
    loaded_note       TEXT,
    is_active         INTEGER NOT NULL DEFAULT 0,
    is_archived       INTEGER NOT NULL DEFAULT 0,
    note              TEXT,
    first_seen_utc    TEXT NOT NULL,
    last_seen_utc     TEXT,
    missing           INTEGER NOT NULL DEFAULT 0,
    UNIQUE (commessa_id, project_name)
);

-- Una sola versione Caricata per commessa e PLC.
CREATE UNIQUE INDEX ux_version_loaded ON version (commessa_id, COALESCE(loaded_plc, '')) WHERE is_loaded = 1;

CREATE TABLE version_event (
    id          INTEGER PRIMARY KEY,
    version_id  INTEGER NOT NULL REFERENCES version(id) ON DELETE CASCADE,
    utc         TEXT NOT NULL,
    kind        TEXT NOT NULL,
    detail      TEXT
);
CREATE INDEX ix_version_event ON version_event (version_id, utc);

-- CAS: XML (senza DocumentInfo), SCL e manifest compressi Brotli, chiave SHA-256 del contenuto.
CREATE TABLE content (
    hash  TEXT PRIMARY KEY,
    kind  TEXT NOT NULL,
    size  INTEGER NOT NULL,
    data  BLOB NOT NULL
);

CREATE TABLE snapshot (
    id                 INTEGER PRIMARY KEY,
    version_id         INTEGER NOT NULL REFERENCES version(id) ON DELETE CASCADE,
    created_utc        TEXT NOT NULL,
    source             TEXT NOT NULL,
    source_detail      TEXT,
    project_path       TEXT,
    plc_name           TEXT,
    project_modified   INTEGER,
    project_saved_utc  TEXT,
    online_state       TEXT,
    norm_version       INTEGER NOT NULL,
    worker_version     TEXT,
    tia_major          INTEGER,
    item_count         INTEGER NOT NULL DEFAULT 0,
    failed_count       INTEGER NOT NULL DEFAULT 0,
    partial            INTEGER NOT NULL DEFAULT 0,
    manifest_hash      TEXT REFERENCES content(hash),
    timings_json       TEXT,
    note               TEXT
);
CREATE INDEX ix_snapshot_version ON snapshot (version_id, created_utc);

CREATE TABLE snapshot_item (
    id                       INTEGER PRIMARY KEY,
    snapshot_id              INTEGER NOT NULL REFERENCES snapshot(id) ON DELETE CASCADE,
    family                   TEXT NOT NULL,
    kind                     TEXT NOT NULL,
    name                     TEXT NOT NULL,
    group_path               TEXT NOT NULL DEFAULT '',
    number                   INTEGER,
    language                 TEXT,
    instance_of              TEXT,
    state                    TEXT NOT NULL,
    reason                   TEXT,
    attrs_json               TEXT,
    code_modified_attr       TEXT,
    interface_modified_attr  TEXT,
    compiled_attr            TEXT,
    h_all                    TEXT,
    h_code                   TEXT,
    h_iface                  TEXT,
    h_init                   TEXT,
    h_text                   TEXT,
    h_meta                   TEXT,
    xml_ref                  TEXT REFERENCES content(hash),
    scl_ref                  TEXT REFERENCES content(hash),
    units_json               TEXT,
    changed_during_export    INTEGER NOT NULL DEFAULT 0,
    UNIQUE (snapshot_id, family, name)
);

CREATE TABLE compare_run (
    id                  INTEGER PRIMARY KEY,
    base_snapshot_id    INTEGER NOT NULL REFERENCES snapshot(id) ON DELETE CASCADE,
    target_snapshot_id  INTEGER NOT NULL REFERENCES snapshot(id) ON DELETE CASCADE,
    created_utc         TEXT NOT NULL,
    norm_version        INTEGER NOT NULL,
    summary_json        TEXT
);

CREATE TABLE compare_item (
    id              INTEGER PRIMARY KEY,
    run_id          INTEGER NOT NULL REFERENCES compare_run(id) ON DELETE CASCADE,
    family          TEXT NOT NULL,
    kind            TEXT,
    name            TEXT NOT NULL,
    old_name        TEXT,
    status          TEXT NOT NULL,
    moved           INTEGER NOT NULL DEFAULT 0,
    facets          TEXT,
    parent_name     TEXT,
    similarity      REAL,
    base_item_id    INTEGER REFERENCES snapshot_item(id) ON DELETE SET NULL,
    target_item_id  INTEGER REFERENCES snapshot_item(id) ON DELETE SET NULL
);
CREATE INDEX ix_compare_item_run ON compare_item (run_id);

CREATE TABLE import_source (
    id             INTEGER PRIMARY KEY,
    commessa_id    INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    rel_path       TEXT NOT NULL,
    sha256         TEXT NOT NULL,
    imported_utc   TEXT NOT NULL,
    confidence     TEXT,
    status         TEXT NOT NULL DEFAULT 'draft',
    UNIQUE (commessa_id, sha256)
);

CREATE TABLE change (
    id                INTEGER PRIMARY KEY,
    commessa_id       INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    title             TEXT NOT NULL,
    description       TEXT,
    motivation        TEXT,
    change_date       TEXT,
    scope             TEXT NOT NULL DEFAULT 'plc',
    draft             INTEGER NOT NULL DEFAULT 0,
    confidence        TEXT,
    excerpt           TEXT,
    import_source_id  INTEGER REFERENCES import_source(id) ON DELETE SET NULL,
    created_utc       TEXT NOT NULL,
    updated_utc       TEXT NOT NULL
);
CREATE INDEX ix_change_commessa ON change (commessa_id);

CREATE TABLE change_block (
    change_id   INTEGER NOT NULL REFERENCES change(id) ON DELETE CASCADE,
    block_name  TEXT NOT NULL COLLATE NOCASE,
    family      TEXT,
    action      TEXT,
    note        TEXT,
    PRIMARY KEY (change_id, block_name)
);
CREATE INDEX ix_change_block_name ON change_block (block_name);

-- Matrice modifica x versione.
CREATE TABLE change_version (
    change_id        INTEGER NOT NULL REFERENCES change(id) ON DELETE CASCADE,
    version_id       INTEGER NOT NULL REFERENCES version(id) ON DELETE CASCADE,
    state            TEXT NOT NULL,
    state_utc        TEXT,
    errors           INTEGER,
    warnings         INTEGER,
    note             TEXT,
    evidence_run_id  INTEGER REFERENCES compare_run(id) ON DELETE SET NULL,
    PRIMARY KEY (change_id, version_id)
);

CREATE TABLE change_event (
    id          INTEGER PRIMARY KEY,
    change_id   INTEGER NOT NULL REFERENCES change(id) ON DELETE CASCADE,
    version_id  INTEGER REFERENCES version(id) ON DELETE CASCADE,
    utc         TEXT NOT NULL,
    kind        TEXT NOT NULL,
    old_state   TEXT,
    new_state   TEXT,
    detail      TEXT
);
CREATE INDEX ix_change_event ON change_event (change_id, utc);

-- Decisioni della riconciliazione, riapplicate ai confronti successivi.
CREATE TABLE reconcile_link (
    id            INTEGER PRIMARY KEY,
    version_id    INTEGER NOT NULL REFERENCES version(id) ON DELETE CASCADE,
    block_family  TEXT NOT NULL,
    block_name    TEXT NOT NULL COLLATE NOCASE,
    change_id     INTEGER REFERENCES change(id) ON DELETE CASCADE,
    decision      TEXT NOT NULL,
    utc           TEXT NOT NULL,
    note          TEXT
);
CREATE UNIQUE INDEX ux_reconcile_link ON reconcile_link (version_id, block_family, block_name, COALESCE(change_id, 0));
