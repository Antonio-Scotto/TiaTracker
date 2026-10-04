-- Pianificazione della commessa: attivita', milestone e fasi con dipendenze fine-inizio,
-- legame con le modifiche del registro e storico di ogni cambiamento (anche delle date).

CREATE TABLE task (
    id               INTEGER PRIMARY KEY,
    commessa_id      INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    parent_id        INTEGER REFERENCES task(id) ON DELETE SET NULL,  -- fase che lo contiene
    sort             INTEGER NOT NULL DEFAULT 0,
    kind             TEXT NOT NULL DEFAULT 'attivita',                -- attivita | milestone | fase
    title            TEXT NOT NULL,
    description      TEXT,
    status           TEXT NOT NULL DEFAULT 'da_fare',                 -- da_fare | in_corso | bloccata | fatta | annullata
    priority         TEXT NOT NULL DEFAULT 'normale',                 -- bassa | normale | alta | urgente
    assignee         TEXT,
    start_date       TEXT NOT NULL,                                   -- yyyy-MM-dd
    end_date         TEXT NOT NULL,                                   -- compreso
    locked           INTEGER NOT NULL DEFAULT 0,
    progress         INTEGER NOT NULL DEFAULT 0,
    estimated_hours  REAL,
    actual_hours     REAL,
    version_id       INTEGER REFERENCES version(id) ON DELETE SET NULL,
    created_utc      TEXT NOT NULL,
    updated_utc      TEXT NOT NULL
);
CREATE INDEX ix_task_commessa ON task (commessa_id, sort);

CREATE TABLE task_link (
    id              INTEGER PRIMARY KEY,
    predecessor_id  INTEGER NOT NULL REFERENCES task(id) ON DELETE CASCADE,
    successor_id    INTEGER NOT NULL REFERENCES task(id) ON DELETE CASCADE,
    lag_days        INTEGER NOT NULL DEFAULT 0,                      -- giorni lavorativi
    UNIQUE (predecessor_id, successor_id)
);

CREATE TABLE task_change (
    task_id    INTEGER NOT NULL REFERENCES task(id) ON DELETE CASCADE,
    change_id  INTEGER NOT NULL REFERENCES change(id) ON DELETE CASCADE,
    PRIMARY KEY (task_id, change_id)
);

-- Storico: niente FK sul task, cosi' resta anche dopo l'eliminazione (con il titolo di allora).
CREATE TABLE task_event (
    id           INTEGER PRIMARY KEY,
    commessa_id  INTEGER NOT NULL REFERENCES commessa(id) ON DELETE CASCADE,
    task_id      INTEGER,
    task_title   TEXT NOT NULL,
    batch        TEXT,                                               -- uno per azione (trascinamento + cascata)
    utc          TEXT NOT NULL,
    user_name    TEXT,
    kind         TEXT NOT NULL,                                      -- creato | modificato | date | collegato | scollegato | eliminato | annullato
    field        TEXT,
    old_value    TEXT,
    new_value    TEXT,
    reason       TEXT,
    cascade      INTEGER NOT NULL DEFAULT 0,                         -- date spostate dalla cascata di un altro task
    undone_by    TEXT                                                -- lotto dell'annullamento
);
CREATE INDEX ix_task_event ON task_event (commessa_id, utc);
CREATE INDEX ix_task_event_batch ON task_event (batch);
