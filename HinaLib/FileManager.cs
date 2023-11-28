using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;

namespace HinaLib
{
    public static class FileManager
    {
    //    public delegate void ProgressChangedHandler(int percent);
    //    public static event ProgressChangedHandler ProgressChanged;

        private static async Task SaveFile(Stream stream, string fileName, long totalBytes, int bufferSize)
        {
            long totalBytesRead = 0;

            using (FileStream fileStream = System.IO.File.Create(fileName))
            {
                byte[] buffer = new byte[bufferSize];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    //ProgressChanged?.Invoke((int)(100 * totalBytesRead / totalBytes));
                }
            }
        }

        // Convert Methods
        public static string ConvertStreamToString(Stream stream)
        {
            // convert stream to string
            StreamReader reader = new StreamReader(stream);
            string text = reader.ReadToEnd();

            return text;
        }

        public static Stream ConvertStringToStream(string str)
        {
            // convert string to stream
            byte[] byteArray = Encoding.ASCII.GetBytes(str);
            MemoryStream stream = new MemoryStream(byteArray);
            
            return stream;
        }

        public static bool CheckFileExist(string pathFile)
        {
            return File.Exists(pathFile);
        }

        public static void DeleteFile(string pathFile)
        {
            File.Delete(pathFile);
        }

        public static string ReadTextFile(string pathFile)
        {
            // StreamReader r = new StreamReader(pathFile);
            // string jsonString = r.ReadToEnd();
            // return jsonString;
            // return File.ReadAllText(pathFile);
            string[] lines = File.ReadAllLines(pathFile);
            return string.Concat(lines);
        }

        public static string GetNameDirectory(string dir)
        {
            return new DirectoryInfo(dir).Name;
        }

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

        public static List<string> GetRelativePaths(string baseDirectory, IList<string> absolutePaths)
        {
            List<string> ret = new List<string>();

            foreach(string absolutePath in absolutePaths)
            {
                ret.Add(GetRelativePath(baseDirectory, absolutePath));
            }

            return ret;
        }

        public static string GetRelativePath(string baseDirectory, string absolutePath)
        {
            Uri absoluteUri = new Uri(absolutePath);
            Uri baseUri = new Uri(baseDirectory + Path.DirectorySeparatorChar);

            // Calcola la differenza tra i percorsi
            Uri relativeUri = baseUri.MakeRelativeUri(absoluteUri);

            // Converti l'URI relativo in una stringa
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }

        internal static void MakeDirectory(string dirPath, bool force = false)
        {
            if (force || !Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }
            else
            {
                throw new Exception("The directory already exist.");
            }
        }

        internal static void MakeDirectoryIfNotExist(string dirPath)
        {
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }
        }

        internal static void MakeFile(Stream stream, string filePath, bool force = false)
        {
            if (force || !File.Exists(filePath))
            {
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }
            else
            {
                throw new Exception("The file already exist.");
            }
        }

        internal static void MakeFile(string filePath, string content, bool force = false)
        {
            if (force || !File.Exists(filePath))
            {
                // System.IO.File.WriteAllText(filePath, content);
                // Create the file, or overwrite if the file exists.
                using (FileStream fs = File.Create(filePath))
                {
                    byte[] info = new UTF8Encoding(true).GetBytes(content);
                    // Add some information to the file.
                    fs.Write(info, 0, info.Length);
                }
            }
            else
            {
                throw new Exception("The file already exist.");
            }
        }

        public static List<string> GetFilesWithSub(string targetDirectory, string? excludedFileName = null)
        {
            List<string> retFiles;
            List<string> allFiles = Directory.GetFiles(targetDirectory, "*.*", SearchOption.AllDirectories).ToList();

            // Filtra i file escludendo quelli con il nome specificato
            if (allFiles.Count > 0 && excludedFileName != null)
            {
                List<string> filteredFiles = allFiles
                    .Where(file => !Path.GetFileName(file).Equals(excludedFileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                retFiles = filteredFiles;
            }
            else
            {
                retFiles = allFiles;
            }
            return retFiles;
        }

        public static string CalcolaMD5Hash(FileInfo fileInfo)
        {
            using (MD5 md5 = MD5.Create())
            {
                using (FileStream stream = fileInfo.OpenRead())
                {
                    // Calcola l'hash MD5 del file
                    byte[] hashBytes = md5.ComputeHash(stream);

                    // Converte l'array di byte in una stringa esadecimale
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                }
            }
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