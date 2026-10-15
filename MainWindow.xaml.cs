using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace BlitzkriegWPF
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const double MinZoom = 0.6;
        private const double MaxZoom = 1.8;
        private const double ZoomStep = 0.1;

        private readonly ObservableCollection<LevelTableViewModel> levelTables = new ObservableCollection<LevelTableViewModel>();
        private readonly ObservableCollection<LevelTableViewModel> displayedTables = new ObservableCollection<LevelTableViewModel>();
        private readonly Dictionary<int, (double Horizontal, double Vertical)> tableScrollPositions = new();
        private readonly HashSet<DataGrid> pendingColumnUpdates = new();

        private bool isGeneratorVisible = true;
        private bool isLoadingFromDb;
        private double currentZoom = 1.0;
        private AppSettings appSettings;
        private double tableWidth = double.NaN;
        private readonly Dictionary<RunItem, bool> lastCheckedStates = new();
        private readonly DispatcherTimer syncTimer;
        private bool isHandlingCheckChange;
        private bool syncInProgress;
        private bool suppressScrollPositionTracking;
        private double outerTableHorizontalOffset;
        private LevelTableViewModel? activeTable;
        private WindowState lastNonMinimizedWindowState = WindowState.Normal;

        public event PropertyChangedEventHandler PropertyChanged;

        public bool IsGeneratorHidden
        {
            get { return !isGeneratorVisible; }
        }

        public double TableWidth
        {
            get { return tableWidth; }
            private set
            {
                if (tableWidth == value)
                    return;

                tableWidth = value;
                OnPropertyChanged(nameof(TableWidth));
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            StateChanged += Window_StateChanged;
            LoadSettings();

            TablesItemsControl.ItemsSource = displayedTables;

            TableScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            SetWindowChrome();
            SetGeneratorVisible(isGeneratorVisible);

            var startupSync = SaveSyncManager.SynchronizeAll();
            var startupWriteback = SaveSyncManager.FlushPendingWrites();
            startupSync.DataChanged |= startupWriteback.DataChanged;
            startupSync.Messages.AddRange(startupWriteback.Messages);

            Reload();
            ShowSyncMessages(startupSync.Messages);

            syncTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            syncTimer.Tick += SyncTimer_Tick;
            syncTimer.Start();
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SetWindowChrome()
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 32,
                ResizeBorderThickness = new Thickness(10),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0)
            });
        }

        private void Reload(bool preserveCurrentOrder = false)
        {
            CaptureScrollPositions(captureOuterOffset: !isGeneratorVisible);

            var oldOrder = preserveCurrentOrder
                ? levelTables.Where(t => !t.IsPlaceholder).Select((t, index) => new { t.IdCompletion, index })
                    .ToDictionary(x => x.IdCompletion, x => x.index)
                : new Dictionary<int, int>();
            var loadedTables = DBManager.GetBlitzkriegs();
            if (preserveCurrentOrder)
            {
                loadedTables = loadedTables
                    .OrderBy(t => oldOrder.ContainsKey(t.IdCompletion) ? 0 : 1)
                    .ThenBy(t => oldOrder.TryGetValue(t.IdCompletion, out int index) ? index : int.MaxValue)
                    .ToList();
            }

            isLoadingFromDb = true;
            lastCheckedStates.Clear();
            try
            {
                levelTables.Clear();
                int levelIndex = 1;
                foreach (var blitzkrieg in loadedTables)
                {
                    var table = new LevelTableViewModel
                    {
                        IdCompletion = blitzkrieg.IdCompletion,
                        LevelName = string.IsNullOrWhiteSpace(blitzkrieg.LevelName)
                            ? $"Level {levelIndex}"
                            : blitzkrieg.LevelName,
                        IsLinkedSave = blitzkrieg.IsLinkedSave,
                        SaveGuid = blitzkrieg.SaveGuid,
                        SavePath = blitzkrieg.SavePath,
                        SyncState = blitzkrieg.SyncState
                    };

                    foreach (var row in blitzkrieg.Runs)
                    {
                        var item = new RunItem
                        {
                            IdRun = row.IdRun,
                            IdStage = row.IdStage,
                            IsStage = row.IsStage,
                            StageNumber = row.StageNumber,
                            StageGuid = row.StageGuid,
                            RangeGuid = row.RangeGuid,
                            IsLinked = row.IsLinked,
                            SortOrder = row.SortOrder,
                            Run = row.Run,
                            IsChecked = row.IsChecked,
                            Attempts = !row.IsStage && row.Attempts != 0 ? row.Attempts.ToString() : string.Empty,
                            Note = row.Note ?? string.Empty,
                            FromValue = row.FromValue,
                            ToValue = row.ToValue,
                            CompletionCounter = row.CompletionCounter,
                            FirstRunFrom = row.FirstRunFrom,
                            FirstRunTo = row.FirstRunTo,
                            BestRunFrom = row.BestRunFrom,
                            BestRunTo = row.BestRunTo,
                            Consider = row.Consider,
                            AutomaticallyClosed = row.AutomaticallyClosed,
                            TimePlayed = row.TimePlayed,
                            CompletedAt = row.CompletedAt,
                            AttemptsToComplete = row.AttemptsToComplete
                        };

                        lastCheckedStates[item] = item.IsChecked;
                        item.PropertyChanged += (_, args) => RunItem_PropertyChanged(table, item, args);
                        table.Runs.Add(item);
                    }

                    UpdateStageAttemptTotals(table);
                    levelTables.Add(table);
                    levelIndex++;
                }
            }
            finally
            {
                isLoadingFromDb = false;
            }

            foreach (var table in levelTables)
                RecomputeStageControls(table);

            EnsurePlaceholders();
            UpdateSavesButtonState();
            ScheduleScrollRestore();
        }

        private void RunItem_PropertyChanged(LevelTableViewModel table, RunItem run, PropertyChangedEventArgs args)
        {
            if (isLoadingFromDb || table.IdCompletion <= 0)
                return;

            if (args.PropertyName == nameof(RunItem.IsChecked))
            {
                if (!isHandlingCheckChange)
                    HandleCheckChange(table, run);
                return;
            }

            // Сохраняем только те значения, которые пользователь действительно редактирует. CanToggle и другие
            // свойства для отображения меняются при перестроении интерфейса и не должны попадать в SQLite.
            if (args.PropertyName == nameof(RunItem.Attempts) && run.IsStage)
            {
                // Число попыток этапа — сумма попыток его ранов, отдельно в базу оно не записывается.
                RequestColumnUpdateForTable(table);
                return;
            }

            if (args.PropertyName == nameof(RunItem.Run) ||
                args.PropertyName == nameof(RunItem.Attempts) ||
                args.PropertyName == nameof(RunItem.Note))
            {
                if (args.PropertyName == nameof(RunItem.Attempts))
                    UpdateStageAttemptTotals(table);

                SaveRun(table, run, updateRangeText: args.PropertyName == nameof(RunItem.Run));
                RequestColumnUpdateForTable(table);
            }
        }

        private void SaveRun(LevelTableViewModel table, RunItem run, bool updateRangeText = false)
        {
            if (isLoadingFromDb || table.IdCompletion <= 0 || (run.IsStage ? run.IdStage <= 0 : run.IdRun <= 0))
                return;

            DBManager.UpdateRun(table.IdCompletion, run, updateRangeText);
            RecomputeStageControls(table);
        }

        private void HandleCheckChange(LevelTableViewModel table, RunItem item)
        {
            bool previous = lastCheckedStates.TryGetValue(item, out bool oldValue) ? oldValue : !item.IsChecked;
            bool requested = item.IsChecked;
            int stageIndex = GetStageIndex(table, item.IdStage);
            var stages = GetStageRows(table);

            if (stageIndex < 0)
            {
                RestoreCheckValue(item, previous);
                return;
            }

            if (requested)
            {
                if (item.IsStage)
                {
                    if (!AreStageRunsChecked(table, stages[stageIndex].IdStage))
                    {
                        RestoreCheckValue(item, previous);
                        return;
                    }
                }

                if (stageIndex > 0 && !stages[stageIndex - 1].IsChecked)
                {
                    if (CanAutoCompleteStage(table, stageIndex - 1))
                        SetCheckSilently(table, stages[stageIndex - 1], true);
                    else
                    {
                        RestoreCheckValue(item, previous);
                        return;
                    }
                }

                if (!AreAllPreviousStagesChecked(stages, stageIndex) ||
                    (item.IsStage && !AreStageRunsChecked(table, item.IdStage)))
                {
                    RestoreCheckValue(item, previous);
                    return;
                }
            }
            else if (item.IsStage)
            {
                if (HasProgressAfter(table, stageIndex))
                {
                    RestoreCheckValue(item, previous);
                    return;
                }
            }
            else
            {
                // Если рана снова сняли отметку, отметки всех следующих этапов тоже нужно сбросить.
                for (int i = stageIndex; i < stages.Count; i++)
                {
                    if (stages[i].IsChecked)
                        SetCheckSilently(table, stages[i], false);
                }
            }

            lastCheckedStates[item] = item.IsChecked;
            DBManager.UpdateRun(table.IdCompletion, item);
            RecomputeStageControls(table);
        }

        private void RestoreCheckValue(RunItem item, bool value)
        {
            isHandlingCheckChange = true;
            try
            {
                item.IsChecked = value;
                lastCheckedStates[item] = value;
            }
            finally
            {
                isHandlingCheckChange = false;
            }
        }

        private void SetCheckSilently(LevelTableViewModel table, RunItem item, bool value)
        {
            if (item.IsChecked == value)
                return;

            isHandlingCheckChange = true;
            try
            {
                item.IsChecked = value;
                lastCheckedStates[item] = value;
            }
            finally
            {
                isHandlingCheckChange = false;
            }
            DBManager.UpdateRun(table.IdCompletion, item);
        }

        private void RecomputeStageControls(LevelTableViewModel table)
        {
            var stages = GetStageRows(table);
            for (int stageIndex = 0; stageIndex < stages.Count; stageIndex++)
            {
                var stage = stages[stageIndex];
                bool allRunsChecked = AreStageRunsChecked(table, stage.IdStage);
                bool previousStagesChecked = AreAllPreviousStagesChecked(stages, stageIndex);
                bool previousCanAdvance = stageIndex > 0 && CanAutoCompleteStage(table, stageIndex - 1);

                stage.CanToggle = stage.IsChecked
                    ? !HasProgressAfter(table, stageIndex)
                    : allRunsChecked && (previousStagesChecked || previousCanAdvance);

                foreach (var run in table.Runs.Where(r => !r.IsStage && r.IdStage == stage.IdStage))
                {
                    if (run.IsChecked || stageIndex == 0)
                        run.CanToggle = true;
                    else
                        run.CanToggle = stages[stageIndex - 1].IsChecked || CanAutoCompleteStage(table, stageIndex - 1);
                }
            }
        }

        private static List<RunItem> GetStageRows(LevelTableViewModel table) =>
            table.Runs.Where(r => r.IsStage).ToList();

        private static int GetStageIndex(LevelTableViewModel table, int stageId)
        {
            var stages = GetStageRows(table);
            return stages.FindIndex(s => s.IdStage == stageId);
        }

        private static bool AreStageRunsChecked(LevelTableViewModel table, int stageId) =>
            table.Runs.Where(r => !r.IsStage && r.IdStage == stageId).All(r => r.IsChecked);

        private static bool AreAllPreviousStagesChecked(IReadOnlyList<RunItem> stages, int stageIndex)
        {
            for (int i = 0; i < stageIndex; i++)
                if (!stages[i].IsChecked)
                    return false;
            return true;
        }

        private bool CanAutoCompleteStage(LevelTableViewModel table, int stageIndex)
        {
            var stages = GetStageRows(table);
            if (stageIndex < 0 || stageIndex >= stages.Count)
                return false;
            return AreAllPreviousStagesChecked(stages, stageIndex) && AreStageRunsChecked(table, stages[stageIndex].IdStage);
        }

        private static bool HasProgressAfter(LevelTableViewModel table, int stageIndex)
        {
            var stages = GetStageRows(table);
            for (int i = stageIndex + 1; i < stages.Count; i++)
            {
                if (stages[i].IsChecked || table.Runs.Any(r => !r.IsStage && r.IdStage == stages[i].IdStage && r.IsChecked))
                    return true;
            }
            return false;
        }

        private async void SyncTimer_Tick(object? sender, EventArgs e)
        {
            if (syncInProgress)
                return;

            syncInProgress = true;
            try
            {
                var report = await Task.Run(SaveSyncManager.SynchronizeAll);
                var writeback = await Task.Run(SaveSyncManager.FlushPendingWrites);
                report.DataChanged |= writeback.DataChanged;
                report.Messages.AddRange(writeback.Messages);

                if (report.DataChanged)
                    Reload(preserveCurrentOrder: true);
                ShowSyncMessages(report.Messages);
            }
            catch (Exception ex)
            {
                LogManager.LogAction("Periodic save sync failed: " + ex, 1);
            }
            finally
            {
                syncInProgress = false;
            }
        }

        private static void ShowSyncMessages(IEnumerable<string> messages)
        {
            string[] items = messages.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();
            if (items.Length == 0)
                return;

            MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, items),
                "Синхронизация сохранений", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void EnsurePlaceholders()
        {
            var placeholders = levelTables.Where(t => t.IsPlaceholder).ToList();
            int realTables = levelTables.Count(t => !t.IsPlaceholder);
            int placeholdersNeeded = isGeneratorVisible ? Math.Max(0, 2 - realTables) : 0;

            for (int i = placeholdersNeeded; i < placeholders.Count; i++)
                levelTables.Remove(placeholders[i]);

            placeholders = levelTables.Where(t => t.IsPlaceholder).ToList();
            while (placeholders.Count < placeholdersNeeded)
            {
                var placeholder = new LevelTableViewModel
                {
                    IdCompletion = -1,
                    LevelName = "LevelName",
                    IsPlaceholder = true
                };
                levelTables.Add(placeholder);
                placeholders.Add(placeholder);
            }

            UpdateDisplayedTables();
        }

        // Не пересоздаём первые две таблицы при переключении генератора. Обновление
        // панели или всего CollectionView пересоздавало все DataGrid и замедляло переключение.
        private void UpdateDisplayedTables()
        {
            var target = isGeneratorVisible
                ? levelTables.Take(2).ToList()
                : levelTables.Where(t => !t.IsPlaceholder).ToList();

            for (int i = displayedTables.Count - 1; i >= 0; i--)
            {
                if (!target.Contains(displayedTables[i]))
                    displayedTables.RemoveAt(i);
            }

            for (int i = 0; i < target.Count; i++)
            {
                if (i < displayedTables.Count && ReferenceEquals(displayedTables[i], target[i]))
                    continue;

                int existingIndex = displayedTables.IndexOf(target[i]);
                if (existingIndex >= 0)
                    displayedTables.Move(existingIndex, i);
                else
                    displayedTables.Insert(i, target[i]);
            }

            while (displayedTables.Count > target.Count)
                displayedTables.RemoveAt(displayedTables.Count - 1);
        }

        private void UpdateSavesButtonState()
        {
            bool hasSaves = levelTables.Any(t => !t.IsPlaceholder && t.Runs.Count > 0);

            SavesToggleMenuItem.IsEnabled = hasSaves;
            SavesToggleMenuItem.Opacity = hasSaves ? 1.0 : 0.35;

            if (!hasSaves && !isGeneratorVisible)
                SetGeneratorVisible(true);
        }

        private void SavesToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!SavesToggleMenuItem.IsEnabled)
                return;

            SetGeneratorVisible(!isGeneratorVisible);
        }

        private void SetGeneratorVisible(bool visible)
        {
            bool visibilityChanged = visible != isGeneratorVisible;
            if (visibilityChanged && displayedTables.Count > 0)
                CaptureScrollPositions(captureOuterOffset: !isGeneratorVisible);

            isGeneratorVisible = visible;
            OnPropertyChanged(nameof(IsGeneratorHidden));

            LeftColumn.Width = new GridLength(visible ? 260 : 0);
            SplitterColumn.Width = new GridLength(visible ? 15 : 0);
            GeneratorPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            TableScrollViewer.HorizontalScrollBarVisibility =
                visible ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

            TableWidth = visible ? double.NaN : 450.0;

            EnsurePlaceholders();
            UpdateMenuHeaderText();

            if (visibilityChanged)
                ScheduleScrollRestore();
        }

        private void UpdateStageAttemptTotals(LevelTableViewModel table)
        {
            foreach (var stage in table.Runs.Where(r => r.IsStage))
            {
                long total = table.Runs
                    .Where(r => !r.IsStage && r.IdStage == stage.IdStage)
                    .Sum(r => (long)ParseAttempts(r.Attempts));

                stage.Attempts = total.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static int ParseAttempts(string? value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempts)
                ? Math.Max(0, attempts)
                : 0;

        private void CaptureScrollPositions(bool captureOuterOffset = false)
        {
            if (captureOuterOffset && TableScrollViewer != null)
                outerTableHorizontalOffset = TableScrollViewer.HorizontalOffset;

            foreach (var grid in FindVisualChildren<DataGrid>(TablesItemsControl))
            {
                if (grid.DataContext is not LevelTableViewModel table || table.IdCompletion <= 0)
                    continue;

                var scrollViewer = FindVisualChild<ScrollViewer>(grid);
                if (scrollViewer != null)
                    tableScrollPositions[table.IdCompletion] =
                        (scrollViewer.HorizontalOffset, scrollViewer.VerticalOffset);
            }

            suppressScrollPositionTracking = true;
        }

        private void ScheduleScrollRestore()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (TableScrollViewer != null)
                    TableScrollViewer.ScrollToHorizontalOffset(outerTableHorizontalOffset);

                foreach (var grid in FindVisualChildren<DataGrid>(TablesItemsControl))
                {
                    if (grid.DataContext is not LevelTableViewModel table ||
                        !tableScrollPositions.TryGetValue(table.IdCompletion, out var position))
                        continue;

                    var scrollViewer = FindVisualChild<ScrollViewer>(grid);
                    if (scrollViewer == null)
                        continue;

                    scrollViewer.ScrollToHorizontalOffset(position.Horizontal);
                    scrollViewer.ScrollToVerticalOffset(position.Vertical);
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    suppressScrollPositionTracking = false;
                }));
            }));
        }

        private void TableDataGrid_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is DataGrid grid)
                QueueDataGridColumnUpdate(grid);
        }

        private void TableDataGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is DataGrid grid)
                QueueDataGridColumnUpdate(grid);
        }

        private void RequestColumnUpdateForTable(LevelTableViewModel table)
        {
            foreach (var grid in FindVisualChildren<DataGrid>(TablesItemsControl))
            {
                if (ReferenceEquals(grid.DataContext, table))
                {
                    QueueDataGridColumnUpdate(grid);
                    break;
                }
            }
        }

        private void QueueAllDataGridColumnUpdates()
        {
            foreach (var grid in FindVisualChildren<DataGrid>(TablesItemsControl))
                QueueDataGridColumnUpdate(grid);
        }

        private void QueueDataGridColumnUpdate(DataGrid grid)
        {
            if (!grid.IsLoaded || !pendingColumnUpdates.Add(grid))
                return;

            grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                pendingColumnUpdates.Remove(grid);
                if (grid.IsLoaded)
                    UpdateDataGridColumnWidths(grid);
            }));
        }

        private static void UpdateDataGridColumnWidths(DataGrid grid)
        {
            if (grid.Columns.Count < 4)
                return;

            SetTextColumnBounds(grid, grid.Columns[0], 0);
            SetTextColumnBounds(grid, grid.Columns[2], 2);

            var noteColumn = grid.Columns[3];
            double longestNoteWidth = MeasureColumnContentWidth(grid, noteColumn, 3);
            noteColumn.MaxWidth = double.PositiveInfinity;
            noteColumn.MinWidth = longestNoteWidth;

            double firstWidth = GetCurrentColumnWidth(grid.Columns[0]);
            double checkWidth = GetCurrentColumnWidth(grid.Columns[1]);
            double attemptsWidth = GetCurrentColumnWidth(grid.Columns[2]);
            double availableWidth = Math.Max(0, grid.ActualWidth - firstWidth - checkWidth - attemptsWidth - 12);
            double targetNoteWidth = Math.Max(longestNoteWidth, availableWidth);

            if (Math.Abs(noteColumn.ActualWidth - targetNoteWidth) > 1 || noteColumn.ActualWidth <= 0)
                noteColumn.Width = new DataGridLength(targetNoteWidth);
        }

        private static void SetTextColumnBounds(DataGrid grid, DataGridColumn column, int columnIndex)
        {
            double contentWidth = MeasureColumnContentWidth(grid, column, columnIndex);
            double maximumWidth = Math.Max(contentWidth, contentWidth * 2);

            // Сначала обновляем пределы в безопасном порядке, чтобы при изменении самого длинного текста
            // минимальная ширина на мгновение не стала больше максимальной.
            column.MinWidth = 0;
            column.MaxWidth = double.PositiveInfinity;
            column.MinWidth = contentWidth;
            column.MaxWidth = maximumWidth;

            double currentWidth = GetCurrentColumnWidth(column);
            double targetWidth = Math.Max(contentWidth, Math.Min(currentWidth, maximumWidth));
            if (Math.Abs(currentWidth - targetWidth) > 1 || column.ActualWidth <= 0)
                column.Width = new DataGridLength(targetWidth);
        }

        private static double GetCurrentColumnWidth(DataGridColumn column)
        {
            if (column.ActualWidth > 0 && !double.IsNaN(column.ActualWidth))
                return column.ActualWidth;
            return column.Width.IsAbsolute ? column.Width.Value : Math.Max(40, column.MinWidth);
        }

        private static double MeasureColumnContentWidth(DataGrid grid, DataGridColumn column, int columnIndex)
        {
            string header = column.Header switch
            {
                TextBlock textBlock => textBlock.Text,
                string text => text,
                _ => column.Header?.ToString() ?? string.Empty
            };

            double width = MeasureTextWidth(grid, header, true);
            foreach (object item in grid.Items)
            {
                if (item is not RunItem run)
                    continue;

                string value = columnIndex switch
                {
                    0 => run.Run,
                    2 => run.Attempts,
                    3 => run.Note,
                    _ => string.Empty
                };
                width = Math.Max(width, MeasureTextWidth(grid, value, false));
            }

            return Math.Max(24, width);
        }

        private static double MeasureTextWidth(DataGrid grid, string? text, bool isHeader)
        {
            if (string.IsNullOrEmpty(text))
                return 24;

            double pixelsPerDip = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
            var typeface = new Typeface(
                grid.FontFamily,
                grid.FontStyle,
                isHeader ? FontWeights.SemiBold : grid.FontWeight,
                grid.FontStretch);

            double maxLineWidth = 0;
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
            {
                var formatted = new FormattedText(
                    line,
                    CultureInfo.CurrentUICulture,
                    grid.FlowDirection,
                    typeface,
                    grid.FontSize,
                    Brushes.Transparent,
                    pixelsPerDip);
                maxLineWidth = Math.Max(maxLineWidth, formatted.WidthIncludingTrailingWhitespace);
            }

            return maxLineWidth + 16; // Добавляем место для внутренних отступов и границ ячейки.
        }

        private void DataGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is not DataGrid grid)
                return;

            if (!suppressScrollPositionTracking &&
                grid.DataContext is LevelTableViewModel table && table.IdCompletion > 0)
            {
                tableScrollPositions[table.IdCompletion] = (e.HorizontalOffset, e.VerticalOffset);
            }

            var header = FindVisualChild<TextBlock>(grid, "NotesHeaderTextBlock");
            if (header == null || grid.Columns.Count < 4)
                return;

            double notesLeft = grid.Columns.Take(3).Sum(c => c.ActualWidth);
            double notesWidth = grid.Columns[3].ActualWidth;
            if (notesWidth <= 0)
                return;

            double left = Math.Max(0, e.HorizontalOffset - notesLeft);
            double right = Math.Min(notesWidth, e.HorizontalOffset + e.ViewportWidth - notesLeft);
            if (right <= left)
                return;

            double center = (left + right) / 2;
            header.RenderTransform = new TranslateTransform(center - notesWidth / 2, 0);
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                yield break;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    yield return typedChild;

                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        private void MakeBlitzkriegButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(StartPosesTextBox.Text))
            {
                MessageBox.Show(
                    "Введите стартовые позиции.",
                    "Генератор",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StartPosesTextBox.Focus();
                return;
            }

            try
            {
                var blitzkrieg = Generator.Generate(StartPosesTextBox.Text);
                BlitzkriegTextBox.Text = string.Join(Environment.NewLine, blitzkrieg.Runs.Select(run => run.Run));

                DBManager.AddBlitzkrieg(blitzkrieg);
                Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось создать блицкриг.\n\n" + ex.Message,
                    "Ошибка генератора",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void CopyStartPosesButton_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(StartPosesTextBox.Text);
        }

        private void CopyBlitzkriegButton_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(BlitzkriegTextBox.Text);
        }

        private void PasteStartPosesButton_Click(object sender, RoutedEventArgs e)
        {
            if (!Clipboard.ContainsText())
                return;

            StartPosesTextBox.Text = Clipboard.GetText();
            StartPosesTextBox.CaretIndex = StartPosesTextBox.Text.Length;
            StartPosesTextBox.Focus();
        }

        private void ClearGeneratorButton_Click(object sender, RoutedEventArgs e)
        {
            StartPosesTextBox.Clear();
            BlitzkriegTextBox.Clear();
            StartPosesTextBox.Focus();
        }

        private static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            Clipboard.SetText(text);
        }

        private void LevelName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2)
                return;

            if (sender is FrameworkElement element &&
                element.DataContext is LevelTableViewModel table &&
                !table.IsPlaceholder)
            {
                table.IsEditingName = true;
            }
        }

        private void LevelNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SaveLevelName(sender);
        }

        private void LevelNameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SaveLevelName(sender);
        }

        private void SaveLevelName(object sender)
        {
            if (!(sender is TextBox textBox) ||
                !(textBox.DataContext is LevelTableViewModel table))
                return;

            table.IsEditingName = false;

            if (table.IdCompletion > 0)
            {
                DBManager.UpdateLevelName(table.IdCompletion, table.LevelName);
            }
        }

        private void DeleteTable_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) ||
                !(button.DataContext is LevelTableViewModel table) ||
                table.IsPlaceholder)
                return;

            if (MessageBox.Show(
                    $"Вы уверены, что хотите удалить таблицу \"{table.LevelName}\"?",
                    "Подтверждение удаления",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            DBManager.DeleteBlitzkrieg(table.IdCompletion);
            Reload();
        }

        private void MoveTableLeft_Click(object sender, RoutedEventArgs e)
        {
            MoveTable(sender, -1);
        }

        private void MoveTableRight_Click(object sender, RoutedEventArgs e)
        {
            MoveTable(sender, 1);
        }

        private void MoveTable(object sender, int offset)
        {
            if (!(sender is Button button) ||
                !(button.DataContext is LevelTableViewModel table))
                return;

            int index = levelTables.IndexOf(table);
            int newIndex = index + offset;

            if (index < 0 || newIndex < 0 || newIndex >= levelTables.Count)
                return;

            levelTables.Move(index, newIndex);
            UpdateDisplayedTables();
            DBManager.UpdateDisplayOrders(
                levelTables.Where(t => !t.IsPlaceholder).Select(t => t.IdCompletion).ToList());
        }

        private static T FindVisualChild<T>(DependencyObject parent, string name) where T : DependencyObject
        {
            if (parent == null)
                return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typed && (child as FrameworkElement)?.Name == name)
                    return typed;

                var result = FindVisualChild<T>(child, name);
                if (result != null)
                    return result;
            }

            return null;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typed)
                    return typed;

                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }

            return null;
        }

        private void UpdateMenuHeaderText()
        {
            SavesToggleMenuItem.Header = Application.Current.TryFindResource(
                isGeneratorVisible ? "Str_Saves" : "Str_Generator")
                ?? (isGeneratorVisible ? "Сохранения" : "Генератор");
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(ZoomStep);

        private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-ZoomStep);

        private void ChangeZoom(double delta)
        {
            currentZoom = Math.Clamp(
                Math.Round(currentZoom + delta, 2),
                MinZoom,
                MaxZoom);

            AppScaleTransform.ScaleX = currentZoom;
            AppScaleTransform.ScaleY = currentZoom;
            ZoomIndicatorText.Text = $"{Math.Round(currentZoom * 100)}%";
            QueueAllDataGridColumnUpdates();

            if (appSettings != null)
            {
                appSettings.Zoom = currentZoom;
                SaveSettings();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;

            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    ChangeZoom(ZoomStep);
                    break;

                case Key.OemMinus:
                case Key.Subtract:
                    ChangeZoom(-ZoomStep);
                    break;

                case Key.D0:
                case Key.NumPad0:
                    ChangeZoom(1.0 - currentZoom);
                    break;

                default:
                    return;
            }

            e.Handled = true;
        }

        private void WindowHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;

            if (WindowState == WindowState.Maximized)
            {
                double mouseX = e.GetPosition(this).X;
                double width = ActualWidth;

                WindowState = WindowState.Normal;
                Left = mouseX - Width * mouseX / width;
                Top = 0;
            }

            DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ThemeBlue_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Apply("Blue");
            SaveTheme("Blue");
        }

        private void ThemeCharcoal_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Apply("Charcoal");
            SaveTheme("Charcoal");
        }

        private void ThemeEmerald_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Apply("Emerald");
            SaveTheme("Emerald");
        }

        private void LangRu_Click(object sender, RoutedEventArgs e)
        {
            LoadDictionary("Languages/Strings.ru.xaml", "Strings.", true);
            SaveLanguage("ru");
        }

        private void LangEn_Click(object sender, RoutedEventArgs e)
        {
            LoadDictionary("Languages/Strings.en.xaml", "Strings.", true);
            SaveLanguage("en");
        }

        private void LoadDictionary(string path, string marker, bool insertFirst)
        {
            try
            {
                var dictionaries = Application.Current.Resources.MergedDictionaries;
                var old = dictionaries.FirstOrDefault(d =>
                    d.Source != null &&
                    d.Source.OriginalString.Contains(marker));

                if (old != null)
                    dictionaries.Remove(old);

                var dictionary = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{path}", UriKind.Absolute)
                };

                if (insertFirst)
                    dictionaries.Insert(0, dictionary);
                else
                    dictionaries.Add(dictionary);

                if (insertFirst)
                {
                    UpdateMenuHeaderText();
                    QueueAllDataGridColumnUpdates();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось загрузить ресурс: {ex.Message}",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void LoadSettings()
        {
            appSettings = AppSettingsService.Load();

            ThemeManager.Initialize(
                string.IsNullOrWhiteSpace(appSettings.Theme)
                    ? "Blue"
                    : appSettings.Theme);

            if (string.Equals(appSettings.Language, "ru", StringComparison.OrdinalIgnoreCase))
                LoadDictionary("Languages/Strings.ru.xaml", "Strings.", true);
            else if (string.Equals(appSettings.Language, "en", StringComparison.OrdinalIgnoreCase))
                LoadDictionary("Languages/Strings.en.xaml", "Strings.", true);

            currentZoom = Math.Clamp(appSettings.Zoom, MinZoom, MaxZoom);
            AppScaleTransform.ScaleX = currentZoom;
            AppScaleTransform.ScaleY = currentZoom;
            ZoomIndicatorText.Text = $"{Math.Round(currentZoom * 100)}%";

            RestoreWindowPlacement();
        }

        private void RestoreWindowPlacement()
        {
            double left;
            double top;
            double width;
            double height;
            bool wasMaximized = appSettings.WasMaximized;

            if (appSettings.HasWindowPlacement &&
                appSettings.WindowWidth >= MinWidth &&
                appSettings.WindowHeight >= MinHeight)
            {
                left = appSettings.WindowLeft;
                top = appSettings.WindowTop;
                width = appSettings.WindowWidth;
                height = appSettings.WindowHeight;
            }
            else if (appSettings.Size is { Length: >= 2 } &&
                     appSettings.Size[0] >= MinWidth &&
                     appSettings.Size[1] >= MinHeight)
            {
                // Переносим размеры из настроек старого формата, если они там были сохранены.
                left = appSettings.Location.X;
                top = appSettings.Location.Y;
                width = appSettings.Size[0];
                height = appSettings.Size[1];
                wasMaximized = false;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            // Если окно было на отключённом мониторе, возвращаем его на видимую часть рабочего стола.
            double virtualLeft = SystemParameters.VirtualScreenLeft;
            double virtualTop = SystemParameters.VirtualScreenTop;
            double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
            bool isVisible = left < virtualRight - 40 &&
                             left + width > virtualLeft + 40 &&
                             top < virtualBottom - 40 &&
                             top + height > virtualTop + 40;

            if (!isVisible)
            {
                width = Math.Min(width, SystemParameters.PrimaryScreenWidth);
                height = Math.Min(height, SystemParameters.PrimaryScreenHeight);
                left = (SystemParameters.PrimaryScreenWidth - width) / 2;
                top = (SystemParameters.PrimaryScreenHeight - height) / 2;
                wasMaximized = false;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = width;
            Height = height;
            Left = left;
            Top = top;

            if (wasMaximized)
                WindowState = WindowState.Maximized;
            else
                lastNonMinimizedWindowState = WindowState.Normal;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState != WindowState.Minimized)
                lastNonMinimizedWindowState = WindowState;
        }

        private void SaveWindowPlacement()
        {
            if (appSettings == null)
                appSettings = new AppSettings();

            // Для развёрнутого или свёрнутого окна сохраняем последние обычные размеры.
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height)
                : RestoreBounds;

            if (bounds.Width < MinWidth || bounds.Height < MinHeight)
                return;

            appSettings.HasWindowPlacement = true;
            appSettings.WindowLeft = bounds.Left;
            appSettings.WindowTop = bounds.Top;
            appSettings.WindowWidth = bounds.Width;
            appSettings.WindowHeight = bounds.Height;
            appSettings.WasMaximized = WindowState == WindowState.Maximized ||
                                       (WindowState == WindowState.Minimized && lastNonMinimizedWindowState == WindowState.Maximized);

            // Заполняем прежние поля, чтобы настройки оставались совместимыми со старыми сборками.
            appSettings.Location = new System.Drawing.Point(
                (int)Math.Round(bounds.Left),
                (int)Math.Round(bounds.Top));
            appSettings.Size = new[]
            {
                (int)Math.Round(bounds.Width),
                (int)Math.Round(bounds.Height)
            };
        }

        private void SaveSettings()
        {
            if (appSettings == null)
                return;

            appSettings.Zoom = currentZoom;
            AppSettingsService.Save(appSettings);
        }

        private void SaveTheme(string theme)
        {
            if (appSettings == null)
                appSettings = new AppSettings();

            appSettings.Theme = theme;
            SaveSettings();
        }

        private void SaveLanguage(string language)
        {
            if (appSettings == null)
                appSettings = new AppSettings();

            appSettings.Language = language;
            SaveSettings();
        }

        protected override void OnClosed(EventArgs e)
        {
            syncTimer.Stop();
            try
            {
                if (!syncInProgress)
                    SaveSyncManager.FlushPendingWrites();
            }
            catch (Exception ex)
            {
                LogManager.LogAction("Final save writeback failed: " + ex, 1);
            }
            SaveWindowPlacement();
            SaveSettings();
            base.OnClosed(e);
        }

        private void DataGridCell_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (sender is DataGridCell cell && cell.Column != null && cell.Column.DisplayIndex == 3)
                e.Handled = true;
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = child;

            while (parent != null)
            {
                if (parent is T result)
                    return result;

                parent = VisualTreeHelper.GetParent(parent);
            }

            return null;
        }

        private void TableTitleTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
            e.Handled = true;

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Выберите сохранение внутриигрового мода",
                Filter = "JSON сохранения (*.json)|*.json|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                var result = new DataTransferManager().Import(dialog.FileName);
                if (result.AlreadyLinked)
                {
                    MessageBox.Show(
                        $"Это сохранение уже связано с таблицей «{result.ExistingLevelName}».{Environment.NewLine}{Environment.NewLine}Новая таблица не создана.{Environment.NewLine}ID: {result.SaveGuid}",
                        "Сохранение уже привязано", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Reload();
                MessageBox.Show(
                    $"Сохранение «{Path.GetFileName(dialog.FileName)}» привязано к таблице.{Environment.NewLine}Резервная копия создана в базе данных.{Environment.NewLine}Синхронизация будет выполняться автоматически раз в минуту.",
                    "Сохранение привязано", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось привязать сохранение." + Environment.NewLine + Environment.NewLine + ex.Message,
                    "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var table = activeTable;
            if (table == null || table.IsPlaceholder)
            {
                MessageBox.Show("Сначала выберите таблицу, щёлкнув по ней.", "Экспорт сохранения",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!table.IsLinkedSave)
            {
                MessageBox.Show("Эта таблица не связана с сохранением внутриигрового мода.", "Экспорт сохранения",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Экспортировать JSON сохранения",
                Filter = "JSON сохранения (*.json)|*.json",
                FileName = $"{MakeSafeFileName(table.LevelName)}.json",
                AddExtension = true,
                DefaultExt = ".json"
            };
            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                SaveSyncManager.ExportLinkedSave(table.IdCompletion, dialog.FileName);
                MessageBox.Show("Сохранение экспортировано в компактном однострочном JSON.", "Экспорт завершён",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось экспортировать сохранение." + Environment.NewLine + Environment.NewLine + ex.Message,
                    "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TableDataGrid_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is LevelTableViewModel table)
                activeTable = table;
        }

        private static string MakeSafeFileName(string? value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "BlitzkriegSave" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return name;
        }
    }
}
