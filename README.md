# NotifyReplay

A small .NET 8 console app that replays notification callbacks against a Notification API, the way an AWS Lambda would. For each GUID it sends:

```
POST {NOTIFY_URL}
Authorization: Bearer <HS256 JWT, payload {"applicationId": APP_ID}, valid 30 minutes>
Content-Type: application/json

"00000000-0000-0000-0000-000000000000"
```

It has a single `Program.cs` and no NuGet packages. The JWT is built with plain HMAC SHA256 and base64url.

## Configuration

Copy `appsettings.example.json` to `appsettings.json` and fill in the three values:

```json
{
  "NOTIFY_URL": "https://your-api/api/Notify",
  "SIGNING_KEY": "<signing key>",
  "APP_ID": "<app id>"
}
```

`appsettings.json` is git-ignored, so the key never gets committed. Run the app from the folder that holds it. An environment variable with the same name overrides the file.

The signing key is used as UTF-8 bytes. Secret values and the token are never printed.

## Usage

Copy `ids.example.txt` to `ids.txt` and list one GUID per line. Blank lines and `#` comments are ignored.

| Command | What it does |
|---|---|
| `dotnet run` | Dry-run: shows the target URL and the GUIDs, sends nothing |
| `dotnet run -- --send --only <guid>` | Sends one GUID (use this first) |
| `dotnet run -- --send` | Sends every GUID not already sent |

`--send` prints a summary and waits for you to type `YES`.

## Safety

- Calls are sent one at a time, 3 seconds apart.
- The run stops on the first non-2xx response, prints the status and body, and exits with code 1. It never retries, because the API may have no duplicate protection.
- Every attempt is appended to `results.csv` (`guid,utc_timestamp,status_code`). GUIDs already recorded as 2xx are skipped, so re-running is safe.
- A network error or timeout is recorded as `ERROR` and stops the run. The request may still have reached the API, so check before re-sending that GUID.
- `appsettings.json`, `ids.txt` and `results.csv` are git-ignored.

Exit codes: `0` OK, `1` failure or cancelled, `2` bad arguments or configuration.
