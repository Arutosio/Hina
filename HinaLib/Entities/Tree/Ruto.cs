using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HinaLib.Entities.Tree
{
    public class Ruto
    {
        private Uri? _uriSosu;

        [JsonPropertyOrder(1)]
        public string UriSosu
        {
            get
            {
                return _uriSosu.ToString();
            }
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    Uri uri = new(value);
                    if (IsValidUri(uri))
                    {
                        _uriSosu = uri;
                    }
                }
                else
                {
                    throw new ArgumentException("Parameter uri cannot be null or empy");
                }
            }
        }

        public Ruto() { }

        public Ruto(string sosu)
        {
            UriSosu = new(sosu);
        }

        public Ruto(Uri uriSosu)
        {
            _uriSosu = uriSosu;
        }

        public string GetName()
        {
            return Ruto.GetName(this._uriSosu);
        }

        public string GetTorankuMainFolder()
        {
            return Ruto.GetTorankuMainFolder(this._uriSosu);
        }

        public string GetTorankuBaseUri()
        {
            return UriSosu.Replace($"{GetTorankuMainFolder()}/{GetName()}", "");
        }

        //STATICI!!!!
        private static bool IsValidUri(Uri uri)
        {
            return Uri.IsWellFormedUriString(uri.ToString(), UriKind.Absolute);
            // Uri uriResult;
            // return Uri.TryCreate(uriName, UriKind.Absolute, out uriResult) 
            //     && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

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
