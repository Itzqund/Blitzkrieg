using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace BlitzkriegWPF;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class RunItem : ObservableObject
{
    private string run = string.Empty;
    private bool isChecked;
    private string attempts = string.Empty;
    private string note = string.Empty;
    private bool canToggle = true;
    private bool isEditingLocked;

    public int IdRun { get; set; }
    public int IdStage { get; set; }
    public bool IsStage { get; set; }
    public int StageNumber { get; set; }
    public string? StageGuid { get; set; }
    public string? RangeGuid { get; set; }
    public bool IsLinked { get; set; }
    public int SortOrder { get; set; }

    public double? FromValue { get; set; }
    public double? ToValue { get; set; }
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

    public string Run
    {
        get => run;
        set => Set(ref run, value ?? string.Empty);
    }

    public bool IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value);
    }

    public string Attempts
    {
        get => attempts;
        set => Set(ref attempts, value ?? string.Empty);
    }

    public string Note
    {
        get => note;
        set => Set(ref note, value ?? string.Empty);
    }

    public bool CanToggle
    {
        get => canToggle;
        set => Set(ref canToggle, value);
    }

    public bool IsEditingLocked
    {
        get => isEditingLocked;
        set => Set(ref isEditingLocked, value);
    }
}

public class LevelTableViewModel : ObservableObject
{
    private string levelName = string.Empty;
    private bool isEditingName;
    private bool isLinkedSave;
    private string? saveGuid;
    private string? savePath;
    private string syncState = "OK";

    public int IdCompletion { get; set; }
    public bool IsPlaceholder { get; set; }
    public ObservableCollection<RunItem> Runs { get; } = new();

    public string LevelName
    {
        get => levelName;
        set => Set(ref levelName, value ?? string.Empty);
    }

    public bool IsEditingName
    {
        get => isEditingName;
        set => Set(ref isEditingName, value);
    }

    public bool IsLinkedSave
    {
        get => isLinkedSave;
        set => Set(ref isLinkedSave, value);
    }

    public string? SaveGuid
    {
        get => saveGuid;
        set => Set(ref saveGuid, value);
    }

    public string? SavePath
    {
        get => savePath;
        set => Set(ref savePath, value);
    }

    public string SyncState
    {
        get => syncState;
        set => Set(ref syncState, value);
    }

    public string GetRunDisplay(BlitzkriegRun row)
    {
        if (row.IsStage)
            return $"Stage {row.StageNumber}";
        if (IsLinkedSave && row.FromValue.HasValue && row.ToValue.HasValue)
            return $"{Math.Floor(row.FromValue.Value).ToString(CultureInfo.InvariantCulture)} - {Math.Floor(row.ToValue.Value).ToString(CultureInfo.InvariantCulture)}";
        return row.Run;
    }
}
