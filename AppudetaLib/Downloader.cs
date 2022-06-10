using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AppudetaLib
{
    public class Downloader
    {
        public static async Task<Stream> DownloadFile(Uri uriToranku, string pathTonkaruFile)
        {
            using (HttpClient httpClient = new())
            {
                // Download a text file.
                // stream = httpClient.GetStringAsync(uriToranku);

                // Download a img file.
                //var stream = await httpClient.GetByteArrayAsync(uriToranku);

                // Download a big file use streams.
                Stream stream = await httpClient.GetStreamAsync(uriToranku);

                return stream;

                // HttpResponseMessage response = await httpClient.GetAsync(uriToranku);
                // using (var fs = new FileStream(pathTonkaruFile, FileMode.CreateNew))
                // {
                //     await response.Content.CopyToAsync(fs);
                // }
            }
        }


        public static string GetDirectoryListingRegexForUrl(string url)
        {
            if (url.Equals("http://www.ibiblio.org/pub/"))
            {
                return "<a href=\".*\">(?<name>.*)</a>";
            }
            throw new NotSupportedException();
        }

        public static List<string> GetFilesList()
        {
            List<string> ret = new();
            string url = "http://www.ibiblio.org/pub/";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string html = reader.ReadToEnd();
                    Regex regex = new Regex(GetDirectoryListingRegexForUrl(url));
                    MatchCollection matches = regex.Matches(html);
                    if (matches.Count > 0)
                    {
                        foreach (Match match in matches)
                        {
                            if (match.Success)
                            {
                                ret.Add(match.Groups["name"].ToString());
                            }
                        }
                    }
                }
            }
            return ret;
        }
    }
}
