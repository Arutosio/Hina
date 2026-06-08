using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hina.PackageManager.Platform.Windows
{
    // Minimal COM interop for Windows shell shortcut creation (.lnk files).
    // Uses the [CoClass] managed-COM idiom so `new IShellLinkW_Coclass()` resolves to
    // the IShellLinkA/W class object via the runtime's COM activation — no progid
    // lookup, no reflection. AOT works with this pattern as of .NET 8+.
    [SupportedOSPlatform("windows")]
    internal static class ShellLink
    {
        public static void Create(string linkPath, string targetPath, string workingDir, string description, string? iconPath = null, string? arguments = null)
        {
            IShellLinkW shellLink = new IShellLinkW_Coclass();
            try
            {
                shellLink.SetPath(targetPath);
                shellLink.SetWorkingDirectory(workingDir);
                if (!string.IsNullOrEmpty(arguments))
                {
                    shellLink.SetArguments(arguments);
                }
                if (!string.IsNullOrEmpty(description))
                {
                    shellLink.SetDescription(description);
                }
                if (!string.IsNullOrEmpty(iconPath))
                {
                    shellLink.SetIconLocation(iconPath, 0);
                }

                IPersistFile persist = (IPersistFile)shellLink;
                persist.Save(linkPath, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellLink);
            }
        }

        // Coclass-by-interface: declaring the interface with [CoClass(...)] lets the
        // compiler emit `Activator.CreateInstance(typeof(ShellLinkCoClass))` + QI cast.
        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [CoClass(typeof(ShellLinkCoClass))]
        private interface IShellLinkW_Coclass : IShellLinkW
        {
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkCoClass
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }
}
