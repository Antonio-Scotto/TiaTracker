-- Stato della versione (una scelta, NULL = nessuno: l'app mostra "Vecchia" se e' superata)
-- e carichi sui PLC (una riga per PLC, corrente finche' ended_utc e' NULL).
-- I flag is_loaded / is_active / is_archived restano, riallineati dal codice: li leggono
-- il registro modifiche, commessa.json e le versioni precedenti dell'app.
-- Niente CHECK sui valori: in SQLite cambiarli vorrebbe dire ricostruire la tabella.

ALTER TABLE version ADD COLUMN state TEXT;
ALTER TABLE version ADD COLUMN state_utc TEXT;
ALTER TABLE version ADD COLUMN state_note TEXT;

CREATE TABLE version_load (
    id                   INTEGER PRIMARY KEY,
    commessa_id          INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    version_id           INTEGER NOT NULL REFERENCES version(id) ON DELETE CASCADE,
    plc                  TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
    loaded_utc           TEXT NOT NULL,
    note                 TEXT,
    proof_snapshot_id    INTEGER REFERENCES snapshot(id) ON DELETE SET NULL,
    created_utc          TEXT NOT NULL,
    ended_utc            TEXT,
    ended_by_version_id  INTEGER REFERENCES version(id) ON DELETE SET NULL,
    ended_reason         TEXT
);
-- Su ogni PLC al massimo un carico corrente per commessa.
CREATE UNIQUE INDEX ux_version_load_current ON version_load (commessa_id, plc) WHERE ended_utc IS NULL;
CREATE INDEX ix_version_load_version ON version_load (version_id, loaded_utc);

-- Carichi correnti dai vecchi flag. Due versioni sullo stesso PLC scritto con maiuscole
-- diverse: resta la piu' recente, l'altra diventa Vecchia.
INSERT INTO version_load (commessa_id, version_id, plc, loaded_utc, note, created_utc)
SELECT commessa_id, id, plc, COALESCE(loaded_utc, first_seen_utc), loaded_note, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM (SELECT v.*, COALESCE(TRIM(v.loaded_plc), '') AS plc,
             ROW_NUMBER() OVER (PARTITION BY v.commessa_id, LOWER(COALESCE(TRIM(v.loaded_plc), ''))
                                ORDER BY v.loaded_utc DESC, v.id DESC) AS rn
      FROM version v
      WHERE v.is_loaded = 1)
WHERE rn = 1;

UPDATE version SET state = CASE
    WHEN id IN (SELECT version_id FROM version_load) THEN 'caricata'
    WHEN is_loaded = 1 THEN 'vecchia'
    WHEN is_active = 1 THEN 'in_lavoro'
    WHEN is_archived = 1 THEN 'archiviata'
END;

UPDATE version SET state_utc = CASE WHEN state = 'caricata' THEN COALESCE(loaded_utc, first_seen_utc)
                                    ELSE strftime('%Y-%m-%dT%H:%M:%fZ', 'now') END
WHERE state IS NOT NULL;

INSERT INTO version_event (version_id, utc, kind, detail)
SELECT id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'stato',
       'Stato iniziale dai vecchi flag: ' || state ||
       CASE WHEN is_loaded = 1 AND is_active = 1 THEN ' (era anche Attiva)' ELSE '' END
FROM version
WHERE state IS NOT NULL;

UPDATE version SET is_loaded   = CASE WHEN state = 'caricata'   THEN 1 ELSE 0 END,
                   is_active   = CASE WHEN state = 'in_lavoro'  THEN 1 ELSE 0 END,
                   is_archived = CASE WHEN state = 'archiviata' THEN 1 ELSE 0 END;
