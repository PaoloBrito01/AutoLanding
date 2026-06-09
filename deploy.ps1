$project  = "C:\SFSMods\AutoLanding"
$dll      = "$project\bin\Release\netstandard2.1\AutoLanding.dll"
$plugins  = "C:\Program Files (x86)\Steam\steamapps\common\Spaceflight Simulator\Spaceflight Simulator Game\BepInEx\plugins"

Write-Host "Compilando..." -ForegroundColor Cyan
dotnet build -c Release $project

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build falhou. Corriga os erros antes de continuar." -ForegroundColor Red
    exit 1
}

Write-Host "Copiando para o jogo..." -ForegroundColor Cyan
Copy-Item $dll "$plugins\AutoLanding.dll" -Force

Write-Host "Pronto! Abra o jogo e teste." -ForegroundColor Green
