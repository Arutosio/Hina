using System.Collections.Generic;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Hooks
{
    // Maps a HookAction to a stable string that identifies "the same hook" across
    // descriptor versions. UpdateService uses it to compute the add/remove diff so
    // that a v2 descriptor with a renamed PATH entry, for example, correctly removes
    // the v1 symlink and creates the v2 one.
    public static class HookIdentity
    {
        public static string For(HookAction hook) => hook switch
        {
            AddToPathHook a => $"addToPath:{a.Name}",
            MimeTypeHook m => $"registerMimeType:{m.MimeType}:{m.EntryId}",
            UrlSchemeHook u => $"registerUrlScheme:{u.Scheme}:{u.EntryId}",
            InstallFontHook f => "installFont:" + string.Join(",", SortedCopy(f.Files)),
            AutostartHook au => $"registerAutostart:{au.EntryId}",
            _ => $"unknown:{hook.GetType().Name}"
        };

        private static List<string> SortedCopy(IReadOnlyList<string> input)
        {
            List<string> copy = new List<string>(input);
            copy.Sort();
            return copy;
        }
    }
}
