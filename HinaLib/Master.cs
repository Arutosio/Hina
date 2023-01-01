using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using HinaLib.Entities;
using HinaLib.Entities.Tree;

namespace HinaLib
{

    public class Master
    {
        public static readonly string mainFileName = "TorankuInfo.json";

        private Action<string, bool> PrintMessage;
        private Func<bool, string> GetInput;

        public Master(Action<string, bool> PrintMethod, Func<bool, string> InputMethod)
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
                // FileManager.MakeDirectory(Toranku.NameHinaFolder(toranku), force);
                // FileManager.MakeFile(Path.Combine(Toranku.NameHinaFolder(toranku), mainFileName), json, force);
                FileManager.MakeFile(Path.Combine(dirToranku, mainFileName), json, force);

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

        public Kekka Check(string dirPath, bool isDeep = false)
        {
            Kekka kekka = new();

            try
            {
                string pathFile = Path.Combine(dirPath, mainFileName);
                if ( FileManager.CheckFileExist(pathFile) )
                {
                    string textFile = FileManager.ReadTextFile(pathFile);
                    textFile = textFile.Replace("\\r\\n", "").Replace("\\\\", "\\");
                    Toranku torankuFromLocal = JsonParse.DeserializeToranku(textFile);

                    Stream stream = Downloader.DownloadFile(new(torankuFromLocal.Ruto.UriSosu)).Result;
                    string textFileSosu = FileManager.ConvertStreamToString(stream);
                    Toranku torankuFromSuso = JsonParse.DeserializeToranku(textFileSosu);
                    PrintMessage($"Localversion: {torankuFromLocal.version}", true);
                    if (isDeep)
                    {
                        List<string> log = new();
                        CompareSosuLocal((Buranchi)torankuFromSuso, (Buranchi)torankuFromLocal, log);
                        if (log.Count == 0)
                        {
                            kekka.Message = $"Already uptodate.";
                            kekka.Status = Status.Done;
                        }
                        else
                        {
                            kekka.Message = "There are files with major version: \r\n";
                            foreach(string logItem in log)
                            {
                                kekka.Message += logItem + "\r\n";
                            }
                            kekka.Message = $"Files with major version: {log.Count}";
                            kekka.Status = Status.Warning;
                        }
                    }
                    else if (torankuFromLocal.version < torankuFromSuso.version)
                    {
                        /*
                        PrintMessage("There is a new version on origin, do you want update? (y/n): ", false);
                        if (GetInput(true).ToUpper().Equals("Y"))
                        {
                            ruto = new(GetInput(true));
                            toranku = new(dirToranku, ruto);
                            kekka.Message = "Update Complete";
                        }
                        else
                        {
                            toranku = new(dirToranku);
                            kekka.Message = "There is a new version on origin, do you want update? (y/n)";
                        }
                        */

                        kekka.Message = $"There is a major version from origin v{torankuFromSuso.version}";
                        kekka.Status = Status.Done;
                    }
                    else if (torankuFromLocal.version == torankuFromSuso.version)
                    {
                        //PrintMessage("Already uptodate.", true);
                        kekka.Message = "Already uptodate.";
                        kekka.Status = Status.Done;
                    }
                    else
                    {
                        //PrintMessage("The local version has a higher version than the source one, it is recommended to re-download.", true);
                        kekka.Message = "The local version has a higher version than the source one, it is recommended to re-download.";
                        kekka.Status = Status.Warning;
                    }
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

        public Kekka Update(string dirPath, bool force = false)
        {
            Kekka kekka = new();

            try
            {
                string pathFile = Path.Combine(dirPath, mainFileName);
                if (FileManager.CheckFileExist(pathFile))
                {
                    // Get Local Toranku
                    string textFile = FileManager.ReadTextFile(pathFile);
                    textFile = textFile.Replace("\\r\\n", "").Replace("\\\\", "\\");
                    Toranku torankuFromLocal = JsonParse.DeserializeToranku(textFile);

                    // Get Sosu Toranku
                    Stream stream = Downloader.DownloadFile(new(torankuFromLocal.Ruto.UriSosu)).Result;
                    string textFileSosu = FileManager.ConvertStreamToString(stream);
                    Toranku torankuFromSuso = JsonParse.DeserializeToranku(textFileSosu);

                    PrintMessage($"Localversion: {torankuFromLocal.version}", true);

                    List<string> log = new();
                    string baseUri = torankuFromSuso.Ruto.UriSosu.Replace(torankuFromSuso.Ruto.GetName(), "");

                    if (torankuFromLocal.version < torankuFromSuso.version || force)
                    {
                        UpdateLocalFromSosu((Buranchi)torankuFromSuso, (Buranchi)torankuFromLocal, dirPath, baseUri, log);

                        if (log.Count == 0)
                        {
                            kekka.Message = $"Already uptodate.";
                        }
                        else
                        {
                            PrintMessage($"Files with major version: {log.Count}", true);

                            kekka.Message = $"Update complete to a major version from origin v{torankuFromSuso.version}";
                        }

                        torankuFromLocal.version = torankuFromSuso.version;
                        string json = JsonParse.SerializeToranku(torankuFromLocal);
                        FileManager.MakeFile(Path.Combine(dirPath, mainFileName), json, force);
                        PrintMessage("Done!", true);
                        
                        kekka.Status = Status.Done;
                    }
                    else if (torankuFromLocal.version == torankuFromSuso.version)
                    {
                        //PrintMessage("Already uptodate.", true);
                        kekka.Message = "Already uptodate.";
                        kekka.Status = Status.Done;
                    }
                    else
                    {
                        //PrintMessage("The local version has a higher version than the source one, it is recommended to re-download.", true);
                        kekka.Message = "The local version has a higher version than the source one, it is recommended to re-download.";
                        kekka.Status = Status.Warning;
                    }
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

        private void CompareSosuLocal(Buranchi suso, Buranchi local, List<string> log)
        {
            if (suso.Name == local.Name)
            {
                //Check Ha inside Buranchi
                if (suso.Leaves != null && suso.Leaves.Count > 0)
                {
                    if (local != null)
                    {
                        foreach (Ha sHa in suso.Leaves)
                        {
                            bool thereIs = false;
                            foreach (Ha lHa in local.Leaves)
                            {
                                if (sHa.Name.Equals(lHa.Name))
                                {
                                    thereIs = true;
                                    if (sHa.Version > lHa.Version)
                                    {
                                        string tmp = $"{lHa.Version} => {sHa.Version} {sHa.Name}";
                                        PrintMessage(tmp, true);
                                        log.Add(tmp);
                                    }
                                    else if (sHa.Version < lHa.Version)
                                    {
                                        string tmp = $"? {lHa.Version} <= {sHa.Version} {sHa.Name}";
                                        PrintMessage(tmp, true);
                                        log.Add(tmp);
                                    }
                                    break;
                                }
                            }
                            if (!thereIs)
                            {
                                string tmp = $"{sHa.Name} Missing.";
                                PrintMessage(tmp, true);
                                log.Add(tmp);
                            }
                        }
                    }
                    else
                    {
                        foreach (Ha sHa in suso.Leaves)
                        {
                            string tmp = $"{sHa.Name} Missing.";
                            PrintMessage(tmp, true);
                            log.Add(tmp);
                        }
                    }
                }

                //Check Buranchi inside
                if (suso.Branches != null)
                {
                    foreach (Buranchi sB in suso.Branches)
                    {
                        bool thereIs = false;
                        foreach (Buranchi lB in local.Branches)
                        {
                            if (sB.Name.Equals(lB.Name))
                            {
                                thereIs = true;
                                CompareSosuLocal(sB, lB, log);
                            }
                        }
                        if (!thereIs)
                        {
                            string tmp = $"Directory {suso.Name} Missing.";
                            PrintMessage(tmp, true);
                            log.Add(tmp);
                            CompareSosuLocal(sB, null, log);
                        }
                    }
                }
            }
        }

        private void UpdateLocalFromSosu(Buranchi suso, Buranchi local, string path, string uri, List<string> log)
        {
            if (suso.Name == local.Name)
            {
                //Check Ha inside Buranchi
                if (suso.Leaves != null && suso.Leaves.Count > 0)
                {
                    if (local != null)
                    {
                        foreach (Ha sHa in suso.Leaves)
                        {
                            bool thereIs = false;
                            foreach (Ha lHa in local.Leaves)
                            {
                                if (sHa.Name.Equals(lHa.Name))
                                {
                                    thereIs = true;
                                    if (sHa.Version > lHa.Version)
                                    {
                                        Stream stream = Downloader.DownloadFile(new(uri + sHa.Name)).Result;
                                        FileManager.MakeFile(stream, Path.Combine(path, sHa.Name), true);
                                        
                                        string tmp = $"Updated {lHa.Version} => {sHa.Version} {sHa.Name}";
                                        lHa.Version = sHa.Version;

                                        log.Add(tmp);
                                        PrintMessage(tmp, true);
                                    }
                                    break;
                                }
                            }
                            if (!thereIs)
                            {
                                Stream stream = Downloader.DownloadFile(new(uri + sHa.Name)).Result;
                                FileManager.MakeFile(stream, Path.Combine(path, sHa.Name), true);
                                local.Leaves.Add(sHa);

                                string tmp = $"Added {sHa.Name}";
                                log.Add(tmp);
                                PrintMessage(tmp, true);
                            }
                        }
                    }
                    else
                    {
                        foreach (Ha sHa in suso.Leaves)
                        {
                            Stream stream = Downloader.DownloadFile(new(uri + sHa.Name)).Result;
                            FileManager.MakeFile(stream, Path.Combine(path, sHa.Name), true);
                            local.Leaves.Add(sHa);

                            string tmp = $"Added {sHa.Name}";
                            log.Add(tmp);
                            PrintMessage(tmp, true);
                        }
                    }
                }

                //Check Buranchi inside
                if (suso.Branches != null)
                {
                    foreach (Buranchi sB in suso.Branches)
                    {
                        bool thereIs = false;
                        foreach (Buranchi lB in local.Branches)
                        {
                            if (sB.Name.Equals(lB.Name))
                            {
                                thereIs = true;
                                string newPath = Path.Combine(path, sB.Name);
                                string newUri = uri + sB.Name + "/";
                                UpdateLocalFromSosu(sB, lB, newPath, newUri, log);
                            }
                        }
                        if (!thereIs)
                        {
                            string newPath = Path.Combine(path, sB.Name);
                            string newUri = uri + sB.Name + "/";

                            FileManager.MakeDirectory(newPath);
                            string tmp = $"Added direcory {sB.Name}";
                            log.Add(tmp);
                            PrintMessage(tmp, true);
                            UpdateLocalFromSosu(sB, null, newPath, newUri, log);
                        }
                    }
                }
            }
        }
    }
}
