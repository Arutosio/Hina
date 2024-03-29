﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection.Metadata.Ecma335;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HinaLib.Entities;
using HinaLib.Entities.Tree;
using HinaLib.Entities.Vitis;

namespace HinaLib
{
    public class Master
    {
        public static readonly string mainFileName = ".HinaInfo.json"; //"TorankuInfo.json";

        private readonly Action<string, bool> PrintMessage;
        private readonly Func<bool, string> GetInput;
        private readonly Action<int> SendProgressBar;

        public Master(Action<string, bool> PrintMethod, Func<bool, string> InputMethod, Action<int> ProgressProcess)
        {
            PrintMessage = PrintMethod;
            GetInput = InputMethod;
            SendProgressBar = ProgressProcess;
            Internet.SendProgressBar = ProgressProcess;
            //Downloader.ProgressChanged += SendProgressBar;
        }

        public Kekka Init(string dirGrappolo, string? strUri = null, bool force = false)
        {
            Kekka kekka = new();

            if (Path.Exists(dirGrappolo))
            {
                DirectoryInfo dirInfo = new(dirGrappolo);
                string hinaFilePath = Path.Combine(dirGrappolo, mainFileName);

                PrintMessage(dirInfo.Name, true);
                try
                {
                    // Eliminazione del vecchio Grappolo se esiste
                    if (FileManager.CheckFileExist(hinaFilePath))
                    {
                        FileManager.DeleteFile(hinaFilePath);
                    }

                    // Creazione Grappolo
                    PrintMessage("Beginning of the construction phase of the Grappolo ... ", false);
                    Ruto ruto;
                    Grappolo initGrappolo;
                    List<string> fullFileNames = FileManager.GetFilesWithSub(dirInfo.FullName);
                    List<string> relativeFileNames = FileManager.GetRelativePaths(dirGrappolo, fullFileNames);
                    //List<Acino> acinoList = Acino.CreateNewListAcino(relativeFileNames);


                    List<Acino> acinoList = new();
                    int count = 0;
                    foreach (string fullFilePath in fullFileNames)
                    {
                        count++;
                        string relativeFilePath = FileManager.GetRelativePath(dirGrappolo, fullFilePath);
                        FileInfo fileInfo = new(fullFilePath);
                        DateTime creationTime = fileInfo.CreationTime;
                        string mD5Hash = FileManager.CalcolaMD5Hash(fileInfo);

                        Acino newAcino = new(relativeFilePath, 0, creationTime, mD5Hash);
                        acinoList.Add(newAcino);

                        SendProgressBar?.Invoke((int)(100 * count / fullFileNames.Count));
                    }

                    if (strUri != null && Internet.IsUrlValid(strUri))
                    {
                        ruto = new(strUri);
                        initGrappolo = new(dirInfo.Name, ruto, acinoList);
                    }
                    else
                    {
                        initGrappolo = new(dirInfo.Name, acinoList);
                    }

                    PrintMessage(" Done!", true);
                    PrintMessage("Beginning of the serialize phase of the Toranku JSON ... ", false);
                    string jsonG = JsonParse.SerializeGrappolo(initGrappolo);
                    // create dir and files
                    FileManager.MakeFile(hinaFilePath, jsonG, force);
                    PrintMessage("Done!", true);

                    kekka.Status = Status.Done;
                    kekka.Message = "Init complete.";

                    #region Toranku Init
                    /* Toranku Init ***********************************************************************************

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
                    string jsonT = JsonParse.SerializeToranku(toranku);
                    PrintMessage("Done!", true);
                    // create dir and files
                    // FileManager.MakeDirectory(Toranku.NameHinaFolder(toranku), force);
                    // FileManager.MakeFile(Path.Combine(Toranku.NameHinaFolder(toranku), mainFileName), json, force);
                    FileManager.MakeFile(Path.Combine(dirToranku, mainFileName), jsonT, force);

                    kekka.Status = Status.Done;
                    kekka.Message = "Init complete.";
                    */
                    #endregion Toranku Init
                }
                catch (Exception ex)
                {
                    kekka.Status = Status.Error;
                    kekka.Message = ex.Message;
                }
            }
            else
            {
                kekka.Status = Status.Error;
                kekka.Message = "The directory does not exist.";
            }
            return kekka;
        }

        public Kekka HUpdate(string dirGrappolo, string? strUri = null, bool force = false)
        {
            Kekka kekka = new();

            try
            {
                bool wasAChangeds = false;

                // Update Grappolo
                DirectoryInfo grappoloDirInfo = new(dirGrappolo);

                if (grappoloDirInfo.Exists)
                {
                    string hinaFilePath = GetFullPathHinaFile(grappoloDirInfo);

                    if(FileManager.CheckFileExist(hinaFilePath))
                    {
                        Grappolo localGrappolo = GetGrappoloFromLocal(grappoloDirInfo);

                        if(localGrappolo.Acinos != null && localGrappolo.Acinos.Count > 0)
                        {
                            List<string> fullPathFileNames = FileManager.GetFilesWithSub(grappoloDirInfo.FullName, mainFileName);
                            List<string> relativeFileNames = FileManager.GetRelativePaths(grappoloDirInfo.FullName, fullPathFileNames);

                            // Rimozione dei file non piu esistendi nella directory al grappolo
                            PrintMessage("Beginning of cleaning the Grappolo phase for files that are no longer present ... ", false);
                            List<Acino> acinosToRemove = new();

                            foreach (Acino acino in localGrappolo.Acinos)
                            {
                                bool exist = false;
                                foreach (string relativeFilePath in relativeFileNames)
                                {
                                    if (acino.FullName.Equals(relativeFilePath))
                                    {
                                        exist = true;
                                        break;
                                    }
                                }
                                if (!exist)
                                {
                                    acinosToRemove.Add(acino);
                                }
                            }
                            // Eliminazione
                            if (acinosToRemove.Count > 0)
                            {
                                foreach (Acino acino in acinosToRemove)
                                {
                                    localGrappolo.Acinos.Remove(acino);
                                }
                                wasAChangeds = true;
                            }
                            PrintMessage("Done! ", true);

                            PrintMessage("Beginning of the Auto update phase for previously existing files ... ", false);
                            List<Acino> newAcinos = new();
                            foreach (string fullFilePath in fullPathFileNames)
                            {
                                if (File.Exists(fullFilePath))
                                {
                                    string relativeFilePath = FileManager.GetRelativePath(dirGrappolo, fullFilePath);
                                    FileInfo fileInfo = new(fullFilePath);
                                    DateTime creationTime = fileInfo.CreationTime;
                                    string mD5Hash = FileManager.CalcolaMD5Hash(fileInfo);

                                    bool isThere = false;
                                    foreach (Acino acino in localGrappolo.Acinos)
                                    {
                                        if (acino.FullName.Equals(relativeFilePath))
                                        {
                                            isThere = true;
                                            // Update del acino che esiosteva gia prima
                                            acino.Version++;
                                            acino.LastUpdate = creationTime;
                                            acino.MD5Hash = mD5Hash;
                                            wasAChangeds = true;
                                            break;
                                        }
                                    }
                                    if (!isThere)
                                    {
                                        Acino newAcino = new Acino(relativeFilePath, 0, creationTime, mD5Hash);
                                        newAcinos.Add(newAcino);
                                    }
                                }
                            }
                            PrintMessage("Done!", true);

                            if (newAcinos.Count > 0)
                            {
                                PrintMessage("Adding new files to local Grappolo ... ", false);
                                localGrappolo.Acinos.AddRange(newAcinos);
                                PrintMessage("Done!", true);
                            }

                            if (wasAChangeds)
                            {
                                PrintMessage("Beginning of the serialize phase of the local Grappolo JSON ... ", false);
                                localGrappolo.Version++;
                                string jsonG = JsonParse.SerializeGrappolo(localGrappolo);
                                // create dir and files
                                FileManager.MakeFile(hinaFilePath, jsonG, true);
                                PrintMessage("Done!", true);

                                kekka.Status = Status.Done;
                                kekka.Message = "The Grappolo has been upgraded.";
                            }
                            else
                            {
                                kekka.Status = Status.Done;
                                kekka.Message = "There have been no changes.";
                            }
                        }
                        else
                        {
                            kekka.Status = Status.Warning;
                            kekka.Message = "There are no Acinos in the Grappolo.";
                        }
                    }
                    else
                    {
                        kekka.Status = Status.Error;
                        kekka.Message = $"The \"{mainFileName}\" file doesn't exist.";
                    }
                }
            }
            catch (Exception ex)
            {
                kekka.Status = Status.Error;
                kekka.Message = ex.Message;
            }

            return kekka;
        }

        public async Task<Kekka> Dupe(string dirGrappolo, Uri uriHinaInfo, bool force = false)
        {
            Kekka kekka = new();

            try
            {
                // Dupe Grappolo
                string nameMainFolder = Ruto.GetTorankuMainFolder(uriHinaInfo);
                string pathMainFolder = Path.Combine(dirGrappolo, nameMainFolder);
                string pathTonkaruFile = Path.Combine(pathMainFolder, Ruto.GetName(uriHinaInfo));

                DirectoryInfo directoryInfo = new(pathMainFolder);

                if (force || !directoryInfo.Exists)
                {
                    string hinaFilePath = Path.Combine(directoryInfo.FullName, mainFileName);

                    Grappolo sosuGrappolo = GetGrappoloFromUri(uriHinaInfo);

                    if (sosuGrappolo.Ruto == null)
                    {
                        sosuGrappolo.Ruto = new(uriHinaInfo);
                    }

                    // Download dei files
                    await DownloadAcinosFilesAsync(directoryInfo, sosuGrappolo);

                    // Aggiorno il Grappolo locale con quello sosu
                    string jsonG = JsonParse.SerializeGrappolo(sosuGrappolo);
                    FileManager.MakeFile(hinaFilePath, jsonG, true);
                    PrintMessage($"Grappolo was saved.", true);

                    // Check files integrity
                    await IntegrityPhaseAsync(directoryInfo, sosuGrappolo);
                    PrintMessage("Integrity check complete", true);
                }
                else
                {
                    kekka.Message = $"Directory \"{directoryInfo.FullName}\" doesn't exist.";
                    kekka.Status = Status.Error;
                }


                #region Toranku Dupe
                /* Toranku Dupe ***********************************************************************************
                string nameMainFolder = Ruto.GetTorankuMainFolder(uriToranku);
                string pathMainFolder = Path.Combine(pathTochi, nameMainFolder);
                string pathTonkaruFile = Path.Combine(pathMainFolder, Ruto.GetName(uriToranku));

                FileManager.MakeDirectory(pathMainFolder, force);
                PrintMessage("Beginning of the download phase of the Toranku json ... ", false);

                await Internet.DownloadAndSaveFile(uriToranku, pathTonkaruFile);

                //(Stream stream, long totalBytes, int bufferSize) result = Downloader.DownloadFile(uriToranku).Result;
                //FileManager.SaveFile(result.stream, pathTonkaruFile, result.totalBytes, result.bufferSize);

                //Stream stream = Downloader.DownloadFile(uriToranku).Result;
                //PrintMessage("Stream complete.", true);

                //FileManager.MakeFile(stream, pathTonkaruFile, force);

                PrintMessage("Saved!", true);

                string textFile = FileManager.ReadTextFile(pathTonkaruFile);
                textFile = textFile.Replace("\\r\\n", "").Replace("\\\\", "\\");
                PrintMessage(textFile, true);
                Toranku toranku = JsonParse.DeserializeToranku(textFile);

                string baseUri = toranku.Ruto.GetTorankuBaseUri();
                Console.WriteLine(baseUri);
                foreach (string str in toranku.Michi())
                {
                    Uri uriFile = new((baseUri + str).Replace("\\", "/"));
                    PrintMessage(uriFile.ToString(), true);
                    string pathFile = Path.Combine(pathTochi, str);
                    Stream streamFile = Internet.DownloadFile(uriFile).Result;

                    FileManager.MakeDirectory(pathFile.Replace(Path.GetFileName(pathFile), ""), force);
                    FileManager.MakeFile(streamFile, pathFile, force);
                    PrintMessage("Saved!", true);
                }
                */
                #endregion Toranku Dupe

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

        public Kekka Check(string dirGrappolo, bool isDeep = false)
        {
            Kekka kekka = new();

            try
            {
                DirectoryInfo grappoloDirInfo = new(dirGrappolo);

                if (grappoloDirInfo.Exists)
                {
                    string hinaFilePath = GetFullPathHinaFile(grappoloDirInfo);

                    if (FileManager.CheckFileExist(hinaFilePath))
                    {
                        //Load local Grappolo
                        Grappolo localGrappolo = GetGrappoloFromLocal(grappoloDirInfo);
                        if (localGrappolo.Acinos != null)
                        {
                            if (localGrappolo.Ruto != null)
                            {
                                Grappolo sosuGrappolo = GetGrappoloFromUri(new(localGrappolo.Ruto.UriSosu));

                                if (sosuGrappolo.Acinos != null)
                                {
                                    if (isDeep || localGrappolo.Version < sosuGrappolo.Version)
                                    {
                                        // Confronta le versioni e scarica la versione nuova e i nuovi file.
                                        foreach (Acino sosuAcino in sosuGrappolo.Acinos)
                                        {
                                            bool isFound = false;
                                            foreach (Acino localAcino in localGrappolo.Acinos)
                                            {
                                                if (sosuAcino.FullName.Equals(localAcino.FullName))
                                                {
                                                    isFound = true;
                                                    if (sosuAcino.Version > localAcino.Version)
                                                    {
                                                        PrintMessage($"{sosuAcino.FullName} {localAcino.Version} -> {sosuAcino.Version}", true);
                                                        break;
                                                    }
                                                    break;
                                                }
                                            }
                                            if (!isFound)
                                            {
                                                PrintMessage($"{sosuAcino.FullName} NewFile = {sosuAcino.Version}", true);
                                            }
                                        }

                                        //kekka.Message = "There are files with major version: \r\n";
                                        //foreach (string logItem in log)
                                        //{
                                        //    kekka.Message += logItem + "\r\n";
                                        //}
                                        //kekka.Message = $"Files with major version: {log.Count}";
                                        kekka.Status = Status.Done;
                                        kekka.Message = "There is a new version.";
                                    }
                                    else
                                    {
                                        //PrintMessage("Already uptodate.", true);
                                        kekka.Status = Status.Done;
                                        kekka.Message = "Already uptodate.";
                                    }
                                }
                                else
                                {
                                    kekka.Status = Status.Warning;
                                    kekka.Message = "There are no Acinos in the online Grappolo.";
                                }
                            }
                            else
                            {
                                kekka.Status = Status.Error;
                                kekka.Message = $"The Hina file does not contain a Ruto configuration inside.";
                            }
                        }
                        else
                        {
                            kekka.Status = Status.Warning;
                            kekka.Message = "There are no Acinos in the local Grappolo.";
                        }
                    }
                    else
                    {
                        kekka.Status = Status.Error;
                        kekka.Message = $"The \"{mainFileName}\" file doesn't exist.";
                    }
                }
                else
                {
                    kekka.Status = Status.Error;
                    kekka.Message = $"Directory \"{grappoloDirInfo.FullName}\" doesn't exist.";
                }

                #region Toranku Check
                /* Toranku Check ***********************************************************************************
                string pathFile = Path.Combine(dirPath, mainFileName);
                if (FileManager.CheckFileExist(pathFile))
                {
                    string textFile = FileManager.ReadTextFile(pathFile);
                    textFile = textFile.Replace("\\r\\n", "").Replace("\\\\", "\\");
                    Toranku torankuFromLocal = JsonParse.DeserializeToranku(textFile);

                    Stream stream = Internet.DownloadFile(new(torankuFromLocal.Ruto.UriSosu)).Result;
                    string textFileSosu = FileManager.ConvertStreamToString(stream);
                    Toranku torankuFromSuso = JsonParse.DeserializeToranku(textFileSosu);
                    PrintMessage($"Localversion: {torankuFromLocal.Version}", true);
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
                            foreach (string logItem in log)
                            {
                                kekka.Message += logItem + "\r\n";
                            }
                            kekka.Message = $"Files with major version: {log.Count}";
                            kekka.Status = Status.Warning;
                        }
                    }
                    else if (torankuFromLocal.Version < torankuFromSuso.Version)
                    {

                        //PrintMessage("There is a new version on origin, do you want update? (y/n): ", false);
                        //if (GetInput(true).ToUpper().Equals("Y"))
                        //{
                        //    ruto = new(GetInput(true));
                        //    toranku = new(dirToranku, ruto);
                        //    kekka.Message = "Update Complete";
                        //}
                        //else
                        //{
                        //    toranku = new(dirToranku);
                        //    kekka.Message = "There is a new version on origin, do you want update? (y/n)";
                        //}


                        kekka.Message = $"There is a major version from origin v{torankuFromSuso.Version}";
                        kekka.Status = Status.Done;
                    }
                    else if (torankuFromLocal.Version == torankuFromSuso.Version)
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
                */
                #endregion Toranku Check
            }
            catch (Exception ex)
            {
                kekka.Status = Status.Error;
                kekka.Message = ex.Message;
            }

            return kekka;
        }

        public async Task<Kekka> Update(string dirGrappolo, bool force = false)
        {
            Kekka kekka = new();

            try
            {
                // Update Grappolo
                DirectoryInfo grappoloDirInfo = new(dirGrappolo);

                if (grappoloDirInfo.Exists)
                {
                    string hinaFilePath = Path.Combine(dirGrappolo, mainFileName);

                    if (FileManager.CheckFileExist(hinaFilePath))
                    {
                        Grappolo localGrappolo = GetGrappoloFromLocal(grappoloDirInfo);
                        if (localGrappolo.Acinos != null)
                        {
                            if (localGrappolo.Ruto?.UriSosu != null)
                            {
                                Grappolo sosuGrappolo = GetGrappoloFromUri(new(localGrappolo.Ruto.UriSosu));

                                if (sosuGrappolo.Acinos != null && sosuGrappolo.Ruto?.UriSosu != null)
                                {
                                    if (localGrappolo.Version < sosuGrappolo.Version)
                                    {
                                        int count = 0;
                                        // Confronta le versioni e scarica la versione nuova e i nuovi file.
                                        foreach (Acino sosuAcino in sosuGrappolo.Acinos)
                                        {
                                            bool toDownload = true;
                                            foreach (Acino localAcino in localGrappolo.Acinos)
                                            {
                                                if (sosuAcino.FullName.Equals(localAcino.FullName))
                                                {
                                                    if (sosuAcino.Version <= localAcino.Version)
                                                    {
                                                        toDownload = false;
                                                        break;
                                                    }
                                                    break;
                                                }
                                            }
                                            if (toDownload)
                                            {
                                                count++;
                                                // Download sosuAcino perche è un file nuovo non presente sul vecchio Grappolo.
                                                Uri uriFile = new(Internet.BuildUrl(sosuGrappolo.Ruto.UriSosu, sosuAcino.FullName));
                                                string filePath = Path.Combine(grappoloDirInfo.FullName, sosuAcino.FullName);

                                                PrintMessage($"{count} - Download: {sosuAcino.FullName}", false);
                                                await Internet.DownloadAndSaveFile(uriFile, filePath);
                                                PrintMessage($" --> Done!", true);
                                            }
                                        }

                                        // Aggiorno il Grappolo locale con quello sosu
                                        string jsonG = JsonParse.SerializeGrappolo(sosuGrappolo);
                                        // Override files
                                        FileManager.MakeFile(hinaFilePath, jsonG, true);
                                        PrintMessage($" Grappolo updated.", true);
                                    }
                                    else
                                    {
                                        kekka.Status = Status.Done;
                                        kekka.Message = "Already uptodate.";
                                    }
                                }
                                else
                                {
                                    kekka.Status = Status.Warning;
                                    kekka.Message = "There are no Acinos in the online Grappolo.";
                                }
                            }
                            else
                            {
                                kekka.Status = Status.Error;
                                kekka.Message = $"The Hina file does not contain a Ruto configuration inside.";
                            }
                        }
                        else
                        {
                            kekka.Status = Status.Warning;
                            kekka.Message = "There are no Acinos in the local Grappolo.";
                        }
                    }
                    else
                    {
                        kekka.Status = Status.Error;
                        kekka.Message = $"The \"{mainFileName}\" file doesn't exist.";
                    }
                }
                else
                {
                    kekka.Message = $"Directory \"{grappoloDirInfo.FullName}\" doesn't exist.";
                    kekka.Status = Status.Error;
                }

                #region Toranku Update
                /* Toranku Update ************************************************************************************************
                string pathFile = Path.Combine(dirPath, mainFileName);
                if (FileManager.CheckFileExist(pathFile))
                {
                    // Get Local Toranku
                    string textFile = FileManager.ReadTextFile(pathFile);
                    textFile = textFile.Replace("\\r\\n", "").Replace("\\\\", "\\");
                    Toranku torankuFromLocal = JsonParse.DeserializeToranku(textFile);

                    // Get Sosu Toranku
                    Stream stream = Internet.DownloadFile(new(torankuFromLocal.Ruto.UriSosu)).Result;
                    string textFileSosu = FileManager.ConvertStreamToString(stream);
                    Toranku torankuFromSuso = JsonParse.DeserializeToranku(textFileSosu);

                    PrintMessage($"Localversion: {torankuFromLocal.Version}", true);

                    List<string> log = new();
                    string baseUri = torankuFromSuso.Ruto.UriSosu.Replace(torankuFromSuso.Ruto.GetName(), "");

                    if (torankuFromLocal.Version < torankuFromSuso.Version || force)
                    {
                        UpdateLocalFromSosu((Buranchi)torankuFromSuso, (Buranchi)torankuFromLocal, dirPath, baseUri, log);

                        if (log.Count == 0)
                        {
                            kekka.Message = $"Already uptodate.";
                        }
                        else
                        {
                            PrintMessage($"Files with major version: {log.Count}", true);

                            kekka.Message = $"Update complete to a major version from origin v{torankuFromSuso.Version}";
                        }

                        torankuFromLocal.Version = torankuFromSuso.Version;
                        string json = JsonParse.SerializeToranku(torankuFromLocal);
                        FileManager.MakeFile(Path.Combine(dirPath, mainFileName), json, force);
                        PrintMessage("Done!", true);
                        
                        kekka.Status = Status.Done;
                    }
                    else if (torankuFromLocal.Version == torankuFromSuso.Version)
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
                */
                #endregion Toranku Update
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

        private async Task IntegrityPhaseAsync(DirectoryInfo directoryInfo, Grappolo grappolo)
        {
            PrintMessage("Start cheking integritive file", true);
            List<Acino> brokenAcinos = CheckAcinoIntegrity(directoryInfo, grappolo);
            if(brokenAcinos.Count > 0)
            {
                PrintMessage($"There is {brokenAcinos.Count}.", true);
            }
            else
            {
                PrintMessage($"All files are integrateve.", true);
            }
            int count = 0;
            foreach (Acino acino in brokenAcinos)
            {
                count++;
                PrintMessage($"{count}/{brokenAcinos.Count} - ReDownload: {acino.FullName} ", true);
                await DownloadAcinoFileIfCorruptedAsync(directoryInfo, grappolo, acino);
                PrintMessage($"", true);
            }
        }

        private static List<Acino> CheckAcinoIntegrity(DirectoryInfo directoryInfo, Grappolo grappolo)
        {
            // Check files integrity
            List<Acino> brokenAcinos = new();
            foreach (Acino acino in grappolo.Acinos)
            {
                string fullFilePath = Path.Combine(directoryInfo.FullName, acino.FullName);
                FileInfo fileInfo = new(fullFilePath);
                string mD5HashFile = FileManager.CalcolaMD5Hash(fileInfo);

                if(!mD5HashFile.Equals(acino.MD5Hash))
                {
                    brokenAcinos.Add(acino);
                }
            }

            return brokenAcinos;
        }

        private async Task DownloadAcinosFilesAsync(DirectoryInfo directoryInfo, Grappolo grappolo)
        {
            // Download dei files
            int count = 0;
            foreach(Acino acino in grappolo.Acinos)
            {
                count++;
                string fullFilePath = Path.Combine(directoryInfo.FullName, acino.FullName);

                // Verifica se la directory esiste, altrimenti creala
                string directory = Path.GetDirectoryName(fullFilePath);
                FileManager.MakeDirectoryIfNotExist(directory);

                Uri uriFile = new(Internet.BuildUrl(grappolo.Ruto.GetRepoUri(), acino.FullName));
                
                PrintMessage($"{count}/{grappolo.Acinos.Count} - Download: {acino.FullName} ", true);
                await Internet.DownloadAndSaveFile(uriFile, fullFilePath);
                PrintMessage("", true);
            }
        }

        private async Task DownloadAcinoFileIfCorruptedAsync(DirectoryInfo directoryInfo, Grappolo grappolo, Acino acino)
        {
            string fullFilePath = Path.Combine(directoryInfo.FullName, acino.FullName);
            FileInfo fileInfo = new(fullFilePath);
            string mD5HashFile = FileManager.CalcolaMD5Hash(fileInfo);
            PrintMessage($"MD5 Acino != File:{acino.MD5Hash} != {mD5HashFile}", true);

            if(!mD5HashFile.Equals(acino.MD5Hash))
            {
                PrintMessage("File non scaricato correttamente. Rieseguo il download", true);
                Uri uriFile = new(Internet.BuildUrl(grappolo.Ruto.GetRepoUri(), acino.FullName));
                await Internet.DownloadAndSaveFile(uriFile, fullFilePath);
            }
        }


        //**** METODI STATICI ********************************************************
        private static string GetFullPathHinaFile(DirectoryInfo grappoloDirInfo)
        {
            return Path.Combine(grappoloDirInfo.FullName, mainFileName);
        }

        private static Grappolo GetGrappoloFromLocal(DirectoryInfo grappoloDirInfo)
        {
            string hinaFilePath = Path.Combine(grappoloDirInfo.FullName, mainFileName);
            string textFileGrappolo = FileManager.ReadTextFile(hinaFilePath);
            Grappolo grappolo = JsonParse.DeserializeGrappolo(textFileGrappolo);

            return grappolo;
        }

        private static Grappolo GetGrappoloFromUri(Uri uriHinaInfo)
        {
            Stream stream = Internet.DownloadFile(uriHinaInfo).Result;
            string textFileGrappolo = FileManager.ConvertStreamToString(stream);
            Grappolo grappolo = JsonParse.DeserializeGrappolo(textFileGrappolo);

            return grappolo;
        }


        /* private void UpdateLocalFromSosu
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
                                        Stream stream = Internet.DownloadFile(new(uri + sHa.Name)).Result;
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
                                Stream stream = Internet.DownloadFile(new(uri + sHa.Name)).Result;
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
                            Stream stream = Internet.DownloadFile(new(uri + sHa.Name)).Result;
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
        */
    }
}
