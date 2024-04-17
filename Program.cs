using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net;

class Config
{
    public string FlashpointDatabase { get; set; }
    public string UltimateDatabase { get; set; }
    public string[] FileServers { get; set; }
    public string[] ImageServers { get; set; }
    public string EntryLanguage { get; set; }
}

public class GameZipper
{
    async static Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: gamezipper <uuid> [url or path to text file]");
            return;
        }

        Config config = new();

        try
        {
            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("config.json"));
        }
        catch
        {
            Console.WriteLine("error: could not parse config.json");
            return;
        }

        Console.WriteLine("entry id: " + args[0]);
        Console.WriteLine($"generating meta.yaml...");

        var fpConnection = new SqliteConnection($"Data Source={config.FlashpointDatabase};Mode=ReadOnly");
        fpConnection.Open();

        List<string> meta = new();
        List<string> paths = new();
        string title = "";

        var fpCommandMain = fpConnection.CreateCommand();
        fpCommandMain.CommandText = $"SELECT title,library,playMode,releaseDate,language,tagsStr,source,platformName,platformsStr,status,applicationPath,launchCommand FROM game WHERE id = $id";
        fpCommandMain.Parameters.AddWithValue("id", args[0]);
        using (var reader = fpCommandMain.ExecuteReader())
        {
            if (!reader.HasRows)
            {
                Console.WriteLine("error: invalid uuid");
                return;
            }

            while (reader.Read())
            {
                if (reader.GetString(10) == "")
                {
                    Console.WriteLine("error: entry appears to have been zipped already");
                    return;
                }

                meta.AddRange(new string[] {
                    "Title: " + Sanitize(reader.GetString(0)),
                    "Library: " + reader.GetString(1),
                    "Play Mode: " + reader.GetString(2),
                    "Release Date: " + (reader.GetString(3).Length == 4 ? $"\"{reader.GetString(3)}\"" : reader.GetString(3)),
                    "Languages: " + Sanitize(reader.GetString(4) == "" && config.EntryLanguage != "" ? config.EntryLanguage : reader.GetString(4)),
                    "Tags: " + reader.GetString(5),
                    "Tag Categories: " + string.Join("; ", reader.GetString(5).Split("; ").Select(_ => "default")),
                    "Source: " + Sanitize(reader.GetString(6)),
                    "Primary Platform: " + reader.GetString(7),
                    "Platforms: " + reader.GetString(8),
                    "Status: " + reader.GetString(9),
                    "Application Path: " + Sanitize(reader.GetString(10)),
                    "Launch Command: " + Sanitize(reader.GetString(11)),
                    "Curation Notes: GameZIP conversion",
                    "UUID: " + args[0],
                    "Group: \"\""
                });

                title = reader.GetString(0);
                paths.AddRange(ToPath(reader.GetString(11)));

                break;
            }
        }

        var fpCommandAddApp = fpConnection.CreateCommand();
        fpCommandAddApp.CommandText = $"SELECT name,applicationPath,launchCommand FROM additional_app WHERE parentGameId = $id";
        fpCommandAddApp.Parameters.AddWithValue("id", args[0]);
        using (var reader = fpCommandAddApp.ExecuteReader())
        {
            if (!reader.HasRows)
            {
                meta.Add("Additional Applications: {}");
            }
            else
            {
                meta.Add("Additional Applications:");
            }

            while (reader.Read())
            {
                if (reader.GetString(0) == "" || reader.GetString(1) == "" || reader.GetString(2) == "")
                {
                    continue;
                }

                if (reader.GetString(1) == ":message:")
                {
                    meta.Add("  Message: " + reader.GetString(2));
                }
                else if (reader.GetString(1) == ":extras:")
                {
                    meta.Add("  Extras: " + reader.GetString(2));
                }
                else
                {
                    meta.AddRange(new string[]
                    {
                        "  " + reader.GetString(0) + ":",
                        "    Heading: " + reader.GetString(0),
                        "    Application Path: " + reader.GetString(1),
                        "    Launch Command: " + reader.GetString(2)
                    });

                    string launchCommand = reader.GetString(2);

                    paths.AddRange(ToPath(reader.GetString(2)));
                }
            }
        }

        fpConnection.Close();

        string safeTitle = string.Join("", title.Split(Path.GetInvalidFileNameChars()));
        string curationDir = @$"out\{safeTitle}\{args[0]}\";
        Directory.CreateDirectory(curationDir + "content");
        File.WriteAllLines(curationDir + "meta.yaml", meta);

        HttpClient client = new();
        for (int i = 0; i < 2; i++)
        {
            string imageType = i == 0 ? "logo" : "screenshot";
            string imageDir = i == 0 ? "Logos" : "Screenshots";
            string imageFile = i == 0 ? "logo.png" : "ss.png";

            Console.WriteLine($"downloading {imageType}...");

            bool success = false;
            foreach (string server in config.ImageServers)
            {
                var request = await client.GetAsync($"{config.ImageServers[0]}/{imageDir}/{args[0][0..2]}/{args[0][2..4]}/{args[0]}.png");
                if (request.StatusCode == HttpStatusCode.OK)
                {
                    var stream = await request.Content.ReadAsStreamAsync();
                    var file = File.Create(curationDir + imageFile);
                    stream.CopyTo(file);

                    stream.Close();
                    file.Close();

                    success = true;
                    break;
                }
            }

            if (!success)
            {
                Console.WriteLine("failed to download " + imageType);
            }
        }

        List<string> wildcardUrls = new();

        if (args.Length > 1)
        {
            List<string> unsortedUrls = new();

            foreach (string arg in args.Skip(1))
            {
                if (!arg.StartsWith("http://"))
                {
                    try
                    {
                        unsortedUrls.AddRange(File.ReadAllLines(arg));
                        continue;
                    }
                    catch { }
                }

                unsortedUrls.Add(arg);
            }

            foreach (string url in unsortedUrls)
            {
                if (url.EndsWith("*"))
                {
                    wildcardUrls.Add(url);
                }
                else
                {
                    paths.AddRange(ToPath(url));
                }
            }
        }

        if (wildcardUrls.Count > 0)
        {
            var ultConnection = new SqliteConnection($"Data Source={config.UltimateDatabase};Mode=ReadOnly");
            ultConnection.Open();

            foreach (string url in wildcardUrls)
            {
                string query = url.Substring(0, url.Length - 1) + "%";
                if (query.StartsWith("http://"))
                {
                    query = query.Substring("http://".Length);
                }
                query = "Legacy/htdocs/" + query;

                var ultCommand = ultConnection.CreateCommand();
                ultCommand.CommandText = $"SELECT path FROM files WHERE path LIKE $query";
                ultCommand.Parameters.AddWithValue("query", query);
                using (var reader = ultCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        paths.Add(reader.GetString(0).Substring("Legacy/htdocs/".Length));
                    }
                }
            }

            ultConnection.Close();
        }

        if (paths.Count == 0)
        {
            Console.WriteLine("error: no files to download");
            return;
        }

        paths = paths.Distinct().OrderBy(s => s).ToList();
        paths.RemoveAll(s => s == "");

        Console.WriteLine($"downloading {paths.Count} files...");

        foreach (string path in paths)
        {
            Console.WriteLine($"downloading {path}...");

            string outPath = curationDir + @"content\" + path.Replace('/', '\\');
            if (File.Exists(outPath))
            {
                Console.WriteLine("file already exists and will be skipped");
                continue;
            }

            try
            {
                Directory.CreateDirectory(outPath.Substring(0, outPath.LastIndexOf(@"\")));
            }
            catch
            {
                Console.WriteLine("file has malformed path and will be skipped");
                continue;
            }
            
            bool success = false;
            foreach (string server in config.FileServers)
            {
                var request = await client.GetAsync($"{server}/{path}");
                if (request.StatusCode == HttpStatusCode.OK)
                {
                    var stream = await request.Content.ReadAsStreamAsync();
                    var file = File.Create(outPath);
                    stream.CopyTo(file);

                    stream.Close();
                    file.Close();

                    success = true;
                    break;
                }
            }

            if (!success)
            {
                Console.WriteLine($"failed to download file");
            }
        }

        Console.WriteLine("zipping...");
        Process.Start("cmd.exe", $"/c cd \"out\\{safeTitle}\" & ..\\..\\7za a -r -ms=on \"..\\{safeTitle}.7z\" & cd ..\\..").WaitForExit();
    }

    public static List<string> ToPath(string url)
    {
        if (url.StartsWith("http://"))
        {
            url = url.Substring("http://".Length);
        }
        if (url.Contains("?"))
        {
            url = url.Substring(0, url.IndexOf("?"));
        }
        if (url.Split("/").Length == 1)
        {
            url += "/";
        }

        List<string> paths = new();
        if (url.EndsWith("/"))
        {
            paths.AddRange(new string[] { url + "index.html", url + "index.htm", url + "index.php", url + "index.phtml" });
        }
        else
        {
            paths.Add(url);
        }

        return paths;
    }

    public static string Sanitize(string value)
    {
        if (value == "")
        {
            value = "\"\"";
        }
        else if (value.StartsWith('"') && value.EndsWith('"'))
        {
            value = "'" + value + "'";
        }

        return value;
    }
}