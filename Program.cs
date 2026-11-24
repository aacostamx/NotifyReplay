// NotifyReplay - replays notification callbacks by calling a Notification API,
// the same way an AWS Lambda would: POST {NOTIFY_URL} with the match request GUID as a JSON string.
//
// Usage:
//   dotnet run                      dry-run: show what would be sent, send nothing
//   dotnet run -- --only <guid>     dry-run for one GUID
//   dotnet run -- --send            send every GUID in ids.txt (asks for YES first)
//   dotnet run -- --send --only <guid>
//
// Config: appsettings.json in the current folder (git-ignored), keys NOTIFY_URL, SIGNING_KEY, APP_ID.
// An environment variable with the same name overrides the file.

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string ConfigFile = "appsettings.json"; // git-ignored, copy from appsettings.example.json
const string IdsFile = "ids.txt";
const string ResultsFile = "results.csv";
var delay = TimeSpan.FromSeconds(3);
var tokenLifetime = TimeSpan.FromMinutes(30);

// ---------- Arguments ----------
bool send = false;
string? only = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--send":
            send = true;
            break;
        case "--only" when i + 1 < args.Length:
            only = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
            Console.Error.WriteLine("Usage: NotifyReplay [--send] [--only <guid>]");
            return 2;
    }
}

// ---------- Configuration ----------
var config = LoadConfig(ConfigFile);
string? Setting(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) config.TryGetValue(name, out value);
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

var notifyUrl = Setting("NOTIFY_URL");
var signingKey = Setting("SIGNING_KEY");
var appId = Setting("APP_ID");

if (notifyUrl is null || !Uri.TryCreate(notifyUrl, UriKind.Absolute, out _))
{
    Console.Error.WriteLine($"NOTIFY_URL is missing or not a valid absolute URL. Copy appsettings.example.json to {ConfigFile} in {Directory.GetCurrentDirectory()} and fill it in.");
    return 2;
}
if (send && (signingKey is null || appId is null))
{
    Console.Error.WriteLine($"Missing settings: {(signingKey is null ? "SIGNING_KEY " : "")}{(appId is null ? "APP_ID" : "")}");
    return 2;
}

// ---------- GUIDs to process ----------
List<string> ids;
if (only is not null)
{
    if (!Guid.TryParse(only, out var g))
    {
        Console.Error.WriteLine($"--only value is not a valid GUID: {only}");
        return 2;
    }
    ids = [g.ToString("D")];
}
else
{
    if (!File.Exists(IdsFile))
    {
        Console.Error.WriteLine($"{IdsFile} not found in {Directory.GetCurrentDirectory()}");
        return 2;
    }
    ids = [];
    var lineNo = 0;
    foreach (var raw in File.ReadAllLines(IdsFile))
    {
        lineNo++;
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        if (!Guid.TryParse(line, out var g))
        {
            Console.Error.WriteLine($"{IdsFile} line {lineNo} is not a valid GUID: {line}");
            return 2;
        }
        var id = g.ToString("D");
        if (!ids.Contains(id)) ids.Add(id); // ignore duplicates in the file
    }
}

var alreadySent = LoadSuccessfulIds(ResultsFile);
var pending = ids.Where(id => !alreadySent.Contains(id)).ToList();

Console.WriteLine($"Mode:    {(send ? "SEND" : "DRY-RUN (nothing will be sent, use --send)")}");
Console.WriteLine($"Target:  POST {notifyUrl}");
Console.WriteLine($"Config:  SIGNING_KEY {(signingKey is null ? "MISSING" : "set")}, APP_ID {(appId is null ? "MISSING" : "set")}");
Console.WriteLine($"IDs:     {ids.Count} total, {ids.Count - pending.Count} already sent (2xx in {ResultsFile}), {pending.Count} to send");
Console.WriteLine();

int sent = 0, skipped = 0, failed = 0;

if (!send)
{
    foreach (var id in ids)
    {
        var willSkip = alreadySent.Contains(id);
        if (willSkip) skipped++;
        Console.WriteLine($"{id} -> {(willSkip ? "skipped (already 2xx)" : "dry-run")}");
    }
    PrintCounts();
    return 0;
}

if (pending.Count == 0)
{
    Console.WriteLine("Nothing to send.");
    skipped = ids.Count;
    PrintCounts();
    return 0;
}

Console.WriteLine($"About to send {pending.Count} notification(s) to {notifyUrl}.");
Console.WriteLine("Each call can send an email. There is no duplicate protection.");
Console.Write("Type YES to continue: ");
if (Console.ReadLine()?.Trim() != "YES")
{
    Console.WriteLine("Cancelled. Nothing was sent.");
    return 1;
}
Console.WriteLine();

// ---------- Send ----------
var tokenCreated = DateTimeOffset.UtcNow;
var token = CreateJwt(appId!, signingKey!, tokenCreated, tokenLifetime);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

bool first = true;
foreach (var id in ids)
{
    if (alreadySent.Contains(id))
    {
        skipped++;
        Console.WriteLine($"{id} -> skipped (already 2xx)");
        continue;
    }

    if (!first) await Task.Delay(delay);
    first = false;

    // Stop before the token can expire mid-call. Re-running is safe: sent IDs are skipped.
    if (DateTimeOffset.UtcNow - tokenCreated > tokenLifetime - TimeSpan.FromMinutes(1))
    {
        Console.WriteLine("Token is about to expire. Stopping here; run again to continue with the rest.");
        PrintCounts();
        return 1;
    }

    var body = JsonSerializer.Serialize(id); // bare JSON string with quotes: "00000000-..."
    using var content = new StringContent(body, Encoding.UTF8, "application/json");
    content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); // no "; charset=utf-8"

    HttpResponseMessage response;
    try
    {
        response = await http.PostAsync(notifyUrl, content);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        failed++;
        AppendResult(ResultsFile, id, "ERROR");
        Console.WriteLine($"{id} -> ERROR ({ex.GetType().Name}: {ex.Message})");
        Console.WriteLine("The request may or may not have reached the API. Check whether the email went out before re-running this ID.");
        PrintCounts();
        return 1;
    }

    using (response)
    {
        var status = (int)response.StatusCode;
        AppendResult(ResultsFile, id, status.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine($"{id} -> {status}");

        if (!response.IsSuccessStatusCode)
        {
            failed++;
            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Stopping on first failure. Status {status} {response.ReasonPhrase}");
            Console.WriteLine($"Response body: {(responseBody.Length == 0 ? "(empty)" : responseBody)}");
            PrintCounts();
            return 1;
        }
        sent++;
    }
}

PrintCounts();
return 0;

// ---------- Helpers ----------

void PrintCounts() =>
    Console.WriteLine($"{Environment.NewLine}Sent: {sent}, Skipped: {skipped}, Failed: {failed}");

// HS256 JWT built with plain HMAC SHA256 and base64url. The key is the UTF-8 bytes of SIGNING_KEY.
static string CreateJwt(string applicationId, string key, DateTimeOffset now, TimeSpan lifetime)
{
    var header = new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT" };
    var payload = new Dictionary<string, object>
    {
        ["applicationId"] = applicationId,
        ["iat"] = now.ToUnixTimeSeconds(),
        ["exp"] = now.Add(lifetime).ToUnixTimeSeconds(),
    };
    var unsigned = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "."
                 + Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
    var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(unsigned));
    return unsigned + "." + Base64Url(signature);
}

static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

// Reads a flat JSON file: { "NOTIFY_URL": "...", "SIGNING_KEY": "...", "APP_ID": "..." }
static Dictionary<string, string?> LoadConfig(string path)
{
    if (!File.Exists(path)) return new();
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    return doc.RootElement.EnumerateObject()
        .Where(p => p.Value.ValueKind == JsonValueKind.String)
        .ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
}

// GUIDs that already got a 2xx. CSV columns: guid,utc_timestamp,status_code
static HashSet<string> LoadSuccessfulIds(string path)
{
    var ok = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return ok;
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var parts = line.Split(',');
        if (parts.Length >= 3 && int.TryParse(parts[2], out var code) && code is >= 200 and < 300)
            ok.Add(parts[0].Trim());
    }
    return ok;
}

static void AppendResult(string path, string id, string status)
{
    var isNew = !File.Exists(path);
    using var writer = new StreamWriter(path, append: true);
    if (isNew) writer.WriteLine("guid,utc_timestamp,status_code");
    writer.WriteLine($"{id},{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)},{status}");
}
