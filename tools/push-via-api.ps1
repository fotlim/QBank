# 通过 GitHub REST API 推送一个本地提交（本机 github.com:443 不通时的唯一出路）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\push-via-api.ps1 -Repo fotlim/QBank -Branch main
param(
    [string]$Repo = "fotlim/QBank",
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"

function GitOut([string[]]$argv) {
    $out = & git @argv 2>$null
    return ($out -join "`n").Trim()
}

$localHead = GitOut @("rev-parse", "HEAD")
$localTree = GitOut @("show", "-s", "--format=%T", "HEAD")
$authorDate = GitOut @("show", "-s", "--format=%aI", "HEAD")
$committerDate = GitOut @("show", "-s", "--format=%cI", "HEAD")

$base = (& gh api "repos/$Repo/git/ref/heads/$Branch" --jq ".object.sha").Trim()
$baseTree = (& gh api "repos/$Repo/git/commits/$base" --jq ".tree.sha").Trim()

Write-Host "repo=$Repo branch=$Branch"
Write-Host "base=$base baseTree=$baseTree"
Write-Host "localHead=$localHead localTree=$localTree"
Write-Host "authorDate=$authorDate committerDate=$committerDate"

# 提交原文（message 必须按字节取，绝不走 PowerShell 的字符串往返 —— 中文会被按 GBK 解码）
$rawFile = Join-Path $env:TEMP "qbank-commit-raw.txt"
& cmd /c "git cat-file commit HEAD > `"$rawFile`""
$rawBytes = [System.IO.File]::ReadAllBytes($rawFile)
$sep = -1
for ($i = 0; $i -lt $rawBytes.Length - 1; $i++) {
    if ($rawBytes[$i] -eq 10 -and $rawBytes[$i + 1] -eq 10) { $sep = $i + 2; break }
}
if ($sep -lt 0) { throw "读不出提交信息（找不到头部结束）" }
$message = [System.Text.UTF8Encoding]::new($false).GetString($rawBytes, $sep, $rawBytes.Length - $sep)
Write-Host ("message 长度 {0} 字符，首行：{1}" -f $message.Length, ($message -split "`n")[0])

# 1) 逐个文件上传 blob —— 内容必须从 git 对象取（工作区是 CRLF，git 存的是 LF）
$files = (GitOut @("diff", "--name-only", "$base..HEAD")) -split "`n" | Where-Object { $_.Trim().Length -gt 0 }
$treeEntries = @()
$tmpBin = Join-Path $env:TEMP "qbank-blob.bin"
$tmpJson = Join-Path $env:TEMP "qbank-request.json"

foreach ($f in $files) {
    $path = $f.Trim()
    $line = GitOut @("ls-tree", "HEAD", "--", $path)
    if (-not $line) { throw "取不到 git 对象：$path" }
    $parts = $line -split "\s+", 4
    $mode = $parts[0]; $blobSha = $parts[2]

    & cmd /c "git cat-file blob $blobSha > `"$tmpBin`""
    $bytes = [System.IO.File]::ReadAllBytes($tmpBin)
    $body = @{ content = [Convert]::ToBase64String($bytes); encoding = "base64" } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($tmpJson, $body, [System.Text.UTF8Encoding]::new($false))

    $newSha = (& gh api --method POST "repos/$Repo/git/blobs" --input $tmpJson --jq ".sha").Trim()
    Write-Host ("  blob {0} {1,10} 字节  {2}" -f $mode, $bytes.Length, $path)
    $treeEntries += @{ path = $path; mode = $mode; type = "blob"; sha = $newSha }
}

# 2) 基于远端 tree 建新 tree，校验 sha 与本地一致
$treeBody = @{ base_tree = $baseTree; tree = $treeEntries } | ConvertTo-Json -Depth 6 -Compress
[System.IO.File]::WriteAllText($tmpJson, $treeBody, [System.Text.UTF8Encoding]::new($false))
$newTree = (& gh api --method POST "repos/$Repo/git/trees" --input $tmpJson --jq ".sha").Trim()
Write-Host "newTree=$newTree"
if ($newTree -ne $localTree) { throw "tree sha 与本地不一致（期望 $localTree）——多半是换行符被改写了" }
Write-Host "tree sha 一致 ✔"

# 3) 建提交，校验 sha 与本地一致
$commitBody = @{
    message   = $message
    tree      = $newTree
    parents   = @($base)
    author    = @{ name = "Escape-Lin"; email = "fotlim@users.noreply.github.com"; date = $authorDate }
    committer = @{ name = "Escape-Lin"; email = "fotlim@users.noreply.github.com"; date = $committerDate }
} | ConvertTo-Json -Depth 6 -Compress
[System.IO.File]::WriteAllText($tmpJson, $commitBody, [System.Text.UTF8Encoding]::new($false))
$newCommit = (& gh api --method POST "repos/$Repo/git/commits" --input $tmpJson --jq ".sha").Trim()
Write-Host "newCommit=$newCommit"
if ($newCommit -ne $localHead) { throw "提交 sha 与本地不一致（期望 $localHead）" }
Write-Host "提交 sha 一致 ✔"

# 4) 移动分支指针
$refBody = @{ sha = $newCommit; force = $false } | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($tmpJson, $refBody, [System.Text.UTF8Encoding]::new($false))
$null = & gh api --method PATCH "repos/$Repo/git/refs/heads/$Branch" --input $tmpJson
Write-Host "已推送 $Branch -> $newCommit"

Remove-Item $tmpBin, $tmpJson, $rawFile -Force -ErrorAction SilentlyContinue