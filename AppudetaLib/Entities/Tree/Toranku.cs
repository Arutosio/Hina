using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppudetaLib.Entities.Tree
{
    public class Toranku
    {
        public static string NameAppudetaFolder(Toranku toranku)
        {
            // return Path.Combine(toranku.DirPath, @".Appudeta");
            return toranku.DirPath;
        }

        [JsonIgnore]
        public DirectoryInfo DirInfo { get; private set; }

        [JsonPropertyOrder(1)]
        public string DirPath { get { return DirInfo.FullName; } private set { } }

        [JsonPropertyOrder(2)]
        public string Name { get; private set; }

        [JsonPropertyOrder(3)]
        public Ruto Ruto { get; set; }

        [JsonPropertyOrder(4)]
        public Buranchi Buranchi { get; private set; }

        public Toranku(string dirPath)
        {
            DirInfo = new(dirPath);
            Name = DirInfo.Name;
            Ruto = new();
            Buranchi = new(DirInfo.Name);
        }
        public Toranku(string dirPath, Ruto ruto)
        {
            DirInfo = new(dirPath);
            Name = DirInfo.Name;
            Ruto = ruto;
            Buranchi = new(DirInfo.Name);
        }
        public Toranku(string dirPath, string name, Ruto ruto, Buranchi buranchi)
        {
            DirInfo = new(dirPath);
            Name = name;
            Ruto = ruto;
            Buranchi = buranchi;
        }

        public string PathMainDir()
        {
            return Path.Combine(DirInfo.FullName, Ruto.GetTorankuMainFolder());
        }
    }
}