<#
    upload-github.ps1 - one-click "commit + push" to GitHub for Visual Studio.
    Run it from the "Upload to GitHub" launch profile (pick it in the Start dropdown, then Ctrl+F5).

    What it does:
      1. Checks git is installed and the repo is in a sane state (no merge/rebase in progress).
      2. First run: creates the local repo and, if you want, the GitHub repo (needs GH_TOKEN) or uses a URL you paste.
      3. Shows how many files changed, asks for a commit message, commits and pushes.
      4. Shows the result in a message box.

    Token (optional but recommended):  setx GH_TOKEN "ghp_xxx"   then restart Visual Studio.
    The token needs "Contents: Read and write" (and "Administration: write" only if you let it create the repo).
    The token is only sent as an HTTP header for this one command; it is never saved in .git/config.
#>
param([string]$Message)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic
$root = $PSScriptRoot
Set-Location $root

function Box([string]$text, [string]$title, [string]$buttons = "OK", [string]$icon = "Information") {
    return [System.Windows.Forms.MessageBox]::Show($text, $title, $buttons, $icon)
}
function Ask([string]$prompt, [string]$title, [string]$default = "") {
    return [Microsoft.VisualBasic.Interaction]::InputBox($prompt, $title, $default)
}
function Fail([string]$text) {
    Box $text "Upload to GitHub - stopped" "OK" "Warning" | Out-Null
    exit 1
}

# ---- git present? ----
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail "git is not installed (or not in PATH)." }

# ---- local repo ----
if (-not (Test-Path (Join-Path $root ".git"))) {
    $a = Box "This folder is not a git repository yet.`n`nCreate one now?" "Upload to GitHub" "YesNo" "Question"
    if ($a -ne "Yes") { exit 2 }
    & git init -b main 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "git init failed." }
}

# ---- sane state (do NOT push from a broken git state) ----
$gitDir = Join-Path $root ".git"
$problems = @()
if ((Test-Path (Join-Path $gitDir "rebase-merge")) -or (Test-Path (Join-Path $gitDir "rebase-apply"))) {
    $problems += "A git rebase is unfinished. Run 'git rebase --abort' (or --continue) first."
}
if (Test-Path (Join-Path $gitDir "MERGE_HEAD")) { $problems += "A git merge is unfinished. Finish or abort it first." }
if (& git ls-files -u 2>$null) { $problems += "There are unresolved merge conflicts." }
# symbolic-ref works on a brand-new repo with no commits too (rev-parse would wrongly say "HEAD").
$branch = (& git symbolic-ref --short -q HEAD 2>$null | Select-Object -First 1)
if (-not $branch) { $problems += "Git is on a detached HEAD. Run 'git checkout main' first." }
if ($problems) { Fail ("Not uploading:`n`n- " + ($problems -join "`n- ")) }

# ---- auth: use GH_TOKEN as a one-shot header if it exists, otherwise Git Credential Manager ----
$auth = @()
if ($env:GH_TOKEN) {
    $b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$($env:GH_TOKEN)"))
    $auth = @("-c", "http.https://github.com/.extraheader=AUTHORIZATION: basic $b64")
}

# ---- remote ----
$remote = (& git remote get-url origin 2>$null | Select-Object -First 1)
if (-not $remote) {
    $url = (Ask "No GitHub remote yet.`n`nPaste the repo URL (https://github.com/you/repo.git).`nLeave EMPTY to create a new repo on GitHub (needs GH_TOKEN)." "Upload to GitHub").Trim()
    if (-not $url) {
        if (-not $env:GH_TOKEN) { Fail "GH_TOKEN is not set, so a repo can't be created automatically.`nSet it (setx GH_TOKEN `"ghp_xxx`") and restart Visual Studio, or paste an existing repo URL." }
        $name = (Ask "New repository name:" "Upload to GitHub" "GameRamCap").Trim()
        if (-not $name) { exit 2 }
        $priv = (Box "Make the repository PRIVATE?`n`nYes = private, No = public." "Upload to GitHub" "YesNo" "Question") -eq "Yes"
        try {
            $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = "application/vnd.github+json"; "User-Agent" = "upload-github" }
            $body = @{ name = $name; private = $priv } | ConvertTo-Json
            $r = Invoke-RestMethod -Method Post -Uri "https://api.github.com/user/repos" -Headers $headers -Body $body -ContentType "application/json"
            $url = $r.clone_url
        }
        catch { Fail "Could not create the repo:`n$($_.Exception.Message)" }
    }
    & git remote add origin $url 2>&1 | Out-Null
    $remote = $url
}

# ---- what would be uploaded? ----
$changes = @(& git status --porcelain 2>$null)
$ahead = 0
$upstream = (& git rev-parse --abbrev-ref "@{u}" 2>$null | Select-Object -First 1)
if ($upstream) { $ahead = [int](& git rev-list --count "@{u}..HEAD" 2>$null) }
$hasCommits = $true
& git rev-parse --verify HEAD 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { $hasCommits = $false }

if ($changes.Count -eq 0 -and $ahead -eq 0 -and $hasCommits -and $upstream) {
    Box "Nothing to upload. Everything is already on GitHub." "Upload to GitHub" "OK" "Information" | Out-Null
    exit 0
}

# ---- warn about files that look like secrets ----
$risky = @($changes | Where-Object { $_ -match '(\.env|\.pfx|\.snk|\.key|\.pem|secret|token|password)' })
if ($risky.Count -gt 0) {
    $list = ($risky | Select-Object -First 8) -join "`n"
    $a = Box "These files look like they may contain secrets:`n`n$list`n`nUpload anyway?" "Upload to GitHub" "YesNo" "Warning"
    if ($a -ne "Yes") { exit 2 }
}

# ---- confirm + commit message ----
if ([string]::IsNullOrWhiteSpace($Message)) {
    $default = "Update " + (Get-Date -Format "yyyy-MM-dd HH:mm")
    $Message = (Ask "Files changed: $($changes.Count)`nRepo: $remote`n`nCommit message:" "Upload to GitHub" $default).Trim()
}
if ([string]::IsNullOrWhiteSpace($Message)) { exit 2 }

# ---- commit ----
$log = @()
if ($changes.Count -gt 0) {
    $log += & git add -A 2>&1
    $log += & git commit -m $Message 2>&1
    if ($LASTEXITCODE -ne 0) {
        $tail = ($log | ForEach-Object { "$_" } | Select-Object -Last 8) -join "`n"
        Box "Commit FAILED (is git user.name / user.email set?).`n`n$tail" "Upload to GitHub - FAILED" "OK" "Error" | Out-Null
        exit 1
    }
}

# ---- push (never force) ----
$branch = (& git symbolic-ref --short -q HEAD 2>$null | Select-Object -First 1)
$log += & git @auth push -u origin $branch 2>&1
$code = $LASTEXITCODE
$tail = ($log | ForEach-Object { "$_" } | Where-Object { $_.Trim() } | Select-Object -Last 10) -join "`n"

if ($code -eq 0) {
    Box "Uploaded to GitHub!`n`n$remote`nBranch: $branch" "Upload to GitHub - done" "OK" "Information" | Out-Null
}
else {
    $hint = ""
    if ($tail -match "non-fast-forward|rejected|fetch first") { $hint = "`n`nThe remote has commits you don't have. Run 'git pull --rebase' once, then try again." }
    Box "Upload FAILED (exit code $code).`n`n$tail$hint" "Upload to GitHub - FAILED" "OK" "Error" | Out-Null
}
exit $code
