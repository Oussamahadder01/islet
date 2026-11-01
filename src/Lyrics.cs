using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Islet
{
    public sealed class LyricLine
    {
        public TimeSpan Time;
        public string Text;
    }

    public sealed class Lyrics
    {
        public List<LyricLine> Lines = new List<LyricLine>();
        public bool Synced;
        public bool Instrumental;

        private static readonly Regex Stamp = new Regex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]");

        // LRC: "[01:23.45] line"; a line may carry several timestamps.
        public static Lyrics FromLrc(string lrc)
        {
            var lyrics = new Lyrics { Synced = true };
            foreach (string raw in lrc.Split('\n'))
            {
                var stamps = Stamp.Matches(raw);
                if (stamps.Count == 0) continue;
                string text = raw.Substring(stamps[stamps.Count - 1].Index + stamps[stamps.Count - 1].Length).Trim();
                foreach (Match m in stamps)
                {
                    double seconds = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                    lyrics.Lines.Add(new LyricLine
                    {
                        Time = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(seconds),
                        Text = text,
                    });
                }
            }
            lyrics.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            return lyrics.Lines.Count > 0 ? lyrics : null;
        }

        public static Lyrics FromPlain(string text)
        {
            var lyrics = new Lyrics();
            foreach (string line in text.Split('\n'))
                if (line.Trim().Length > 0) lyrics.Lines.Add(new LyricLine { Text = line.Trim() });
            return lyrics.Lines.Count > 0 ? lyrics : null;
        }

        // Index of the line being sung at this position, or -1 before the first.
        // Unsynced lyrics are spread evenly over the track as an approximation.
        public int IndexAt(TimeSpan position, TimeSpan duration)
        {
            if (Lines.Count == 0) return -1;
            if (!Synced)
            {
                if (duration <= TimeSpan.Zero) return 0;
                double fraction = position.TotalSeconds / duration.TotalSeconds;
                return Math.Max(0, Math.Min(Lines.Count - 1, (int)(fraction * Lines.Count)));
            }
            // A small lead so the line appears as it starts being sung.
            var t = position + TimeSpan.FromMilliseconds(250);
            int index = -1;
            for (int i = 0; i < Lines.Count && Lines[i].Time <= t; i++) index = i;
            return index;
        }
    }

    // Lyrics from LRCLIB (lrclib.net): a free, open lyrics database that needs
    // no account or key. Only the title, artist, album and duration are sent,
    // and only when lyrics are shown. Results, including misses, are cached on
    // disk so a track is never fetched twice.
    public sealed class LyricsService
    {
        private const string Api = "https://lrclib.net/api/";
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Islet", "lyrics");

        private readonly Dictionary<string, Lyrics> memory = new Dictionary<string, Lyrics>();

        static LyricsService()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        // True when the last Find could not reach the service (as opposed to
        // the song simply having no lyrics); such results are never cached.
        public bool LastLookupFailed { get; private set; }

        // Null when no lyrics exist (or the service is unreachable).
        public async Task<Lyrics> Find(string title, string artist, string album, TimeSpan duration)
        {
            LastLookupFailed = false;
            string key = title + "\u0001" + artist;
            Lyrics cached;
            if (memory.TryGetValue(key, out cached)) return cached;

            string file = Path.Combine(CacheDir, Hash(key) + ".txt");
            Lyrics result;
            bool fromDisk = TryReadCache(file, out result);
            if (!fromDisk)
            {
                bool reachable;
                result = Fetch(await Query(title, artist, album, duration), out reachable);
                if (result == null && reachable)
                {
                    // Retry with the decorations streaming services add removed.
                    string plainTitle = CleanTitle(title), mainArtist = MainArtist(artist);
                    if (plainTitle != title || mainArtist != artist)
                        result = Fetch(await Query(plainTitle, mainArtist, null, duration), out reachable);
                }
                if (!reachable)
                {
                    LastLookupFailed = true;
                    return null;
                }
                WriteCache(file, result);
            }
            memory[key] = result;
            return result;
        }

        // Exact lookup first, then a search ranked by duration.
        private static async Task<object> Query(string title, string artist, string album, TimeSpan duration)
        {
            string common = "track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);
            string exact = Api + "get?" + common
                + (string.IsNullOrEmpty(album) ? "" : "&album_name=" + Uri.EscapeDataString(album))
                + (duration > TimeSpan.Zero ? "&duration=" + (int)Math.Round(duration.TotalSeconds) : "");
            object found = await Download(exact);
            if (found is Dictionary<string, object>) return found;
            if (found == null) return null; // unreachable
            object list = await Download(Api + "search?" + common);
            var items = list as object[];
            if (items == null) return list == null ? null : (object)"miss";
            var best = items.OfType<Dictionary<string, object>>()
                .OrderByDescending(d => d.ContainsKey("syncedLyrics") && d["syncedLyrics"] != null)
                .ThenBy(d => duration > TimeSpan.Zero && d.ContainsKey("duration") && d["duration"] != null
                    ? Math.Abs(Convert.ToDouble(d["duration"], CultureInfo.InvariantCulture) - duration.TotalSeconds) : 0)
                .FirstOrDefault();
            return best ?? (object)"miss";
        }

        // Parsed JSON, "miss" for a 404, or null when the service could not be reached.
        private static async Task<object> Download(string url)
        {
            using (var client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers[HttpRequestHeader.UserAgent] = "Islet (personal dynamic island for Windows)";
                var download = client.DownloadStringTaskAsync(url);
                if (await Task.WhenAny(download, Task.Delay(8000)) != download)
                {
                    client.CancelAsync();
                    return null;
                }
                try
                {
                    return new JavaScriptSerializer().DeserializeObject(await download);
                }
                catch (WebException ex)
                {
                    var response = ex.Response as HttpWebResponse;
                    if (response != null && response.StatusCode == HttpStatusCode.NotFound) return "miss";
                    Log.Error("lyrics request", ex);
                    return null;
                }
                catch (ArgumentException ex)
                {
                    Log.Error("lyrics json", ex);
                    return "miss";
                }
            }
        }

        private static Lyrics Fetch(object answer, out bool reachable)
        {
            reachable = answer != null;
            var d = answer as Dictionary<string, object>;
            if (d == null) return null;
            if (d.ContainsKey("instrumental") && d["instrumental"] is bool && (bool)d["instrumental"])
                return new Lyrics { Instrumental = true };
            string synced = d.ContainsKey("syncedLyrics") ? d["syncedLyrics"] as string : null;
            string plain = d.ContainsKey("plainLyrics") ? d["plainLyrics"] as string : null;
            return (synced != null ? Lyrics.FromLrc(synced) : null) ?? (plain != null ? Lyrics.FromPlain(plain) : null);
        }

        // "Song - Remastered 2011" / "Song (feat. X)" -> "Song"
        private static string CleanTitle(string title)
        {
            string t = Regex.Replace(title, @"\s*[\(\[](feat\.?|ft\.?|with|from|remaster|live|version)[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) t = t.Substring(0, dash);
            return t.Trim();
        }

        // "A, B & C feat. D" -> "A"
        private static string MainArtist(string artist)
        {
            var parts = Regex.Split(artist, @"\s*(?:,|&|\bfeat\.?|\bft\.?|\bx\b)\s*", RegexOptions.IgnoreCase);
            return parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim() : artist;
        }

        // Cache format: first line synced|plain|instrumental|none, then the text.
        private static bool TryReadCache(string file, out Lyrics lyrics)
        {
            lyrics = null;
            try
            {
                if (!File.Exists(file)) return false;
                string[] content = File.ReadAllText(file, Encoding.UTF8).Split(new[] { '\n' }, 2);
                switch (content[0].Trim())
                {
                    case "synced": lyrics = Lyrics.FromLrc(content.Length > 1 ? content[1] : ""); return true;
                    case "plain": lyrics = Lyrics.FromPlain(content.Length > 1 ? content[1] : ""); return true;
                    case "instrumental": lyrics = new Lyrics { Instrumental = true }; return true;
                    default:
                        // Misses are retried after a day: lyrics get added to LRCLIB over time.
                        return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(1);
                }
            }
            catch (IOException ex)
            {
                Log.Error("lyrics cache read", ex);
                return false;
            }
        }

        private static void WriteCache(string file, Lyrics lyrics)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string body;
                if (lyrics == null) body = "none";
                else if (lyrics.Instrumental) body = "instrumental";
                else if (lyrics.Synced)
                    body = "synced\n" + string.Join("\n", lyrics.Lines.Select(l =>
                        string.Format(CultureInfo.InvariantCulture, "[{0:00}:{1:00.00}]{2}", (int)l.Time.TotalMinutes, l.Time.TotalSeconds % 60, l.Text)));
                else body = "plain\n" + string.Join("\n", lyrics.Lines.Select(l => l.Text));
                File.WriteAllText(file, body, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                Log.Error("lyrics cache write", ex);
            }
        }

        private static string Hash(string key)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
        }
    }
}
