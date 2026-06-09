using System.Collections.Generic;
using Hina.PackageManager.Descriptor;

namespace Hina.Builder.Init
{
    // Everything the wizard collected, ready to assemble into a descriptor.
    internal sealed class ScaffoldAnswers
    {
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Publisher { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? Homepage { get; init; }
        public string BaseUrl { get; init; } = string.Empty;
        public string Channel { get; init; } = "stable";
        public string PublicKey { get; init; } = string.Empty;
        public ExecMap Exec { get; init; } = new ExecMap();
        public List<ShellEntry> Entries { get; init; } = new List<ShellEntry>();
        public SandboxSpec? Sandbox { get; init; }
        public bool AllowInsecure { get; init; }
    }

    // Assembles a validated AppDescriptor from the wizard's answers. Pure: throws
    // DescriptorValidationException (the same the rest of Hina uses) if the result is invalid,
    // so the wizard can re-prompt instead of writing a broken descriptor.
    internal static class DescriptorScaffolder
    {
        public static AppDescriptor Build(ScaffoldAnswers a)
        {
            AppDescriptor descriptor = new AppDescriptor
            {
                SchemaVersion = DescriptorValidator.CurrentSchemaVersion,
                Name = a.Name,
                DisplayName = a.DisplayName,
                Version = a.Version,
                Publisher = a.Publisher,
                Description = a.Description,
                Homepage = a.Homepage,
                BaseUrl = a.BaseUrl,
                Channel = a.Channel,
                PublicKey = a.PublicKey,
                Exec = a.Exec,
                Entries = a.Entries,
                Sandbox = a.Sandbox
            };

            ValidationContext ctx = new ValidationContext { AllowInsecure = a.AllowInsecure };
            DescriptorValidator.Validate(descriptor, ctx).EnsureValid();
            return descriptor;
        }
    }
}
