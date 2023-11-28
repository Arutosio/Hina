using HinaLib.Entities.Tree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace HinaLib.Entities.Vitis
{
    public class Grappolo
    {
        public readonly DirectoryInfo GrappoloDir;

        [JsonPropertyOrder(1)]
        public Ruto? Ruto { get; set; }

        [JsonPropertyOrder(2)]
        public int Version { get; set; }

        [JsonPropertyOrder(3)]
        public string Name { get; set; }

        [JsonPropertyOrder(4)]
        public List<Acino>? Acinos { get; set; }

        //public Grappolo(string name, List<string> fullFileNames)
        //{
        //    Name = name;
        //    InitAcinos(fullFileNames);
        //}

        //public Grappolo(string name, Ruto ruto, List<string> fullFileNames)
        //{
        //    Name = name;
        //    Ruto = ruto;
        //    InitAcinos(fullFileNames);
        //}

        //public void Init(Ruto ruto, List<string> fullFileNames)
        //{
        //    Ruto = ruto;
        //    InitAcinos(fullFileNames);
        //}

        public Grappolo() { }

        public Grappolo(string name, List<Acino> acinos)
        {
            Name = name;
            Acinos = acinos;
        }

        public Grappolo(string name, Ruto ruto, List<Acino> acinos)
        {
            Name = name;
            Ruto = ruto;
            Acinos = acinos;
        }

        private void InitAcinos(List<string> fullFileNames)
        {
            if (Acinos == null)
            {
                Acinos = new List<Acino>();
            }

            foreach (var fullFileName in fullFileNames)
            {
                Acinos.Add(new Acino(fullFileName));
            }
        }

        public string AcinoFullPathName(Acino acino)
        {
            string ret = Path.Combine(Ruto.GetRepoUri(), acino.FullName);
            
            return ret;
        }

        public Uri BuildUrl(string baseUrl, string relativePath)
        {
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }

            Uri baseUri = new Uri(baseUrl);

            if (Uri.TryCreate(baseUri, relativePath, out Uri resultUri))
            {
                return resultUri;
            }

            throw new ArgumentException("Impossibile costruire l'URL con i percorsi forniti.");
        }
    }
}
