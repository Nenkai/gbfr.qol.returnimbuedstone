# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/gbfr.qol.returnimbuedstone/*" -Force -Recurse
dotnet publish "./gbfr.qol.returnimbuedstone.csproj" -c Release -o "$env:RELOADEDIIMODS/gbfr.qol.returnimbuedstone" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location