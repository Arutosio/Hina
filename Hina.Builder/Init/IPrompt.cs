using System;
using System.Collections.Generic;

namespace Hina.Builder.Init
{
    // The wizard's only channel to the user. Abstracted so InitCommand can be driven by a
    // scripted prompt in tests (no real console I/O).
    internal interface IPrompt
    {
        // Free-text question. Empty input returns def (or "" if def is null).
        string Ask(string question, string? def);

        // Yes/no. Empty input returns def.
        bool Confirm(string question, bool def);

        // Pick one of options by number. Empty input returns defaultIndex.
        int Choose(string question, IReadOnlyList<string> options, int defaultIndex);
    }

    // Real console implementation. Shows the default in brackets so "just press Enter" works
    // for every prompt.
    internal sealed class ConsolePrompt : IPrompt
    {
        public string Ask(string question, string? def)
        {
            string suffix = string.IsNullOrEmpty(def) ? "" : $" [{def}]";
            Console.Write($"{question}{suffix}: ");
            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return def ?? string.Empty;
            }
            return line.Trim();
        }

        public bool Confirm(string question, bool def)
        {
            string hint = def ? "[Y/n]" : "[y/N]";
            Console.Write($"{question} {hint}: ");
            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return def;
            }
            string v = line.Trim().ToLowerInvariant();
            return v == "y" || v == "yes";
        }

        public int Choose(string question, IReadOnlyList<string> options, int defaultIndex)
        {
            Console.WriteLine(question);
            for (int i = 0; i < options.Count; i++)
            {
                string marker = i == defaultIndex ? " (default)" : "";
                Console.WriteLine($"  {i + 1}. {options[i]}{marker}");
            }
            Console.Write($"Choice [{defaultIndex + 1}]: ");
            string? line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return defaultIndex;
            }
            if (int.TryParse(line.Trim(), out int n) && n >= 1 && n <= options.Count)
            {
                return n - 1;
            }
            return defaultIndex;
        }
    }
}
