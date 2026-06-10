namespace Hina.Core.Inputs
{
    // One file selected for packaging: where it lives on disk and the
    // '/'-separated path it will have inside the manifest / install root.
    public sealed class InputFile
    {
        public string AbsolutePath { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;

        // Full path of the input root this file was taken from. With merged
        // roots this tells overrides apart (the winning root differs from the
        // overridden one).
        public string SourceRoot { get; init; } = string.Empty;
    }
}
