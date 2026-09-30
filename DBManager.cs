using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace BlitzkriegWPF
{
    public static class DBManager
    {
        private static readonly string DbFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blitzkrieg");

        private static readonly string DbPath = Path.Combine(DbFolder, "BlitzkriegDB.db");
        private static readonly string SeedDbPath = Path.Combine(AppContext.BaseDirectory, "BlitzkriegDB.db");
        private static readonly string ConnectionString = $"Data Source={DbPath};";

        static DBManager()
        {
            SQLitePCL.Batteries.Init();
            Directory.CreateDirectory(DbFolder);
            EnsureUserDatabase();
            InitializeDatabase();
        }

        private static void EnsureUserDatabase()
        {
            if (File.Exists(DbPath))
                return;

            if (File.Exists(SeedDbPath))
            {
                File.Copy(SeedDbPath, DbPath);
                return;
            }
        }

        private static SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            new SqliteCommand("PRAGMA foreign_keys = ON;", connection).ExecuteNonQuery();
            return connection;
        }

        private static void InitializeDatabase()
        {
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS Completions (
    ID_Completion INTEGER PRIMARY KEY AUTOINCREMENT,
    ID_Level INTEGER,
    Level_Name TEXT,
    Is_Imported INTEGER DEFAULT 0,
    Display_Order INTEGER DEFAULT 0,
    Last_Modified TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS Runs (
    ID_Run INTEGER PRIMARY KEY AUTOINCREMENT,
    ID_Completion INTEGER NOT NULL,
    Run TEXT,
    Status INTEGER DEFAULT 0,
    Attempts INTEGER DEFAULT 0,
    Note TEXT,
    FOREIGN KEY (ID_Completion)
        REFERENCES Completions (ID_Completion)
);

CREATE INDEX IF NOT EXISTS IX_Runs_Completion
    ON Runs (ID_Completion);";
                command.ExecuteNonQuery();
            }

            AddColumnIfMissing("Completions", "Display_Order", "INTEGER DEFAULT 0");
            AddColumnIfMissing("Completions", "Last_Modified", "TEXT");

            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE Completions
SET Last_Modified = strftime('%Y-%m-%d %H:%M:%f', 'now')
WHERE Last_Modified IS NULL;";
                command.ExecuteNonQuery();
            }
        }

        private static void AddColumnIfMissing(string table, string column, string definition)
        {
            using (var connection = OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";

                try
                {
                    command.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
                {
                    // Колонка уже существует.
                }
            }
        }

        public static List<Blitzkrieg> GetBlitzkriegs()
        {
            var result = new List<Blitzkrieg>();
            var byId = new Dictionary<int, Blitzkrieg>();

            const string sql = @"
SELECT
    c.ID_Completion,
    c.Level_Name,
    r.ID_Run,
    r.Run,
    r.Status,
    r.Attempts,
    r.Note
FROM Completions c
LEFT JOIN Runs r ON r.ID_Completion = c.ID_Completion
ORDER BY c.Last_Modified DESC, c.Display_Order, c.ID_Completion DESC, r.ID_Run;";

            using (var connection = OpenConnection())
            using (var command = new SqliteCommand(sql, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);

                    if (!byId.TryGetValue(id, out var blitzkrieg))
                    {
                        blitzkrieg = new Blitzkrieg
                        {
                            IdCompletion = id,
                            LevelName = reader.IsDBNull(1) ? null : reader.GetString(1)
                        };

                        byId.Add(id, blitzkrieg);
                        result.Add(blitzkrieg);
                    }

                    if (!reader.IsDBNull(2))
                    {
                        blitzkrieg.Runs.Add(new BlitzkriegRun
                        {
                            IdRun = reader.GetInt32(2),
                            Run = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            IsChecked = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
                            Attempts = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                            Note = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
                        });
                    }
                }
            }

            return result;
        }

        public static int AddBlitzkrieg(Blitzkrieg blitzkrieg, string levelName = null, int? idLevel = null, bool isImported = false)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = new SqliteCommand(@"
INSERT INTO Completions
    (ID_Level, Level_Name, Is_Imported, Display_Order, Last_Modified)
VALUES
    (@level, @name, @imported,
     COALESCE((SELECT MAX(Display_Order) + 1 FROM Completions), 0),
     strftime('%Y-%m-%d %H:%M:%f', 'now'));
SELECT last_insert_rowid();", connection, transaction))
                {
                    command.Parameters.AddWithValue("@level", (object)idLevel ?? DBNull.Value);
                    command.Parameters.AddWithValue("@name", (object)levelName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@imported", isImported ? 1 : 0);

                    int completionId = Convert.ToInt32((long)command.ExecuteScalar());

                    InsertRuns(connection, transaction, completionId, blitzkrieg.Runs);
                    transaction.Commit();
                    return completionId;
                }
            }
        }

        private static void InsertRuns(SqliteConnection connection, SqliteTransaction transaction, int completionId, IEnumerable<BlitzkriegRun> runs)
        {
            const string sql = @"
INSERT INTO Runs (ID_Completion, Run, Status, Attempts, Note)
VALUES (@completion, @run, @status, @attempts, @note);";

            foreach (var run in runs)
            {
                using (var command = new SqliteCommand(sql, connection, transaction))
                {
                    command.Parameters.AddWithValue("@completion", completionId);
                    command.Parameters.AddWithValue("@run", run.Run ?? string.Empty);
                    command.Parameters.AddWithValue("@status", run.IsChecked ? 1 : 0);
                    command.Parameters.AddWithValue("@attempts", run.Attempts);
                    command.Parameters.AddWithValue("@note", (object)run.Note ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
            }
        }

        public static void UpdateRun(int completionId, RunItem run)
        {
            const string sql = @"
UPDATE Runs
SET Run = @run,
    Status = @status,
    Attempts = @attempts,
    Note = @note
WHERE ID_Run = @id
  AND ID_Completion = @completion;

UPDATE Completions
SET Last_Modified = strftime('%Y-%m-%d %H:%M:%f', 'now')
WHERE ID_Completion = @completion;";

            using (var connection = OpenConnection())
            using (var command = new SqliteCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@run", run.Run ?? string.Empty);
                command.Parameters.AddWithValue("@status", run.IsChecked ? 1 : 0);
                command.Parameters.AddWithValue("@attempts", ParseAttempts(run));
                command.Parameters.AddWithValue("@note", (object)run.Note ?? DBNull.Value);
                command.Parameters.AddWithValue("@id", run.IdRun);
                command.Parameters.AddWithValue("@completion", completionId);
                command.ExecuteNonQuery();
            }
        }

        public static void UpdateLevelName(int completionId, string name)
        {
            const string sql = @"
UPDATE Completions
SET Level_Name = @name,
    Last_Modified = strftime('%Y-%m-%d %H:%M:%f', 'now')
WHERE ID_Completion = @id;";

            using (var connection = OpenConnection())
            using (var command = new SqliteCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@name", (object)name ?? DBNull.Value);
                command.Parameters.AddWithValue("@id", completionId);
                command.ExecuteNonQuery();
            }
        }

        public static void UpdateDisplayOrders(IReadOnlyList<int> completionIds)
        {
            const string sql = @"
UPDATE Completions
SET Display_Order = @order
WHERE ID_Completion = @id;";

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = new SqliteCommand(sql, connection, transaction))
            {
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
        }

        public static void DeleteBlitzkrieg(int completionId)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                DeleteRuns(connection, transaction, completionId);

                using (var command = new SqliteCommand(
                    "DELETE FROM Completions WHERE ID_Completion = @id;",
                    connection, transaction))
                {
                    command.Parameters.AddWithValue("@id", completionId);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        private static void DeleteRuns(SqliteConnection connection, SqliteTransaction transaction, int completionId)
        {
            using (var command = new SqliteCommand(
                "DELETE FROM Runs WHERE ID_Completion = @id;",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@id", completionId);
                command.ExecuteNonQuery();
            }
        }

        private static int ParseAttempts(RunItem run)
        {
            return int.TryParse(run.Attempts, out int attempts) ? attempts : 0;
        }
    }
}
