using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Appudeta.Utilities
{
    public static class FileManager
    {
        public static IList<string> GetAll(string targetDirectory)
        {
            List<string> ret = new();
            IList<string> dirs = GetDirectorys(targetDirectory);
            IList<string> files = GetAllFiles(targetDirectory);
            ret.AddRange(dirs);
            ret.AddRange(files);

            return ret;
        }

        public static IList<string> GetAllFiles(string targetDirectory)
        {
            // Get the list of files found in the directory.
            IList<string> files = Directory.GetFiles(targetDirectory).ToList();
            foreach (string fileName in files)
            {
                PrintType(fileName);
            }
            return files;
        }

        public static IList<string> GetDirectorys(string targetDirectory)
        {
            // Recurse into subdirectories of this directory.
            IList<string> subDirectoryEntries = Directory.GetDirectories(targetDirectory);
            return subDirectoryEntries;
        }

        public static IList<string> GetAllSubDirectory(string directory)
        {
            List<string> directorys = new();
            foreach (string subDirectory in GetDirectorys(directory))
            {
                directorys.AddRange(GetAllSubDirectory(subDirectory));
            }
            return directorys;
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
    }
}