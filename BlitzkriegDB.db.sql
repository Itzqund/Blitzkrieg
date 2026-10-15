-- Схема базы данных «Блицкриг» v0.2.0-alpha (SQLite)
-- Приложение также создаёт эти таблицы при запуске и переносит данные из старых схем.
BEGIN TRANSACTION;

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
    "From" REAL,
    "To" REAL,
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

INSERT OR IGNORE INTO SchemaInfo (Singleton, SchemaVersion) VALUES (1, 2);

COMMIT;
