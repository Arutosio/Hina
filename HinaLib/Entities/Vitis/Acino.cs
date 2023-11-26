using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HinaLib.Entities.Vitis
{
    public class Acino
    {
        [JsonPropertyOrder(1)]
        public string FullName { get; set; }

        [JsonPropertyOrder(2)]
        public int Version { get; set; }

        [JsonPropertyOrder(3)]
        public DateTime LastUpdate { get; set; }

        [JsonPropertyOrder(4)]
        public string MD5Hash { get; set; }

        public Acino() { }

        public Acino(string name)
        {
            FullName = name;
            LastUpdate = DateTime.Now;
        }

        // [JsonConstructor]
        public Acino(string fullName, int version, string lastUpdate, string mD5Hash)
        {
            FullName = fullName;
            Version = version;
            LastUpdate = Convert.ToDateTime(lastUpdate);
            MD5Hash = mD5Hash;
        }

        public Acino(string fullName, int version, DateTime lastUpdate, string mD5Hash)
        {
            FullName = fullName;
            Version = version;
            LastUpdate = lastUpdate;
            MD5Hash = mD5Hash;
        }

        public string GetFileName() {
            return Path.GetFileName(FullName);
        }

        //internal static List<Acino> CreateNewListAcino(List<string> relativeFileNames, int version = 0, DateTime dateTime = new(), string mD5Hash)
        //{
        //    List<Acino> acinos = new List<Acino>();
        //    foreach (string relativeFileName in relativeFileNames)
        //    {
        //        acinos.Add(new Acino(relativeFileName, version, dateTime, mD5Hash));
        //    }
        //    return acinos;
        //}
    }
}
