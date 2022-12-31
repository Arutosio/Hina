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
        private Func<bool?, string> GetInput;

        public Master(Action<string, bool?> PrintMethod, Func<bool?, string> InputMethod)
        {
            PrintMessage = PrintMethod;
            GetInput = InputMethod;
        }

        public Kekka Init(string dirToranku, bool force = false)
        {
            Kekka kekka = new();
            DirectoryInfo dirInfo = new(dirToranku);

            try
            {
                Toranku toranku;
                Ruto ruto;
                PrintMessage("Do you wanna add a UriSosu? (y/n) : ", false);
                if (GetInput(true).ToUpper().Equals("Y"))
                {
                    PrintMessage(": ", false);
                    ruto = new(GetInput(true));
                    toranku = new(dirToranku, ruto);
                }
                else
                {
                    toranku = new(dirToranku);
                }

                PrintMessage("Beginning of the construction phase of the Toranku ... ", false);

                GetBurachis(dirToranku, (Buranchi)toranku);
                PrintMessage("Done!", true);
                PrintMessage("Beginning of the serialize phase of the Toranku JSON ... ", false);
                string json = JsonParse.SerializeToranku(toranku);
                PrintMessage("Done!", true);
                // create dir and files
                // FileManager.MakeDirectory(Toranku.NameAppudetaFolder(toranku), force);
                // FileManager.MakeFile(Path.Combine(Toranku.NameAppudetaFolder(toranku), "TorankuInfo.json"), json, force);
                FileManager.MakeFile(Path.Combine(dirToranku, "TorankuInfo.json"), json, force);

                kekka.Status = Status.Done;
                kekka.Message = "Init complete.";
            }
            catch (Exception ex)
            {
                kekka.Status = Status.Error;
                kekka.Message = ex.Message;
            }

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

                Stream stream = Downloader.DownloadFile(uriToranku).Result;
                PrintMessage("Stream complete.", true);

                FileManager.MakeFile(stream, pathTonkaruFile, force);
                PrintMessage("Saved!", true);
                
                string textFile = FileManager.ReadTextFile(pathTonkaruFile);
                textFile = textFile.Replace("\\r\\n", "").Replace("\\\\", "\\");
                PrintMessage(textFile, true);
                Toranku toranku = JsonParse.DeserializeToranku(textFile);

                string baseUri = toranku.Ruto.GetTorankuBaseUri();
                Console.WriteLine(baseUri);
                foreach (string str in toranku.Michi())
                {
                    Uri uriFile = new( (baseUri + str).Replace("\\", "/") );
                    PrintMessage(uriFile.ToString(), true);
                    string pathFile = Path.Combine(pathTochi, str);
                    Stream streamFile = Downloader.DownloadFile(uriFile).Result;

                    FileManager.MakeDirectory(pathFile.Replace(Path.GetFileName(pathFile), ""), force);
                    FileManager.MakeFile(streamFile, pathFile, force);
                    PrintMessage("Saved!", true);
                }

                kekka.Status = Status.Done;
                kekka.Message = "Dupe complete.";
            }
            catch (Exception ex)
            {
                kekka.Status = Status.Error;
                kekka.Message = ex.Message;
            }

            return kekka;
        }

        public Kekka Check(string dirPath)
        {
            Kekka kekka = new();

            try
            {
                if ( !FileManager.CheckFileExist(dirPath) )
                {
                    string textFile = FileManager.ReadTextFile(Path.Combine(dirPath,"TorankuInfo.json"));
                    textFile = textFile.Replace("\\r\\n", "").Replace("\\\\", "\\");
                    PrintMessage(textFile, true);
                    Toranku toranku = JsonParse.DeserializeToranku(textFile);


                    kekka.Message = "Check complete.";
                    kekka.Status = Status.Done;
                }
                else
                {
                    PrintMessage("The file TorankuInfo.json doesn't exist.", true);
                    kekka.Message = "Finish.";
                    kekka.Status = Status.Warning;
                }

            }
            catch (Exception ex)
            {
                kekka.Status = Status.Error;
                kekka.Message = ex.Message;
            }

            return kekka;
        }

        public (Status, string) Update(string dirPath, bool force = false)
        {
            Status status = Status.Unknow;
            string message = string.Empty;

            return (status, message);
        }

        //Utilitis Method
        //private Ruto CreateKi(string dirPath)
        //{
        //    List<string> files = FileManager.GetFilesWithSub(dirPath) as List<string>;
        //    Ruto ruto = new(dirPath);
        //    return ruto;
        //}

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
