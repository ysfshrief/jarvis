using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Persistence;

/// <summary>
/// The single local SQLite database (jarvis.db). It is the source of truth for memory,
/// conversations, tasks, reminders, notifications and the activity log.
/// Schema changes are append-only numbered migrations.
/// </summary>
public sealed class JarvisDatabase
{
    private readonly string _connectionString;

    public JarvisDatabase(JarvisPaths paths) : this(paths.DatabasePath) { }

    public JarvisDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public static string Now() => DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    public static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    public static DateTimeOffset? ParseNullable(object? value) =>
        value is string s && !string.IsNullOrEmpty(s) ? Parse(s) : null;

    private void Migrate()
    {
        using var conn = Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }
        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);";
            create.ExecuteNonQuery();
        }

        long current;
        using (var get = conn.CreateCommand())
        {
            get.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            current = (long)get.ExecuteScalar()!;
        }

        for (var i = (int)current; i < Migrations.Length; i++)
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Migrations[i];
                cmd.ExecuteNonQuery();
            }
            using (var ver = conn.CreateCommand())
            {
                ver.Transaction = tx;
                ver.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
                ver.Parameters.AddWithValue("$v", i + 1);
                ver.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static readonly string[] Migrations =
    [
        // v1: initial schema
        """
        CREATE TABLE memories (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            content TEXT NOT NULL,
            subject TEXT,
            source TEXT NOT NULL,
            confidence REAL NOT NULL DEFAULT 1.0,
            tags TEXT,
            search_text TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            expires_at TEXT,
            last_used_at TEXT,
            use_count INTEGER NOT NULL DEFAULT 0
        );
        CREATE VIRTUAL TABLE memories_fts USING fts5(
            search_text, content='memories', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2'
        );
        CREATE TRIGGER memories_ai AFTER INSERT ON memories BEGIN
            INSERT INTO memories_fts(rowid, search_text) VALUES (new.rowid, new.search_text);
        END;
        CREATE TRIGGER memories_ad AFTER DELETE ON memories BEGIN
            INSERT INTO memories_fts(memories_fts, rowid, search_text) VALUES ('delete', old.rowid, old.search_text);
        END;
        CREATE TRIGGER memories_au AFTER UPDATE OF search_text ON memories BEGIN
            INSERT INTO memories_fts(memories_fts, rowid, search_text) VALUES ('delete', old.rowid, old.search_text);
            INSERT INTO memories_fts(rowid, search_text) VALUES (new.rowid, new.search_text);
        END;

        CREATE TABLE conversations (
            id TEXT PRIMARY KEY,
            title TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
            role TEXT NOT NULL,
            content TEXT NOT NULL,
            lang TEXT,
            source TEXT,
            meta TEXT,
            created_at TEXT NOT NULL
        );
        CREATE INDEX ix_messages_conversation ON messages(conversation_id, id);

        CREATE TABLE tasks (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            notes TEXT,
            state TEXT NOT NULL,
            priority TEXT NOT NULL DEFAULT 'normal',
            project TEXT,
            due_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT
        );

        CREATE TABLE reminders (
            id TEXT PRIMARY KEY,
            text TEXT NOT NULL,
            due_at TEXT NOT NULL,
            status TEXT NOT NULL,
            lang TEXT,
            created_at TEXT NOT NULL,
            fired_at TEXT
        );
        CREATE INDEX ix_reminders_due ON reminders(status, due_at);

        CREATE TABLE activity (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            kind TEXT NOT NULL,
            tool TEXT,
            summary TEXT NOT NULL,
            risk TEXT,
            status TEXT,
            details TEXT,
            conversation_id TEXT,
            duration_ms INTEGER
        );
        CREATE INDEX ix_activity_ts ON activity(ts);

        CREATE TABLE notifications (
            id TEXT PRIMARY KEY,
            ts TEXT NOT NULL,
            title TEXT NOT NULL,
            body TEXT,
            priority TEXT NOT NULL,
            source TEXT,
            group_key TEXT,
            status TEXT NOT NULL,
            delivered_at TEXT
        );

        CREATE TABLE offline_queue (
            id TEXT PRIMARY KEY,
            created_at TEXT NOT NULL,
            tool TEXT NOT NULL,
            args TEXT NOT NULL,
            summary TEXT NOT NULL,
            conversation_id TEXT,
            lang TEXT,
            status TEXT NOT NULL
        );
        """,
        // v2: provenance and confirmation of memories, entities and relationships, semantic vectors, pattern learner state.
        """
        ALTER TABLE memories ADD COLUMN provenance TEXT;
        ALTER TABLE memories ADD COLUMN confirmed_at TEXT;

        CREATE TABLE entities (
            id TEXT PRIMARY KEY,
            type TEXT NOT NULL,
            name TEXT NOT NULL,
            norm TEXT NOT NULL,
            aliases TEXT,
            notes TEXT,
            source TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ux_entities_norm ON entities(type, norm);

        CREATE TABLE relations (
            id TEXT PRIMARY KEY,
            from_id TEXT NOT NULL,
            relation TEXT NOT NULL,
            to_id TEXT NOT NULL,
            source TEXT NOT NULL,
            confidence REAL NOT NULL,
            provenance TEXT,
            created_at TEXT NOT NULL,
            UNIQUE(from_id, relation, to_id)
        );

        CREATE TABLE memory_entities (
            memory_id TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            PRIMARY KEY (memory_id, entity_id)
        );
        CREATE INDEX ix_memory_entities_entity ON memory_entities(entity_id);

        CREATE TABLE embeddings (
            owner_type TEXT NOT NULL,
            owner_id TEXT NOT NULL,
            model TEXT NOT NULL,
            dims INTEGER NOT NULL,
            hash TEXT NOT NULL,
            vector BLOB NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (owner_type, owner_id)
        );

        CREATE TABLE learner_state (
            key TEXT PRIMARY KEY,
            status TEXT NOT NULL,
            memory_id TEXT,
            evidence TEXT,
            updated_at TEXT NOT NULL
        );
        """,
        // v3: workflows (multi-step tracked goals) and recurring tasks.
        """
        ALTER TABLE tasks ADD COLUMN recurrence TEXT;

        CREATE TABLE workflows (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            goal TEXT,
            template TEXT NOT NULL,
            status TEXT NOT NULL,
            entity_id TEXT,
            entity_name TEXT,
            due_at TEXT,
            recurrence TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT
        );

        CREATE TABLE workflow_steps (
            id TEXT PRIMARY KEY,
            workflow_id TEXT NOT NULL,
            ord INTEGER NOT NULL,
            title TEXT NOT NULL,
            status TEXT NOT NULL,
            depends_on TEXT,
            due_at TEXT,
            waiting_for TEXT,
            follow_up_at TEXT,
            requires_approval INTEGER NOT NULL DEFAULT 0,
            action TEXT,
            notes TEXT,
            notified TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT
        );
        CREATE INDEX ix_workflow_steps_wf ON workflow_steps(workflow_id, ord);

        CREATE TABLE workflow_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            workflow_id TEXT NOT NULL,
            step_id TEXT,
            ts TEXT NOT NULL,
            kind TEXT NOT NULL,
            text TEXT NOT NULL
        );
        CREATE INDEX ix_workflow_events_wf ON workflow_events(workflow_id, id);
        """,
        // v4: file knowledge index.
        """
        CREATE TABLE files (
            id TEXT PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            ext TEXT NOT NULL,
            kind TEXT NOT NULL,
            size INTEGER NOT NULL,
            modified_at TEXT NOT NULL,
            created_at TEXT,
            indexed_at TEXT NOT NULL,
            status TEXT NOT NULL,
            note TEXT,
            title TEXT,
            author TEXT,
            pages INTEGER,
            project TEXT,
            text_chars INTEGER NOT NULL DEFAULT 0,
            metadata TEXT,
            name_search TEXT NOT NULL
        );
        CREATE INDEX ix_files_modified ON files(modified_at);

        CREATE TABLE file_chunks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            file_id TEXT NOT NULL,
            ord INTEGER NOT NULL,
            text TEXT NOT NULL,
            search_text TEXT NOT NULL
        );
        CREATE INDEX ix_file_chunks_file ON file_chunks(file_id, ord);
        CREATE VIRTUAL TABLE file_chunks_fts USING fts5(search_text, content='file_chunks', content_rowid='id');
        CREATE TRIGGER file_chunks_ai AFTER INSERT ON file_chunks BEGIN
            INSERT INTO file_chunks_fts(rowid, search_text) VALUES (new.id, new.search_text);
        END;
        CREATE TRIGGER file_chunks_ad AFTER DELETE ON file_chunks BEGIN
            INSERT INTO file_chunks_fts(file_chunks_fts, rowid, search_text) VALUES ('delete', old.id, old.search_text);
        END;

        CREATE TABLE file_entities (
            file_id TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            PRIMARY KEY (file_id, entity_id)
        );
        CREATE INDEX ix_file_entities_entity ON file_entities(entity_id);
        """,
        // v5: executive inbox (accounts, mail_messages, drafts, learned sender categories).
        """
        CREATE TABLE mail_accounts (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            address TEXT NOT NULL,
            display_name TEXT,
            config TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            status TEXT NOT NULL,
            status_message TEXT,
            last_sync TEXT,
            created_at TEXT NOT NULL
        );

        CREATE TABLE mail_messages (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL,
            external_id TEXT NOT NULL,
            folder TEXT,
            message_id_header TEXT,
            in_reply_to TEXT,
            thread_key TEXT,
            from_name TEXT,
            from_addr TEXT NOT NULL,
            to_addrs TEXT,
            cc_addrs TEXT,
            subject TEXT,
            snippet TEXT,
            body TEXT,
            received_at TEXT NOT NULL,
            is_read INTEGER NOT NULL DEFAULT 0,
            bulk INTEGER NOT NULL DEFAULT 0,
            category TEXT NOT NULL,
            category_source TEXT NOT NULL,
            reason TEXT,
            handled INTEGER NOT NULL DEFAULT 0,
            search_text TEXT NOT NULL,
            UNIQUE (account_id, external_id)
        );
        CREATE INDEX ix_mail_messages_received ON mail_messages(received_at);
        CREATE INDEX ix_mail_messages_category ON mail_messages(category, handled);
        CREATE VIRTUAL TABLE mail_messages_fts USING fts5(search_text, content='mail_messages', content_rowid='rowid');
        CREATE TRIGGER mail_messages_ai AFTER INSERT ON mail_messages BEGIN
            INSERT INTO mail_messages_fts(rowid, search_text) VALUES (new.rowid, new.search_text);
        END;
        CREATE TRIGGER mail_messages_ad AFTER DELETE ON mail_messages BEGIN
            INSERT INTO mail_messages_fts(mail_messages_fts, rowid, search_text) VALUES ('delete', old.rowid, old.search_text);
        END;
        CREATE TRIGGER mail_messages_au AFTER UPDATE OF search_text ON mail_messages BEGIN
            INSERT INTO mail_messages_fts(mail_messages_fts, rowid, search_text) VALUES ('delete', old.rowid, old.search_text);
            INSERT INTO mail_messages_fts(rowid, search_text) VALUES (new.rowid, new.search_text);
        END;

        CREATE TABLE message_entities (
            message_id TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            PRIMARY KEY (message_id, entity_id)
        );

        CREATE TABLE drafts (
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL,
            reply_to_id TEXT,
            to_addrs TEXT NOT NULL,
            cc_addrs TEXT,
            subject TEXT NOT NULL,
            body TEXT NOT NULL,
            status TEXT NOT NULL,
            created_by TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            sent_at TEXT,
            error TEXT
        );
        CREATE INDEX ix_drafts_status ON drafts(status);

        CREATE TABLE mail_sender_rules (
            address TEXT PRIMARY KEY,
            category TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        """,
    ];
}
