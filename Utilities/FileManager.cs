using System;
using System.Collections;
using System.IO;

namespace Appudeta.Utilities
{
    public static class FileManager
    {
        // Process all files in the directory passed in, recurse on any directories
        // that are found, and process the files they contain.
        public static void ProcessDirectory(string targetDirectory)
        {
            // Process the list of files found in the directory.
            string[] fileEntries = Directory.GetFiles(targetDirectory);
            foreach (string fileName in fileEntries)
                ProcessFile(fileName);

            // Recurse into subdirectories of this directory.
            string[] subdirectoryEntries = Directory.GetDirectories(targetDirectory);
            foreach (string subdirectory in subdirectoryEntries)
                ProcessDirectory(subdirectory);
        }

        // Insert logic for processing found files here.
        public static void ProcessFile(string path)
        {
            Console.WriteLine("Processed file '{0}'.", path);
        }
        public static string[] GetAllFiles(string pathLocalFolder)
        {
            // Return all file names in root directory into array.
            return Directory.GetFiles(pathLocalFolder);
        }
        public static string[] GetAllFiles(string pathLocalFolder, string exstaction)
        {
            // Put all exstaction files in root directory into array.
            // ... This is case-insensitive.
            return Directory.GetFiles(pathLocalFolder, $"*.{exstaction}");
        }
        public static void ReplaceFile(string oldFile, string newFile)
        {

        }
    }
}