using System.Collections.Generic;
using Hina.PackageManager.Sandbox;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // Unit tests for WindowsSandbox.QuoteArg and WindowsSandbox.BuildCommandLine.
    //
    // Both methods are internal static and reachable via InternalsVisibleTo
    // (Hina.PackageManager.csproj:13). No P/Invoke or OS dependency — runs on any host.
    //
    // The expected quoted forms are derived from the canonical CommandLineToArgvW rules
    // (MSVC argv / Daniel Colascione's ArgvQuote):
    //   • Backslashes are only special immediately before a '"' or the closing '"'.
    //   • Before '"': emit 2*N+1 backslashes, then escaped '"'.
    //   • At closing '"': emit 2*N backslashes so the final '\' does NOT escape it.
    //   • Elsewhere: backslashes are literal.
    public class WindowsSandboxCommandLineTests
    {
        // ---- QuoteArg (BUG-020 regression tests) ----

        // Fast-path: simple args with no special chars are returned as-is.
        [Fact]
        public void QuoteArg_SimpleArg_NoQuotesAdded()
        {
            Assert.Equal("hello", WindowsSandbox.QuoteArg("hello"));
        }

        // Arg with a space must be wrapped in quotes; no backslash content.
        [Fact]
        public void QuoteArg_SpaceInArg_WrapsInQuotes()
        {
            Assert.Equal("\"a b\"", WindowsSandbox.QuoteArg("a b"));
        }

        // Internal double-quote → \" (no preceding backslash, so 2*0+1 = 1 backslash).
        [Fact]
        public void QuoteArg_InternalQuote_EscapedCorrectly()
        {
            // a"b  →  "a\"b"
            Assert.Equal("\"a\\\"b\"", WindowsSandbox.QuoteArg("a\"b"));
        }

        // BUG-020 core case: path with a space that ends in a backslash.
        // The trailing '\' precedes the closing '"' → must be doubled → "C:\dir\\".
        [Fact]
        public void QuoteArg_TrailingBackslashWithSpace_DoublesBackslash()
        {
            // "C:\dir\" (with space somewhere → quoting forced by space in caller context,
            // but QuoteArg always quotes when the arg contains a space regardless).
            // Input: @"C:\dir\" (i.e. C:\dir\)  — has no space, so fast-path applies UNLESS
            // we include a space.  Use a path with a space to force quoting.
            Assert.Equal(@"""C:\Program Files\\""", WindowsSandbox.QuoteArg(@"C:\Program Files\"));
        }

        // Backslash immediately before an embedded quote: 2*1+1 = 3 backslashes + quote.
        [Fact]
        public void QuoteArg_BackslashBeforeInternalQuote_TripleBackslash()
        {
            // a\"b  (literal: a, \, ", b)  →  "a\\\"b"
            Assert.Equal("\"a\\\\\\\"b\"", WindowsSandbox.QuoteArg("a\\\"b"));
        }

        // Two trailing backslashes + space forces quoting; the two backslashes end the
        // string (space precedes them logically only if they come last — here space comes
        // first so the backslashes ARE the trailing run before closing '"').
        // Input: " \\"  (space, \, \)  →  trailing run = 2 → emit 4 backslashes before '"'
        // Expected: "\" \\\\\""  i.e.  " \\\\"  wrapped in outer quotes.
        [Fact]
        public void QuoteArg_TwoTrailingBackslashes_BothDoubled()
        {
            // Input chars: space, \, \   — space forces quoting; \\ is trailing run of 2.
            // Trailing rule: emit 2*2=4 backslashes before closing '"'.
            // Result: " \\\\" surrounded by '"' → "\" \\\\\""
            Assert.Equal("\" \\\\\\\\\"", WindowsSandbox.QuoteArg(" \\\\"));
        }

        // Empty string → "" (always quoted, no content, no trailing run).
        [Fact]
        public void QuoteArg_EmptyString_ProducesEmptyQuotedString()
        {
            Assert.Equal("\"\"", WindowsSandbox.QuoteArg(""));
        }

        // Arg with only backslashes and a space: trailing run must be doubled.
        [Fact]
        public void QuoteArg_OnlyBackslashesWithSpace()
        {
            // Input: "\ " (one backslash + space)
            // space forces quoting; backslash is not trailing (space follows), so emitted as-is.
            Assert.Equal("\"\\ \"", WindowsSandbox.QuoteArg("\\ "));

            // Input: " \" (space + one backslash) — trailing backslash before closing "
            Assert.Equal("\" \\\\\"", WindowsSandbox.QuoteArg(" \\"));
        }

        // \n and \v also trigger quoting (extended fast-path, BUG-020 recommendation).
        [Fact]
        public void QuoteArg_NewlineAndVerticalTab_AreQuoted()
        {
            string withNewline = WindowsSandbox.QuoteArg("a\nb");
            Assert.StartsWith("\"", withNewline);
            Assert.EndsWith("\"", withNewline);

            string withVtab = WindowsSandbox.QuoteArg("a\vb");
            Assert.StartsWith("\"", withVtab);
            Assert.EndsWith("\"", withVtab);
        }

        // ---- BuildCommandLine (integration of QuoteArg) ----

        // The first element is always the exe path; subsequent elements are appArgs.
        [Fact]
        public void BuildCommandLine_SimpleArgs_ProducesCorrectBuffer()
        {
            char[] buf = WindowsSandbox.BuildCommandLine(
                @"C:\tools\app.exe",
                new List<string> { "arg1", "arg2" });

            string cmd = new string(buf).TrimEnd('\0');
            Assert.Equal(@"C:\tools\app.exe arg1 arg2", cmd);
        }

        // Exe path with spaces must be quoted; args with spaces must each be quoted.
        [Fact]
        public void BuildCommandLine_SpacesInExeAndArgs_QuotedCorrectly()
        {
            char[] buf = WindowsSandbox.BuildCommandLine(
                @"C:\Program Files\app.exe",
                new List<string> { "hello world", "plain" });

            string cmd = new string(buf).TrimEnd('\0');
            Assert.Equal(@"""C:\Program Files\app.exe"" ""hello world"" plain", cmd);
        }

        // BUG-020 regression: exe path ending in backslash inside quotes must not
        // cause the closing quote to be escaped.
        [Fact]
        public void BuildCommandLine_ExeInDirEndingWithBackslash_ClosingQuoteNotEscaped()
        {
            // Exe is "C:\Program Files\app.exe"; the dir part ends in '\' only when the
            // exe name has a space forcing quoting. Use an exe whose full path has a space.
            char[] buf = WindowsSandbox.BuildCommandLine(
                @"C:\My Tools\app.exe",
                new List<string>());

            string cmd = new string(buf).TrimEnd('\0');
            // Must open and close cleanly: "C:\My Tools\app.exe"
            Assert.Equal(@"""C:\My Tools\app.exe""", cmd);
            // The closing char before \0 must be the closing '"', not a backslash.
            Assert.EndsWith("\"", cmd);
        }

        // Buffer is NUL-terminated (CreateProcessW requirement).
        [Fact]
        public void BuildCommandLine_BufferIsNulTerminated()
        {
            char[] buf = WindowsSandbox.BuildCommandLine("app.exe", new List<string>());
            Assert.Equal('\0', buf[buf.Length - 1]);
        }
    }
}
