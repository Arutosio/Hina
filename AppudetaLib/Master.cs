using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AppudetaLib.Entities;
using AppudetaLib.Entities.Tree;

namespace AppudetaLib
{
    public class Master
    {
        //private WebClient WebClient { get; set; }
        private Action<string, bool?> PrintMessage;

        public Master(Action<string, bool?> ConsolePrint)
        {
            PrintMessage = ConsolePrint;
            //WebClient = new();
        }

        public Kekka Init(string dirToranku, bool force = false)
        {
            Kekka kekka = new();
            kekka.Status = Status.Unknow;
            kekka.Message = string.Empty;

            DirectoryInfo dirInfo = new(dirToranku);
            try
            {
                Toranku toranku = new Toranku(dirToranku);
                PrintMessage("Beginning of the construction phase of the Toranku ... ", false);
                GetBurachis(dirToranku, toranku.Buranchi);
                PrintMessage("Done!", true);
                PrintMessage("Beginning of the serialize phase of the Toranku JSON ... ", false);
                string json = JsonParse.SerializeToranku(toranku);
                PrintMessage("Done!", true);
                // create dir and files
                FileManager.MakeDirectory(Toranku.NameAppudetaFolder(toranku), force);
                FileManager.MakeFile(Path.Combine(Toranku.NameAppudetaFolder(toranku), "TorankuInfo.json"), json, force);
            }
            catch (Exception ex)
            {
                kekka.Status = Status.Error;
                kekka.Message = ex.Message;
                return kekka;
            }
            kekka.Status = Status.Done;
            kekka.Message = "Init complete.";
            return kekka;
        }

        public Kekka Dupe(string pathTochi, Uri uriToranku, bool force = false)
        {
            Kekka kekka = new();

            try
            {
                string nameMainFolder = Ruto.GetTorankuMainFolder(uriToranku);
                string pathMainFolder = Path.Combine(pathTochi, nameMainFolder);
                string pathTonkaruFile = Path.Combine(pathMainFolder, Ruto.GetName(uriToranku));

                FileManager.MakeDirectory(pathMainFolder, force);
                PrintMessage("Beginning of the download phase of the Toranku json ... ", false);

                Stream stream = Downloader.DownloadFile(uriToranku, pathTonkaruFile).GetAwaiter().GetResult();
                using (FileStream fileStream = File.Create(pathTonkaruFile))
                {
                    stream.CopyTo(fileStream);
                }
                PrintMessage("Saved!", true);
                string textFile = FileManager.ReadTextFile(pathTonkaruFile.Replace("person.jsonld", "TorankuInfo.json"));
                textFile = textFile.Replace("\\r\\n", "").Replace("\\\\", "\\");
                Toranku toranku = JsonParse.DeserializeToranku(textFile);
                foreach (string str in toranku.Buranchi.Michi())
                {
                    Console.WriteLine(str);
                }
            }
            catch (Exception ex)
            {
                kekka.Status = Status.Error;
                kekka.Message = ex.Message;
                return kekka;
            }
            kekka.Status = Status.Done;
            kekka.Message = "Dupe complete.";
            return kekka;
        }

        public (Status, string) Update(string dirPath, bool force = false)
        {
            Status status = Status.Unknow;
            string message = string.Empty;

            return (status, message);
        }

        //Utilitis Method
        private Ruto CreateKi(string dirPath)
        {
            List<string> files = FileManager.GetFilesWithSub(dirPath) as List<string>;
            Ruto ruto = new(dirPath);
            return ruto;
        }

        private List<Ha> GetHas(string dirPath)
        {
            List<Ha> has = new();
            foreach (string item in FileManager.GetFiles(dirPath))
            {
                has.Add(new(Path.GetFileName(item)));
            }
            return has;
        }

        private void GetBurachis(string pathBuranchi, Buranchi buranchi)
        {
            if (Directory.Exists(pathBuranchi))
            {
                buranchi.Leaves = GetHas(pathBuranchi);
                foreach (string item in FileManager.GetDirectories(pathBuranchi))
                {
                    Buranchi b = new Buranchi(new DirectoryInfo(item).Name);
                    GetBurachis(item, b);
                    if (buranchi.Branches is null)
                    {
                        buranchi.Branches = new();
                    }
                    buranchi.Branches.Add(b);
                }
            }
        }
    }
}
