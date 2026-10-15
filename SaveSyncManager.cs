using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace BlitzkriegWPF;

public sealed class SaveSyncReport
{
    public bool DataChanged { get; set; }
    public List<string> Messages { get; } = new();
}

/// <summary>
/// Синхронизирует подключённые JSON-сохранения мода с SQLite. Все поля принадлежат Geometry Dash,
/// кроме заметок: ими управляет приложение. Изменения из приложения ждут, пока игра
/// завершится, и только после этого записываются обратно в файл.
/// </summary>
public static class SaveSyncManager
{
    private const string StateOk = "OK";
    private const string StateMissing = "Missing";
    private const string StateCorrupt = "Corrupt";
    private const string StateDifferentSave = "DifferentSave";

    public static LinkSaveResult LinkSave(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Не указан путь к сохранению.", nameof(path));

        var read = ReadValidDocument(path);
        return DBManager.CreateLinkedSave(read.Document, path);
    }

    public static SaveSyncReport SynchronizeAll()
    {
        var report = new SaveSyncReport();
        foreach (var linked in DBManager.GetLinkedSaves())
        {
            try
            {
                if (!File.Exists(linked.SavePath))
                {
                    // Некоторые версии игры или мода заменяют сохранение через
                    // временный файл и переименование. Не считаем сохранение пропавшим, пока игра
                    // может записывать его; проверим ещё раз после закрытия игры.
                    if (IsGeometryDashRunning())
                        continue;

                    HandleUnavailable(linked, StateMissing, "файл сохранения не найден", report);
                    continue;
                }

                ReadResult read;
                try
                {
                    read = ReadValidDocument(linked.SavePath);
                }
                catch (Exception ex)
                {
                    // Во время сохранения файл может ненадолго оказаться неполным или заблокированным.
                    // Не объявляем его повреждённым, пока Geometry Dash не завершится.
                    if (IsGeometryDashRunning())
                        continue;

                    HandleUnavailable(linked, StateCorrupt, "файл сохранения не удалось прочитать или проверить", report);
                    LogManager.LogAction($"Save sync: invalid file '{linked.SavePath}': {ex.Message}", 1);
                    continue;
                }

                if (!string.Equals(read.Document.SaveGuid, linked.SaveGuid, StringComparison.OrdinalIgnoreCase))
                {
                    HandleUnavailable(linked, StateDifferentSave,
                        $"по сохранённому пути найден файл с другим ID ({read.Document.SaveGuid})", report);
                    continue;
                }

                string currentCompact = read.Document.ToCompactJson();
                string currentHash = DBManager.ComputeHash(currentCompact);
                bool wasUnavailable = !string.Equals(linked.SyncState, StateOk, StringComparison.OrdinalIgnoreCase);

                if (!string.Equals(currentHash, linked.LastSyncedHash, StringComparison.OrdinalIgnoreCase))
                {
                    // В историю резервных копий попадает только проверенный JSON.
                    DBManager.RecordBackup(linked.IdLinkedSave, linked.SaveGuid, linked.SavePath, currentCompact);
                    bool changed = DBManager.ApplyModSnapshot(linked.IdLinkedSave, read.Document,
                        forceOverwrite: false, updateBaseline: true);
                    report.DataChanged |= changed;
                }
                else
                {
                    string previous = DBManager.SetLinkedSaveState(linked.IdLinkedSave, StateOk);
                    wasUnavailable |= !string.Equals(previous, StateOk, StringComparison.OrdinalIgnoreCase);
                }

                if (wasUnavailable)
                {
                    report.Messages.Add($"Сохранение «{linked.LevelName}» снова доступно. Синхронизация возобновлена.");
                    report.DataChanged = true;
                }
            }
            catch (Exception ex)
            {
                report.Messages.Add($"Не удалось синхронизировать «{linked.LevelName}»: {ex.Message}");
                LogManager.LogAction($"Save sync error for '{linked.SavePath}': {ex}", 1);
            }
        }

        return report;
    }

    /// <summary>Записывает отложенные изменения приложения, только когда Geometry Dash закрыт.</summary>
    public static SaveSyncReport FlushPendingWrites()
    {
        var report = new SaveSyncReport();
        if (IsGeometryDashRunning())
            return report;

        foreach (var linked in DBManager.GetLinkedSaves())
        {
            if (!string.Equals(linked.SyncState, StateOk, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (!File.Exists(linked.SavePath))
                    continue;

                ReadResult initialRead;
                try
                {
                    initialRead = ReadValidDocument(linked.SavePath);
                }
                catch
                {
                    continue; // О проблеме сообщит обычная проверка сохранений.
                }

                if (!string.Equals(initialRead.Document.SaveGuid, linked.SaveGuid, StringComparison.OrdinalIgnoreCase))
                    continue;

                string currentCompact = initialRead.Document.ToCompactJson();
                string currentHash = DBManager.ComputeHash(currentCompact);
                if (!string.Equals(currentHash, linked.LastSyncedHash, StringComparison.OrdinalIgnoreCase))
                {
                    DBManager.RecordBackup(linked.IdLinkedSave, linked.SaveGuid, linked.SavePath, currentCompact);
                    DBManager.ApplyModSnapshot(linked.IdLinkedSave, initialRead.Document,
                        forceOverwrite: false, updateBaseline: true);
                }

                var table = DBManager.GetBlitzkriegById(linked.IdCompletion);
                if (table == null)
                    continue;

                // Обновляем только известные приложению поля; остальные данные JSON сохраняются.
                initialRead.Document.ApplyTable(table);
                string output = initialRead.Document.ToCompactJson();
                if (string.Equals(output, currentCompact, StringComparison.Ordinal))
                {
                    DBManager.SetSyncedBaseline(linked.IdLinkedSave, currentCompact);
                    continue;
                }

                // Перед заменой ещё раз проверяем, не запустилась ли игра
                // пока мы обрабатывали другое сохранение.
                if (IsGeometryDashRunning())
                    continue;

                // Перед безопасной заменой сохраняем предыдущую исправную версию файла.
                DBManager.RecordBackup(linked.IdLinkedSave, linked.SaveGuid, linked.SavePath, currentCompact);
                AtomicWriteIfUnchanged(linked.SavePath, initialRead.RawText, output);
                DBManager.RecordBackup(linked.IdLinkedSave, linked.SaveGuid, linked.SavePath, output);
                DBManager.SetSyncedBaseline(linked.IdLinkedSave, output);
                report.DataChanged = true;
            }
            catch (FileChangedDuringWriteException)
            {
                // Мод успел изменить файл во время записи. При следующей проверке объединим изменения.
            }
            catch (IOException)
            {
                // Если файл занят или недоступен, повторим попытку при следующей минутной проверке.
            }
            catch (Exception ex)
            {
                report.Messages.Add($"Не удалось записать изменения в «{linked.LevelName}»: {ex.Message}");
                LogManager.LogAction($"Save writeback error for '{linked.SavePath}': {ex}", 1);
            }
        }

        return report;
    }

    public static void ExportLinkedSave(int completionId, string outputPath)
    {
        var linked = DBManager.GetLinkedSaves().FirstOrDefault(s => s.IdCompletion == completionId)
            ?? throw new InvalidOperationException("Эта таблица не связана с сохранением мода.");

        ModSaveDocument document;
        try
        {
            var current = ReadValidDocument(linked.SavePath);
            if (!string.Equals(current.Document.SaveGuid, linked.SaveGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("По привязанному пути найдено другое сохранение.");
            document = current.Document;
        }
        catch
        {
            string? backup = DBManager.GetLatestBackupJson(linked.IdLinkedSave);
            string source = !string.IsNullOrWhiteSpace(backup) ? backup : linked.LastSyncedJson;
            document = ModSaveDocument.ParseAndValidate(source);
        }

        var table = DBManager.GetBlitzkriegById(completionId)
            ?? throw new InvalidOperationException("Таблица больше не существует.");
        document.ApplyTable(table);
        string compact = document.ToCompactJson();
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, compact, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void HandleUnavailable(LinkedSaveInfo linked, string state, string reason, SaveSyncReport report)
    {
        string previous = DBManager.SetLinkedSaveState(linked.IdLinkedSave, state);
        bool changedState = !string.Equals(previous, state, StringComparison.OrdinalIgnoreCase);
        if (!changedState)
            return;

        // Загружаем данные из копии один раз при переходе в состояние недоступного файла.
        // Иначе при каждой проверке будут теряться изменения, сделанные пока файл недоступен.
        string? backupJson = DBManager.GetLatestBackupJson(linked.IdLinkedSave);
        if (!string.IsNullOrWhiteSpace(backupJson))
        {
            try
            {
                var backup = ModSaveDocument.ParseAndValidate(backupJson);
                if (string.Equals(backup.SaveGuid, linked.SaveGuid, StringComparison.OrdinalIgnoreCase))
                    // Сверяем копию с последней синхронизированной версией, а не перезаписываем без разбора
                    // локальные изменения, сделанные после неё.
                    report.DataChanged |= DBManager.ApplyModSnapshot(linked.IdLinkedSave, backup,
                        forceOverwrite: false, updateBaseline: false);
            }
            catch (Exception ex)
            {
                LogManager.LogAction($"Backup is invalid for '{linked.SavePath}': {ex.Message}", 1);
                backupJson = null;
            }
        }

        string recovery = string.IsNullOrWhiteSpace(backupJson)
            ? "Исправная резервная копия не найдена; используются последние данные из базы."
            : "Для работы загружена резервная копия. Оригинальный файл не изменён.";
        report.Messages.Add($"С сохранением «{linked.LevelName}» проблема: {reason}. {recovery}");
    }

    private static ReadResult ReadValidDocument(string path)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                string raw = File.ReadAllText(path, Encoding.UTF8);
                var document = ModSaveDocument.ParseAndValidate(raw);

                // Мод может записывать файл во время проверки, поэтому принимаем только стабильное содержимое.
                string confirmation = File.ReadAllText(path, Encoding.UTF8);
                if (!string.Equals(raw, confirmation, StringComparison.Ordinal))
                {
                    lastError = new IOException("Файл сохранения изменился во время чтения.");
                    if (attempt < 2)
                        Thread.Sleep(250);
                    continue;
                }

                return new ReadResult(raw, document);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                lastError = ex;
                if (attempt < 2)
                    Thread.Sleep(250);
            }
        }
        throw new InvalidOperationException("Не удалось прочитать корректный JSON после нескольких попыток.", lastError);
    }

    private static void AtomicWriteIfUnchanged(string path, string expectedOriginalText, string output)
    {
        string temporaryPath = path + ".blitzkrieg.tmp";
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(output);

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            string latestText = File.ReadAllText(path, Encoding.UTF8);
            if (!string.Equals(latestText, expectedOriginalText, StringComparison.Ordinal))
                throw new FileChangedDuringWriteException();

            // Последняя проверка перед заменой файла сохранения.
            if (IsGeometryDashRunning())
                throw new IOException("Geometry Dash запустился во время подготовки синхронизации.");

            // В Windows File.Replace заменяет файл атомарно, если оба файла находятся на одном диске.
            File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch { /* Если временный файл не удалось удалить, попробуем в следующий раз. */ }
        }
    }

    private static bool IsGeometryDashRunning()
    {
        try
        {
            return Process.GetProcessesByName("GeometryDash").Length > 0 ||
                   Process.GetProcessesByName("GeometryDash64").Length > 0;
        }
        catch
        {
            // Если не удалось проверить, запущена ли игра, безопаснее не трогать сохранение.
            return true;
        }
    }

    private sealed record ReadResult(string RawText, ModSaveDocument Document);

    private sealed class FileChangedDuringWriteException : IOException { }
}
