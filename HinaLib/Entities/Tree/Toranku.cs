using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HinaLib.Entities.Tree
{
    public class Toranku : Buranchi
    {
        [JsonIgnore]
        public DirectoryInfo DirInfo { get; private set; }

        [JsonPropertyOrder(1)]
        public float version { get; set; }

        [JsonPropertyOrder(2)]
        public Ruto? Ruto { get; set; }

        public Toranku() { }

        public Toranku(string dirPath)
        {
            DirInfo = new(dirPath);
            Name = DirInfo.Name;
            Branches= new();
            Leaves = new();
        }
        public Toranku(string dirPath, Ruto ruto)
        {
            DirInfo = new(dirPath);
            Name = DirInfo.Name;
            Ruto = ruto;
            Branches = new();
            Leaves = new();
        }

        public string PathMainDir()
        {
            return Path.Combine(DirInfo.FullName, Ruto.GetTorankuMainFolder());
        }

        internal List<string> GetUrisFromTorankuObj(Toranku toranku)
        {
            List<string> uris = new();

            return uris;
        }
    }
}