<#
    upload-github.ps1 - one-click "commit + push" to GitHub for Visual Studio.
    Run it from the "Upload to GitHub" launch profile (pick it in the Start dropdown, then Ctrl+F5).

    What it does:
      1. Checks git is installed and the repo is in a sane state (no merge/rebase in progress).
      2. First run: creates the local repo and, if you want, the GitHub repo (needs GH_TOKEN) or uses a URL you paste.
      3. Shows how many files changed, asks for a commit message, commits and pushes.
      4. After a successful push: builds the exe (dotnet publish) and creates a new GitHub Release
         (auto version v0.1.1, v0.1.2, ...) with the exe files attached.
      5. Shows the result in a message box.

    The Release step needs a token (GH_TOKEN, or being logged in with the GitHub CLI: gh auth login).
    Token (optional for push, REQUIRED for Release):  setx GH_TOKEN "ghp_xxx"   then restart Visual Studio.
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
    if ($url -match '^(github_pat_|ghp_|gho_|ghu_|ghs_|ghr_)') {
        Fail "What you pasted looks like an access token, not a repo URL.`n`nNothing was saved. Never paste a token here - set it with setx GH_TOKEN instead.`nIf that token was shared anywhere, revoke it on GitHub and create a new one."
    }
    if ($url -notmatch '^(https://github\.com/[^/\s]+/[^/\s]+|git@github\.com:[^/\s]+/[^/\s]+)$') {
        Fail "That is not a GitHub repo URL.`n`nExpected: https://github.com/you/repo.git"
    }
    & git remote add origin $url 2>&1 | Out-Null
    $remote = $url
}

if ($remote -match '^(github_pat_|ghp_|gho_|ghu_|ghs_|ghr_)' -or $remote -notmatch '^(https://|git@|ssh://)') {
    Fail "The 'origin' remote is not a valid URL (it may contain a pasted token).`n`nFix it in a terminal:`n  git remote remove origin`nthen run this button again. If a token was pasted anywhere, revoke it on GitHub."
}

# ---- what would be uploaded? ----
$releaseOnly = $false
$changes = @(& git status --porcelain 2>$null)
$ahead = 0
$upstream = (& git rev-parse --abbrev-ref "@{u}" 2>$null | Select-Object -First 1)
if ($upstream) { $ahead = [int](& git rev-list --count "@{u}..HEAD" 2>$null) }
$hasCommits = $true
& git rev-parse --verify HEAD 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { $hasCommits = $false }

if ($changes.Count -eq 0 -and $ahead -eq 0 -and $hasCommits -and $upstream) {
    $a = Box "Nothing to upload. Everything is already on GitHub.`n`nCreate a new Release from the latest commit anyway?" "Upload to GitHub" "YesNo" "Question"
    if ($a -ne "Yes") { exit 0 }
    $releaseOnly = $true
    $Message = (& git log -1 --pretty=%s 2>$null | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Message)) { $Message = "Release" }
}

# ---- warn about files that look like secrets ----
$risky = @($changes | Where-Object { $_ -match '(\.env|\.pfx|\.snk|\.key|\.pem|secret|token|password)' })
if ($risky.Count -gt 0) {
    $list = ($risky | Select-Object -First 8) -join "`n"
    $a = Box "These files look like they may contain secrets:`n`n$list`n`nUpload anyway?" "Upload to GitHub" "YesNo" "Warning"
    if ($a -ne "Yes") { exit 2 }
}

if (-not $releaseOnly) {
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

    # ---- push result ----
    if ($code -ne 0) {
        $hint = ""
        if ($tail -match "non-fast-forward|rejected|fetch first") { $hint = "`n`nThe remote has commits you don't have. Run 'git pull --rebase' once, then try again." }
        Box "Upload FAILED (exit code $code).`n`n$tail$hint" "Upload to GitHub - FAILED" "OK" "Error" | Out-Null
        exit $code
    }
}

# ---- release: build exe + create GitHub Release (runs after every successful upload) ----
$BuildStandalone = $true     # also attach the big self-contained exe (runs without .NET installed)
$relMsg = ""
$relOk = $true
try {
    $ProgressPreference = 'SilentlyContinue'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    if ($remote -notmatch 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$') { throw "Could not read owner/repo from the remote URL." }
    $owner = $Matches[1]; $repo = $Matches[2]

    $token = $env:GH_TOKEN
    if (-not $token -and (Get-Command gh -ErrorAction SilentlyContinue)) { $token = (& gh auth token 2>$null | Select-Object -First 1) }
    if (-not $token) { throw "No token. Set GH_TOKEN (setx GH_TOKEN `"ghp_xxx`") and restart Visual Studio, or run 'gh auth login'." }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet SDK not found in PATH." }

    $h = @{ Authorization = "Bearer $token"; Accept = "application/vnd.github+json"; "User-Agent" = "upload-github" }

    # next version = latest release + 1 (patch); first release starts from the csproj <Version>
    $ver = "0.1.0"
    $csproj = Get-ChildItem $root -Filter *.csproj | Select-Object -First 1
    if ($csproj -and ((Get-Content $csproj.FullName -Raw) -match '<Version>(\d+\.\d+\.\d+)</Version>')) { $ver = $Matches[1] }
    try {
        $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/latest" -Headers $h
        if ($latest.tag_name -match '^v(\d+)\.(\d+)\.(\d+)') { $ver = "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)" }
    } catch { }   # 404 = no release yet -> keep $ver

    # build
    $out = Join-Path $env:TEMP "GameRamCap-release"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $proj = $csproj.FullName
    $files = @()

    $b = & dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true "-p:Version=$ver" -o (Join-Path $out "small") --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("Build failed:`n" + (($b | ForEach-Object { "$_" } | Select-Object -Last 8) -join "`n")) }
    $f1 = Join-Path $out "GameRamCap-v$ver.exe"
    Copy-Item (Join-Path $out "small\GameRamCap.exe") $f1
    $files += $f1

    if ($BuildStandalone) {
        $b = & dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true "-p:Version=$ver" -o (Join-Path $out "full") --nologo -v q 2>&1
        if ($LASTEXITCODE -ne 0) { throw ("Standalone build failed:`n" + (($b | ForEach-Object { "$_" } | Select-Object -Last 8) -join "`n")) }
        $f2 = Join-Path $out "GameRamCap-v$ver-standalone.exe"
        Copy-Item (Join-Path $out "full\GameRamCap.exe") $f2
        $files += $f2
    }

    # create release on the commit we just pushed
    $sha = (& git rev-parse HEAD 2>$null | Select-Object -First 1)
    $body = @{ tag_name = "v$ver"; target_commitish = $sha; name = "GameRamCap v$ver"; body = $Message; generate_release_notes = $false; draft = $false } | ConvertTo-Json
    $rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$owner/$repo/releases" -Headers $h -Body $body -ContentType "application/json; charset=utf-8"

    # upload the exe files
    $uploadBase = ($rel.upload_url -replace '\{.*$', '')
    foreach ($f in $files) {
        $name = [IO.Path]::GetFileName($f)
        Invoke-RestMethod -Method Post -Uri "${uploadBase}?name=$name" -Headers $h -InFile $f -ContentType "application/octet-stream" | Out-Null
    }
    $relMsg = "Release v$ver published:`n$($rel.html_url)"
}
catch {
    $relOk = $false
    $relMsg = "Code was pushed, but the Release step FAILED:`n`n$($_.Exception.Message)"
}

if ($relOk) {
    Box "Uploaded to GitHub!`n`n$remote`nBranch: $branch`n`n$relMsg" "Upload to GitHub - done" "OK" "Information" | Out-Null
}
else {
    Box "Uploaded to GitHub (code only).`n`n$relMsg" "Upload to GitHub - release failed" "OK" "Warning" | Out-Null
    exit 1
}
exit 0
