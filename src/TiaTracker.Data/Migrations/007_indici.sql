-- Indici sulle colonne che puntano ad altre tabelle. Senza, SQLite scorre tutta la tabella
-- figlia per ogni riga cancellata dal padre (ON DELETE SET NULL / CASCADE): togliere uno
-- snapshot con migliaia di elementi faceva due scansioni di compare_item per elemento.

CREATE INDEX IF NOT EXISTS ix_compare_item_base ON compare_item (base_item_id);
CREATE INDEX IF NOT EXISTS ix_compare_item_target ON compare_item (target_item_id);
CREATE INDEX IF NOT EXISTS ix_compare_run_base ON compare_run (base_snapshot_id);
CREATE INDEX IF NOT EXISTS ix_compare_run_target ON compare_run (target_snapshot_id);
CREATE INDEX IF NOT EXISTS ix_change_version_version ON change_version (version_id);
CREATE INDEX IF NOT EXISTS ix_change_version_evidence ON change_version (evidence_run_id);
CREATE INDEX IF NOT EXISTS ix_version_load_proof ON version_load (proof_snapshot_id);
CREATE INDEX IF NOT EXISTS ix_hw_snapshot_snapshot ON hw_snapshot (snapshot_id);
CREATE INDEX IF NOT EXISTS ix_doc_export_hw ON doc_export (hw_snapshot_id);
CREATE INDEX IF NOT EXISTS ix_task_event_task ON task_event (task_id);

-- Riferimenti ai contenuti: la pulizia dei contenuti orfani cancella righe di content, e per
-- ognuna SQLite controlla chi la usa ancora.
CREATE INDEX IF NOT EXISTS ix_snapshot_item_xml ON snapshot_item (xml_ref);
CREATE INDEX IF NOT EXISTS ix_snapshot_item_scl ON snapshot_item (scl_ref);
CREATE INDEX IF NOT EXISTS ix_snapshot_manifest ON snapshot (manifest_hash);
CREATE INDEX IF NOT EXISTS ix_hw_snapshot_data ON hw_snapshot (data_hash);

-- Tabelle piccole, per completezza (un test controlla che ogni chiave esterna abbia il suo indice).
CREATE INDEX IF NOT EXISTS ix_scan_root_commessa ON scan_root (commessa_id);
CREATE INDEX IF NOT EXISTS ix_version_scan_root ON version (scan_root_id);
CREATE INDEX IF NOT EXISTS ix_change_import_source ON change (import_source_id);
CREATE INDEX IF NOT EXISTS ix_change_event_version ON change_event (version_id);
CREATE INDEX IF NOT EXISTS ix_reconcile_link_change ON reconcile_link (change_id);
CREATE INDEX IF NOT EXISTS ix_ip_device_tia_version ON ip_device (tia_version_id);
CREATE INDEX IF NOT EXISTS ix_version_load_ended_by ON version_load (ended_by_version_id);
CREATE INDEX IF NOT EXISTS ix_doc_export_version ON doc_export (version_id);
CREATE INDEX IF NOT EXISTS ix_task_version ON task (version_id);
CREATE INDEX IF NOT EXISTS ix_task_parent ON task (parent_id);
CREATE INDEX IF NOT EXISTS ix_task_link_successor ON task_link (successor_id);
CREATE INDEX IF NOT EXISTS ix_task_change_change ON task_change (change_id);
