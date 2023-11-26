using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HinaLib
{

    public class Internet
    {
        //public delegate void ProgressChangedHandler(int percent);
        //public static event ProgressChangedHandler ProgressChanged;
        internal static Action<int> SendProgressBar;

        /*
        public static async Task<(Stream stream, long totalBytes, int bufferSize)> DownloadFile(Uri url)
        {
            using (HttpClient client = new HttpClient())
            {
                using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength.Value;
                    int bufferSize = (int)Math.Min(4096, totalBytes);

                    Stream stream = await response.Content.ReadAsStreamAsync();

                    ProgressChanged?.Invoke(0);

                    return (stream, totalBytes, bufferSize);
                }
            }
        }
        */

        public static async Task DownloadAndSaveFile(Uri url, string fileName, bool force = false)
        {
            if(force)
            {
                string directory = Path.GetDirectoryName(fileName);

                // Verifica se la directory esiste, altrimenti creala
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }

            using (HttpClient client = new HttpClient())
            {
                using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength.Value;
                    long totalBytesRead = 0;

                    using (Stream stream = await response.Content.ReadAsStreamAsync())
                    {
                        using (FileStream fileStream = System.IO.File.Create(fileName))
                        {
                            byte[] buffer = new byte[4096];
                            int bytesRead;
                            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalBytesRead += bytesRead;

                                //Opzione 1
                                //ProgressChanged?.Invoke((int)(100 * totalBytesRead / totalBytes));

                                //Opzione 2
                                SendProgressBar?.Invoke((int)(100 * totalBytesRead / totalBytes));
                            }
                        }
                    }
                }
            }
        }


        public static async Task<Stream> DownloadFile(Uri uriToranku)
        {
            HttpClientHandler clientHandler = new();
            clientHandler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; };

            // Pass the handler to httpclient(from you are calling api)
            using (HttpClient httpClient = new(clientHandler))
            {
                // Download a text file.
                //stream = httpClient.GetStringAsync(uriToranku);

                // Download a img file.
                //var stream = await httpClient.GetByteArrayAsync(uriToranku);

                // Download a big file use streams.
                Stream stream = await httpClient.GetStreamAsync(uriToranku);
                return stream;
                // HttpResponseMessage response = await httpClient.GetAsync(uriToranku);
                //using (var fs = new FileStream(pathTonkaruFile, FileMode.CreateNew))
                //{
                //    await response.Content.CopyToAsync(fs);
                //}
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

        public static bool IsUrlValid(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri result) && (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);
        }

        public static string BuildUrl(string baseUrl, string relativePath)
        {
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }

            Uri baseUri = new Uri(baseUrl);

            if (Uri.TryCreate(baseUri, relativePath, out Uri resultUri))
            {
                return resultUri.ToString();
            }

            throw new ArgumentException("Impossibile costruire l'URL con i percorsi forniti.");
        }
    }
}
