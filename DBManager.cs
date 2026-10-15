using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlitzkriegWPF;

public static class DBManager
{
    private const int CurrentSchemaVersion = 2;
    private static readonly string DbFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blitzkrieg");
    private static readonly string DbPath = Path.Combine(DbFolder, "BlitzkriegDB.db");
    // Поддерживаем путь, который использовала одна из предыдущих версий приложения.
    private static readonly string AlternateLegacyDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlitzkriegWPF", "BlitzkriegDB.db");
    private static readonly string SeedDbPath = Path.Combine(AppContext.BaseDirectory, "BlitzkriegDB.db");
    private static readonly string ConnectionString = $"Data Source={DbPath};";
    private static readonly Regex StageRegex = new(@"^\s*Stage\s+(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RangeRegex = new(@"^\s*(-?\d+(?:[.,]\d+)?)\s*-\s*(-?\d+(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);

    static DBManager()
    {
        SQLitePCL.Batteries.Init();
        Directory.CreateDirectory(DbFolder);
        EnsureUserDatabase();
        InitializeDatabase();
    }

    private static void EnsureUserDatabase()
    {
        if (File.Exists(DbPath) && new FileInfo(DbPath).Length > 0)
            return;

        if (File.Exists(AlternateLegacyDbPath) && new FileInfo(AlternateLegacyDbPath).Length > 0)
        {
            if (File.Exists(DbPath)) File.Delete(DbPath);
            File.Copy(AlternateLegacyDbPath, DbPath);
            return;
        }

        if (File.Exists(DbPath))
            return; // Пустая база будет создана сразу после завершения этого метода.

        if (File.Exists(SeedDbPath))
            File.Copy(SeedDbPath, DbPath);
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InitializeDatabase()
    {
        using var connection = OpenConnection();
        bool hasCompletions = TableExists(connection, "Completions");
        if (!hasCompletions)
        {
            using var transaction = connection.BeginTransaction();
            CreateCurrentSchema(connection, transaction);
            SetSchemaVersion(connection, transaction, CurrentSchemaVersion);
            transaction.Commit();
            return;
        }

        if (IsCurrentSchema(connection))
        {
            using var transaction = connection.BeginTransaction();
            CreateSupportTables(connection, transaction);
            SetSchemaVersion(connection, transaction, CurrentSchemaVersion);
            transaction.Commit();
            return;
        }

        // В версии 0.1 раны хранились плоским списком. В одном из промежуточных вариантов
        // уже появились этапы и новая структура ранов. Оба варианта переносим в одной транзакции.
        var snapshot = ReadLegacySnapshot(connection);
        CreatePreMigrationBackup();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        using (var transaction = connection.BeginTransaction())
        {
            foreach (string table in new[] { "SaveBackups", "Runs", "Stages", "LinkedSaves", "Completions", "SchemaInfo" })
            {
                using var drop = new SqliteCommand($"DROP TABLE IF EXISTS \"{table}\";", connection, transaction);
                drop.ExecuteNonQuery();
            }

            CreateCurrentSchema(connection, transaction);
            MigrateLegacySnapshot(connection, transaction, snapshot);
            SetSchemaVersion(connection, transaction, CurrentSchemaVersion);
            transaction.Commit();
        }

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
    }


    private static void CreatePreMigrationBackup()
    {
        if (!File.Exists(DbPath) || new FileInfo(DbPath).Length == 0)
            return;

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string backupPath = Path.Combine(DbFolder, $"BlitzkriegDB.pre-v0.2.0-alpha-{timestamp}-{Guid.NewGuid():N}.db");
        // Если резервную копию не удалось создать, прерываем миграцию, чтобы не рисковать
        // единственным экземпляром базы пользователя.
        File.Copy(DbPath, backupPath, overwrite: false);
    }

    private static bool IsCurrentSchema(SqliteConnection connection)
    {
        if (!TableExists(connection, "SchemaInfo") ||
            !TableExists(connection, "Stages") ||
            !TableExists(connection, "Runs") ||
            !TableExists(connection, "LinkedSaves") ||
            !ColumnExists(connection, "Completions", "Level_Name") ||
            ColumnExists(connection, "Completions", "ID_Level") ||
            !ColumnExists(connection, "Stages", "StageGuid") ||
            !ColumnExists(connection, "Stages", "Checked") ||
            !ColumnExists(connection, "Stages", "Note") ||
            !ColumnExists(connection, "Runs", "RangeGuid") ||
            !ColumnExists(connection, "Runs", "From") ||
            !ColumnExists(connection, "Runs", "To"))
            return false;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SchemaVersion FROM SchemaInfo WHERE Singleton = 1;";
        object? value = command.ExecuteScalar();
        return value != null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) >= CurrentSchemaVersion;
    }

    private static void CreateCurrentSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS SchemaInfo (
                Singleton INTEGER PRIMARY KEY CHECK (Singleton = 1),
                SchemaVersion INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Completions (
                ID_Completion INTEGER PRIMARY KEY AUTOINCREMENT,
                Level_Name TEXT NOT NULL DEFAULT '',
                Is_Imported INTEGER NOT NULL DEFAULT 0,
                Display_Order INTEGER NOT NULL DEFAULT 0,
                Last_Modified TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS Stages (
                ID_Stage INTEGER PRIMARY KEY AUTOINCREMENT,
                ID_Completion INTEGER NOT NULL,
                StageGuid TEXT,
                StageNumber INTEGER NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                Checked INTEGER NOT NULL DEFAULT 0,
                Note TEXT NOT NULL DEFAULT '',
                CompletionCounter INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (ID_Completion) REFERENCES Completions(ID_Completion) ON DELETE CASCADE,
                UNIQUE (ID_Completion, StageGuid)
            );

            CREATE TABLE IF NOT EXISTS Runs (
                ID_Run INTEGER PRIMARY KEY AUTOINCREMENT,
                ID_Stage INTEGER NOT NULL,
                RangeGuid TEXT,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                RunText TEXT NOT NULL DEFAULT '',
                ""From"" REAL,
                ""To"" REAL,
                Status INTEGER NOT NULL DEFAULT 0,
                Attempts INTEGER NOT NULL DEFAULT 0,
                Note TEXT NOT NULL DEFAULT '',
                FirstRunFrom REAL,
                FirstRunTo REAL,
                BestRunFrom REAL,
                BestRunTo REAL,
                Consider INTEGER NOT NULL DEFAULT 1,
                AutomaticallyClosed INTEGER NOT NULL DEFAULT 0,
                TimePlayed REAL NOT NULL DEFAULT 0,
                CompletedAt INTEGER,
                AttemptsToComplete INTEGER NOT NULL DEFAULT 0,
                CompletionCounter INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (ID_Stage) REFERENCES Stages(ID_Stage) ON DELETE CASCADE,
                UNIQUE (ID_Stage, RangeGuid)
            );

            CREATE TABLE IF NOT EXISTS LinkedSaves (
                ID_LinkedSave INTEGER PRIMARY KEY AUTOINCREMENT,
                ID_Completion INTEGER NOT NULL UNIQUE,
                SaveGuid TEXT NOT NULL UNIQUE COLLATE NOCASE,
                SavePath TEXT NOT NULL,
                LastSyncedJson TEXT NOT NULL,
                LastSyncedHash TEXT NOT NULL,
                SyncState TEXT NOT NULL DEFAULT 'OK',
                LastSyncUtc TEXT,
                FOREIGN KEY (ID_Completion) REFERENCES Completions(ID_Completion) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SaveBackups (
                ID_SaveBackup INTEGER PRIMARY KEY AUTOINCREMENT,
                ID_LinkedSave INTEGER,
                Save_Guid TEXT,
                Save_Backup_Path TEXT,
                Save_Json TEXT,
                Content_Hash TEXT,
                Created_At TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                Source_Path TEXT,
                FOREIGN KEY (ID_LinkedSave) REFERENCES LinkedSaves(ID_LinkedSave) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS LogType (
                ID_Log_Type INTEGER PRIMARY KEY AUTOINCREMENT,
                Log_Type_Name TEXT,
                Caption TEXT
            );
            CREATE TABLE IF NOT EXISTS Logs (
                ID_Log INTEGER PRIMARY KEY AUTOINCREMENT,
                ID_Log_Type INTEGER,
                Log TEXT,
                new_col TEXT,
                FOREIGN KEY (ID_Log_Type) REFERENCES LogType(ID_Log_Type)
            );
            CREATE TABLE IF NOT EXISTS ReportType (
                ID_Report_Type INTEGER PRIMARY KEY AUTOINCREMENT,
                Report_Type_Name TEXT,
                Caption TEXT
            );
            CREATE TABLE IF NOT EXISTS Report (
                ID_Report INTEGER PRIMARY KEY AUTOINCREMENT,
                ID_Report_Type INTEGER,
                Timestamp TEXT,
                Path TEXT,
                FOREIGN KEY (ID_Report_Type) REFERENCES ReportType(ID_Report_Type)
            );

            CREATE INDEX IF NOT EXISTS IX_Stages_Completion_Order ON Stages(ID_Completion, SortOrder, StageNumber);
            CREATE INDEX IF NOT EXISTS IX_Runs_Stage_Order ON Runs(ID_Stage, SortOrder, ID_Run);
            CREATE INDEX IF NOT EXISTS IX_SaveBackups_Link_Date ON SaveBackups(ID_LinkedSave, Created_At DESC);
            ", connection, transaction);
        command.ExecuteNonQuery();
    }

    private static void CreateSupportTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = new SqliteCommand(@"
            CREATE TABLE IF NOT EXISTS LogType (ID_Log_Type INTEGER PRIMARY KEY AUTOINCREMENT, Log_Type_Name TEXT, Caption TEXT);
            CREATE TABLE IF NOT EXISTS Logs (ID_Log INTEGER PRIMARY KEY AUTOINCREMENT, ID_Log_Type INTEGER, Log TEXT, new_col TEXT, FOREIGN KEY (ID_Log_Type) REFERENCES LogType(ID_Log_Type));
            CREATE TABLE IF NOT EXISTS ReportType (ID_Report_Type INTEGER PRIMARY KEY AUTOINCREMENT, Report_Type_Name TEXT, Caption TEXT);
            CREATE TABLE IF NOT EXISTS Report (ID_Report INTEGER PRIMARY KEY AUTOINCREMENT, ID_Report_Type INTEGER, Timestamp TEXT, Path TEXT, FOREIGN KEY (ID_Report_Type) REFERENCES ReportType(ID_Report_Type));
            CREATE TABLE IF NOT EXISTS SaveBackups (
                ID_SaveBackup INTEGER PRIMARY KEY AUTOINCREMENT,
                ID_LinkedSave INTEGER,
                Save_Guid TEXT,
                Save_Backup_Path TEXT,
                Save_Json TEXT,
                Content_Hash TEXT,
                Created_At TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                Source_Path TEXT,
                FOREIGN KEY (ID_LinkedSave) REFERENCES LinkedSaves(ID_LinkedSave) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_Stages_Completion_Order ON Stages(ID_Completion, SortOrder, StageNumber);
            CREATE INDEX IF NOT EXISTS IX_Runs_Stage_Order ON Runs(ID_Stage, SortOrder, ID_Run);
            CREATE INDEX IF NOT EXISTS IX_SaveBackups_Link_Date ON SaveBackups(ID_LinkedSave, Created_At DESC);", connection, transaction);
        command.ExecuteNonQuery();
    }

    private static void SetSchemaVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = new SqliteCommand(@"
            INSERT INTO SchemaInfo (Singleton, SchemaVersion) VALUES (1, @version)
            ON CONFLICT(Singleton) DO UPDATE SET SchemaVersion = excluded.SchemaVersion;", connection, transaction);
        command.Parameters.AddWithValue("@version", version);
        command.ExecuteNonQuery();
    }

    private sealed class LegacySnapshot
    {
        public List<Dictionary<string, object?>> Completions { get; } = new();
        public List<Dictionary<string, object?>> Stages { get; } = new();
        public List<Dictionary<string, object?>> Runs { get; } = new();
        public List<Dictionary<string, object?>> SaveBackups { get; } = new();
        public bool RunsUseCompletionId { get; set; }
        public bool RunsUseStageId { get; set; }
        public bool RunsHaveRangeValues { get; set; }
    }

    private static LegacySnapshot ReadLegacySnapshot(SqliteConnection connection)
    {
        var snapshot = new LegacySnapshot();
        snapshot.Completions.AddRange(ReadRows(connection, "Completions"));
        if (TableExists(connection, "Stages")) snapshot.Stages.AddRange(ReadRows(connection, "Stages"));
        if (TableExists(connection, "Runs")) snapshot.Runs.AddRange(ReadRows(connection, "Runs"));
        if (TableExists(connection, "SaveBackups")) snapshot.SaveBackups.AddRange(ReadRows(connection, "SaveBackups"));

        var runColumns = snapshot.Runs.FirstOrDefault()?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? GetColumnNames(connection, "Runs");
        snapshot.RunsUseCompletionId = runColumns.Contains("ID_Completion");
        snapshot.RunsUseStageId = runColumns.Contains("ID_Stage");
        snapshot.RunsHaveRangeValues = runColumns.Contains("From") && runColumns.Contains("To");
        return snapshot;
    }

    private static List<Dictionary<string, object?>> ReadRows(SqliteConnection connection, string table)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\";";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static HashSet<string> GetColumnNames(SqliteConnection connection, string table)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists(connection, table)) return result;
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }

    private static void MigrateLegacySnapshot(SqliteConnection connection, SqliteTransaction transaction, LegacySnapshot old)
    {
        foreach (var completion in old.Completions.OrderBy(r => IntValue(r, "ID_Completion")))
        {
            using var command = new SqliteCommand(@"
                INSERT INTO Completions (ID_Completion, Level_Name, Is_Imported, Display_Order, Last_Modified)
                VALUES (@id, @name, @imported, @order, @modified);", connection, transaction);
            command.Parameters.AddWithValue("@id", IntValue(completion, "ID_Completion"));
            command.Parameters.AddWithValue("@name", StringValue(completion, "Level_Name"));
            command.Parameters.AddWithValue("@imported", IntValue(completion, "Is_Imported"));
            command.Parameters.AddWithValue("@order", IntValue(completion, "Display_Order"));
            command.Parameters.AddWithValue("@modified", StringValue(completion, "Last_Modified") is { Length: > 0 } modified ? modified : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        var knownCompletions = old.Completions.Select(r => IntValue(r, "ID_Completion")).ToHashSet();

        if (old.RunsUseStageId && old.Stages.Count > 0)
        {
            var knownStageIds = new HashSet<int>();
            foreach (var stage in old.Stages.OrderBy(r => IntValue(r, "ID_Completion")).ThenBy(r => IntValue(r, "SortOrder")).ThenBy(r => IntValue(r, "StageNumber")))
            {
                int completionId = IntValue(stage, "ID_Completion");
                if (!knownCompletions.Contains(completionId)) continue;
                int stageId = IntValue(stage, "ID_Stage");
                knownStageIds.Add(stageId);
                InsertMigratedStage(connection, transaction, completionId, stageId,
                    StringValue(stage, "StageGuid"), IntValue(stage, "StageNumber", 1), IntValue(stage, "SortOrder"),
                    IntValue(stage, "Checked"), StringValue(stage, "Note"), IntValue(stage, "CompletionCounter"));
            }

            var stageIdsByCompletion = old.Stages.GroupBy(r => IntValue(r, "ID_Completion"))
                .ToDictionary(g => g.Key, g => g.OrderBy(r => IntValue(r, "SortOrder")).ThenBy(r => IntValue(r, "StageNumber")).Select(r => IntValue(r, "ID_Stage")).ToList());

            foreach (var run in old.Runs.OrderBy(r => IntValue(r, "ID_Run")))
            {
                int stageId = IntValue(run, "ID_Stage");
                if (!knownStageIds.Contains(stageId))
                {
                    int completionId = FindCompletionForLegacyStage(old.Stages, stageId);
                    if (completionId <= 0 || !knownCompletions.Contains(completionId)) continue;
                    if (!stageIdsByCompletion.TryGetValue(completionId, out var stageIds))
                    {
                        stageIds = new List<int>();
                        stageIdsByCompletion[completionId] = stageIds;
                    }
                    stageId = EnsureMigratedStage(connection, transaction, completionId, stageIds.Count + 1, stageIds.Count);
                    stageIds.Add(stageId);
                    knownStageIds.Add(stageId);
                }
                InsertMigratedRange(connection, transaction, run, stageId, old.RunsHaveRangeValues);
            }
        }
        else if (old.RunsUseCompletionId)
        {
            foreach (var completion in old.Completions)
            {
                int completionId = IntValue(completion, "ID_Completion");
                var rows = old.Runs.Where(r => IntValue(r, "ID_Completion") == completionId)
                    .OrderBy(r => IntValue(r, "ID_Run")).ToList();

                var oldStagesForCompletion = old.Stages.Where(r => IntValue(r, "ID_Completion") == completionId)
                    .OrderBy(r => IntValue(r, "SortOrder")).ThenBy(r => IntValue(r, "StageNumber")).ToList();
                var oldStageRows = oldStagesForCompletion.ToDictionary(r => IntValue(r, "StageNumber"));

                int currentStageId = 0;
                int stageOrder = 0;
                bool sawStageMarker = rows.Any(r => StageRegex.IsMatch(StringValue(r, "Run")));
                if (!sawStageMarker && oldStagesForCompletion.Count > 0)
                {
                    foreach (var stage in oldStagesForCompletion)
                    {
                        int stageId = EnsureMigratedStage(connection, transaction, completionId,
                            IntValue(stage, "StageNumber", stageOrder + 1), stageOrder,
                            IntValue(stage, "Checked"), StringValue(stage, "Note"), IntValue(stage, "CompletionCounter"), StringValue(stage, "StageGuid"));
                        stageOrder++;
                        if (currentStageId == 0) currentStageId = stageId;
                    }
                }

                foreach (var row in rows)
                {
                    string runText = StringValue(row, "Run");
                    var stageMatch = StageRegex.Match(runText);
                    if (stageMatch.Success)
                    {
                        int stageNumber = int.TryParse(stageMatch.Groups[1].Value, out int parsedStage) ? parsedStage : stageOrder + 1;
                        oldStageRows.TryGetValue(stageNumber, out var oldStage);
                        currentStageId = EnsureMigratedStage(connection, transaction, completionId, stageNumber, stageOrder++,
                            IntValue(oldStage ?? row, "Checked", IntValue(row, "Status")),
                            StringValue(oldStage ?? row, "Note"),
                            IntValue(oldStage ?? row, "CompletionCounter"), StringValue(oldStage ?? row, "StageGuid"));
                        continue;
                    }

                    if (currentStageId == 0)
                    {
                        currentStageId = EnsureMigratedStage(connection, transaction, completionId, 1, stageOrder++);
                    }
                    InsertMigratedRange(connection, transaction, row, currentStageId, hasRangeValues: false);
                }
            }
        }
        else if (old.Stages.Count > 0)
        {
            // В промежуточной версии могли сохраниться этапы без ранов.
            foreach (var stage in old.Stages.OrderBy(r => IntValue(r, "ID_Stage")))
            {
                int completionId = IntValue(stage, "ID_Completion");
                if (!knownCompletions.Contains(completionId)) continue;
                InsertMigratedStage(connection, transaction, completionId, IntValue(stage, "ID_Stage"),
                    StringValue(stage, "StageGuid"), IntValue(stage, "StageNumber", 1), IntValue(stage, "SortOrder"),
                    IntValue(stage, "Checked"), StringValue(stage, "Note"), IntValue(stage, "CompletionCounter"));
            }
        }

        foreach (var backup in old.SaveBackups.OrderBy(r => IntValue(r, "ID_SaveBackup")))
        {
            using var command = new SqliteCommand(@"
                INSERT INTO SaveBackups (ID_SaveBackup, Save_Backup_Path, Created_At)
                VALUES (@id, @path, CURRENT_TIMESTAMP);", connection, transaction);
            command.Parameters.AddWithValue("@id", IntValue(backup, "ID_SaveBackup"));
            command.Parameters.AddWithValue("@path", StringValue(backup, "Save_Backup_Path"));
            command.ExecuteNonQuery();
        }
    }

    private static int FindCompletionForLegacyStage(List<Dictionary<string, object?>> stages, int stageId) =>
        stages.FirstOrDefault(r => IntValue(r, "ID_Stage") == stageId) is { } row ? IntValue(row, "ID_Completion") : 0;

    private static int EnsureMigratedStage(SqliteConnection connection, SqliteTransaction transaction,
        int completionId, int stageNumber, int sortOrder, int checkedValue = 0, string? note = null,
        int completionCounter = 0, string? stageGuid = null)
    {
        using var command = new SqliteCommand(@"
            INSERT INTO Stages (ID_Completion, StageGuid, StageNumber, SortOrder, Checked, Note, CompletionCounter)
            VALUES (@completion, @guid, @number, @order, @checked, @note, @counter);
            SELECT last_insert_rowid();", connection, transaction);
        command.Parameters.AddWithValue("@completion", completionId);
        command.Parameters.AddWithValue("@guid", DbValue(NullIfEmpty(stageGuid ?? string.Empty)));
        command.Parameters.AddWithValue("@number", stageNumber);
        command.Parameters.AddWithValue("@order", sortOrder);
        command.Parameters.AddWithValue("@checked", checkedValue == 1 ? 1 : 0);
        command.Parameters.AddWithValue("@note", note ?? string.Empty);
        command.Parameters.AddWithValue("@counter", completionCounter);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertMigratedStage(SqliteConnection connection, SqliteTransaction transaction,
        int completionId, int stageId, string? stageGuid, int stageNumber, int sortOrder,
        int checkedValue, string? note, int completionCounter)
    {
        using var command = new SqliteCommand(@"
            INSERT INTO Stages (ID_Stage, ID_Completion, StageGuid, StageNumber, SortOrder, Checked, Note, CompletionCounter)
            VALUES (@id, @completion, @guid, @number, @order, @checked, @note, @counter);", connection, transaction);
        command.Parameters.AddWithValue("@id", stageId);
        command.Parameters.AddWithValue("@completion", completionId);
        command.Parameters.AddWithValue("@guid", DbValue(NullIfEmpty(stageGuid ?? string.Empty)));
        command.Parameters.AddWithValue("@number", stageNumber);
        command.Parameters.AddWithValue("@order", sortOrder);
        command.Parameters.AddWithValue("@checked", checkedValue == 1 ? 1 : 0);
        command.Parameters.AddWithValue("@note", note ?? string.Empty);
        command.Parameters.AddWithValue("@counter", completionCounter);
        command.ExecuteNonQuery();
    }

    private static void InsertMigratedRange(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<string, object?> oldRun, int stageId, bool hasRangeValues)
    {
        double? from = hasRangeValues ? DoubleNullable(oldRun, "From") : null;
        double? to = hasRangeValues ? DoubleNullable(oldRun, "To") : null;
        string text = StringValue(oldRun, "Run");
        if (string.IsNullOrWhiteSpace(text) && from.HasValue && to.HasValue)
            text = FormatRange(from.Value, to.Value);
        if (!from.HasValue || !to.HasValue)
        {
            if (TryParseRange(text, out double parsedFrom, out double parsedTo))
            {
                from ??= parsedFrom;
                to ??= parsedTo;
            }
        }

        using var command = new SqliteCommand(@"
            INSERT INTO Runs (ID_Run, ID_Stage, SortOrder, RunText, ""From"", ""To"", Status, Attempts, Note,
                FirstRunFrom, FirstRunTo, BestRunFrom, BestRunTo, Consider, AutomaticallyClosed, TimePlayed,
                CompletedAt, AttemptsToComplete, CompletionCounter)
            VALUES (@id, @stage, @order, @text, @from, @to, @status, @attempts, @note,
                @firstFrom, @firstTo, @bestFrom, @bestTo, @consider, @auto, @timePlayed, @completedAt, @attemptsToComplete, @counter);", connection, transaction);
        command.Parameters.AddWithValue("@id", IntValue(oldRun, "ID_Run"));
        command.Parameters.AddWithValue("@stage", stageId);
        command.Parameters.AddWithValue("@order", IntValue(oldRun, "SortOrder", IntValue(oldRun, "ID_Run")));
        command.Parameters.AddWithValue("@text", text);
        command.Parameters.AddWithValue("@from", DbValue(from));
        command.Parameters.AddWithValue("@to", DbValue(to));
        command.Parameters.AddWithValue("@status", IntValue(oldRun, "Status") == 1 ? 1 : 0);
        command.Parameters.AddWithValue("@attempts", IntValue(oldRun, "Attempts"));
        command.Parameters.AddWithValue("@note", StringValue(oldRun, "Note"));
        command.Parameters.AddWithValue("@firstFrom", DbValue(DoubleNullable(oldRun, "FirstRunFrom")));
        command.Parameters.AddWithValue("@firstTo", DbValue(DoubleNullable(oldRun, "FirstRunTo")));
        command.Parameters.AddWithValue("@bestFrom", DbValue(DoubleNullable(oldRun, "BestRunFrom")));
        command.Parameters.AddWithValue("@bestTo", DbValue(DoubleNullable(oldRun, "BestRunTo")));
        command.Parameters.AddWithValue("@consider", IntValue(oldRun, "Consider", 1) == 0 ? 0 : 1);
        command.Parameters.AddWithValue("@auto", IntValue(oldRun, "AutomaticallyClosed"));
        command.Parameters.AddWithValue("@timePlayed", DoubleValue(oldRun, "TimePlayed"));
        command.Parameters.AddWithValue("@completedAt", DbValue(LongNullable(oldRun, "CompletedAt")));
        command.Parameters.AddWithValue("@attemptsToComplete", IntValue(oldRun, "AttemptsToComplete"));
        command.Parameters.AddWithValue("@counter", IntValue(oldRun, "CompletionCounter"));
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        command.Parameters.AddWithValue("@name", name);
        return command.ExecuteScalar() != null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column) =>
        GetColumnNames(connection, table).Contains(column);

    private static int IntValue(Dictionary<string, object?> row, string key, int fallback = 0)
    {
        if (!row.TryGetValue(key, out var value) || value == null || value == DBNull.Value) return fallback;
        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
    }

    private static long? LongNullable(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null || value == DBNull.Value) return null;
        return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : null;
    }

    private static string StringValue(Dictionary<string, object?> row, string key, string fallback = "")
    {
        if (!row.TryGetValue(key, out var value) || value == null || value == DBNull.Value) return fallback;
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
    }

    private static double DoubleValue(Dictionary<string, object?> row, string key, double fallback = 0d) =>
        DoubleNullable(row, key) ?? fallback;

    private static double? DoubleNullable(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null || value == DBNull.Value) return null;
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result) ? result : null;
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    public static List<Blitzkrieg> GetBlitzkriegs()
    {
        var result = new List<Blitzkrieg>();
        var byCompletion = new Dictionary<int, Blitzkrieg>();
        var lastStageId = new Dictionary<int, int>();

        const string sql = @"
            SELECT c.ID_Completion, c.Level_Name, IFNULL(ls.SaveGuid, ''), IFNULL(ls.SavePath, ''), IFNULL(ls.SyncState, 'OK'),
                   s.ID_Stage, s.StageGuid, s.StageNumber, s.SortOrder AS StageOrder, s.Checked AS StageChecked,
                   s.Note AS StageNote, s.CompletionCounter AS StageCounter,
                   r.ID_Run, r.RangeGuid, r.SortOrder AS RunOrder, r.RunText, r.""From"", r.""To"", r.Status,
                   r.Attempts, r.Note AS RunNote, r.FirstRunFrom, r.FirstRunTo, r.BestRunFrom, r.BestRunTo,
                   r.Consider, r.AutomaticallyClosed, r.TimePlayed, r.CompletedAt, r.AttemptsToComplete, r.CompletionCounter AS RunCounter
            FROM Completions c
            LEFT JOIN LinkedSaves ls ON ls.ID_Completion = c.ID_Completion
            LEFT JOIN Stages s ON s.ID_Completion = c.ID_Completion
            LEFT JOIN Runs r ON r.ID_Stage = s.ID_Stage
            ORDER BY c.Last_Modified DESC, c.Display_Order, c.ID_Completion DESC, s.SortOrder, s.StageNumber, r.SortOrder, r.ID_Run;";

        using var connection = OpenConnection();
        using var command = new SqliteCommand(sql, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int completionId = reader.GetInt32(0);
            if (!byCompletion.TryGetValue(completionId, out var table))
            {
                table = new Blitzkrieg
                {
                    IdCompletion = completionId,
                    LevelName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    IsLinkedSave = !reader.IsDBNull(2) && !string.IsNullOrWhiteSpace(reader.GetString(2)),
                    SaveGuid = reader.IsDBNull(2) ? null : NullIfEmpty(reader.GetString(2)),
                    SavePath = reader.IsDBNull(3) ? null : NullIfEmpty(reader.GetString(3)),
                    SyncState = reader.IsDBNull(4) ? "OK" : reader.GetString(4)
                };
                byCompletion.Add(completionId, table);
                result.Add(table);
            }

            if (reader.IsDBNull(5)) continue;
            int stageId = reader.GetInt32(5);
            if (!lastStageId.TryGetValue(completionId, out int existingStageId) || existingStageId != stageId)
            {
                table.Runs.Add(new BlitzkriegRun
                {
                    IdStage = stageId,
                    IsStage = true,
                    StageGuid = reader.IsDBNull(6) ? null : reader.GetString(6),
                    StageNumber = reader.IsDBNull(7) ? 1 : reader.GetInt32(7),
                    SortOrder = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    IsChecked = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                    Note = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    CompletionCounter = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                    IsLinked = table.IsLinkedSave,
                    Run = $"Stage {(reader.IsDBNull(7) ? 1 : reader.GetInt32(7))}"
                });
                lastStageId[completionId] = stageId;
            }

            if (reader.IsDBNull(12)) continue;
            double? from = reader.IsDBNull(16) ? null : reader.GetDouble(16);
            double? to = reader.IsDBNull(17) ? null : reader.GetDouble(17);
            string displayRun = from.HasValue && to.HasValue && table.IsLinkedSave
                ? FormatRange(from.Value, to.Value)
                : (reader.IsDBNull(15) ? string.Empty : reader.GetString(15));
            table.Runs.Add(new BlitzkriegRun
            {
                IdRun = reader.GetInt32(12),
                IdStage = stageId,
                RangeGuid = reader.IsDBNull(13) ? null : reader.GetString(13),
                SortOrder = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                Run = displayRun,
                FromValue = from,
                ToValue = to,
                IsChecked = !reader.IsDBNull(18) && reader.GetInt32(18) == 1,
                Attempts = reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                Note = reader.IsDBNull(20) ? string.Empty : reader.GetString(20),
                FirstRunFrom = reader.IsDBNull(21) ? null : reader.GetDouble(21),
                FirstRunTo = reader.IsDBNull(22) ? null : reader.GetDouble(22),
                BestRunFrom = reader.IsDBNull(23) ? null : reader.GetDouble(23),
                BestRunTo = reader.IsDBNull(24) ? null : reader.GetDouble(24),
                Consider = reader.IsDBNull(25) || reader.GetInt32(25) == 1,
                AutomaticallyClosed = !reader.IsDBNull(26) && reader.GetInt32(26) == 1,
                TimePlayed = reader.IsDBNull(27) ? 0 : reader.GetDouble(27),
                CompletedAt = reader.IsDBNull(28) ? null : reader.GetInt64(28),
                AttemptsToComplete = reader.IsDBNull(29) ? 0 : reader.GetInt32(29),
                CompletionCounter = reader.IsDBNull(30) ? 0 : reader.GetInt32(30),
                IsLinked = table.IsLinkedSave
            });
        }

        return result;
    }

    public static Blitzkrieg? GetBlitzkriegById(int completionId) =>
        GetBlitzkriegs().FirstOrDefault(t => t.IdCompletion == completionId);

    public static int AddBlitzkrieg(Blitzkrieg blitzkrieg, string? levelName = null, int? idLevel = null, bool isImported = false)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        int completionId;
        using (var command = new SqliteCommand(@"
            INSERT INTO Completions (Level_Name, Is_Imported, Display_Order, Last_Modified)
            VALUES (@name, @imported, COALESCE((SELECT MAX(Display_Order) + 1 FROM Completions), 0), strftime('%Y-%m-%d %H:%M:%f', 'now'));
            SELECT last_insert_rowid();", connection, transaction))
        {
            command.Parameters.AddWithValue("@name", levelName ?? string.Empty);
            command.Parameters.AddWithValue("@imported", isImported ? 1 : 0);
            completionId = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        InsertGeneratedRows(connection, transaction, completionId, blitzkrieg.Runs);
        transaction.Commit();
        return completionId;
    }

    private static void InsertGeneratedRows(SqliteConnection connection, SqliteTransaction transaction, int completionId, IEnumerable<BlitzkriegRun> rows)
    {
        int stageOrder = 0;
        int runOrder = 0;
        int currentStageId = 0;
        foreach (var row in rows)
        {
            var stageMatch = StageRegex.Match(row.Run ?? string.Empty);
            if (stageMatch.Success)
            {
                int stageNumber = int.TryParse(stageMatch.Groups[1].Value, out int parsed) ? parsed : stageOrder + 1;
                currentStageId = InsertStage(connection, transaction, completionId, null, stageNumber, stageOrder++, row.IsChecked, row.Note, row.CompletionCounter);
                runOrder = 0;
                continue;
            }

            if (currentStageId == 0)
                currentStageId = InsertStage(connection, transaction, completionId, null, 1, stageOrder++, false, string.Empty, 0);

            double? from = null;
            double? to = null;
            if (TryParseRange(row.Run, out double fromParsed, out double toParsed))
            {
                from = fromParsed;
                to = toParsed;
            }
            InsertRun(connection, transaction, currentStageId, null, runOrder++, row.Run, from, to,
                row.IsChecked, row.Attempts, row.Note, row.FirstRunFrom, row.FirstRunTo, row.BestRunFrom, row.BestRunTo,
                row.Consider, row.AutomaticallyClosed, row.TimePlayed, row.CompletedAt, row.AttemptsToComplete, row.CompletionCounter);
        }
    }

    private static int InsertStage(SqliteConnection connection, SqliteTransaction transaction, int completionId,
        string? stageGuid, int stageNumber, int sortOrder, bool isChecked, string? note, int completionCounter)
    {
        using var command = new SqliteCommand(@"
            INSERT INTO Stages (ID_Completion, StageGuid, StageNumber, SortOrder, Checked, Note, CompletionCounter)
            VALUES (@completion, @guid, @number, @order, @checked, @note, @counter);
            SELECT last_insert_rowid();", connection, transaction);
        command.Parameters.AddWithValue("@completion", completionId);
        command.Parameters.AddWithValue("@guid", DbValue(NullIfEmpty(stageGuid ?? string.Empty)));
        command.Parameters.AddWithValue("@number", stageNumber);
        command.Parameters.AddWithValue("@order", sortOrder);
        command.Parameters.AddWithValue("@checked", isChecked ? 1 : 0);
        command.Parameters.AddWithValue("@note", note ?? string.Empty);
        command.Parameters.AddWithValue("@counter", completionCounter);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertRun(SqliteConnection connection, SqliteTransaction transaction, int stageId,
        string? rangeGuid, int sortOrder, string runText, double? from, double? to, bool isChecked, int attempts,
        string? note, double? firstRunFrom, double? firstRunTo, double? bestRunFrom, double? bestRunTo,
        bool consider, bool automaticallyClosed, double timePlayed, long? completedAt, int attemptsToComplete, int completionCounter)
    {
        using var command = new SqliteCommand(@"
            INSERT INTO Runs (ID_Stage, RangeGuid, SortOrder, RunText, ""From"", ""To"", Status, Attempts, Note,
                FirstRunFrom, FirstRunTo, BestRunFrom, BestRunTo, Consider, AutomaticallyClosed, TimePlayed,
                CompletedAt, AttemptsToComplete, CompletionCounter)
            VALUES (@stage, @guid, @order, @text, @from, @to, @status, @attempts, @note,
                @firstFrom, @firstTo, @bestFrom, @bestTo, @consider, @auto, @timePlayed, @completedAt, @attemptsToComplete, @counter);", connection, transaction);
        command.Parameters.AddWithValue("@stage", stageId);
        command.Parameters.AddWithValue("@guid", DbValue(rangeGuid));
        command.Parameters.AddWithValue("@order", sortOrder);
        command.Parameters.AddWithValue("@text", runText ?? string.Empty);
        command.Parameters.AddWithValue("@from", DbValue(from));
        command.Parameters.AddWithValue("@to", DbValue(to));
        command.Parameters.AddWithValue("@status", isChecked ? 1 : 0);
        command.Parameters.AddWithValue("@attempts", Math.Max(0, attempts));
        command.Parameters.AddWithValue("@note", note ?? string.Empty);
        command.Parameters.AddWithValue("@firstFrom", DbValue(firstRunFrom));
        command.Parameters.AddWithValue("@firstTo", DbValue(firstRunTo));
        command.Parameters.AddWithValue("@bestFrom", DbValue(bestRunFrom));
        command.Parameters.AddWithValue("@bestTo", DbValue(bestRunTo));
        command.Parameters.AddWithValue("@consider", consider ? 1 : 0);
        command.Parameters.AddWithValue("@auto", automaticallyClosed ? 1 : 0);
        command.Parameters.AddWithValue("@timePlayed", timePlayed);
        command.Parameters.AddWithValue("@completedAt", DbValue(completedAt));
        command.Parameters.AddWithValue("@attemptsToComplete", attemptsToComplete);
        command.Parameters.AddWithValue("@counter", completionCounter);
        command.ExecuteNonQuery();
    }

    public static void UpdateRun(int completionId, RunItem run, bool updateRangeText = false)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        if (run.IsStage)
        {
            using var command = new SqliteCommand(@"
                UPDATE Stages SET Checked = @checked, Note = @note
                WHERE ID_Stage = @stage AND ID_Completion = @completion;", connection, transaction);
            command.Parameters.AddWithValue("@checked", run.IsChecked ? 1 : 0);
            command.Parameters.AddWithValue("@note", run.Note ?? string.Empty);
            command.Parameters.AddWithValue("@stage", run.IdStage);
            command.Parameters.AddWithValue("@completion", completionId);
            command.ExecuteNonQuery();
        }
        else
        {
            double? from = run.FromValue;
            double? to = run.ToValue;
            // В таблице границы связанного рана показываются с округлением вниз. Не нужно
            // разбирать это отображаемое значение при изменении заметок, попыток или отметок,
            // иначе исходное значение вроде 18.98 потеряет точность.
            if (updateRangeText && TryParseRange(run.Run, out double parsedFrom, out double parsedTo))
            {
                from = parsedFrom;
                to = parsedTo;
                run.FromValue = from;
                run.ToValue = to;
            }

            using var command = new SqliteCommand(@"
                UPDATE Runs SET RunText = @text, ""From"" = @from, ""To"" = @to, Status = @status,
                    Attempts = @attempts, Note = @note
                WHERE ID_Run = @id AND ID_Stage IN (SELECT ID_Stage FROM Stages WHERE ID_Completion = @completion);", connection, transaction);
            command.Parameters.AddWithValue("@text", run.Run ?? string.Empty);
            command.Parameters.AddWithValue("@from", DbValue(from));
            command.Parameters.AddWithValue("@to", DbValue(to));
            command.Parameters.AddWithValue("@status", run.IsChecked ? 1 : 0);
            command.Parameters.AddWithValue("@attempts", ParseAttempts(run));
            command.Parameters.AddWithValue("@note", run.Note ?? string.Empty);
            command.Parameters.AddWithValue("@id", run.IdRun);
            command.Parameters.AddWithValue("@completion", completionId);
            command.ExecuteNonQuery();
        }

        using (var command = new SqliteCommand("UPDATE Completions SET Last_Modified = strftime('%Y-%m-%d %H:%M:%f', 'now') WHERE ID_Completion = @id;", connection, transaction))
        {
            command.Parameters.AddWithValue("@id", completionId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static void UpdateLevelName(int completionId, string? name)
    {
        using var connection = OpenConnection();
        using var command = new SqliteCommand(@"
            UPDATE Completions SET Level_Name = @name, Last_Modified = strftime('%Y-%m-%d %H:%M:%f', 'now')
            WHERE ID_Completion = @id;", connection);
        command.Parameters.AddWithValue("@name", name ?? string.Empty);
        command.Parameters.AddWithValue("@id", completionId);
        command.ExecuteNonQuery();
    }

    public static void UpdateDisplayOrders(IReadOnlyList<int> completionIds)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = new SqliteCommand("UPDATE Completions SET Display_Order = @order WHERE ID_Completion = @id;", connection, transaction);
        var order = command.Parameters.Add("@order", SqliteType.Integer);
        var id = command.Parameters.Add("@id", SqliteType.Integer);
        for (int i = 0; i < completionIds.Count; i++)
        {
            order.Value = i;
            id.Value = completionIds[i];
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static void DeleteBlitzkrieg(int completionId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = new SqliteCommand("DELETE FROM Completions WHERE ID_Completion = @id;", connection, transaction);
        command.Parameters.AddWithValue("@id", completionId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static int ParseAttempts(RunItem run) =>
        int.TryParse(run.Attempts, NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempts) ? Math.Max(0, attempts) : 0;

    private static string FormatRange(double from, double to) =>
        $"{Math.Floor(from).ToString(CultureInfo.InvariantCulture)} - {Math.Floor(to).ToString(CultureInfo.InvariantCulture)}";

    private static bool TryParseRange(string? text, out double from, out double to)
    {
        from = to = 0;
        var match = RangeRegex.Match(text ?? string.Empty);
        if (!match.Success) return false;
        string fromText = match.Groups[1].Value.Replace(',', '.');
        string toText = match.Groups[2].Value.Replace(',', '.');
        return double.TryParse(fromText, NumberStyles.Float, CultureInfo.InvariantCulture, out from) &&
               double.TryParse(toText, NumberStyles.Float, CultureInfo.InvariantCulture, out to) &&
               double.IsFinite(from) && double.IsFinite(to) && from >= 0 && from <= 100 && to >= 0 && to <= 100 && from <= to;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static List<LinkedSaveInfo> GetLinkedSaves()
    {
        var result = new List<LinkedSaveInfo>();
        using var connection = OpenConnection();
        using var command = new SqliteCommand(@"
            SELECT ls.ID_LinkedSave, ls.ID_Completion, ls.SaveGuid, ls.SavePath, ls.LastSyncedJson,
                   ls.LastSyncedHash, ls.SyncState, c.Level_Name
            FROM LinkedSaves ls JOIN Completions c ON c.ID_Completion = ls.ID_Completion
            ORDER BY ls.ID_LinkedSave;", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LinkedSaveInfo
            {
                IdLinkedSave = reader.GetInt32(0),
                IdCompletion = reader.GetInt32(1),
                SaveGuid = reader.GetString(2),
                SavePath = reader.GetString(3),
                LastSyncedJson = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                LastSyncedHash = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                SyncState = reader.IsDBNull(6) ? "OK" : reader.GetString(6),
                LevelName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
            });
        }
        return result;
    }

    public static LinkSaveResult CreateLinkedSave(ModSaveDocument document, string path)
    {
        string compactJson = document.ToCompactJson();
        string hash = ComputeHash(compactJson);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var check = new SqliteCommand("SELECT ls.ID_LinkedSave, c.ID_Completion, c.Level_Name FROM LinkedSaves ls JOIN Completions c ON c.ID_Completion = ls.ID_Completion WHERE ls.SaveGuid = @guid COLLATE NOCASE;", connection, transaction))
        {
            check.Parameters.AddWithValue("@guid", document.SaveGuid);
            using var reader = check.ExecuteReader();
            if (reader.Read())
            {
                return new LinkSaveResult
                {
                    AlreadyLinked = true,
                    IdLinkedSave = reader.GetInt32(0),
                    IdCompletion = reader.GetInt32(1),
                    ExistingLevelName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    SaveGuid = document.SaveGuid
                };
            }
        }

        int completionId;
        using (var command = new SqliteCommand(@"
            INSERT INTO Completions (Level_Name, Is_Imported, Display_Order, Last_Modified)
            VALUES (@name, 1, COALESCE((SELECT MAX(Display_Order) + 1 FROM Completions), 0), strftime('%Y-%m-%d %H:%M:%f', 'now'));
            SELECT last_insert_rowid();", connection, transaction))
        {
            command.Parameters.AddWithValue("@name", document.ProfileName);
            completionId = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        foreach (var stage in document.Stages.Select((value, index) => (value, index)))
        {
            int stageId = InsertStage(connection, transaction, completionId, stage.value.Id, stage.value.StageNumber,
                stage.index, stage.value.IsChecked, stage.value.Note, stage.value.CompletionCounter);
            foreach (var range in stage.value.Ranges.Select((value, index) => (value, index)))
            {
                InsertRun(connection, transaction, stageId, range.value.Id, range.index, FormatRange(range.value.From, range.value.To),
                    range.value.From, range.value.To, range.value.IsChecked, range.value.Attempts, range.value.Note,
                    range.value.FirstRunFrom, range.value.FirstRunTo, range.value.BestRunFrom, range.value.BestRunTo,
                    range.value.Consider, range.value.AutomaticallyClosed, range.value.TimePlayed, range.value.CompletedAt,
                    range.value.AttemptsToComplete, range.value.CompletionCounter);
            }
        }

        int linkedId;
        using (var command = new SqliteCommand(@"
            INSERT INTO LinkedSaves (ID_Completion, SaveGuid, SavePath, LastSyncedJson, LastSyncedHash, SyncState, LastSyncUtc)
            VALUES (@completion, @guid, @path, @json, @hash, 'OK', @time);
            SELECT last_insert_rowid();", connection, transaction))
        {
            command.Parameters.AddWithValue("@completion", completionId);
            command.Parameters.AddWithValue("@guid", document.SaveGuid);
            command.Parameters.AddWithValue("@path", Path.GetFullPath(path));
            command.Parameters.AddWithValue("@json", compactJson);
            command.Parameters.AddWithValue("@hash", hash);
            command.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            linkedId = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        InsertBackup(connection, transaction, linkedId, document.SaveGuid, path, compactJson, hash);
        transaction.Commit();
        return new LinkSaveResult { Success = true, IdLinkedSave = linkedId, IdCompletion = completionId, SaveGuid = document.SaveGuid };
    }

    public static bool ApplyModSnapshot(int linkedSaveId, ModSaveDocument document, bool forceOverwrite, bool updateBaseline)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var link = GetLinkedSave(connection, transaction, linkedSaveId);
        if (link == null) return false;

        JsonNode? baselineRoot = null;
        try { baselineRoot = string.IsNullOrWhiteSpace(link.LastSyncedJson) ? null : JsonNode.Parse(link.LastSyncedJson); }
        catch { baselineRoot = null; }

        int completionId = link.IdCompletion;
        var existingStages = ReadExistingStages(connection, transaction, completionId);
        var existingRanges = ReadExistingRanges(connection, transaction, completionId);
        var currentStageIds = document.Stages.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentAllRangeIds = document.Stages.SelectMany(s => s.Ranges).Select(r => r.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool visibleDataChanged = forceOverwrite || !string.Equals(link.LastSyncedHash, ComputeHash(document.ToCompactJson()), StringComparison.OrdinalIgnoreCase);

        JsonObject? baselineObject = baselineRoot as JsonObject;
        if (baselineObject == null || forceOverwrite || !ModSaveDocument.JsonFieldEquals(baselineObject, document.Root, "profileName"))
        {
            // Если мод изменил profileName, используем его название. Если JSON не менялся, локальное переименование ждёт синхронизации.
            if (baselineObject == null || forceOverwrite || !ModSaveDocument.JsonFieldEquals(baselineObject, document.Root, "profileName"))
                UpdateCompletionName(connection, transaction, completionId, document.ProfileName);
        }

        foreach (var stage in document.Stages.Select((value, index) => (value, index)))
        {
            existingStages.TryGetValue(stage.value.Id, out var priorStage);
            JsonObject? oldStageNode = ModSaveDocument.FindStageNode(baselineRoot, stage.value.Id);
            bool stageChecked = priorStage?.Checked ?? stage.value.IsChecked;
            if (priorStage == null || forceOverwrite || oldStageNode == null || !ModSaveDocument.JsonFieldEquals(oldStageNode, stage.value.Node, "checked"))
                stageChecked = stage.value.IsChecked;
            string stageNote = priorStage?.Note ?? stage.value.Note;

            int stageId;
            if (priorStage == null)
            {
                stageId = InsertStage(connection, transaction, completionId, stage.value.Id, stage.value.StageNumber,
                    stage.index, stageChecked, stageNote, stage.value.CompletionCounter);
            }
            else
            {
                stageId = priorStage.IdStage;
                using var update = new SqliteCommand(@"
                    UPDATE Stages SET StageNumber=@number, SortOrder=@order, Checked=@checked, Note=@note, CompletionCounter=@counter
                    WHERE ID_Stage=@id AND ID_Completion=@completion;", connection, transaction);
                update.Parameters.AddWithValue("@number", stage.value.StageNumber);
                update.Parameters.AddWithValue("@order", stage.index);
                update.Parameters.AddWithValue("@checked", stageChecked ? 1 : 0);
                update.Parameters.AddWithValue("@note", stageNote);
                update.Parameters.AddWithValue("@counter", stage.value.CompletionCounter);
                update.Parameters.AddWithValue("@id", stageId);
                update.Parameters.AddWithValue("@completion", completionId);
                update.ExecuteNonQuery();
            }

            foreach (var range in stage.value.Ranges.Select((value, index) => (value, index)))
            {
                existingRanges.TryGetValue(range.value.Id, out var priorRange);
                JsonObject? oldRangeNode = ModSaveDocument.FindRangeNode(baselineRoot, stage.value.Id, range.value.Id);
                bool changedFrom = priorRange == null || forceOverwrite || oldRangeNode == null || !ModSaveDocument.JsonFieldEquals(oldRangeNode, range.value.Node, "from");
                bool changedTo = priorRange == null || forceOverwrite || oldRangeNode == null || !ModSaveDocument.JsonFieldEquals(oldRangeNode, range.value.Node, "to");
                bool changedChecked = priorRange == null || forceOverwrite || oldRangeNode == null || !ModSaveDocument.JsonFieldEquals(oldRangeNode, range.value.Node, "checked");
                bool changedAttempts = priorRange == null || forceOverwrite || oldRangeNode == null || !ModSaveDocument.JsonFieldEquals(oldRangeNode, range.value.Node, "attempts");

                double from = changedFrom ? range.value.From : priorRange?.From ?? range.value.From;
                double to = changedTo ? range.value.To : priorRange?.To ?? range.value.To;
                bool checkedValue = changedChecked ? range.value.IsChecked : priorRange?.Checked ?? range.value.IsChecked;
                int attempts = changedAttempts ? range.value.Attempts : priorRange?.Attempts ?? range.value.Attempts;
                string note = priorRange?.Note ?? range.value.Note;

                if (priorRange == null)
                {
                    InsertRun(connection, transaction, stageId, range.value.Id, range.index, FormatRange(from, to), from, to,
                        checkedValue, attempts, note, range.value.FirstRunFrom, range.value.FirstRunTo, range.value.BestRunFrom,
                        range.value.BestRunTo, range.value.Consider, range.value.AutomaticallyClosed, range.value.TimePlayed,
                        range.value.CompletedAt, range.value.AttemptsToComplete, range.value.CompletionCounter);
                }
                else
                {
                    using var update = new SqliteCommand(@"
                        UPDATE Runs SET ID_Stage=@stage, SortOrder=@order, RunText=@text, ""From""=@from, ""To""=@to,
                            Status=@status, Attempts=@attempts, Note=@note, FirstRunFrom=@firstFrom, FirstRunTo=@firstTo,
                            BestRunFrom=@bestFrom, BestRunTo=@bestTo, Consider=@consider, AutomaticallyClosed=@auto,
                            TimePlayed=@timePlayed, CompletedAt=@completedAt, AttemptsToComplete=@attemptsToComplete,
                            CompletionCounter=@counter
                        WHERE ID_Run=@id;", connection, transaction);
                    update.Parameters.AddWithValue("@stage", stageId);
                    update.Parameters.AddWithValue("@order", range.index);
                    update.Parameters.AddWithValue("@text", FormatRange(from, to));
                    update.Parameters.AddWithValue("@from", from);
                    update.Parameters.AddWithValue("@to", to);
                    update.Parameters.AddWithValue("@status", checkedValue ? 1 : 0);
                    update.Parameters.AddWithValue("@attempts", Math.Max(0, attempts));
                    update.Parameters.AddWithValue("@note", note);
                    update.Parameters.AddWithValue("@firstFrom", DbValue(range.value.FirstRunFrom));
                    update.Parameters.AddWithValue("@firstTo", DbValue(range.value.FirstRunTo));
                    update.Parameters.AddWithValue("@bestFrom", DbValue(range.value.BestRunFrom));
                    update.Parameters.AddWithValue("@bestTo", DbValue(range.value.BestRunTo));
                    update.Parameters.AddWithValue("@consider", range.value.Consider ? 1 : 0);
                    update.Parameters.AddWithValue("@auto", range.value.AutomaticallyClosed ? 1 : 0);
                    update.Parameters.AddWithValue("@timePlayed", range.value.TimePlayed);
                    update.Parameters.AddWithValue("@completedAt", DbValue(range.value.CompletedAt));
                    update.Parameters.AddWithValue("@attemptsToComplete", range.value.AttemptsToComplete);
                    update.Parameters.AddWithValue("@counter", range.value.CompletionCounter);
                    update.Parameters.AddWithValue("@id", priorRange.IdRun);
                    update.ExecuteNonQuery();
                }
            }

            foreach (var staleRange in existingRanges.Values.Where(r => r.StageGuid != null && string.Equals(r.StageGuid, stage.value.Id, StringComparison.OrdinalIgnoreCase) && !currentAllRangeIds.Contains(r.RangeGuid)))
            {
                using var delete = new SqliteCommand("DELETE FROM Runs WHERE ID_Run=@id;", connection, transaction);
                delete.Parameters.AddWithValue("@id", staleRange.IdRun);
                delete.ExecuteNonQuery();
            }
        }

        foreach (var staleStage in existingStages.Values.Where(s => string.IsNullOrWhiteSpace(s.StageGuid) || !currentStageIds.Contains(s.StageGuid)))
        {
            using var delete = new SqliteCommand("DELETE FROM Stages WHERE ID_Stage=@id;", connection, transaction);
            delete.Parameters.AddWithValue("@id", staleStage.IdStage);
            delete.ExecuteNonQuery();
        }

        if (visibleDataChanged)
        {
            using var touch = new SqliteCommand("UPDATE Completions SET Last_Modified=strftime('%Y-%m-%d %H:%M:%f', 'now') WHERE ID_Completion=@id;", connection, transaction);
            touch.Parameters.AddWithValue("@id", completionId);
            touch.ExecuteNonQuery();
        }

        if (updateBaseline)
        {
            string compact = document.ToCompactJson();
            using var update = new SqliteCommand(@"
                UPDATE LinkedSaves SET LastSyncedJson=@json, LastSyncedHash=@hash, LastSyncUtc=@time, SyncState='OK'
                WHERE ID_LinkedSave=@id;", connection, transaction);
            update.Parameters.AddWithValue("@json", compact);
            update.Parameters.AddWithValue("@hash", ComputeHash(compact));
            update.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("@id", linkedSaveId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return visibleDataChanged;
    }

    private static void UpdateCompletionName(SqliteConnection connection, SqliteTransaction transaction, int completionId, string name)
    {
        using var command = new SqliteCommand("UPDATE Completions SET Level_Name=@name WHERE ID_Completion=@id;", connection, transaction);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@id", completionId);
        command.ExecuteNonQuery();
    }

    private sealed class ExistingStage
    {
        public int IdStage { get; set; }
        public string? StageGuid { get; set; }
        public int StageNumber { get; set; }
        public bool Checked { get; set; }
        public string Note { get; set; } = string.Empty;
        public int CompletionCounter { get; set; }
    }

    private sealed class ExistingRange
    {
        public int IdRun { get; set; }
        public int IdStage { get; set; }
        public string? StageGuid { get; set; }
        public string RangeGuid { get; set; } = string.Empty;
        public double? From { get; set; }
        public double? To { get; set; }
        public bool Checked { get; set; }
        public int Attempts { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    private static Dictionary<string, ExistingStage> ReadExistingStages(SqliteConnection connection, SqliteTransaction transaction, int completionId)
    {
        var result = new Dictionary<string, ExistingStage>(StringComparer.OrdinalIgnoreCase);
        using var command = new SqliteCommand("SELECT ID_Stage, StageGuid, StageNumber, Checked, Note, CompletionCounter FROM Stages WHERE ID_Completion=@id;", connection, transaction);
        command.Parameters.AddWithValue("@id", completionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stage = new ExistingStage
            {
                IdStage = reader.GetInt32(0),
                StageGuid = reader.IsDBNull(1) ? null : reader.GetString(1),
                StageNumber = reader.GetInt32(2),
                Checked = reader.GetInt32(3) == 1,
                Note = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                CompletionCounter = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
            };
            if (!string.IsNullOrWhiteSpace(stage.StageGuid)) result[stage.StageGuid] = stage;
        }
        return result;
    }

    private static Dictionary<string, ExistingRange> ReadExistingRanges(SqliteConnection connection, SqliteTransaction transaction, int completionId)
    {
        var result = new Dictionary<string, ExistingRange>(StringComparer.OrdinalIgnoreCase);
        using var command = new SqliteCommand(@"
            SELECT r.ID_Run, r.ID_Stage, s.StageGuid, r.RangeGuid, r.""From"", r.""To"", r.Status, r.Attempts, r.Note
            FROM Runs r JOIN Stages s ON s.ID_Stage=r.ID_Stage WHERE s.ID_Completion=@id;", connection, transaction);
        command.Parameters.AddWithValue("@id", completionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var range = new ExistingRange
            {
                IdRun = reader.GetInt32(0),
                IdStage = reader.GetInt32(1),
                StageGuid = reader.IsDBNull(2) ? null : reader.GetString(2),
                RangeGuid = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                From = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                To = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Checked = reader.GetInt32(6) == 1,
                Attempts = reader.GetInt32(7),
                Note = reader.IsDBNull(8) ? string.Empty : reader.GetString(8)
            };
            if (!string.IsNullOrWhiteSpace(range.RangeGuid)) result[range.RangeGuid] = range;
        }
        return result;
    }

    private sealed class InternalLinkedSave
    {
        public int IdLinkedSave { get; set; }
        public int IdCompletion { get; set; }
        public string SaveGuid { get; set; } = string.Empty;
        public string SavePath { get; set; } = string.Empty;
        public string LastSyncedJson { get; set; } = string.Empty;
        public string LastSyncedHash { get; set; } = string.Empty;
        public string SyncState { get; set; } = "OK";
    }

    private static InternalLinkedSave? GetLinkedSave(SqliteConnection connection, SqliteTransaction transaction, int id)
    {
        using var command = new SqliteCommand("SELECT ID_LinkedSave, ID_Completion, SaveGuid, SavePath, LastSyncedJson, LastSyncedHash, SyncState FROM LinkedSaves WHERE ID_LinkedSave=@id;", connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new InternalLinkedSave
        {
            IdLinkedSave = reader.GetInt32(0),
            IdCompletion = reader.GetInt32(1),
            SaveGuid = reader.GetString(2),
            SavePath = reader.GetString(3),
            LastSyncedJson = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            LastSyncedHash = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            SyncState = reader.IsDBNull(6) ? "OK" : reader.GetString(6)
        };
    }

    public static string? GetLatestBackupJson(int linkedSaveId)
    {
        using var connection = OpenConnection();
        using var command = new SqliteCommand("SELECT Save_Json FROM SaveBackups WHERE ID_LinkedSave=@id AND Save_Json IS NOT NULL ORDER BY Created_At DESC, ID_SaveBackup DESC LIMIT 1;", connection);
        command.Parameters.AddWithValue("@id", linkedSaveId);
        return command.ExecuteScalar() as string;
    }

    public static void RecordBackup(int linkedSaveId, string saveGuid, string sourcePath, string json)
    {
        string compact;
        try { compact = ModSaveDocument.ParseAndValidate(json).ToCompactJson(); }
        catch { return; }
        string hash = ComputeHash(compact);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        InsertBackup(connection, transaction, linkedSaveId, saveGuid, sourcePath, compact, hash);
        transaction.Commit();
    }

    private static void InsertBackup(SqliteConnection connection, SqliteTransaction transaction,
        int linkedSaveId, string saveGuid, string sourcePath, string json, string hash)
    {
        using (var check = new SqliteCommand("SELECT 1 FROM SaveBackups WHERE ID_LinkedSave=@id AND Content_Hash=@hash LIMIT 1;", connection, transaction))
        {
            check.Parameters.AddWithValue("@id", linkedSaveId);
            check.Parameters.AddWithValue("@hash", hash);
            if (check.ExecuteScalar() != null) return;
        }

        using (var insert = new SqliteCommand(@"
            INSERT INTO SaveBackups (ID_LinkedSave, Save_Guid, Save_Backup_Path, Save_Json, Content_Hash, Created_At, Source_Path)
            VALUES (@id, @guid, NULL, @json, @hash, @created, @path);", connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", linkedSaveId);
            insert.Parameters.AddWithValue("@guid", saveGuid);
            insert.Parameters.AddWithValue("@json", json);
            insert.Parameters.AddWithValue("@hash", hash);
            insert.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("@path", sourcePath);
            insert.ExecuteNonQuery();
        }

        using var prune = new SqliteCommand(@"
            DELETE FROM SaveBackups WHERE ID_LinkedSave=@id AND ID_SaveBackup NOT IN (
                SELECT ID_SaveBackup FROM SaveBackups WHERE ID_LinkedSave=@id
                ORDER BY Created_At DESC, ID_SaveBackup DESC LIMIT 3);", connection, transaction);
        prune.Parameters.AddWithValue("@id", linkedSaveId);
        prune.ExecuteNonQuery();
    }

    public static string SetLinkedSaveState(int linkedSaveId, string state)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        string previous = "OK";
        using (var read = new SqliteCommand("SELECT SyncState FROM LinkedSaves WHERE ID_LinkedSave=@id;", connection, transaction))
        {
            read.Parameters.AddWithValue("@id", linkedSaveId);
            previous = Convert.ToString(read.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "OK";
        }
        using (var update = new SqliteCommand("UPDATE LinkedSaves SET SyncState=@state WHERE ID_LinkedSave=@id;", connection, transaction))
        {
            update.Parameters.AddWithValue("@state", state);
            update.Parameters.AddWithValue("@id", linkedSaveId);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return previous;
    }

    public static void SetSyncedBaseline(int linkedSaveId, string compactJson, string state = "OK")
    {
        using var connection = OpenConnection();
        using var command = new SqliteCommand(@"
            UPDATE LinkedSaves SET LastSyncedJson=@json, LastSyncedHash=@hash, LastSyncUtc=@time, SyncState=@state
            WHERE ID_LinkedSave=@id;", connection);
        command.Parameters.AddWithValue("@json", compactJson);
        command.Parameters.AddWithValue("@hash", ComputeHash(compactJson));
        command.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@id", linkedSaveId);
        command.ExecuteNonQuery();
    }

    public static void WriteLog(string message, int logTypeId)
    {
        using var connection = OpenConnection();
        using var command = new SqliteCommand(@"INSERT INTO Logs (ID_Log_Type, Log)
            VALUES (CASE WHEN EXISTS (SELECT 1 FROM LogType WHERE ID_Log_Type=@type) THEN @type ELSE NULL END, @message);", connection);
        command.Parameters.AddWithValue("@type", logTypeId);
        command.Parameters.AddWithValue("@message", message ?? string.Empty);
        command.ExecuteNonQuery();
    }

    public static string ComputeHash(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes);
    }
}

public sealed class LinkedSaveInfo
{
    public int IdLinkedSave { get; set; }
    public int IdCompletion { get; set; }
    public string SaveGuid { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public string LastSyncedJson { get; set; } = string.Empty;
    public string LastSyncedHash { get; set; } = string.Empty;
    public string SyncState { get; set; } = "OK";
    public string LevelName { get; set; } = string.Empty;
}

public sealed class LinkSaveResult
{
    public bool Success { get; set; }
    public bool AlreadyLinked { get; set; }
    public int IdLinkedSave { get; set; }
    public int IdCompletion { get; set; }
    public string SaveGuid { get; set; } = string.Empty;
    public string ExistingLevelName { get; set; } = string.Empty;
}
