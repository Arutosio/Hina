using System.Collections.Generic;
using Hina.Builder.Init;

namespace Hina.Builder.Tests
{
    // Drives InitCommand without a console. Separate queues per method type — the wizard calls
    // Ask/Confirm/Choose in a fixed order within each type, so tests don't have to interleave.
    // Ask mirrors ConsolePrompt: an empty scripted answer means "pressed Enter" → returns def.
    internal sealed class ScriptedPrompt : IPrompt
    {
        private readonly Queue<string> _asks;
        private readonly Queue<bool> _confirms;
        private readonly Queue<int> _chooses;

        public ScriptedPrompt(
            IEnumerable<string>? asks = null,
            IEnumerable<bool>? confirms = null,
            IEnumerable<int>? chooses = null)
        {
            _asks = new Queue<string>(asks ?? new List<string>());
            _confirms = new Queue<bool>(confirms ?? new List<bool>());
            _chooses = new Queue<int>(chooses ?? new List<int>());
        }

        public string Ask(string question, string? def)
        {
            string answer = _asks.Count > 0 ? _asks.Dequeue() : string.Empty;
            return string.IsNullOrWhiteSpace(answer) ? (def ?? string.Empty) : answer.Trim();
        }

        public bool Confirm(string question, bool def)
            => _confirms.Count > 0 ? _confirms.Dequeue() : def;

        public int Choose(string question, IReadOnlyList<string> options, int defaultIndex)
            => _chooses.Count > 0 ? _chooses.Dequeue() : defaultIndex;
    }
}
