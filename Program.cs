using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Appudeta.AppInterface;
using Appudeta.ObjectsDefinitions;
using Appudeta.Utilities;

namespace Appudeta
{
    class Program
    {
        static void Main(string[] args)
        {
            // Title[] ts = new Title[] { new Title("Program"), new Title("App"), new Title("PROPROPROPROPRO"), new Title("") };
            // PanelInterface panelInterface = new(10, 10, '▓', "~>", ts, ConsoleColor.DarkMagenta, true, "APP LIST", "PROCESS", 20);
            // panelInterface.BuilderStrAppInterface();
            HeadInfo();
            WebClient client = new();
            foreach (RepositoryInfo r in GetRepositoryInfos())
            {
                IList<string> files = FileManager.GetFilesWithSub(r.PathLocal);
                List<Uri> fileUri = GenUrisFiles(r.Origin);
                foreach (Uri uri in fileUri)
                {
                    //Uri uri = new Uri("https://raw.githubusercontent.com/Arutosio/Jira-Groovy-Script/master/Add%20~%20%20LOG%20Message%20in%20the%20Validator_Post-Function_Conditions.groovy");
                    string[] filePathInside = FileManager.SterelizePathFromUri(uri, r.UriOrigin);
                    string savePath = Path.Combine(r.PathLocal, Path.Combine(filePathInside));
                    try
                    {
                        string fileToPrint = FileManager.PrintType(savePath);

                        Printer.PrintStringPatter(fileToPrint);
                        Printer.PrintLine(ConsoleColor.Cyan, uri.ToString());
                        Printer.PrintLine(ConsoleColor.Green, savePath);
                        //client.DownloadFile(r.Origin, r.Name);
                    }
                    catch (Exception ex)
                    {
                        Printer.PrintLine(ConsoleColor.Red, ex.Message);
                        // Environment.Exit(0);
                    }
                    //break;
                }
            }

            //Console.ReadKey();

            // FileVersionInfo myFileVersionInfo = FileVersionInfo.GetVersionInfo(file);
            // // Print the file name and version number.
            // Console.WriteLine("File: " + myFileVersionInfo.FileDescription + '\n' +
            // "Version number: " + myFileVersionInfo.FileVersion);
        }

        public static void HeadInfo()
        {
            Printer.PrintLine("Program is developed by ", ConsoleColor.Magenta, "@Arutosio");
            Printer.PrintLine("Open Source: ", ConsoleColor.Cyan, "https://github.com/Arutosio/Appudeta");
        }
        public static RepositoryInfo[] GetRepositoryInfos()
        {
            //Json Test
            string myJson = File.ReadAllText("./Repositorys.json");
            RepositoryInfo[] ret = JsonReader.Deserialize(myJson);
            foreach (RepositoryInfo r in ret)
            {
                Console.WriteLine($"\n\r{r.Name}");
            }
            return ret;
        }
        public static List<Uri> GenUrisFiles(string origin)
        {
            List<Uri> urisFiles = new();
            urisFiles.Add(new Uri("https://github.com/Arutosio/Appudeta"));
            urisFiles.Add(new Uri("https://github.com/Arutosio/Miru-Naibu"));
            return urisFiles;
        }
    }
}
