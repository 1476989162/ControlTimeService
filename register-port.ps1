# 一次性注册 HTTP 监听权限（需管理员 PowerShell 运行）
# 用法: .\register-port.ps1
#       .\register-port.ps1 -Port 19528

param(
    [int]$Port = 9528
)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "请以管理员身份运行 PowerShell，然后重新执行此脚本。" -ForegroundColor Red
    exit 1
}

$url = "http://+:$Port/"
Write-Host "注册 URL 预留: $url" -ForegroundColor Cyan

netsh http delete urlacl url=$url 2>$null | Out-Null
netsh http add urlacl url=$url user=Everyone

if ($LASTEXITCODE -eq 0) {
    Write-Host "成功！ControlCenter 可使用端口 $Port。" -ForegroundColor Green
    Write-Host "客户端 control_config.json 中 server_url 请设为: http://<本机IP>:$Port" -ForegroundColor Yellow
} else {
    Write-Host "注册失败，请检查 netsh 输出。" -ForegroundColor Red
    exit 1
}
