using System.Collections.Generic;

namespace BlitzkriegWPF
{
    public class Blitzkrieg
    {
        public int IdCompletion { get; set; }
        public string LevelName { get; set; }
        public List<BlitzkriegRun> Runs { get; } = new List<BlitzkriegRun>();
    }

    public class BlitzkriegRun
    {
        public int IdRun { get; set; }
        public string Run { get; set; }
        public bool IsChecked { get; set; }
        public int Attempts { get; set; }
        public string Note { get; set; }
    }
}
