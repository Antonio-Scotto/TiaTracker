-- Documenti della commessa (Lista Hardware, Lista IO...): note dell'utente per riga e righe
-- aggiunte a mano, che sopravvivono a ogni rigenerazione da TIA; storico delle generazioni.

CREATE TABLE doc_note (
    commessa_id INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    list_kind   TEXT NOT NULL,                     -- hardware | io
    row_key     TEXT NOT NULL COLLATE NOCASE,      -- percorso del modulo, PLC|indirizzo
    note        TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (commessa_id, list_kind, row_key)
);

CREATE TABLE doc_manual_row (
    id          INTEGER PRIMARY KEY,
    commessa_id INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    list_kind   TEXT NOT NULL,
    sort        INTEGER NOT NULL DEFAULT 0,
    cells_json  TEXT NOT NULL,                     -- {chiave colonna: valore}
    updated_utc TEXT NOT NULL
);
CREATE INDEX ix_doc_manual_row ON doc_manual_row (commessa_id, list_kind, sort);

CREATE TABLE doc_export (
    id             INTEGER PRIMARY KEY,
    commessa_id    INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    list_kind      TEXT NOT NULL,                  -- ip | hardware | io | registro | piano
    version_id     INTEGER REFERENCES version(id) ON DELETE SET NULL,
    hw_snapshot_id INTEGER REFERENCES hw_snapshot(id) ON DELETE SET NULL,
    generated_utc  TEXT NOT NULL,
    formats        TEXT NOT NULL,                  -- es. "docx,pdf,csv"
    files_json     TEXT
);
CREATE INDEX ix_doc_export ON doc_export (commessa_id, list_kind, generated_utc);
