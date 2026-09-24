namespace FolderTrail.Core.SharedKernel.Persistence;

/// <summary>
/// The schema is shared by all features, but each table has one owning feature that writes it:
/// watched_roots/exclusions (WatchedFolders), inventory (Reconciliation), activity_events (Monitoring and
/// Reconciliation write, Timeline reads), monitoring_gaps (Monitoring), app_settings (Settings).
/// Never edit a released migration; append a new one.
/// </summary>
public static class SchemaMigrations
{
    public static readonly Migration V1 = new(
        1,
        "Initial schema",
        """
        CREATE TABLE schema_version (
            version      INTEGER NOT NULL PRIMARY KEY,
            applied_utc  INTEGER NOT NULL,
            description  TEXT    NOT NULL
        );

        CREATE TABLE watched_roots (
            id                        INTEGER PRIMARY KEY,
            display_path              TEXT    NOT NULL,
            path_key                  TEXT    NOT NULL,
            include_subfolders        INTEGER NOT NULL,
            is_paused                 INTEGER NOT NULL DEFAULT 0,
            added_utc                 INTEGER NOT NULL,
            removed_utc               INTEGER NULL,
            baseline_completed_utc    INTEGER NULL,
            baseline_item_count       INTEGER NULL,
            last_successful_scan_utc  INTEGER NULL,
            last_event_utc            INTEGER NULL,
            last_scan_skipped_count   INTEGER NOT NULL DEFAULT 0,
            last_scan_warning         TEXT    NULL
        );
        CREATE UNIQUE INDEX ux_watched_roots_active_path ON watched_roots (path_key) WHERE removed_utc IS NULL;

        CREATE TABLE exclusions (
            id          INTEGER PRIMARY KEY,
            root_id     INTEGER NULL REFERENCES watched_roots (id) ON DELETE CASCADE,
            pattern     TEXT    NOT NULL,
            is_enabled  INTEGER NOT NULL DEFAULT 1,
            is_builtin  INTEGER NOT NULL DEFAULT 0,
            sort_order  INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_exclusions_root ON exclusions (root_id);

        CREATE TABLE inventory (
            root_id         INTEGER NOT NULL REFERENCES watched_roots (id) ON DELETE CASCADE,
            path_key        TEXT    NOT NULL,
            display_path    TEXT    NOT NULL,
            kind            INTEGER NOT NULL,
            length          INTEGER NULL,
            last_write_utc  INTEGER NULL,
            recorded_utc    INTEGER NOT NULL,
            PRIMARY KEY (root_id, path_key)
        ) WITHOUT ROWID;

        CREATE TABLE activity_events (
            id              INTEGER PRIMARY KEY,
            root_id         INTEGER NOT NULL REFERENCES watched_roots (id) ON DELETE CASCADE,
            detected_utc    INTEGER NOT NULL,
            event_type      INTEGER NOT NULL,
            source          INTEGER NOT NULL,
            kind            INTEGER NOT NULL,
            path            TEXT    NOT NULL,
            path_key        TEXT    NOT NULL,
            name            TEXT    NOT NULL,
            old_path        TEXT    NULL,
            old_path_key    TEXT    NULL,
            length          INTEGER NULL,
            last_write_utc  INTEGER NULL
        );
        CREATE INDEX ix_events_time ON activity_events (detected_utc DESC, id DESC);
        CREATE INDEX ix_events_root_time ON activity_events (root_id, detected_utc DESC, id DESC);
        CREATE INDEX ix_events_type_time ON activity_events (event_type, detected_utc DESC, id DESC);
        CREATE INDEX ix_events_path ON activity_events (path_key);

        CREATE TABLE monitoring_gaps (
            id         INTEGER PRIMARY KEY,
            root_id    INTEGER NOT NULL REFERENCES watched_roots (id) ON DELETE CASCADE,
            reason     INTEGER NOT NULL,
            start_utc  INTEGER NULL,
            end_utc    INTEGER NULL,
            detail     TEXT    NULL
        );
        CREATE INDEX ix_gaps_root ON monitoring_gaps (root_id, end_utc);

        CREATE TABLE app_settings (
            key    TEXT NOT NULL PRIMARY KEY,
            value  TEXT NOT NULL
        ) WITHOUT ROWID;
        """);

    public static readonly Migration V2 = new(
        2,
        "Previous metadata for detected changes, merged notification counts, old-path index",
        """
        ALTER TABLE activity_events ADD COLUMN previous_length INTEGER NULL;
        ALTER TABLE activity_events ADD COLUMN previous_last_write_utc INTEGER NULL;
        ALTER TABLE activity_events ADD COLUMN previous_observed_utc INTEGER NULL;
        ALTER TABLE activity_events ADD COLUMN merged_count INTEGER NOT NULL DEFAULT 1;
        CREATE INDEX ix_events_old_path ON activity_events (old_path_key) WHERE old_path_key IS NOT NULL;
        """);

    public static IReadOnlyList<Migration> All { get; } = [V1, V2];

    public static int LatestVersion => All[^1].Version;
}
