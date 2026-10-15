using System.Collections.Generic;

namespace BlitzkriegWPF;

/// <summary>Таблица уровня в интерфейсе. Этапы и их раны идут подряд в исходном порядке.</summary>
public class Blitzkrieg
{
    public int IdCompletion { get; set; }
    public string? LevelName { get; set; }
    public bool IsLinkedSave { get; set; }
    public string? SaveGuid { get; set; }
    public string? SavePath { get; set; }
    public string SyncState { get; set; } = "OK";
    public List<BlitzkriegRun> Runs { get; } = new();
}

/// <summary>
/// Строка общей таблицы. Для этапа данные берутся из Stages,
/// а для обычного рана — из Runs.
/// </summary>
public class BlitzkriegRun
{
    public int IdRun { get; set; }
    public int IdStage { get; set; }
    public bool IsStage { get; set; }
    public int StageNumber { get; set; }
    public string? StageGuid { get; set; }
    public string? RangeGuid { get; set; }
    public bool IsLinked { get; set; }

    public string Run { get; set; } = string.Empty;
    public bool IsChecked { get; set; }
    public int Attempts { get; set; }
    public string Note { get; set; } = string.Empty;

    public double? FromValue { get; set; }
    public double? ToValue { get; set; }
    public int SortOrder { get; set; }

    public int CompletionCounter { get; set; }
    public double? FirstRunFrom { get; set; }
    public double? FirstRunTo { get; set; }
    public double? BestRunFrom { get; set; }
    public double? BestRunTo { get; set; }
    public bool Consider { get; set; } = true;
    public bool AutomaticallyClosed { get; set; }
    public double TimePlayed { get; set; }
    public long? CompletedAt { get; set; }
    public int AttemptsToComplete { get; set; }
}
