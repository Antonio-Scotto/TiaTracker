-- Cartella di uscita ordinata (TiaTrackerOut) e cartella di rete per i backup .rar, per commessa.
ALTER TABLE commessa ADD COLUMN output_dir TEXT;
ALTER TABLE commessa ADD COLUMN backup_dir TEXT;

-- Layout di rete: indirizzi IP dei dispositivi, inseriti a mano o incollati da Google Fogli.
CREATE TABLE ip_device (
    id             INTEGER PRIMARY KEY,
    commessa_id    INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    sort           INTEGER NOT NULL DEFAULT 0,
    network        TEXT,
    ip             TEXT,
    subnet         TEXT,
    gateway        TEXT,
    name           TEXT,
    kind           TEXT,
    profinet_name  TEXT,
    location       TEXT,
    mac            TEXT,
    notes          TEXT,
    updated_utc    TEXT NOT NULL
);
CREATE INDEX ix_ip_device ON ip_device (commessa_id, sort);
