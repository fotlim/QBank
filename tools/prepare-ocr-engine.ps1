<#
    准备随程序内置的离线 OCR 引擎（PaddleOCR-json）。

    用途：完整版安装包会把这里生成出来的目录原样打进安装包，
          用户装完就能直接识图，不用另外下 Umi-OCR。

    做法：从本机已装的 Umi-OCR 复制一份，再裁掉用不上的东西：
      · 只留简体中文模型（其余语言各 5~11MB，绝大多数人一辈子用不上）
      · 删掉实测可以不要的文件（onnxruntime / concorde / vcomp / i18n.csv / py 脚本）
      · 保留 mklml、opencv、paddle_inference、mkldnn、paddle2onnx、libiomp5md ——
        这几个缺一个引擎就起不来，实测过，别手贱删。

    产出：<仓库根>\.ocr-engine\（已 gitignore，不进版本库）
         约 242MB / 26 个文件。

    用法：
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\prepare-ocr-engine.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\prepare-ocr-engine.ps1 -Source "D:\Umi-OCR"
#>
param(
    [string]$Source = "",
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = Join-Path $repo ".ocr-engine" }

function Fail($msg) { Write-Host "✗ $msg" -ForegroundColor Red; exit 1 }

# ── 1. 找源引擎目录 ────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($Source)) {
    $candidates = @()
    foreach ($drive in @("C:", "D:", "E:", "F:")) {
        $candidates += "$drive\Umi-OCR"
        $candidates += "$drive\Program Files\Umi-OCR"
        $candidates += "$drive\Tools\Umi-OCR"
    }
    $candidates += "$env:LOCALAPPDATA\Programs\Umi-OCR"
    $candidates += "$env:USERPROFILE\Umi-OCR"

    foreach ($root in $candidates) {
        if (-not (Test-Path $root)) { continue }
        $plugins = Join-Path $root "UmiOCR-data\plugins"
        if (-not (Test-Path $plugins)) { continue }
        $hit = Get-ChildItem $plugins -Directory -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -like "*PaddleOCR*" } |
               Where-Object { Test-Path (Join-Path $_.FullName "PaddleOCR-json.exe") } |
               Select-Object -First 1
        if ($hit) { $Source = $hit.FullName; break }
    }
}

if ([string]::IsNullOrWhiteSpace($Source) -or -not (Test-Path (Join-Path $Source "PaddleOCR-json.exe"))) {
    Fail "没找到 PaddleOCR-json.exe。请先在 Umi-OCR 里装好 PaddleOCR 插件，或用 -Source 参数指定引擎目录。"
}

Write-Host "源引擎：$Source" -ForegroundColor Cyan

# ── 2. 复制 ────────────────────────────────────────────
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out -Force | Out-Null
Copy-Item (Join-Path $Source "*") $Out -Recurse -Force

# ── 3. 裁模型：只留简体中文 ────────────────────────────
$models = Join-Path $Out "models"
$keepModels = @("ch_PP-OCRv3_det_infer", "ch_PP-OCRv3_rec_infer", "ch_ppocr_mobile_v2.0_cls_infer")
$keepConfigs = @("config_chinese.txt", "dict_chinese.txt", "configs.txt")

Get-ChildItem $models -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    if ($keepModels -notcontains $_.Name) {
        Write-Host "  裁掉模型：$($_.Name)"
        Remove-Item $_.FullName -Recurse -Force
    }
}
Get-ChildItem $models -File -ErrorAction SilentlyContinue | ForEach-Object {
    if ($keepConfigs -notcontains $_.Name -and $_.Name -notlike "dict_*") {
        Remove-Item $_.FullName -Force
    } elseif ($_.Name -like "dict_*" -and $keepConfigs -notcontains $_.Name) {
        Write-Host "  裁掉字典：$($_.Name)"
        Remove-Item $_.FullName -Force
    }
}

# ── 4. 裁实测可删的文件 ────────────────────────────────
# 下面这些移走后引擎仍然能正常识别（已验证）：
$drop = @("onnxruntime.dll", "concrt140.dll", "vcomp140.dll", "i18n.csv",
          "README.md", "PPOCR_api.py", "PPOCR_config.py", "PPOCR_umi.py", "__init__.py")
foreach ($f in $drop) {
    $p = Join-Path $Out $f
    if (Test-Path $p) { Write-Host "  裁掉：$f"; Remove-Item $p -Force }
}
$pycache = Join-Path $Out "__pycache__"
if (Test-Path $pycache) { Remove-Item $pycache -Recurse -Force }

# ── 5. 冒烟测试：真跑一次识别，确认裁完还能用 ──────────
$exe = Join-Path $Out "PaddleOCR-json.exe"
$testPng = Join-Path $Out "_selftest.png"
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 900, 240
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$font = New-Object System.Drawing.Font("Microsoft YaHei", 26, [System.Drawing.FontStyle]::Bold)
$g.DrawString("1、中国的首都是哪座城市?", $font, [System.Drawing.Brushes]::Black, 20, 20)
$g.DrawString("A. 上海    B. 北京    C. 广州", $font, [System.Drawing.Brushes]::Black, 20, 110)
$g.Dispose()
$bmp.Save($testPng, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "正在冒烟测试（真识别一张图）…" -ForegroundColor Cyan
$t0 = Get-Date
# --config_path / --image_path 都按当前工作目录解析，必须切到引擎目录里跑。
# 另外引擎会把启动日志写到 stderr，PS5.1 在 ErrorActionPreference=Stop 下会把它当成
# 致命错误直接中断，所以拿 Start-Process 把两条流分别落盘，只认 stdout 里的 JSON。
$stdoutFile = Join-Path $env:TEMP ("qbank-ocr-out-" + [guid]::NewGuid().ToString("N") + ".txt")
$stderrFile = Join-Path $env:TEMP ("qbank-ocr-err-" + [guid]::NewGuid().ToString("N") + ".txt")
$proc = Start-Process -FilePath $exe `
    -ArgumentList @("--config_path", "models\config_chinese.txt", "--image_path", $testPng) `
    -WorkingDirectory $Out -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
$ms = [math]::Round(((Get-Date) - $t0).TotalMilliseconds)
$raw = if (Test-Path $stdoutFile) { Get-Content $stdoutFile -Encoding UTF8 } else { @() }
$errText = if (Test-Path $stderrFile) { (Get-Content $stderrFile -Encoding UTF8) -join "`n" } else { "" }
Remove-Item $stdoutFile, $stderrFile -Force -ErrorAction SilentlyContinue

$line = ($raw | Where-Object { $_ -match '^\{"code"' } | Select-Object -First 1)
Remove-Item $testPng -Force -ErrorAction SilentlyContinue

if (-not $line) {
    Fail "引擎裁完跑不起来（退出码 $($proc.ExitCode)），没有 JSON 输出。`nstdout: $(($raw | Out-String).Trim())`nstderr: $($errText.Trim())"
}

$json = $line | ConvertFrom-Json
if ($json.code -ne 100) { Fail "引擎裁完报错：code=$($json.code) data=$($json.data)" }
if (-not ($json.data | Where-Object { $_.text -match '首都' })) {
    Fail "引擎能跑但没认出「首都」，识别结果：$(($json.data | ForEach-Object { $_.text }) -join ' | ')"
}

# ── 6. 报告 ────────────────────────────────────────────
$stat = Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum
Write-Host ""
Write-Host "✓ 引擎就绪：$Out" -ForegroundColor Green
Write-Host ("  文件 {0} 个，共 {1:N2} MB，识别耗时 {2} ms" -f $stat.Count, ($stat.Sum / 1MB), $ms)
Write-Host ""
Write-Host "接下来打完整版安装包：" -ForegroundColor Cyan
Write-Host "  ISCC.exe /DBundleOcr=1 install\qbank.iss"
