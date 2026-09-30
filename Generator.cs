using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BlitzkriegWPF
{
    public static class Generator
    {
        public static Blitzkrieg Generate(string input)
        {
            var startPoses = ParseStartPoses(input);
            var numbers = new[] { 0 }.Concat(startPoses).Append(100).ToArray();
            var runs = new List<BlitzkriegRun>();
            int stage = 1;

            for (int left = numbers.Length - 2; left > 0; left--, stage++)
            {
                runs.Add(new BlitzkriegRun { Run = $"Stage {stage}" });

                for (int from = left, to = numbers.Length - 1; from >= 0; from--, to--)
                    runs.Add(new BlitzkriegRun { Run = $"{numbers[from]} - {numbers[to]}" });
            }

            runs.Add(new BlitzkriegRun { Run = $"Stage {stage}" });
            runs.Add(new BlitzkriegRun { Run = "0 - 100" });

            var result = new Blitzkrieg();
            result.Runs.AddRange(runs);
            return result;
        }

        private static int[] ParseStartPoses(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Array.Empty<int>();

            return Regex.Matches(input, @"-?\d+")
                .Cast<Match>()
                .Select(m => int.TryParse(m.Value, out int n) && n >= 1 && n <= 99 ? (int?)n : null)
                .Where(n => n.HasValue)
                .Select(n => n.Value)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();
        }
    }
}
