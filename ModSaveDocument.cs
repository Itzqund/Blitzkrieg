using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlitzkriegWPF;

/// <summary>
/// Представление сохранения мода. Исходный JsonObject остаётся нетронутым, поэтому поля,
/// которые приложение не знает, сохраняются при каждом чтении и записи.
/// </summary>
public sealed class ModSaveDocument
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false
    };

    private ModSaveDocument(JsonObject root, string saveGuid, string profileName, List<ModStageDocument> stages)
    {
        Root = root;
        SaveGuid = saveGuid;
        ProfileName = profileName;
        Stages = stages;
    }

    public JsonObject Root { get; }
    public string SaveGuid { get; }
    public string ProfileName { get; }
    public List<ModStageDocument> Stages { get; }

    public string ToCompactJson() => Root.ToJsonString(CompactOptions);

    public static ModSaveDocument ParseAndValidate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Файл сохранения пуст.");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("JSON сохранения повреждён или имеет неверный формат.", ex);
        }

        if (node is not JsonObject root)
            throw new InvalidOperationException("Корень JSON должен быть объектом.");

        string rootId = GetRequiredString(root, "id", "корневой id");
        if (!Guid.TryParse(rootId, out Guid saveGuid))
            throw new InvalidOperationException("Корневое поле id не является корректным GUID.");

        string profileName = GetRequiredString(root, "profileName", "profileName");

        if (root["data"] is not JsonObject data || data["stages"] is not JsonArray stageArray)
            throw new InvalidOperationException("В сохранении отсутствует объект data или массив data.stages.");

        var stages = new List<ModStageDocument>();
        var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int stageIndex = 0; stageIndex < stageArray.Count; stageIndex++)
        {
            if (stageArray[stageIndex] is not JsonObject stageNode)
                throw new InvalidOperationException($"Этап {stageIndex + 1} имеет неверный формат.");

            string stageIdText = GetRequiredString(stageNode, "id", $"stages[{stageIndex}].id");
            if (!Guid.TryParse(stageIdText, out Guid stageGuid))
                throw new InvalidOperationException($"У этапа {stageIndex + 1} некорректный id.");
            string stageId = stageGuid.ToString("D");
            if (!allIds.Add(stageId))
                throw new InvalidOperationException($"GUID {stageId} встречается в сохранении повторно.");

            int stageNumber = GetRequiredInt(stageNode, "stage", $"stages[{stageIndex}].stage");
            if (stageNumber < 1)
                throw new InvalidOperationException($"У этапа {stageIndex + 1} номер должен быть не меньше 1.");
            bool stageChecked = GetRequiredBool(stageNode, "checked", $"stages[{stageIndex}].checked");
            string stageNote = GetOptionalString(stageNode, "note") ?? string.Empty;
            int completionCounter = GetOptionalInt(stageNode, "completionCounter", 0);

            if (stageNode["ranges"] is not JsonArray rangeArray)
                throw new InvalidOperationException($"У этапа {stageNumber} отсутствует массив ranges.");

            var ranges = new List<ModRangeDocument>();
            for (int rangeIndex = 0; rangeIndex < rangeArray.Count; rangeIndex++)
            {
                if (rangeArray[rangeIndex] is not JsonObject rangeNode)
                    throw new InvalidOperationException($"Диапазон {rangeIndex + 1} этапа {stageNumber} имеет неверный формат.");

                string rangeIdText = GetRequiredString(rangeNode, "id", $"stages[{stageIndex}].ranges[{rangeIndex}].id");
                if (!Guid.TryParse(rangeIdText, out Guid rangeGuid))
                    throw new InvalidOperationException($"У диапазона {rangeIndex + 1} этапа {stageNumber} некорректный id.");
                string rangeId = rangeGuid.ToString("D");
                if (!allIds.Add(rangeId))
                    throw new InvalidOperationException($"GUID {rangeId} встречается в сохранении повторно.");

                double from = GetRequiredDouble(rangeNode, "from", $"range {rangeId}.from");
                double to = GetRequiredDouble(rangeNode, "to", $"range {rangeId}.to");
                if (from < 0 || from > 100 || to < 0 || to > 100 || from > to)
                    throw new InvalidOperationException($"У диапазона {rangeId} некорректные границы from/to.");

                bool checkedValue = GetRequiredBool(rangeNode, "checked", $"range {rangeId}.checked");
                int attempts = GetRequiredInt(rangeNode, "attempts", $"range {rangeId}.attempts");
                if (attempts < 0)
                    throw new InvalidOperationException($"У диапазона {rangeId} количество попыток меньше нуля.");

                ranges.Add(new ModRangeDocument(
                    rangeNode,
                    rangeId,
                    from,
                    to,
                    checkedValue,
                    GetOptionalString(rangeNode, "note") ?? string.Empty,
                    attempts,
                    GetOptionalDouble(rangeNode, "firstRunFrom"),
                    GetOptionalDouble(rangeNode, "firstRunTo"),
                    GetOptionalDouble(rangeNode, "bestRunFrom"),
                    GetOptionalDouble(rangeNode, "bestRunTo"),
                    GetOptionalBool(rangeNode, "consider", true),
                    GetOptionalBool(rangeNode, "automaticallyClosed", false),
                    GetOptionalDouble(rangeNode, "timePlayed") ?? 0d,
                    GetOptionalLong(rangeNode, "completedAt"),
                    GetOptionalInt(rangeNode, "attemptsToComplete", 0),
                    GetOptionalInt(rangeNode, "completionCounter", 0)));
            }

            stages.Add(new ModStageDocument(
                stageNode,
                stageId,
                stageNumber,
                stageChecked,
                stageNote,
                completionCounter,
                ranges));
        }

        if (stages.Count == 0)
            throw new InvalidOperationException("В сохранении нет этапов.");

        return new ModSaveDocument(root, saveGuid.ToString("D"), profileName, stages);
    }

    /// <summary>Обновляет только используемые приложением поля, остальные данные мода не трогает.</summary>
    public void ApplyTable(Blitzkrieg table)
    {
        Root["profileName"] = table.LevelName ?? string.Empty;

        var rowsByStageGuid = table.Runs
            .Where(r => r.IsStage && !string.IsNullOrWhiteSpace(r.StageGuid))
            .ToDictionary(r => r.StageGuid!, StringComparer.OrdinalIgnoreCase);
        var rowsByRangeGuid = table.Runs
            .Where(r => !r.IsStage && !string.IsNullOrWhiteSpace(r.RangeGuid))
            .ToDictionary(r => r.RangeGuid!, StringComparer.OrdinalIgnoreCase);

        foreach (var stage in Stages)
        {
            if (!rowsByStageGuid.TryGetValue(stage.Id, out var stageRow))
                continue;

            // Заметки принадлежат приложению; отметки выполнения здесь тоже можно менять.
            // Номер этапа и счётчики принадлежат моду, поэтому приложение их не перезаписывает.
            stage.Node["checked"] = stageRow.IsChecked;
            stage.Node["note"] = stageRow.Note ?? string.Empty;

            foreach (var range in stage.Ranges)
            {
                if (!rowsByRangeGuid.TryGetValue(range.Id, out var rangeRow))
                    continue;

                if (rangeRow.FromValue.HasValue)
                    range.Node["from"] = rangeRow.FromValue.Value;
                if (rangeRow.ToValue.HasValue)
                    range.Node["to"] = rangeRow.ToValue.Value;

                range.Node["checked"] = rangeRow.IsChecked;
                range.Node["note"] = rangeRow.Note ?? string.Empty;
                range.Node["attempts"] = Convert.ToInt32(rangeRow.Attempts);

                // Статистику мода — первые и лучшие раны, время, даты, счётчики
                // и служебные флаги — оставляем в JSON без изменений.
            }
        }
    }

    public static bool JsonFieldEquals(JsonObject? first, JsonObject? second, string field)
    {
        JsonNode? firstValue = first is not null && first.TryGetPropertyValue(field, out var a) ? a : null;
        JsonNode? secondValue = second is not null && second.TryGetPropertyValue(field, out var b) ? b : null;
        return JsonNode.DeepEquals(firstValue, secondValue);
    }

    public static JsonObject? FindStageNode(JsonNode? root, string id)
    {
        if (root is not JsonObject rootObject || rootObject["data"] is not JsonObject data || data["stages"] is not JsonArray stages)
            return null;

        foreach (var item in stages)
            if (item is JsonObject stage && string.Equals(GetOptionalString(stage, "id"), id, StringComparison.OrdinalIgnoreCase))
                return stage;
        return null;
    }

    public static JsonObject? FindRangeNode(JsonNode? root, string stageId, string rangeId)
    {
        var stage = FindStageNode(root, stageId);
        if (stage?["ranges"] is not JsonArray ranges)
            return null;

        foreach (var item in ranges)
            if (item is JsonObject range && string.Equals(GetOptionalString(range, "id"), rangeId, StringComparison.OrdinalIgnoreCase))
                return range;
        return null;
    }

    private static int ParseNonNegativeInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result >= 0 ? result : 0;

    private static string GetRequiredString(JsonObject node, string key, string label) =>
        GetOptionalString(node, key) is { } value
            ? value
            : throw new InvalidOperationException($"В сохранении отсутствует строковое поле {label}.");

    private static string? GetOptionalString(JsonObject node, string key)
    {
        if (node[key] is not JsonValue value || !value.TryGetValue<string>(out var result))
            return null;
        return result;
    }

    private static int GetRequiredInt(JsonObject node, string key, string label) =>
        TryReadInt(node[key], out int value)
            ? value
            : throw new InvalidOperationException($"Поле {label} должно быть целым числом.");

    private static int GetOptionalInt(JsonObject node, string key, int fallback) =>
        TryReadInt(node[key], out int value) ? value : fallback;

    private static long? GetOptionalLong(JsonObject node, string key) =>
        TryReadLong(node[key], out long value) ? value : null;

    private static bool GetRequiredBool(JsonObject node, string key, string label) =>
        TryReadBool(node[key], out bool value)
            ? value
            : throw new InvalidOperationException($"Поле {label} должно быть логическим значением.");

    private static bool GetOptionalBool(JsonObject node, string key, bool fallback) =>
        TryReadBool(node[key], out bool value) ? value : fallback;

    private static double GetRequiredDouble(JsonObject node, string key, string label) =>
        TryReadDouble(node[key], out double value) && double.IsFinite(value)
            ? value
            : throw new InvalidOperationException($"Поле {label} должно быть конечным числом.");

    private static double? GetOptionalDouble(JsonObject node, string key) =>
        TryReadDouble(node[key], out double value) && double.IsFinite(value) ? value : null;

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue(out value))
            return true;
        return jsonValue.TryGetValue(out long longValue) && longValue >= int.MinValue && longValue <= int.MaxValue && (value = (int)longValue) == longValue;
    }

    private static bool TryReadLong(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue jsonValue && (jsonValue.TryGetValue(out value) ||
            (jsonValue.TryGetValue(out double number) && double.IsFinite(number) && number >= long.MinValue && number <= long.MaxValue && (value = (long)number) == number));
    }

    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static bool TryReadDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue(out value))
            return true;
        if (jsonValue.TryGetValue(out long longValue))
        {
            value = longValue;
            return true;
        }
        return false;
    }
}

public sealed class ModStageDocument
{
    public ModStageDocument(JsonObject node, string id, int stageNumber, bool isChecked, string note, int completionCounter, List<ModRangeDocument> ranges)
    {
        Node = node;
        Id = id;
        StageNumber = stageNumber;
        IsChecked = isChecked;
        Note = note;
        CompletionCounter = completionCounter;
        Ranges = ranges;
    }

    public JsonObject Node { get; }
    public string Id { get; }
    public int StageNumber { get; }
    public bool IsChecked { get; }
    public string Note { get; }
    public int CompletionCounter { get; }
    public List<ModRangeDocument> Ranges { get; }
}

public sealed class ModRangeDocument
{
    public ModRangeDocument(JsonObject node, string id, double from, double to, bool isChecked, string note,
        int attempts, double? firstRunFrom, double? firstRunTo, double? bestRunFrom, double? bestRunTo,
        bool consider, bool automaticallyClosed, double timePlayed, long? completedAt,
        int attemptsToComplete, int completionCounter)
    {
        Node = node;
        Id = id;
        From = from;
        To = to;
        IsChecked = isChecked;
        Note = note;
        Attempts = attempts;
        FirstRunFrom = firstRunFrom;
        FirstRunTo = firstRunTo;
        BestRunFrom = bestRunFrom;
        BestRunTo = bestRunTo;
        Consider = consider;
        AutomaticallyClosed = automaticallyClosed;
        TimePlayed = timePlayed;
        CompletedAt = completedAt;
        AttemptsToComplete = attemptsToComplete;
        CompletionCounter = completionCounter;
    }

    public JsonObject Node { get; }
    public string Id { get; }
    public double From { get; }
    public double To { get; }
    public bool IsChecked { get; }
    public string Note { get; }
    public int Attempts { get; }
    public double? FirstRunFrom { get; }
    public double? FirstRunTo { get; }
    public double? BestRunFrom { get; }
    public double? BestRunTo { get; }
    public bool Consider { get; }
    public bool AutomaticallyClosed { get; }
    public double TimePlayed { get; }
    public long? CompletedAt { get; }
    public int AttemptsToComplete { get; }
    public int CompletionCounter { get; }
}
