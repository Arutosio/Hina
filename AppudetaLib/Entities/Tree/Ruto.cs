using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AppudetaLib.Entities.Tree
{
    public class Ruto
    {
        public Uri UriSosu { get; private set; }

        public Ruto() { }
        public Ruto(string sosu)
        {
            UriSosu = new(sosu);
        }
        public Ruto(Uri uriSosu)
        {
            UriSosu = uriSosu;
        }

        public string Sosu()
        {
            return UriSosu?.ToString();
        }

        public bool IsValidUri()
        {
            return Uri.IsWellFormedUriString(Sosu(), UriKind.Absolute);
            // Uri uriResult;
            // return Uri.TryCreate(uriName, UriKind.Absolute, out uriResult) 
            //     && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        public string GetName()
        {
            return Ruto.GetName(this.UriSosu);
        }

        public string GetTorankuMainFolder()
        {
            return Ruto.GetTorankuMainFolder(this.UriSosu);
        }

        //STATICI!!!!
        public static string GetName(Uri uri)
        {
            return uri.Segments.LastOrDefault();
        }

        public static string GetTorankuMainFolder(Uri uri)
        {
            return uri.Segments[uri.Segments.Length - 2].Replace("/", "");
        }
    }
}
