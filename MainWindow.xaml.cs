using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
        private ICollectionView levelTablesView;

        private bool isGeneratorVisible = true;
        private bool isLoadingFromDb;
        private double currentZoom = 1.0;
        private AppSettings appSettings;
        private double tableWidth = double.NaN;

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
            LoadSettings();

            levelTablesView = new ListCollectionView(levelTables);
            levelTablesView.Filter = FilterTables;
            TablesItemsControl.ItemsSource = levelTablesView;

            TableScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            SetWindowChrome();
            SetGeneratorVisible(isGeneratorVisible);
            Reload();
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
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0)
            });
        }

        private void Reload()
        {
            isLoadingFromDb = true;

            try
            {
                levelTables.Clear();

                int levelIndex = 1;
                foreach (var blitzkrieg in DBManager.GetBlitzkriegs())
                {
                    var table = new LevelTableViewModel
                    {
                        IdCompletion = blitzkrieg.IdCompletion,
                        LevelName = string.IsNullOrWhiteSpace(blitzkrieg.LevelName)
                            ? $"Level {levelIndex}"
                            : blitzkrieg.LevelName
                    };

                    foreach (var run in blitzkrieg.Runs)
                    {
                        var item = new RunItem
                        {
                            IdRun = run.IdRun,
                            Run = run.Run,
                            IsChecked = run.IsChecked,
                            Attempts = ShouldShowAttempts(run) ? run.Attempts.ToString() : string.Empty,
                            Note = run.Note ?? string.Empty
                        };

                        item.PropertyChanged += (_, __) => SaveRun(table, item);
                        table.Runs.Add(item);
                    }

                    levelTables.Add(table);
                    levelIndex++;
                }
            }
            finally
            {
                isLoadingFromDb = false;
            }

            EnsurePlaceholders();
            RefreshView();
            UpdateSavesButtonState();
        }

        private static bool ShouldShowAttempts(BlitzkriegRun run)
        {
            return !run.Run.StartsWith("Stage", StringComparison.OrdinalIgnoreCase)
                   && run.Attempts != 0;
        }

        private void SaveRun(LevelTableViewModel table, RunItem run)
        {
            if (isLoadingFromDb || table.IdCompletion <= 0 || run.IdRun <= 0)
                return;

            DBManager.UpdateRun(table.IdCompletion, run);
        }

        private void EnsurePlaceholders()
        {
            for (int i = levelTables.Count - 1; i >= 0; i--)
                if (levelTables[i].IsPlaceholder)
                    levelTables.RemoveAt(i);

            if (!isGeneratorVisible)
                return;

            int realTables = levelTables.Count(t => !t.IsPlaceholder);

            while (realTables < 2)
            {
                levelTables.Add(new LevelTableViewModel
                {
                    IdCompletion = -1,
                    LevelName = "LevelName",
                    IsPlaceholder = true
                });
                realTables++;
            }
        }

        private void RefreshView()
        {
            levelTablesView?.Refresh();
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

        private void UpdateSavesButtonState()
        {
            bool hasSaves = levelTables.Any(t => !t.IsPlaceholder && t.Runs.Count > 0);

            SavesToggleMenuItem.IsEnabled = hasSaves;
            SavesToggleMenuItem.Opacity = hasSaves ? 1.0 : 0.35;

            if (!hasSaves && !isGeneratorVisible)
                SetGeneratorVisible(true);
        }

        private bool FilterTables(object item)
        {
            if (!(item is LevelTableViewModel table))
                return false;

            return isGeneratorVisible
                ? levelTables.IndexOf(table) < 2
                : !table.IsPlaceholder;
        }

        private void SavesToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!SavesToggleMenuItem.IsEnabled)
                return;

            SetGeneratorVisible(!isGeneratorVisible);
        }

        private void SetGeneratorVisible(bool visible)
        {
            isGeneratorVisible = visible;
            OnPropertyChanged(nameof(IsGeneratorHidden));

            LeftColumn.Width = new GridLength(visible ? 260 : 0);
            SplitterColumn.Width = new GridLength(visible ? 15 : 0);
            GeneratorPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            TableScrollViewer.HorizontalScrollBarVisibility =
                visible ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

            TableWidth = visible ? double.NaN : 450.0;

            TablesItemsControl.ItemsPanel = (ItemsPanelTemplate)FindResource(
                visible ? "TwoColumnTablesPanel" : "HorizontalTablesPanel");

            EnsurePlaceholders();
            RefreshView();
            UpdateMenuHeaderText();
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
            DBManager.UpdateDisplayOrders(
                levelTables.Where(t => !t.IsPlaceholder).Select(t => t.IdCompletion).ToList());
        }

        private void DataGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!(sender is DataGrid grid))
                return;

            var header = FindVisualChild<TextBlock>(grid, "NotesHeaderTextBlock");
            if (header == null)
                return;

            const double notesLeft = 205;
            const double notesWidth = 400;

            double left = Math.Max(0, e.HorizontalOffset - notesLeft);
            double right = Math.Min(notesWidth, e.HorizontalOffset + e.ViewportWidth - notesLeft);

            if (right <= left)
                return;

            double center = (left + right) / 2;
            header.RenderTransform = new TranslateTransform(center - notesWidth / 2, 0);
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
                    UpdateMenuHeaderText();
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
    }
}
