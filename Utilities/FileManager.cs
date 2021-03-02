using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Appudeta.Utilities
{
    public static class FileManager
    {
        public static IList<string> GetContent(string targetDirectory)
        {
            List<string> ret = new();
            IList<string> dirs = GetDirectories(targetDirectory);
            IList<string> files = GetFiles(targetDirectory);
            ret.AddRange(dirs);
            ret.AddRange(files);

            return ret;
        }

        public static IList<string> GetFiles(string targetDirectory)
        {
            // Get the list of files found in the directory.
            IList<string> files = Directory.GetFiles(targetDirectory).ToList();
            return files;
        }
        public static IList<string> GetFilesWithSub(string targetDirectory)
        {
            List<string> files = Directory.GetFiles(targetDirectory, "*.*", SearchOption.AllDirectories).ToList();
            return files;
        }

        public static IList<string> GetDirectories(string targetDirectory)
        {
            // Recurse into subdirectories of this directory.
            IList<string> subDirectoryEntries = Directory.GetDirectories(targetDirectory).ToList();
            return subDirectoryEntries;
        }

        public static IList<string> GetAllSubDirectories(string directory)
        {
            List<string> ret = new();
            List<string> directorys = (GetDirectories(directory) as List<string>);
            foreach (string subDirectory in directorys)
            {
                ret.AddRange(GetAllSubDirectories(subDirectory));
            }
            return ret;
        }


        public static string PrintType(string path)
        {
            string ret = string.Empty;
            if (File.Exists(path))
            {
                // Console.WriteLine("File '{0}'.", path);
                // Printer.PrintLine(ConsoleColor.Green, $"File: {path}");
                ret = $"{ConsoleColor.Green}╠File:║ {path}";
            }
            else if (Directory.Exists(path))
            {
                // Console.WriteLine("Dir'{0}'.", path);
                // Printer.PrintLine(ConsoleColor.Blue, $"Dir: {path}");
                ret = $"{ConsoleColor.Blue}╠Dir:║ {path}";
            }
            else
            {
                // Console.WriteLine("???'{0}'.", path);
                // Printer.PrintLine(ConsoleColor.Red, $"???: {path}");
                ret = $"{ConsoleColor.Red}╠???:║ {path}";
            }
            return ret;
        }
        public static string[] SterelizePathFromUri(Uri uri, Uri uriOrigin)
        {
            return uri.AbsolutePath.Replace(uriOrigin.AbsolutePath, "").Replace("%20", " ").Split('/');
        }
    }
}