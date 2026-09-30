using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BlitzkriegWPF
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }

    public class RunItem : ObservableObject
    {
        private string run;
        private bool isChecked;
        private string attempts;
        private string note;

        public int IdRun { get; set; }

        public string Run
        {
            get { return run; }
            set { Set(ref run, value); }
        }

        public bool IsChecked
        {
            get { return isChecked; }
            set { Set(ref isChecked, value); }
        }

        public string Attempts
        {
            get { return attempts; }
            set { Set(ref attempts, value); }
        }

        public string Note
        {
            get { return note; }
            set { Set(ref note, value); }
        }
    }

    public class LevelTableViewModel : ObservableObject
    {
        private string levelName;
        private bool isEditingName;

        public int IdCompletion { get; set; }
        public bool IsPlaceholder { get; set; }

        public ObservableCollection<RunItem> Runs { get; } = new ObservableCollection<RunItem>();

        public string LevelName
        {
            get { return levelName; }
            set { Set(ref levelName, value); }
        }

        public bool IsEditingName
        {
            get { return isEditingName; }
            set { Set(ref isEditingName, value); }
        }
    }
}
