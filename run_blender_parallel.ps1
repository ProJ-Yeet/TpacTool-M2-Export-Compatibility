# 并行运行 Blender FBX 批量导入导出（多个 --background 进程，每个处理一段文件）。
# 用法：在 PowerShell 里运行  ./run_blender_parallel.ps1
# 注意：Blender 单个进程内场景操作是单线程的，并行靠多个进程。

# ---- 可配置项 ----
$Blender   = "D:\ProgramFiles\Blender Foundation\Blender 5.1\blender.exe"
$Script    = "D:\Projects\Bannerlord\TpacTool\blender_batch_fbx_roundtrip.py"
$ImportDir = "D:\用户文档\Desktop\Bannerlord\animations\human_skeleton"
$Jobs      = 4          # 同时开的进程数（按 CPU 核数调，建议 2~8）
# ------------------

if (-not (Test-Path $Blender)) { Write-Error "找不到 Blender: $Blender"; exit 1 }
if (-not (Test-Path $Script))  { Write-Error "找不到脚本: $Script"; exit 1 }

$files = @(Get-ChildItem $ImportDir -Recurse -Filter *.fbx -ErrorAction SilentlyContinue)
$n = $files.Count
if ($n -eq 0) { Write-Error "导入目录下没有 FBX: $ImportDir"; exit 1 }

$per = [math]::Ceiling($n / [double]$Jobs)
Write-Host "共 $n 个 FBX，分 $Jobs 个进程，每进程约 $per 个"

$started = 0
for ($i = 0; $i -lt $Jobs; $i++) {
    $start = $i * $per + 1
    $end   = [math]::Min(($i + 1) * $per, $n)
    if ($start -gt $n) { continue }
    Write-Host "启动进程 $($i+1): 文件 $start .. $end"
    Start-Process -FilePath $Blender -ArgumentList @(
        "--background", "--python", $Script, "--", "$start", "$end"
    ) -WindowStyle Hidden
    $started++
}
Write-Host "已启动 $started 个 Blender 后台进程，请观察输出目录。"
