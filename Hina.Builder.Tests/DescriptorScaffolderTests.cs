using System.Collections.Generic;
using Hina.Builder.Init;
using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;

namespace Hina.Builder.Tests
{
    public sealed class DescriptorScaffolderTests
    {
        private static string FreshPublicKey()
        {
            (string _, string pub) = KeyGenerator.GenerateEd25519();
            return pub;
        }

        private static ScaffoldAnswers ValidAnswers() => new ScaffoldAnswers
        {
            Name = "mygame",
            DisplayName = "My Game",
            Version = "1.0.0",
            Publisher = "Acme",
            Description = "A game",
            BaseUrl = "https://patch.example.com/",
            Channel = "stable",
            PublicKey = FreshPublicKey(),
            Exec = new ExecMap { Linux = "game" },
            Entries = new List<ShellEntry>
            {
                new ShellEntry { Id = "mygame", Name = "My Game", Exec = "game" }
            },
            Sandbox = new SandboxAnswers { Enabled = true, NeedsNetwork = true, DataLocation = DataLocation.Config }.ToSpec()
        };

        [Fact]
        public void Build_ValidAnswers_ProducesValidDescriptor()
        {
            AppDescriptor d = DescriptorScaffolder.Build(ValidAnswers());

            Assert.Equal("mygame", d.Name);
            Assert.Equal("game", d.Exec.Linux);
            Assert.Single(d.Entries);
            Assert.True(d.Sandbox!.Enabled);
            // And it passes the canonical validator.
            Assert.True(DescriptorValidator.Validate(d).IsValid);
        }

        [Fact]
        public void Build_InvalidName_Throws()
        {
            ScaffoldAnswers a = ValidAnswers();
            ScaffoldAnswers bad = new ScaffoldAnswers
            {
                Name = "Bad Name",          // spaces + uppercase → invalid
                DisplayName = a.DisplayName,
                Version = a.Version,
                Publisher = a.Publisher,
                Description = a.Description,
                BaseUrl = a.BaseUrl,
                Channel = a.Channel,
                PublicKey = a.PublicKey,
                Exec = a.Exec,
                Entries = new List<ShellEntry>()
            };

            Assert.Throws<DescriptorValidationException>(() => DescriptorScaffolder.Build(bad));
        }

        [Fact]
        public void Build_NoExec_Throws()
        {
            ScaffoldAnswers a = ValidAnswers();
            ScaffoldAnswers noExec = new ScaffoldAnswers
            {
                Name = a.Name,
                DisplayName = a.DisplayName,
                Version = a.Version,
                Publisher = a.Publisher,
                Description = a.Description,
                BaseUrl = a.BaseUrl,
                Channel = a.Channel,
                PublicKey = a.PublicKey,
                Exec = new ExecMap(),       // none defined
                Entries = new List<ShellEntry>()
            };

            Assert.Throws<DescriptorValidationException>(() => DescriptorScaffolder.Build(noExec));
        }
    }
}
