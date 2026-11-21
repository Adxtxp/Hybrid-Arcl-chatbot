# Running the ARCL chatbot on your own PC

This guide gets the "Ask ARCL Chatbot" button working on your local copy of the ARCL website.
The chatbot is a separate program (`ARCL-chatbot\api`) that the website loads its button from,
at the address in `arcl\web.config` → `ArclChatbotUrl` (`http://localhost:5000`). If the chatbot
isn't running, the website still works but the button doesn't appear.

All commands below are PowerShell, run from `ARCLCode\ARCL-chatbot` unless stated.

## 1. Install (one time)

| Needed | Why |
|---|---|
| **.NET 10 SDK** | the chatbot targets `net10.0` |
| **SQL Server 2025** (Developer edition is free) + `sqlcmd` | the chatbot stores the rulebooks as vectors using SQL Server 2025's `VECTOR` type; older versions will not work |
| **Visual Studio 2022/2026** with the ASP.NET workload | you already use it for the website |
| **A Google Gemini API key** | from https://aistudio.google.com/apikey — used for answers and embeddings |

## 2. Settings (one time)

The chatbot reads its secrets from Windows **user environment variables**, so nothing secret is
stored in SVN. Set them once, then **restart Visual Studio** so it picks them up:

```powershell
setx GEMINI_API_KEY      "<your Gemini key>"
setx ARCL_DB_CONNECTION  "Server=localhost;Database=arcl_chatbot;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;"
setx ARCL_ADMIN_API_KEY  "<any long random string - the admin dashboard password>"
```

Change `Server=localhost` to your instance name if it isn't the default one (e.g. `localhost\SQLEXPRESS`).
Use a database name that isn't the website's own database; `arcl_chatbot` is used below.

## 3. Create the database (one time)

```powershell
$S = "localhost"          # your SQL Server instance
$D = "arcl_chatbot"
sqlcmd -S $S -Q "CREATE DATABASE $D"
sqlcmd -S $S -d $D -I -i sql\001_schema.sql
```

## 4. Load the rulebooks and FAQ (one time, ~10 minutes)

This downloads the rules from arcl.org, splits them into sections and stores them with Gemini
embeddings. Open a **new** PowerShell window first so the variables from step 2 are loaded.

```powershell
cd ingest
dotnet run -- --league mens   --url https://arcl.org/Docs/Mens_League_Rules.htm   --anchors SectionAnchors/mens.json   --effective-start 2024-01-01
dotnet run -- --league womens --url https://arcl.org/Docs/Womens_League_Rules.htm --anchors SectionAnchors/womens.json --effective-start 2017-02-08
dotnet run -- --league youth  --url https://arcl.org/Docs/Kids_League_Rules.htm    --anchors SectionAnchors/youth.json  --effective-start 2024-02-24
dotnet run -- --league mens   --url https://arcl.org/Pages/Content/FAQ.aspx --anchors SectionAnchors/faq.json --effective-start 2026-09-22
dotnet run -- --league womens --url https://arcl.org/Pages/Content/FAQ.aspx --anchors SectionAnchors/faq.json --effective-start 2026-09-22
dotnet run -- --league youth  --url https://arcl.org/Pages/Content/FAQ.aspx --anchors SectionAnchors/faq.json --effective-start 2026-09-22
cd ..
```

Then the remaining database scripts, **in this order** (003 needs the rulebooks loaded first):

```powershell
foreach ($f in "003_seed_decision_trees","005_admin_review_tracking","006_team_ranking","007_token_tracking","008_phase2_hardening","009_phase3_operations","010_handoff_email_delivery") {
  sqlcmd -S $S -d $D -I -i "sql\$f.sql"
}
```

## 5. Copy the web files and download the re-ranker (one time)

```powershell
Copy-Item frontend\*.html, frontend\*.js, frontend\*.css api\wwwroot\
powershell -File scripts\download-reranker.ps1
```

The first line is required: the website's chat button loads `widget.js` from `api\wwwroot`.
The re-ranker (~90 MB) improves answer quality; the chatbot still runs without it.

## 6. Run the website and the chatbot together

1. Open `ARCLCode\arcl\arcl.sln` in Visual Studio. The **Chatbot** folder contains the chatbot projects.
2. Right-click the solution → **Configure Startup Projects…** → **Multiple startup projects** →
   set **arcl** and **ArclChatbot.Api** to **Start**.
3. Press **F5**. Open the site at **https://localhost:44313** (not the http port; the site redirects it wrongly).

The "Ask ARCL Chatbot" button appears bottom-right. To check the chatbot on its own:
http://localhost:5000/index.html. Admin dashboard: http://localhost:5000/admin.html (enter the
`ARCL_ADMIN_API_KEY` value).

## Optional

- **Admin dashboard inside the website** (Secure Links → Chatbot Dashboard): pick a long random
  string, `setx ARCL_SITE_SSO_SECRET "<it>"`, and add the same value to your local
  `arcl\web.config` under `<appSettings>`:
  `<add key="ArclChatbotSsoSecret" value="<it>" />`. Don't commit that line to SVN.
- **Admin emails for contact requests**: see "Admin page" and the email settings in `README.md`.
  Without them, emails are written to `api\outbox\` instead of being sent.

## Troubleshooting

| Symptom | Fix |
|---|---|
| No chat button on the site | Is ArclChatbot.Api running? Does http://localhost:5000/widget.js load? If not, redo step 5. |
| Chatbot says it can't find an answer to everything | Step 4 didn't load anything; check its output for errors and that `GEMINI_API_KEY` is set in that window. |
| Startup error about the connection string or key | Step 2 variables aren't visible: restart Visual Studio (or the PowerShell window). |
| `sqlcmd` error about `QUOTED_IDENTIFIER` | You left out `-I`. |
| Error about the `VECTOR` type | SQL Server is older than 2025. |
| Port 5000 already in use | Another copy of the chatbot is running; stop it. |

Full technical details are in `README.md`.
