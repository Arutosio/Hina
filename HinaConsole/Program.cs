using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using HinaLib;
using HinaLib.Entities;
using HinaLib.Entities.Tree;

namespace HinaConsole
{
    class Program
    {
        private static Master master = new(ConsolePrint, ConsoleInput);

        static void ConsolePrint(string msg, bool EndWithNewLine = false)
        {
            if (EndWithNewLine == false)
            {
                Console.Write(msg);
            }
            else
            {
                Console.WriteLine(msg);
            }
        }

        static string ConsoleInput(bool EndWithNewLine = false)
        {
            if (EndWithNewLine == false)
            {
                return "SINGLE LINEEEE"; //Console.Read();
            }
            else
            {
                return Console.ReadLine();
            }
        }

        static void Main(string[] args)
        {
            Kekka kekka;
            //string haJson = FileManager.ReadTextFile(@"D:\User\GitHub\Hina\contexts\Ha.json");
            //string buranchiJson = FileManager.ReadTextFile(@"D:\User\GitHub\Hina\contexts\Buranchi.json");
            //string torankuInfoJson = FileManager.ReadTextFile(@"D:\User\GitHub\Hina\contexts\TorankuInfo.json");
            //Ha ha = JsonParse.DeserializeHa(haJson);
            //Buranchi buranchi = JsonParse.DeserializeBuranchi(buranchiJson);
            //Toranku toranku = JsonParse.DeserializeToranku(torankuInfoJson);

            if (args.Any())
            {
                kekka = RunCommand(args);
                Console.WriteLine($"{kekka.Status.ToString()}: {kekka.Message}");
            }
            else
            {
                HeadInfo();
                bool isContinue = true;
                do
                {
                    string commandLine = string.Empty;
                    Console.Write("Run Command: ");
                    commandLine = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(commandLine))
                    {
                        if (commandLine.ToUpper().Equals("EXIT"))
                        {
                            isContinue = false;
                        }
                        else
                        {
                            kekka = RunCommand(commandLine.Split(" "));
                            Console.WriteLine($"{kekka.Status.ToString()}: {kekka.Message}");
                            //Printer.PrintLine(ConsoleColor.DarkYellow, $"Command \"{string.Join(" ", command)}\" not found.");
                        }
                    }
                } while (isContinue);
            }



            // master.Init(@"D:\User\Heroku", true);
            // string dirPath = @"C:\Users\stefa\Desktop\HinaTestProgram";

            // DirectoryInfo dirInfo = new(dirPath);
            // Console.WriteLine(dirInfo.Name);
            // Console.WriteLine(Path.GetDirectoryName(dirPath));
            //Kekka kekka = master.Init(dirPath, true);

            // foreach (var item in Downloader.GetFilesList())
            // {
            //     Console.WriteLine(item);
            // }
            // foreach (Toranku r in GetRepositoryInfos())
            // {
            //     IList<string> files = FileManager.GetFilesWithSub(r.PathLocal);
            //     List<Uri> fileUri = GenUrisFiles(r.Origin);
            //     foreach (Uri uri in fileUri)
            //     {
            //         //Uri uri = new Uri("https://raw.githubusercontent.com/Arutosio/Jira-Groovy-Script/master/Add%20~%20%20LOG%20Message%20in%20the%20Validator_Post-Function_Conditions.groovy");
            //         string[] filePathInside = FileManager.SterelizePathFromUri(uri, r.UriOrigin);
            //         string savePath = Path.Combine(r.PathLocal, Path.Combine(filePathInside));
            //         try
            //         {
            //             string fileToPrint = FileManager.PrintType(savePath);

            //             Printer.PrintStringPatter(fileToPrint);
            //             Printer.PrintLine(ConsoleColor.Cyan, uri.ToString());
            //             Printer.PrintLine(ConsoleColor.Green, savePath);
            //             //client.DownloadFile(r.Origin, r.Name);
            //         }
            //         catch (Exception ex)
            //         {
            //             Printer.PrintLine(ConsoleColor.Red, ex.Message);
            //             // Environment.Exit(0);
            //         }
            //         //break;
            //     }
            // }

            //Console.ReadKey();

            // FileVersionInfo myFileVersionInfo = FileVersionInfo.GetVersionInfo(file);
            // // Print the file name and version number.
            // Console.WriteLine("File: " + myFileVersionInfo.FileDescription + '\n' +
            // "Version number: " + myFileVersionInfo.FileVersion);
        }

        public static void HeadInfo()
        {
            Printer.PrintLine("Program is developed by ", ConsoleColor.Magenta, "@Arutosio");
            Printer.PrintLine("Open Source: ", ConsoleColor.Cyan, "https://github.com/Arutosio/Hina\n");
        }

        private static Kekka RunCommand(string[] command)
        {
            Kekka retKekka = new();
            switch (command[0].ToUpper())
            {
                case "DIR":
                    Console.WriteLine($"Command Dir {Directory.GetCurrentDirectory()}");
                    break;
                case "CHECK":
                    switch (command.Length)
                    {
                        case 1:
                            retKekka = master.Check(Directory.GetCurrentDirectory());
                            break;
                        case 2:
                            if (command[1].ToUpper().Equals("deep") )
                            {
                                retKekka = master.Check(Directory.GetCurrentDirectory(), true);
                            }
                            else
                            {
                                retKekka = master.Check(command[1]);
                            }
                            break;
                        case 3:
                            if (command[2].ToUpper().Equals("DEEP"))
                            {
                                retKekka = master.Check(command[1], true);
                            }
                            break;
                    }
                    break;
                case "DUPE":
                    switch (command.Length)
                    {
                        case 2:
                            if (Uri.IsWellFormedUriString(command[1], UriKind.Absolute))
                            {
                                retKekka =  master.Dupe(Directory.GetCurrentDirectory(), new Uri(command[1]));
                            }
                            break;
                        case 3:
                            if (Uri.IsWellFormedUriString(command[1], UriKind.Absolute) && (command[2].ToUpper().Equals("TRUE") || command[2].ToUpper().Equals("FALSE")))
                            {
                                retKekka =  master.Dupe(Directory.GetCurrentDirectory(), new Uri(command[1]), Convert.ToBoolean(command[2]));
                            }
                            break;
                    }
                    break;
                case "UPDATE":
                    switch (command.Length)
                    {
                        case 1:
                            retKekka = master.Update(Directory.GetCurrentDirectory());
                            break;
                        case 2:
                            retKekka = master.Update(command[1], true);
                            break;
                    }
                    break;
                case "INIT":
                    switch (command.Length)
                    {
                        case 1:
                            retKekka = master.Init(Directory.GetCurrentDirectory());
                            break;
                        case 2:
                            retKekka = master.Init(command[1]);
                            break;
                        case 3:
                            retKekka = master.Init(command[1], Convert.ToBoolean(command[2]));
                            break;
                    }
                    break;
                default:
                    retKekka.Status = Status.Unknow;
                    retKekka.Message = $"Command \"{string.Join(" ", command)}\" not found.";
                    break;
            }
            return retKekka;
        }


        // public static Toranku[] GetRepositoryInfos()
        // {
        //     //Json Test
        //     string myJson = File.ReadAllText("../Repositorys.json");
        //     Toranku[] ret = JsonParse.DeserializeCollectionRepositoryInfo(myJson);
        //     foreach (Toranku r in ret)
        //     {
        //         Console.WriteLine($"\n\r{r.Name}");
        //     }
        //     return ret;
        // }

        public static List<Uri> GenUrisFiles(string origin)
        {
            List<Uri> urisFiles = new();
            urisFiles.Add(new Uri("https://github.com/Arutosio/Hina"));
            urisFiles.Add(new Uri("https://github.com/Arutosio/Miru-Naibu"));
            return urisFiles;
        }

        //public static Type GetType(string value)
        //{
        //    Type type = null;
        //    switch (value)
        //    {
        //        case :
        //            break;
        //    }
        //    return type
        //}
    }
}
